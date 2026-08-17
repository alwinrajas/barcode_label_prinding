using System.Text;
using BarcodePrinter.Application.Abstractions;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Printing;
using BarcodePrinter.Infrastructure.Templates;
using BarcodePrinter.Domain;
using BarcodePrinter.Infrastructure.Services;
using BarcodePrinter.Labels;
using BarcodePrinter.Labels.Binding;
using BarcodePrinter.Labels.Native;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using SkiaSharp;

namespace BarcodePrinter.Infrastructure.Printing;

/// <summary>Warning flags a label that will render but with a defect the
/// operator should know about (e.g. a blank feedback QR).</summary>
public sealed record LabelPreview(
    byte[]? Png, string Zpl, string Format, string? Unavailable, string? Warning = null);

/// <summary>
/// Produces the print screen's preview.
///
/// A preview MUST NOT create a print transaction: it allocates no carton
/// numbers, writes no job, touches no sequence and enqueues nothing. It reads
/// the template and the product, binds values in memory and draws. That is why
/// it takes its own read-only path rather than reusing the submit pipeline with
/// a flag — a flag is one wrong branch away from a phantom job.
/// </summary>
public sealed class LabelPreviewService(
    IDbConnectionFactory connections,
    TemplateRenderService renderer,
    LabelRasterizer rasterizer,
    IProductImageStore imageStore,
    ISettingsProvider settings,
    FieldBinder binder,
    ILogger<LabelPreviewService> logger)
{
    public async Task<LabelPreview> RenderAsync(PrintPreviewRequest request, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var product = await LoadProductAsync(conn, request.ProductId, ct);
        var templateId = await TemplateResolver.ResolveAsync(
            conn, request.TemplateId, request.ProductId, request.PrinterId, ct);

        // The ZPL is produced by the real render path, so what the preview shows
        // and what the printer receives cannot diverge.
        var zpl = await renderer.RenderPreviewAsync(
            templateId, product,
            request.Batch ?? product.DefaultBatch,
            request.ProductionDate ?? product.DefaultProductionDate,
            request.ExpiryDate ?? product.DefaultExpiryDate,
            request.QuantityText ?? product.DefaultQuantityText,
            request.CartonNumber ?? 1, request.CartonTotal ?? 1, conn, ct);

        var template = await LoadDefinitionAsync(conn, templateId, ct);
        if (template is null)
        {
            // A client-supplied printer file: we hold no geometry model for it,
            // so there is nothing faithful to draw. Say so plainly rather than
            // inventing a picture that might not match the media (§6.4).
            return new LabelPreview(null, zpl, "Zpl",
                "A visual preview is not available for a supplied printer file. " +
                "The label data below is exactly what will be sent to the printer.");
        }

        var (definition, mappings, fields) = template.Value;

        try
        {
            var context = await BuildContextAsync(product, request, ct);
            var bound = binder.Bind(mappings, context);

            // Values arrive keyed by placeholder; the rasteriser draws elements,
            // so re-key by element id via the registration-time field index —
            // the same association the print path uses, so preview and print
            // cannot bind the same value to different elements.
            var bindable = NativeTemplateAdapter.BindableElements(definition);
            var placeholderByElementId = TemplateRenderService.MapPlaceholdersToElements(fields, bindable);
            var byElement = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (elementId, placeholder) in placeholderByElementId)
            {
                if (bound.TryGetValue(placeholder, out var value))
                {
                    byElement[elementId] = value;
                }
            }

            byte[] png;
            var loaded = new List<SKBitmap>();
            try
            {
                png = rasterizer.RenderPng(definition, byElement, LoadImage);
            }
            finally
            {
                // The loader owns bitmap lifetime (the rasterizer draws only).
                foreach (var bitmap in loaded)
                {
                    bitmap.Dispose();
                }
            }
            return new LabelPreview(png, zpl, NativeTemplateAdapter.FormatName, null,
                await BuildWarningAsync(definition, mappings, product, ct));

            SKBitmap? LoadImage(string hash)
            {
                try
                {
                    using var stream = imageStore
                        .OpenAsync(hash, ImageVariant.Full, ct).GetAwaiter().GetResult();
                    var bitmap = stream is null ? null : SKBitmap.Decode(stream);
                    if (bitmap is not null)
                    {
                        loaded.Add(bitmap);
                    }
                    return bitmap;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Preview could not load product image {Hash}", hash);
                    return null;
                }
            }
        }
        catch (FieldBindingException ex)
        {
            // Required data missing is a legitimate preview outcome and the
            // reason to preview at all — report it, do not throw a 500.
            return new LabelPreview(null, zpl, NativeTemplateAdapter.FormatName, ex.Message);
        }
    }

    /// <summary>Defects the operator should know about BEFORE a 500-carton run:
    /// things that render on screen but would come out wrong (or missing) on
    /// the physical label. Reported together so one does not mask another.</summary>
    private async Task<string?> BuildWarningAsync(
        LabelDefinition definition, IReadOnlyList<FieldMapping> mappings,
        ProductSnapshot product, CancellationToken ct)
    {
        var warnings = new List<string>();

        if (mappings.Any(m => string.Equals(
                m.DataKey, TokenVocabulary.FeedbackUrlKey, StringComparison.OrdinalIgnoreCase)) &&
            string.IsNullOrWhiteSpace(await settings.GetAsync("Label:FeedbackFormUrl", ct)))
        {
            warnings.Add("The feedback QR code will be blank — no feedback form URL is configured. " +
                         "An administrator can set it under Settings.");
        }

        // The on-screen rasteriser draws any size; the ZPL converter refuses
        // images beyond its dot limit. Without this the preview would show a
        // picture the printer silently omits.
        if (!string.IsNullOrWhiteSpace(product.ImageHash))
        {
            var oversized = definition.Elements.OfType<ImageElement>().Any(e =>
                definition.MmToDots(e.WidthMm) > ZplImageConverter.MaxDots ||
                definition.MmToDots(e.HeightMm) > ZplImageConverter.MaxDots);
            if (oversized)
            {
                warnings.Add("The product image is too large to print at this label's resolution " +
                             "and will be left blank. Ask an administrator to reduce the image area " +
                             "on the template.");
            }
        }

        return warnings.Count == 0 ? null : string.Join(" ", warnings);
    }

    private async Task<PrintContext> BuildContextAsync(
        ProductSnapshot product, PrintPreviewRequest request, CancellationToken ct)
    {
        var dateFormat = await settings.GetAsync("Label:DateFormat", ct) ?? "dd/MM/yyyy";
        var timestampFormat = await settings.GetAsync("Label:TimestampFormat", ct) ?? "dd/MM/yyyy HH:mm";
        var feedbackUrl = await settings.GetAsync("Label:FeedbackFormUrl", ct);
        var companyName = await settings.GetAsync("Company:Name", ct);

        var carton = request.CartonNumber ?? 1;
        var total = request.CartonTotal ?? 1;

        return new PrintContext(
            new ProductValues(product.Code, product.Description, product.BarcodeValue,
                product.Uom, product.Size, product.Color, product.ImageHash),
            new EffectiveValues(
                request.Batch ?? product.DefaultBatch,
                request.ProductionDate ?? product.DefaultProductionDate,
                request.ExpiryDate ?? product.DefaultExpiryDate,
                request.QuantityText ?? product.DefaultQuantityText),
            new CartonValues(carton, total, carton, carton,
                carton.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            // Marked as a preview, so a template that prints the job number
            // cannot show a plausible-looking one that was never issued.
            new JobValues("PREVIEW", "preview", "Preview", false),
            new SettingsValues(feedbackUrl, companyName, dateFormat, timestampFormat),
            DateTime.Now);
    }

    /// <summary>Definition plus mapping for a Native template; null when the
    /// template is a supplied printer file. The raw field rows travel along so
    /// the caller can re-key placeholders to elements the way the print path does.</summary>
    private static async Task<(LabelDefinition Definition, IReadOnlyList<FieldMapping> Mappings,
            IReadOnlyList<TemplateFieldRow> Fields)?>
        LoadDefinitionAsync(MySqlConnection conn, long templateId, CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<DefinitionRow>(new CommandDefinition(
            """
            SELECT t.template_format AS Format, v.artifact_blob AS Artifact,
                   CAST(v.id AS SIGNED) AS VersionId
            FROM label_templates t
            JOIN label_template_versions v ON v.template_id = t.id AND v.version = t.current_version
            WHERE t.id = @templateId AND t.is_active = 1
            """, new { templateId }, cancellationToken: ct));

        if (row is null || !string.Equals(
                row.Format, NativeTemplateAdapter.FormatName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var fields = (await conn.QueryAsync<TemplateFieldRow>(new CommandDefinition(
            """
            SELECT placeholder_ref AS PlaceholderRef, data_key AS DataKey, data_kind AS DataKind,
                   format_string AS FormatString, transform AS Transform, max_length AS MaxLength,
                   overflow AS Overflow, is_required AS IsRequired,
                   fallback_value AS FallbackValue, sample_value AS SampleValue
            FROM label_template_fields WHERE template_version_id = @VersionId ORDER BY sort_order
            """, new { row.VersionId }, cancellationToken: ct))).ToList();

        var mappings = fields.Select(f => new FieldMapping(
            f.PlaceholderRef, f.DataKey,
            Enum.Parse<FieldDataKind>(f.DataKind, ignoreCase: true),
            f.FormatString,
            Enum.Parse<FieldTransform>(f.Transform, ignoreCase: true),
            f.MaxLength,
            Enum.Parse<OverflowBehaviour>(f.Overflow, ignoreCase: true),
            // Preview must render what it can even when a required value is
            // absent; submit still enforces the requirement.
            IsRequired: false,
            f.FallbackValue)).ToList();

        return (LabelDefinition.Parse(Encoding.UTF8.GetString(row.Artifact)), mappings, fields);
    }

    private static async Task<ProductSnapshot> LoadProductAsync(
        MySqlConnection conn, long productId, CancellationToken ct) =>
        await conn.QuerySingleOrDefaultAsync<ProductSnapshot>(new CommandDefinition(
            """
            SELECT CAST(p.id AS SIGNED) AS Id, p.code AS Code, p.description AS Description,
                   COALESCE(NULLIF(p.barcode_value, ''), p.code) AS BarcodeValue,
                   u.code AS Uom, p.size AS Size, p.color AS Color,
                   p.default_batch AS DefaultBatch,
                   p.default_production_date AS DefaultProductionDate,
                   p.default_expiry_date AS DefaultExpiryDate,
                   p.default_quantity_text AS DefaultQuantityText,
                   pi.content_hash AS ImageHash, p.is_active AS IsActive
            FROM products p
            LEFT JOIN uoms u ON u.id = p.uom_id
            LEFT JOIN product_images pi ON pi.id = p.primary_image_id
            WHERE p.id = @productId
            """, new { productId }, cancellationToken: ct))
        ?? throw new NotFoundException("Product", productId);

    private sealed class DefinitionRow
    {
        public string Format { get; set; } = "";
        public byte[] Artifact { get; set; } = [];
        public long VersionId { get; set; }
    }
}
