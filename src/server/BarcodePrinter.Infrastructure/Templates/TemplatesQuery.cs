using System.Text;
using BarcodePrinter.Application.Abstractions;
using BarcodePrinter.Contracts.Templates;
using BarcodePrinter.Infrastructure.Services;
using Dapper;

namespace BarcodePrinter.Infrastructure.Templates;

public sealed class TemplatesQuery(IDbConnectionFactory connections, TemplateAdapterRegistry adapters)
{
    public async Task<IReadOnlyList<TemplateSummary>> ListAsync(CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var rows = await conn.QueryAsync<SummaryRow>(new CommandDefinition(
            """
            SELECT CAST(t.id AS SIGNED) AS Id, t.code AS Code, t.name AS Name,
                   t.template_format AS TemplateFormat, t.width_mm AS WidthMm,
                   t.height_mm AS HeightMm, t.dpi AS Dpi, t.current_version AS CurrentVersion,
                   t.is_active AS IsActive, t.is_default AS IsDefault,
                   (SELECT COUNT(*) FROM label_template_fields f
                    JOIN label_template_versions v ON v.id = f.template_version_id
                    WHERE v.template_id = t.id AND v.version = t.current_version) AS MappedFieldCount
            FROM label_templates t
            ORDER BY t.is_default DESC, t.code
            """, cancellationToken: ct));

        return rows.Select(r => new TemplateSummary(
            r.Id, r.Code, r.Name, r.TemplateFormat, r.WidthMm, r.HeightMm, r.Dpi,
            r.CurrentVersion, r.IsActive, r.IsDefault, r.MappedFieldCount)).ToList();
    }

    public async Task<TemplateDetail?> GetAsync(long id, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);

        var row = await conn.QuerySingleOrDefaultAsync<DetailRow>(new CommandDefinition(
            """
            SELECT CAST(t.id AS SIGNED) AS Id, t.code AS Code, t.name AS Name,
                   t.description AS Description, t.template_format AS TemplateFormat,
                   t.width_mm AS WidthMm, t.height_mm AS HeightMm, t.dpi AS Dpi, t.gap_mm AS GapMm,
                   t.orientation AS Orientation, t.layout_type AS LayoutType,
                   t.media_type AS MediaType, t.media_tracking AS MediaTracking,
                   t.current_version AS CurrentVersion, t.is_active AS IsActive,
                   t.is_default AS IsDefault,
                   CAST(v.id AS SIGNED) AS VersionId,
                   COALESCE(v.artifact_filename, '') AS ArtifactFileName,
                   v.artifact_hash AS ArtifactHash, v.artifact_blob AS Artifact
            FROM label_templates t
            JOIN label_template_versions v ON v.template_id = t.id AND v.version = t.current_version
            WHERE t.id = @id
            """, new { id }, cancellationToken: ct));
        if (row is null)
        {
            return null;
        }

        var fields = (await conn.QueryAsync<FieldRow>(new CommandDefinition(
            """
            SELECT CAST(id AS SIGNED) AS Id, placeholder_ref AS PlaceholderRef,
                   field_label AS FieldLabel, data_key AS DataKey, data_kind AS DataKind,
                   format_string AS FormatString, transform AS Transform, max_length AS MaxLength,
                   overflow AS Overflow, is_required AS IsRequired,
                   fallback_value AS FallbackValue, sample_value AS SampleValue
            FROM label_template_fields WHERE template_version_id = @VersionId ORDER BY sort_order
            """, new { row.VersionId }, cancellationToken: ct))).ToList();

        // Re-inspect the stored artifact so the mapping UI always reflects the
        // real file rather than a cached snapshot.
        var adapter = adapters.Resolve(row.TemplateFormat);
        var detected = adapter.Inspect(Encoding.UTF8.GetString(row.Artifact))
            .Select(d => new DetectedFieldDto(
                d.CommandIndex, d.InferredKind.ToString(), d.SampleValue, d.X, d.Y, d.Context))
            .ToList();

        return new TemplateDetail(
            row.Id, row.Code, row.Name, row.Description, row.TemplateFormat,
            row.WidthMm, row.HeightMm, row.Dpi, row.GapMm,
            row.Orientation, row.LayoutType, row.MediaType, row.MediaTracking,
            row.CurrentVersion, row.IsActive, row.IsDefault,
            row.VersionId, row.ArtifactFileName, row.ArtifactHash,
            fields.Select(f => new TemplateFieldDto(
                f.Id, f.PlaceholderRef, f.FieldLabel, f.DataKey, f.DataKind,
                f.FormatString, f.Transform, f.MaxLength, f.Overflow,
                f.IsRequired, f.FallbackValue, f.SampleValue,
                int.TryParse(f.SampleValue, out var idx) ? idx : -1)).ToList(),
            detected);
    }

    public async Task<byte[]?> GetArtifactAsync(long id, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<byte[]?>(new CommandDefinition(
            """
            SELECT v.artifact_blob FROM label_template_versions v
            JOIN label_templates t ON t.id = v.template_id AND t.current_version = v.version
            WHERE t.id = @id
            """, new { id }, cancellationToken: ct));
    }

    private class SummaryRow
    {
        public long Id { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string TemplateFormat { get; set; } = "";
        public decimal? WidthMm { get; set; }
        public decimal? HeightMm { get; set; }
        public short? Dpi { get; set; }
        public int CurrentVersion { get; set; }
        public bool IsActive { get; set; }
        public bool IsDefault { get; set; }
        public int MappedFieldCount { get; set; }
    }

    private sealed class DetailRow : SummaryRow
    {
        public string? Description { get; set; }
        public decimal? GapMm { get; set; }
        public string? Orientation { get; set; }
        public string? LayoutType { get; set; }
        public string? MediaType { get; set; }
        public string? MediaTracking { get; set; }
        public long VersionId { get; set; }
        public string ArtifactFileName { get; set; } = "";
        public string ArtifactHash { get; set; } = "";
        public byte[] Artifact { get; set; } = [];
    }

    private sealed class FieldRow
    {
        public long Id { get; set; }
        public string PlaceholderRef { get; set; } = "";
        public string FieldLabel { get; set; } = "";
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
}
