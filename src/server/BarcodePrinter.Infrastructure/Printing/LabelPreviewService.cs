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

public sealed record LabelPreview(byte[]? Png, string Zpl, string Format, string? Unavailable);

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

        // The ZPL is produced by the real render path, so what the preview shows
        // and what the printer receives cannot diverge.
        var zpl = await renderer.RenderPreviewAsync(
            request.TemplateId, product,
            request.Batch ?? product.DefaultBatch,
            request.ProductionDate ?? product.DefaultProductionDate,
            request.ExpiryDate ?? product.DefaultExpiryDate,
            request.QuantityText ?? product.DefaultQuantityText,
            request.CartonNumber ?? 1, request.CartonTotal ?? 1, conn, ct);

        var template = await LoadDefinitionAsync(conn, request.TemplateId, ct);
        if (template is null)
        {
            // A client-supplied printer file: we hold no geometry model for it,
            // so there is nothing faithful to draw. Say so plainly rather than
            // inventing a picture that might not match the media (§6.4).
            return new LabelPreview(null, zpl, "Zpl",
                "A visual preview is not available for a supplied printer file. " +
                "The label data below is exactly what will be sent to the printer.");
        }

        var (definition, mappings) = template.Value;

        try
        {
            var context = await BuildContextAsync(product, request, ct);
            var bound = binder.Bind(mappings, context);

            // Values arrive keyed by placeholder; the rasteriser draws elements,
            // so re-key by element id using the adapter's own ordering.
            var bindable = NativeTemplateAdapter.BindableElements(definition);
            var byElement = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < bindable.Count && i < mappings.Count; i++)
            {
                if (bound.TryGetValue(mappings[i].PlaceholderRef, out var value))
                {
                    byElement[bindable[i].Id] = value;
                }
            }

            var png = rasterizer.RenderPng(definition, byElement, LoadImage);
            return new LabelPreview(png, zpl, NativeTemplateAdapter.FormatName, null);
        }
        catch (FieldBindingException ex)
        {
            // Required data missing is a legitimate preview outcome and the
            // reason to preview at all — report it, do not throw a 500.
            return new LabelPreview(null, zpl, NativeTemplateAdapter.FormatName, ex.Message);
        }

        SKBitmap? LoadImage(string hash)
        {
            try
            {
                using var stream = imageStore
                    .OpenAsync(hash, ImageVariant.Full, ct).GetAwaiter().GetResult();
                return stream is null ? null : SKBitmap.Decode(stream);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Preview could not load product image {Hash}", hash);
                return null;
            }
        }
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
    /// template is a supplied printer file.</summary>
    private static async Task<(LabelDefinition Definition, IReadOnlyList<FieldMapping> Mappings)?>
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

        return (LabelDefinition.Parse(Encoding.UTF8.GetString(row.Artifact)), mappings);
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
