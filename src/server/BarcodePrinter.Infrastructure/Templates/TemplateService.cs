using System.Security.Cryptography;
using System.Text;
using BarcodePrinter.Application.Abstractions;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Templates;
using BarcodePrinter.Domain;
using BarcodePrinter.Labels;
using BarcodePrinter.Labels.Binding;
using BarcodePrinter.Labels.Zpl;
using BarcodePrinter.Infrastructure.Services;
using Dapper;

namespace BarcodePrinter.Infrastructure.Templates;

/// <summary>Resolves the adapter for a template's format (B-4). The unknown
/// format (C-2) is a registry entry, not a redesign.</summary>
public sealed class TemplateAdapterRegistry(IEnumerable<ITemplateAdapter> adapters)
{
    private readonly Dictionary<string, ITemplateAdapter> _byFormat =
        adapters.ToDictionary(a => a.Format, StringComparer.OrdinalIgnoreCase);

    public ITemplateAdapter Resolve(string format) =>
        _byFormat.TryGetValue(format, out var adapter)
            ? adapter
            : throw new DomainException("TEMPLATE_FORMAT_UNSUPPORTED",
                $"No adapter is registered for template format '{format}'. " +
                $"Supported: {string.Join(", ", _byFormat.Keys)}.");
}

/// <summary>
/// Template registration (blueprint §4.4). v1 is CONFIGURATION only — register,
/// version, activate, set default, map fields, preview. No designer, no raw
/// JSON editing (A-16); the client's file owns the layout.
/// </summary>
public sealed class TemplateService(
    IDbConnectionFactory connections,
    TemplateAdapterRegistry adapters,
    IAuditWriter audit)
{
    public async Task<long> RegisterAsync(
        RegisterTemplateRequest request, string artifactFileName, byte[] artifact,
        ActorInfo actor, CancellationToken ct)
    {
        var adapter = adapters.Resolve(request.TemplateFormat);
        var text = Encoding.UTF8.GetString(artifact);

        // Fail fast on an unreadable artifact rather than at print time.
        var detected = adapter.Inspect(text);
        if (detected.Count == 0)
        {
            throw new DomainException("TEMPLATE_NO_FIELDS",
                "No printable fields were found in this file. Check that it is a "
                + "label file captured from the printer (see the template upload help).");
        }

        await using var conn = await connections.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var existingId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM label_templates WHERE code = @Code", new { request.Code },
            transaction: tx, cancellationToken: ct));

        long templateId;
        int version;
        if (existingId is { } id)
        {
            // Re-uploading an existing code creates a NEW immutable version —
            // history keeps pointing at the version it printed with (§4.3).
            templateId = id;
            version = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COALESCE(MAX(version), 0) + 1 FROM label_template_versions WHERE template_id = @templateId",
                new { templateId }, transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE label_templates
                SET name = @Name, description = @Description, width_mm = @WidthMm,
                    height_mm = @HeightMm, dpi = @Dpi, gap_mm = @GapMm,
                    orientation = @Orientation, layout_type = @LayoutType,
                    media_type = @MediaType, media_tracking = @MediaTracking,
                    current_version = @version, updated_at = UTC_TIMESTAMP(3), updated_by = @UserId
                WHERE id = @templateId
                """,
                new
                {
                    templateId, version, actor.UserId,
                    request.Name, request.Description, request.WidthMm, request.HeightMm,
                    request.Dpi, request.GapMm, request.Orientation, request.LayoutType,
                    request.MediaType, request.MediaTracking,
                }, transaction: tx, cancellationToken: ct));
        }
        else
        {
            version = 1;
            templateId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO label_templates
                    (code, name, description, template_format, width_mm, height_mm, dpi, gap_mm,
                     orientation, layout_type, media_type, media_tracking,
                     current_version, is_active, is_default, created_at, created_by)
                VALUES
                    (@Code, @Name, @Description, @TemplateFormat, @WidthMm, @HeightMm, @Dpi, @GapMm,
                     @Orientation, @LayoutType, @MediaType, @MediaTracking,
                     1, 0, 0, UTC_TIMESTAMP(3), @UserId);
                SELECT LAST_INSERT_ID();
                """,
                new
                {
                    request.Code, request.Name, request.Description, request.TemplateFormat,
                    request.WidthMm, request.HeightMm, request.Dpi, request.GapMm,
                    request.Orientation, request.LayoutType, request.MediaType,
                    request.MediaTracking, actor.UserId,
                }, transaction: tx, cancellationToken: ct));
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(artifact));
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO label_template_versions
                (template_id, version, artifact_blob, artifact_hash, artifact_filename, created_at, created_by)
            VALUES (@templateId, @version, @artifact, @hash, @artifactFileName, UTC_TIMESTAMP(3), @UserId)
            """,
            new { templateId, version, artifact, hash, artifactFileName, actor.UserId },
            transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);

        await audit.WriteAsync(new AuditEntry("LabelTemplateRegistered", "Info",
            actor.UserId, actor.Username, "LabelTemplate", request.Code,
            AfterJson: $"{{\"version\":{version},\"hash\":\"{hash}\",\"fields\":{detected.Count}}}",
            CorrelationId: actor.CorrelationId), ct);

        return templateId;
    }

    public async Task SaveFieldMappingAsync(
        long templateId, SaveFieldMappingRequest request, ActorInfo actor, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);

        var versionId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            """
            SELECT CAST(v.id AS SIGNED) FROM label_template_versions v
            JOIN label_templates t ON t.id = v.template_id AND t.current_version = v.version
            WHERE t.id = @templateId
            """, new { templateId }, cancellationToken: ct))
            ?? throw new NotFoundException("LabelTemplate", templateId);

        // Validate every mapping against the closed vocabulary BEFORE writing —
        // an invalid mapping must never reach the printer (§5.2).
        foreach (var field in request.Fields)
        {
            if (!Enum.TryParse<FieldDataKind>(field.DataKind, ignoreCase: true, out var kind))
            {
                throw new DomainException(ErrorCodes.ValidationFailed,
                    $"Unknown field kind '{field.DataKind}'.");
            }
            if (!TokenVocabulary.IsAllowedForKind(field.DataKey, kind))
            {
                throw new DomainException(ErrorCodes.ValidationFailed,
                    kind == FieldDataKind.QrCode
                        ? "QR fields may only carry the static feedback URL "
                          + $"('{TokenVocabulary.FeedbackUrlKey}') — confirmed requirement."
                        : $"'{field.DataKey}' is not a valid data key for a {kind} field.");
            }
        }

        var duplicates = request.Fields.GroupBy(f => f.PlaceholderRef).Where(g => g.Count() > 1).ToList();
        if (duplicates.Count > 0)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                $"Placeholder '{duplicates[0].Key}' is mapped more than once.");
        }

        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM label_template_fields WHERE template_version_id = @versionId",
            new { versionId }, transaction: tx, cancellationToken: ct));

        foreach (var (field, order) in request.Fields.Select((f, i) => (f, i)))
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO label_template_fields
                    (template_version_id, placeholder_ref, field_label, data_key, data_kind,
                     format_string, transform, max_length, overflow, is_required,
                     fallback_value, sample_value, sort_order)
                VALUES
                    (@versionId, @PlaceholderRef, @FieldLabel, @DataKey, @DataKind,
                     @FormatString, @Transform, @MaxLength, @Overflow, @IsRequired,
                     @FallbackValue, @CommandIndexAsSample, @order)
                """,
                new
                {
                    versionId, order,
                    field.PlaceholderRef, field.FieldLabel, field.DataKey, field.DataKind,
                    field.FormatString, field.Transform, field.MaxLength, field.Overflow,
                    field.IsRequired, field.FallbackValue,
                    // sample_value doubles as the artifact anchor: the ^FD index
                    // this placeholder replaces, so Prepare can be re-run.
                    CommandIndexAsSample = field.CommandIndex.ToString(),
                }, transaction: tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);

        await audit.WriteAsync(new AuditEntry("LabelTemplateFieldsMapped", "Info",
            actor.UserId, actor.Username, "LabelTemplate", templateId.ToString(),
            AfterJson: $"{{\"fields\":{request.Fields.Count}}}",
            CorrelationId: actor.CorrelationId), ct);
    }

    public async Task SetActiveAsync(long templateId, bool active, ActorInfo actor, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);

        if (active)
        {
            var mapped = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                SELECT COUNT(*) FROM label_template_fields f
                JOIN label_template_versions v ON v.id = f.template_version_id
                JOIN label_templates t ON t.id = v.template_id AND t.current_version = v.version
                WHERE t.id = @templateId
                """, new { templateId }, cancellationToken: ct));
            if (mapped == 0)
            {
                throw new DomainException("TEMPLATE_NOT_MAPPED",
                    "Map at least one field before activating this template.");
            }
        }

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE label_templates SET is_active = @active, updated_at = UTC_TIMESTAMP(3), updated_by = @UserId WHERE id = @templateId",
            new { templateId, active, actor.UserId }, cancellationToken: ct));

        await audit.WriteAsync(new AuditEntry(
            active ? "LabelTemplateActivated" : "LabelTemplateDeactivated", "Info",
            actor.UserId, actor.Username, "LabelTemplate", templateId.ToString(),
            CorrelationId: actor.CorrelationId), ct);
    }

    public async Task SetDefaultAsync(long templateId, ActorInfo actor, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE label_templates SET is_default = (id = @templateId)",
            new { templateId }, transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);

        await audit.WriteAsync(new AuditEntry("LabelTemplateDefaultChanged", "Info",
            actor.UserId, actor.Username, "LabelTemplate", templateId.ToString(),
            CorrelationId: actor.CorrelationId), ct);
    }

    /// <summary>Renders the current version with sample data — the preview and
    /// test-print path (§4.4). Returns ZPL the admin can inspect or send.</summary>
    public async Task<string> RenderSampleAsync(long templateId, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);

        var version = await conn.QuerySingleOrDefaultAsync<VersionRow>(new CommandDefinition(
            """
            SELECT CAST(v.id AS SIGNED) AS Id, t.template_format AS Format,
                   v.artifact_blob AS Artifact, t.code AS Code
            FROM label_template_versions v
            JOIN label_templates t ON t.id = v.template_id AND t.current_version = v.version
            WHERE t.id = @templateId
            """, new { templateId }, cancellationToken: ct))
            ?? throw new NotFoundException("LabelTemplate", templateId);

        var fields = (await conn.QueryAsync<FieldRow>(new CommandDefinition(
            """
            SELECT placeholder_ref AS PlaceholderRef, data_key AS DataKey, data_kind AS DataKind,
                   format_string AS FormatString, transform AS Transform, max_length AS MaxLength,
                   overflow AS Overflow, is_required AS IsRequired,
                   fallback_value AS FallbackValue, sample_value AS SampleValue
            FROM label_template_fields WHERE template_version_id = @Id ORDER BY sort_order
            """, new { version.Id }, cancellationToken: ct))).ToList();

        if (fields.Count == 0)
        {
            throw new DomainException("TEMPLATE_NOT_MAPPED",
                "Map the template's fields before previewing it.");
        }

        var adapter = adapters.Resolve(version.Format);
        var artifactText = Encoding.UTF8.GetString(version.Artifact);

        var placeholders = fields
            .Where(f => int.TryParse(f.SampleValue, out _))
            .ToDictionary(f => int.Parse(f.SampleValue!), f => f.PlaceholderRef);

        var mappings = fields.Select(f => new FieldMapping(
            f.PlaceholderRef, f.DataKey,
            Enum.Parse<FieldDataKind>(f.DataKind, ignoreCase: true),
            f.FormatString,
            Enum.Parse<FieldTransform>(f.Transform, ignoreCase: true),
            f.MaxLength,
            Enum.Parse<OverflowBehaviour>(f.Overflow, ignoreCase: true),
            f.IsRequired, f.FallbackValue)).ToList();

        var prepared = adapter.Prepare(artifactText, $"R:{version.Code}.ZPL", placeholders);
        var bound = new FieldBinder().Bind(mappings, SamplePrintContext());

        return prepared.DefinePayload + "\n" +
               adapter.RenderRecall(new RenderRequest(prepared, mappings, bound));
    }

    /// <summary>Representative data drawn from the physical label samples, so a
    /// preview looks like a real label rather than lorem ipsum.</summary>
    private static PrintContext SamplePrintContext() => new(
        new ProductValues("5GCAPM2N", "5G M2 CAP", "5GCAPM2N", "PCS", "M2", "NATURAL", null),
        new EffectiveValues("CONE", new DateOnly(2026, 7, 21), new DateOnly(2027, 7, 21), "750[D]"),
        new CartonValues(1, 10, 1, 10, "1"),
        new JobValues("PREVIEW", "preview", "Preview", false),
        new SettingsValues("https://forms.gle/EXAMPLE", "", "dd/MM/yyyy", "dd/MM/yyyy HH:mm"),
        DateTime.Now);

    private sealed record VersionRow(long Id, string Format, byte[] Artifact, string Code);

    private sealed class FieldRow
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
}

