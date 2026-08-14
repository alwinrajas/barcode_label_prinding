using System.Security.Cryptography;
using System.Text;
using BarcodePrinter.Application.Printing;
using BarcodePrinter.Contracts;
using BarcodePrinter.Domain;
using BarcodePrinter.Infrastructure.Templates;
using BarcodePrinter.Labels;
using BarcodePrinter.Labels.Binding;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using MySqlConnector;

namespace BarcodePrinter.Infrastructure.Printing;

/// <summary>
/// Turns a job into printer bytes: the stored format is emitted ONCE, then one
/// small recall per label (§6.2). A 500-carton job therefore sends the layout
/// once and ~200 bytes per label instead of the full template 500 times.
/// </summary>
public sealed class TemplateRenderService(
    TemplateAdapterRegistry adapters,
    FieldBinder binder,
    IMemoryCache cache,
    ZplImageConverter images,
    LabelRasterizer rasterizer,
    Application.Abstractions.IProductImageStore imageStore,
    Application.Abstractions.ISettingsProvider settings)
{
    public async Task<RenderedPayload> RenderJobAsync(
        long templateId, ProductSnapshot product,
        string? batch, DateOnly? productionDate, DateOnly? expiryDate, string? quantityText,
        CartonAllocation allocation, ICartonNumberingStrategy strategy,
        string jobNo, string username, string printerName, short copiesPerLabel,
        bool isReprint, MySqlConnection conn, System.Data.Common.DbTransaction? tx,
        CancellationToken ct)
    {
        var template = await LoadTemplateAsync(conn, tx, templateId, ct);
        var adapter = adapters.Resolve(template.Format);

        var mappings = BuildMappings(template.Fields);
        var placeholders = template.Fields
            .Where(f => int.TryParse(f.SampleValue, out _))
            .ToDictionary(f => int.Parse(f.SampleValue!), f => f.PlaceholderRef);

        if (mappings.Count == 0)
        {
            throw new DomainException("TEMPLATE_NOT_MAPPED",
                "This label template has no field mapping yet. Ask an administrator to complete its setup.");
        }

        var artifact = Encoding.UTF8.GetString(template.Artifact);
        var prepared = adapter.Prepare(artifact, $"R:{template.Code}.ZPL", placeholders);

        // The product is constant for a job, so its raster goes into the stored
        // format ONCE rather than with every label (§6.2). On a 500-carton run
        // that is the difference between the network and the printer being the
        // bottleneck.
        var definePayload = await SubstituteImagesAsync(
            template.Format, artifact, prepared, mappings, product.ImageHash, ct);

        var dateFormat = await settings.GetAsync("Label:DateFormat", ct) ?? "dd/MM/yyyy";
        var timestampFormat = await settings.GetAsync("Label:TimestampFormat", ct) ?? "dd/MM/yyyy HH:mm";
        var feedbackUrl = await settings.GetAsync("Label:FeedbackFormUrl", ct);
        var companyName = await settings.GetAsync("Company:Name", ct);

        var output = new StringBuilder(4_096);
        output.Append(definePayload);            // layout + graphics: ONCE per job

        var now = DateTime.Now;
        foreach (var carton in allocation.Numbers)
        {
            var context = new PrintContext(
                new ProductValues(product.Code, product.Description, product.BarcodeValue,
                    product.Uom, product.Size, product.Color, product.ImageHash),
                new EffectiveValues(batch, productionDate, expiryDate, quantityText),
                new CartonValues(carton, allocation.Total, allocation.From, allocation.To,
                    strategy.Format(carton, allocation)),
                new JobValues(jobNo, username, printerName, isReprint),
                new SettingsValues(feedbackUrl, companyName, dateFormat, timestampFormat),
                now);

            var bound = binder.Bind(mappings, context);
            output.Append(adapter.RenderRecall(
                new RenderRequest(prepared, mappings, bound, copiesPerLabel)));
        }

        var bytes = Encoding.UTF8.GetBytes(output.ToString());
        return new RenderedPayload("Zpl", bytes,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>
    /// Renders a job as one picture per label, for printers that speak no
    /// printer language of their own (§7.2).
    ///
    /// Only definitions we own can be rasterised. A client-supplied printer file
    /// is opaque geometry, so pairing one with an office printer is a
    /// configuration mistake and is reported as such rather than printing
    /// something wrong.
    /// </summary>
    public async Task<RenderedPayload> RenderRasterJobAsync(
        long templateId, ProductSnapshot product,
        string? batch, DateOnly? productionDate, DateOnly? expiryDate, string? quantityText,
        CartonAllocation allocation, ICartonNumberingStrategy strategy,
        string jobNo, string username, string printerName, short copiesPerLabel,
        short? printerDpi, MySqlConnection conn, System.Data.Common.DbTransaction? tx,
        CancellationToken ct)
    {
        var template = await LoadTemplateAsync(conn, tx, templateId, ct);
        if (!string.Equals(template.Format, Labels.Native.NativeTemplateAdapter.FormatName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("TEMPLATE_NOT_RASTERISABLE",
                $"Template '{template.Code}' is a printer file and can only be sent to a label " +
                "printer. Choose a label printer, or a template designed in the application.");
        }

        var mappings = BuildMappings(template.Fields);
        if (mappings.Count == 0)
        {
            throw new DomainException("TEMPLATE_NOT_MAPPED",
                "This label template has no field mapping yet. Ask an administrator to complete its setup.");
        }

        var definition = Labels.Native.LabelDefinition
            .Parse(Encoding.UTF8.GetString(template.Artifact))
            // An office printer's resolution is not the label designer's; render
            // at the device's own dpi so the label is the size it should be.
            .AtDpi(printerDpi is > 0 ? printerDpi.Value : 203);

        var bindable = Labels.Native.NativeTemplateAdapter.BindableElements(definition);
        var dateFormat = await settings.GetAsync("Label:DateFormat", ct) ?? "dd/MM/yyyy";
        var timestampFormat = await settings.GetAsync("Label:TimestampFormat", ct) ?? "dd/MM/yyyy HH:mm";
        var feedbackUrl = await settings.GetAsync("Label:FeedbackFormUrl", ct);
        var companyName = await settings.GetAsync("Company:Name", ct);

        var now = DateTime.Now;
        var copies = copiesPerLabel < 1 ? 1 : (int)copiesPerLabel;
        var pages = new List<byte[]>((int)allocation.Total * copies);

        foreach (var carton in allocation.Numbers)
        {
            var context = new PrintContext(
                new ProductValues(product.Code, product.Description, product.BarcodeValue,
                    product.Uom, product.Size, product.Color, product.ImageHash),
                new EffectiveValues(batch, productionDate, expiryDate, quantityText),
                new CartonValues(carton, allocation.Total, allocation.From, allocation.To,
                    strategy.Format(carton, allocation)),
                new JobValues(jobNo, username, printerName, false),
                new SettingsValues(feedbackUrl, companyName, dateFormat, timestampFormat),
                now);

            var bound = binder.Bind(mappings, context);
            var byElement = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < bindable.Count && i < mappings.Count; i++)
            {
                if (bound.TryGetValue(mappings[i].PlaceholderRef, out var value))
                {
                    byElement[bindable[i].Id] = value;
                }
            }

            var png = rasterizer.RenderPng(definition, byElement, hash => LoadBitmap(hash, ct));

            // Copies are pages here; a GDI printer has no ^PQ equivalent.
            for (var copy = 0; copy < copies; copy++)
            {
                pages.Add(png);
            }
        }

        var bytes = BarcodePrinter.Printing.Abstractions.RasterLabelPayload.Pack(pages);
        return new RenderedPayload("Raster", bytes,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private SkiaSharp.SKBitmap? LoadBitmap(string hash, CancellationToken ct)
    {
        try
        {
            using var stream = imageStore.OpenAsync(hash, Application.Abstractions.ImageVariant.Full, ct)
                .GetAwaiter().GetResult();
            return stream is null ? null : SkiaSharp.SKBitmap.Decode(stream);
        }
        catch (Exception)
        {
            // A missing picture degrades to a blank area; the label still prints.
            return null;
        }
    }

    /// <summary>Single-label render for the on-screen preview.</summary>
    public async Task<string> RenderPreviewAsync(
        long templateId, ProductSnapshot product,
        string? batch, DateOnly? productionDate, DateOnly? expiryDate, string? quantityText,
        long cartonNumber, long cartonTotal, MySqlConnection conn, CancellationToken ct)
    {
        var allocation = new CartonAllocation(cartonNumber, cartonNumber, cartonTotal);
        var payload = await RenderJobAsync(
            templateId, product, batch, productionDate, expiryDate, quantityText,
            allocation, new ManualRangeCartonStrategy(CartonNumberFormat.Bare),
            "PREVIEW", "preview", "Preview", 1, false, conn, null, ct);
        return Encoding.UTF8.GetString(payload.Data);
    }

    /// <summary>
    /// Replaces each image placeholder in the stored format with the product's
    /// raster, or with nothing when there is no usable image — a label prints
    /// without its picture rather than not printing at all.
    ///
    /// Only applies to definitions we own: a client-supplied artifact carries
    /// its own graphics and must pass through byte-for-byte (A-15).
    /// </summary>
    private async Task<string> SubstituteImagesAsync(
        string format, string artifact, PreparedTemplate prepared,
        IReadOnlyList<FieldMapping> mappings, string? imageHash, CancellationToken ct)
    {
        var imageFields = mappings.Where(m => m.DataKind == FieldDataKind.Image).ToList();
        if (imageFields.Count == 0 ||
            !string.Equals(format, Labels.Native.NativeTemplateAdapter.FormatName,
                StringComparison.OrdinalIgnoreCase))
        {
            return prepared.DefinePayload;
        }

        var definition = Labels.Native.LabelDefinition.Parse(artifact);
        var payload = prepared.DefinePayload;

        foreach (var field in imageFields)
        {
            var detected = prepared.Fields.FirstOrDefault(f => f.SampleValue == field.PlaceholderRef);
            var element = detected is null
                ? null
                : definition.Elements.OfType<Labels.Native.ImageElement>()
                    .ElementAtOrDefault(0);

            var placeholder = $"^FN{field.PlaceholderRef}^FS";
            var raster = element is null
                ? null
                : await images.TryRenderAsync(
                    imageHash,
                    definition.MmToDots(element.WidthMm),
                    definition.MmToDots(element.HeightMm), ct);

            payload = payload.Replace(placeholder, raster ?? "^FS", StringComparison.Ordinal);
        }

        return payload;
    }

    private static List<FieldMapping> BuildMappings(IReadOnlyList<TemplateFieldRow> fields) =>
        fields.Select(f => new FieldMapping(
            f.PlaceholderRef, f.DataKey,
            Enum.Parse<FieldDataKind>(f.DataKind, ignoreCase: true),
            f.FormatString,
            Enum.Parse<FieldTransform>(f.Transform, ignoreCase: true),
            f.MaxLength,
            Enum.Parse<OverflowBehaviour>(f.Overflow, ignoreCase: true),
            f.IsRequired, f.FallbackValue)).ToList();

    /// <summary>Template artifact + mapping, cached 5 min. Templates change
    /// rarely; a batch run must not re-read them per label.</summary>
    private async Task<TemplateBundle> LoadTemplateAsync(
        MySqlConnection conn, System.Data.Common.DbTransaction? tx, long templateId, CancellationToken ct)
    {
        var key = $"tpl-bundle:{templateId}";
        if (cache.TryGetValue<TemplateBundle>(key, out var cached) && cached is not null)
        {
            return cached;
        }

        var head = await conn.QuerySingleOrDefaultAsync<TemplateHeadRow>(new CommandDefinition(
            """
            SELECT t.code AS Code, t.template_format AS Format, t.current_version AS Version,
                   CAST(v.id AS SIGNED) AS VersionId, v.artifact_blob AS Artifact
            FROM label_templates t
            JOIN label_template_versions v ON v.template_id = t.id AND v.version = t.current_version
            WHERE t.id = @templateId AND t.is_active = 1
            """, new { templateId }, transaction: tx, cancellationToken: ct))
            ?? throw new DomainException(ErrorCodes.ValidationFailed,
                "This label template is not available. Choose an active template.");

        var fields = (await conn.QueryAsync<TemplateFieldRow>(new CommandDefinition(
            """
            SELECT placeholder_ref AS PlaceholderRef, data_key AS DataKey, data_kind AS DataKind,
                   format_string AS FormatString, transform AS Transform, max_length AS MaxLength,
                   overflow AS Overflow, is_required AS IsRequired,
                   fallback_value AS FallbackValue, sample_value AS SampleValue
            FROM label_template_fields WHERE template_version_id = @VersionId ORDER BY sort_order
            """, new { head.VersionId }, transaction: tx, cancellationToken: ct))).ToList();

        var bundle = new TemplateBundle(head.Code, head.Format, head.Version, head.Artifact, fields);
        cache.Set(key, bundle, TimeSpan.FromMinutes(5));
        return bundle;
    }

    private sealed record TemplateHeadRow(
        string Code, string Format, int Version, long VersionId, byte[] Artifact);

    private sealed record TemplateBundle(
        string Code, string Format, int Version, byte[] Artifact, IReadOnlyList<TemplateFieldRow> Fields);
}

public sealed class TemplateFieldRow
{
    public string PlaceholderRef { get; set; } = "";
    public string DataKey { get; set; } = "";
    public string DataKind { get; set; } = "";
    public string? FormatString { get; set; }
    public string Transform { get; set; } = "None";
    public int? MaxLength { get; set; }
    public string Overflow { get; set; } = "Error";
    public bool IsRequired { get; set; }
    public string? FallbackValue { get; set; }
    public string? SampleValue { get; set; }
}
