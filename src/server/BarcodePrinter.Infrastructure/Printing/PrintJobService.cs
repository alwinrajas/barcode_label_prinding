using System.Text;
using System.Text.Json;
using BarcodePrinter.Application.Abstractions;
using BarcodePrinter.Application.Printing;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Printing;
using BarcodePrinter.Domain;
using BarcodePrinter.Infrastructure.Services;
using BarcodePrinter.Infrastructure.Templates;
using BarcodePrinter.Labels;
using BarcodePrinter.Labels.Binding;
using Dapper;
using MySqlConnector;

namespace BarcodePrinter.Infrastructure.Printing;

/// <summary>Signals a newly queued job to whichever dispatcher owns the printer.</summary>
public interface IPrintJobQueue
{
    ValueTask EnqueueAsync(long jobId, CancellationToken ct);
}

/// <summary>
/// Print submission (blueprint §8.2). ONE transaction does everything expensive
/// and irreversible: snapshot the effective values, allocate carton numbers,
/// render the payload, persist job + items. A number can never be issued to a
/// job that failed to persist, and a retry re-sends identical bytes.
/// </summary>
public sealed class PrintJobService(
    IDbConnectionFactory connections,
    TemplateRenderService renderer,
    CartonStrategyResolver strategies,
    IPrintJobQueue queue,
    IAuditWriter audit,
    ISettingsProvider settings,
    ICartonSequenceAllocator sequences,
    IPrintJobStatusBroadcaster jobStatus)
{
    public async Task<PrintJobCreatedResponse> SubmitAsync(
        PrintRequest request, ActorInfo actor, CancellationToken ct)
    {
        if (request.CopiesPerLabel is < 1 or > 99)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "Copies per label must be between 1 and 99.");
        }

        await using var conn = await connections.OpenAsync(ct);

        var product = await LoadProductAsync(conn, request.ProductId, ct);
        var printer = await LoadPrinterAsync(conn, request.PrinterId, ct);
        var strategy = await strategies.ResolveAsync(ct);

        // Effective values: master defaults overridden by what the operator
        // typed on the print screen (A-9). What we snapshot is what prints.
        var batch = Coalesce(request.Batch, product.DefaultBatch);
        var productionDate = request.ProductionDate ?? product.DefaultProductionDate;
        var expiryDate = request.ExpiryDate ?? product.DefaultExpiryDate;
        var quantityText = Coalesce(request.QuantityText, product.DefaultQuantityText);

        if (expiryDate is { } exp && productionDate is { } prod && exp < prod)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "Expiry date cannot be before the production date.");
        }

        var context = new CartonNumberingContext(
            product.Id, product.Code, batch, DateOnly.FromDateTime(DateTime.UtcNow),
            request.LabelCount, request.CartonFrom, request.CartonTo);

        await using var tx = await conn.BeginTransactionAsync(ct);

        // Pessimistic, short, and inside the SAME transaction as the insert.
        var allocation = await strategy.AllocateAsync(context, tx, ct);

        var jobNo = await NextJobNumberAsync(tx, ct);
        var labelCount = (int)allocation.Total;
        var correlationId = actor.CorrelationId ?? Guid.NewGuid().ToString();

        var overrides = BuildOverrides(request, product);

        var jobId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO print_jobs
                (requested_at, job_no, requested_by_user_id, printer_id, template_id,
                 template_version, product_id,
                 snap_product_code, snap_description, snap_barcode_value, snap_uom,
                 snap_size, snap_color, snap_batch, snap_production_date, snap_expiry_date,
                 snap_quantity_text, snap_image_hash, snap_timestamp_text, overrides_json,
                 carton_from, carton_to, carton_total, copies_per_label, label_count,
                 status, workstation, correlation_id, concurrency_stamp)
            VALUES
                (UTC_TIMESTAMP(3), @jobNo, @UserId, @PrinterId, @TemplateId,
                 @templateVersion, @ProductId,
                 @Code, @Description, @BarcodeValue, @Uom,
                 @Size, @Color, @batch, @productionDate, @expiryDate,
                 @quantityText, @ImageHash, @timestampText, @overrides,
                 @cartonFrom, @cartonTo, @cartonTotal, @CopiesPerLabel, @labelCount,
                 'Queued', @workstation, @correlationId, UUID());
            SELECT LAST_INSERT_ID();
            """,
            new
            {
                jobNo, actor.UserId, request.PrinterId, request.TemplateId,
                templateVersion = await CurrentTemplateVersionAsync(conn, request.TemplateId, tx, ct),
                request.ProductId,
                product.Code, product.Description, product.BarcodeValue, product.Uom,
                product.Size, product.Color, batch, productionDate, expiryDate,
                quantityText, product.ImageHash,
                timestampText = await FormatTimestampAsync(ct),
                overrides,
                cartonFrom = allocation.From, cartonTo = allocation.To,
                cartonTotal = allocation.Total,
                request.CopiesPerLabel, labelCount,
                workstation = request.Workstation ?? actor.Workstation,
                correlationId,
            }, transaction: tx, cancellationToken: ct));

        // Render every label now (§8.2): a bad template or a missing required
        // field fails here, in front of the user — never mid-run at the printer.
        // An office printer cannot interpret ZPL, so a job aimed at one is
        // rendered to pictures instead. Everything downstream — payload row,
        // queue, retry, byte-replay reprint — is identical; only the bytes and
        // the transport differ (§7.2).
        var payload = printer.ConnectionType == "WindowsGraphics"
            ? await renderer.RenderRasterJobAsync(
                request.TemplateId, product, batch, productionDate, expiryDate, quantityText,
                allocation, strategy, jobNo, actor.Username, printer.Name,
                request.CopiesPerLabel, printer.Dpi, conn, tx, ct)
            : await renderer.RenderJobAsync(
                request.TemplateId, product, batch, productionDate, expiryDate, quantityText,
                allocation, strategy, jobNo, actor.Username, printer.Name,
                request.CopiesPerLabel, isReprint: false, conn, tx, ct);

        await StorePayloadAsync(conn, tx, jobId, payload, ct);
        await InsertItemsAsync(conn, tx, jobId, allocation, product.BarcodeValue, ct);

        await tx.CommitAsync(ct);

        await audit.WriteAsync(new AuditEntry("LabelsPrinted", "Info",
            actor.UserId, actor.Username, "PrintJob", jobNo,
            AfterJson: JsonSerializer.Serialize(new
            {
                product.Code, batch, cartons = $"{allocation.From}-{allocation.To}",
                labels = labelCount, printer = printer.Name,
            }),
            Workstation: request.Workstation, CorrelationId: correlationId), ct);

        await queue.EnqueueAsync(jobId, ct);

        return new PrintJobCreatedResponse(jobId, jobNo, allocation.From, allocation.To, labelCount);
    }

    /// <summary>
    /// Reprint REPLAYS the stored bytes (§14.2): re-rendering would pick up
    /// today's template, settings and product data and silently produce a
    /// different label. Carton numbers are reused — a reprint replaces a
    /// damaged label, it is not a new carton.
    /// </summary>
    public async Task<PrintJobCreatedResponse> ReprintAsync(
        ReprintRequest request, ActorInfo actor, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);

        var source = await conn.QuerySingleOrDefaultAsync<SourceJobRow>(new CommandDefinition(
            """
            SELECT CAST(j.id AS SIGNED) AS Id, j.requested_at AS RequestedAt, j.job_no AS JobNo,
                   CAST(j.printer_id AS SIGNED) AS PrinterId, CAST(j.template_id AS SIGNED) AS TemplateId,
                   j.template_version AS TemplateVersion, CAST(j.product_id AS SIGNED) AS ProductId,
                   j.snap_product_code AS SnapProductCode, j.snap_description AS SnapDescription,
                   j.snap_barcode_value AS SnapBarcodeValue, j.snap_uom AS SnapUom,
                   j.snap_size AS SnapSize, j.snap_color AS SnapColor, j.snap_batch AS SnapBatch,
                   j.snap_production_date AS SnapProductionDate, j.snap_expiry_date AS SnapExpiryDate,
                   j.snap_quantity_text AS SnapQuantityText, j.snap_image_hash AS SnapImageHash,
                   j.snap_timestamp_text AS SnapTimestampText,
                   j.carton_from AS CartonFrom, j.carton_to AS CartonTo, j.carton_total AS CartonTotal,
                   j.copies_per_label AS CopiesPerLabel, j.label_count AS LabelCount
            FROM print_jobs j WHERE j.id = @SourceJobId
            """, new { request.SourceJobId }, cancellationToken: ct))
            ?? throw new NotFoundException("PrintJob", request.SourceJobId);

        var payload = await conn.QuerySingleOrDefaultAsync<PayloadRow>(new CommandDefinition(
            "SELECT format AS Format, payload AS Data, payload_hash AS Hash FROM print_job_payloads WHERE job_id = @SourceJobId",
            new { request.SourceJobId }, cancellationToken: ct))
            ?? throw new DomainException("REPRINT_PAYLOAD_MISSING",
                "The original print data is no longer available, so this job cannot be reprinted exactly.");

        var reasonRequired = string.Equals(
            await settings.GetAsync("Print:ReprintReasonRequired", ct), "true", StringComparison.OrdinalIgnoreCase);
        if (reasonRequired && string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Enter a reason for the reprint.");
        }

        await using var tx = await conn.BeginTransactionAsync(ct);
        var jobNo = await NextJobNumberAsync(tx, ct);

        var jobId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO print_jobs
                (requested_at, job_no, requested_by_user_id, printer_id, template_id,
                 template_version, product_id,
                 snap_product_code, snap_description, snap_barcode_value, snap_uom,
                 snap_size, snap_color, snap_batch, snap_production_date, snap_expiry_date,
                 snap_quantity_text, snap_image_hash, snap_timestamp_text,
                 carton_from, carton_to, carton_total, copies_per_label, label_count,
                 status, is_reprint, source_job_id, reprint_reason, authorized_by_user_id,
                 workstation, correlation_id, concurrency_stamp)
            VALUES
                (UTC_TIMESTAMP(3), @jobNo, @UserId, @PrinterId, @TemplateId,
                 @TemplateVersion, @ProductId,
                 @SnapProductCode, @SnapDescription, @SnapBarcodeValue, @SnapUom,
                 @SnapSize, @SnapColor, @SnapBatch, @SnapProductionDate, @SnapExpiryDate,
                 @SnapQuantityText, @SnapImageHash, @SnapTimestampText,
                 @CartonFrom, @CartonTo, @CartonTotal, @CopiesPerLabel, @LabelCount,
                 'Queued', 1, @SourceId, @reason, @UserId,
                 @workstation, @correlationId, UUID());
            SELECT LAST_INSERT_ID();
            """,
            new
            {
                jobNo, actor.UserId, source.PrinterId, source.TemplateId, source.TemplateVersion,
                source.ProductId, source.SnapProductCode, source.SnapDescription,
                source.SnapBarcodeValue, source.SnapUom, source.SnapSize, source.SnapColor,
                source.SnapBatch, source.SnapProductionDate, source.SnapExpiryDate,
                source.SnapQuantityText, source.SnapImageHash, source.SnapTimestampText,
                source.CartonFrom, source.CartonTo, source.CartonTotal,
                source.CopiesPerLabel, source.LabelCount,
                SourceId = source.Id, reason = request.Reason,
                workstation = request.Workstation ?? actor.Workstation,
                correlationId = actor.CorrelationId ?? Guid.NewGuid().ToString(),
            }, transaction: tx, cancellationToken: ct));

        // Byte-identical replay.
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO print_job_payloads
                (job_id, requested_at, format, compressed, payload, byte_count, payload_hash, created_at)
            VALUES (@jobId, UTC_TIMESTAMP(3), @Format, 0, @Data, @length, @Hash, UTC_TIMESTAMP(3))
            """,
            new { jobId, payload.Format, payload.Data, length = payload.Data.Length, payload.Hash },
            transaction: tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO print_job_items
                (requested_at, job_id, sequence_no, carton_no, carton_total, barcode_value, status)
            SELECT UTC_TIMESTAMP(3), @jobId, sequence_no, carton_no, carton_total, barcode_value, 'Pending'
            FROM print_job_items WHERE job_id = @SourceId
            """, new { jobId, SourceId = source.Id }, transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);

        await audit.WriteAsync(new AuditEntry("LabelsReprinted", "Security",
            actor.UserId, actor.Username, "PrintJob", jobNo,
            AfterJson: JsonSerializer.Serialize(new
            {
                sourceJob = source.JobNo, request.Reason,
                cartons = $"{source.CartonFrom}-{source.CartonTo}",
            }),
            CorrelationId: actor.CorrelationId), ct);

        await queue.EnqueueAsync(jobId, ct);

        return new PrintJobCreatedResponse(jobId, jobNo,
            source.CartonFrom ?? 0, source.CartonTo ?? 0, source.LabelCount);
    }

    public async Task CancelAsync(long jobId, ActorInfo actor, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var changed = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE print_jobs SET status = 'Cancelled', completed_at = UTC_TIMESTAMP(3)
            WHERE id = @jobId AND status = 'Queued'
            """, new { jobId }, cancellationToken: ct));

        if (changed == 0)
        {
            throw new DomainException("PRINT_CANCEL_TOO_LATE",
                "This job has already started printing and can no longer be cancelled.");
        }
        await audit.WriteAsync(new AuditEntry("PrintJobCancelled", "Info",
            actor.UserId, actor.Username, "PrintJob", jobId.ToString(),
            CorrelationId: actor.CorrelationId), ct);
        await jobStatus.JobChangedAsync(jobId, ct);
    }

    // ---- helpers ---------------------------------------------------------------

    private static string? Coalesce(string? primary, string? fallback) =>
        string.IsNullOrWhiteSpace(primary) ? fallback : primary.Trim();

    /// <summary>Records which fields the operator overrode, for audit (A-10).</summary>
    private static string? BuildOverrides(PrintRequest request, ProductSnapshot product)
    {
        var changes = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(request.Batch) && request.Batch != product.DefaultBatch)
        {
            changes["batch"] = new { from = product.DefaultBatch, to = request.Batch };
        }
        if (request.ProductionDate is { } p && p != product.DefaultProductionDate)
        {
            changes["productionDate"] = new { from = product.DefaultProductionDate, to = p };
        }
        if (request.ExpiryDate is { } e && e != product.DefaultExpiryDate)
        {
            changes["expiryDate"] = new { from = product.DefaultExpiryDate, to = e };
        }
        if (!string.IsNullOrWhiteSpace(request.QuantityText) &&
            request.QuantityText != product.DefaultQuantityText)
        {
            changes["quantityText"] = new { from = product.DefaultQuantityText, to = request.QuantityText };
        }
        return changes.Count == 0 ? null : JsonSerializer.Serialize(changes);
    }

    private async Task<string> FormatTimestampAsync(CancellationToken ct)
    {
        var format = await settings.GetAsync("Label:TimestampFormat", ct) ?? "dd/MM/yyyy HH:mm";
        try
        {
            return DateTime.Now.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return DateTime.Now.ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }
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
            is { } product && product.IsActive
            ? product
            : throw new DomainException(ErrorCodes.ValidationFailed,
                "This product is not available for printing. Check that it exists and is active.");

    private static async Task<PrinterRow> LoadPrinterAsync(
        MySqlConnection conn, long printerId, CancellationToken ct) =>
        await conn.QuerySingleOrDefaultAsync<PrinterRow>(new CommandDefinition(
            """
            SELECT CAST(id AS SIGNED) AS Id, name AS Name, dispatch_mode AS DispatchMode,
                   connection_type AS ConnectionType, dpi AS Dpi, is_active AS IsActive
            FROM printers WHERE id = @printerId
            """, new { printerId }, cancellationToken: ct))
            is { } printer && printer.IsActive
            ? printer
            : throw new DomainException(ErrorCodes.ValidationFailed,
                "This printer is not available. Choose another printer.");

    private static async Task<int> CurrentTemplateVersionAsync(
        MySqlConnection conn, long templateId, System.Data.Common.DbTransaction tx, CancellationToken ct) =>
        await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT current_version FROM label_templates WHERE id = @templateId AND is_active = 1",
            new { templateId }, transaction: tx, cancellationToken: ct))
        ?? throw new DomainException(ErrorCodes.ValidationFailed,
            "This label template is not available. Choose an active template.");

    /// <summary>PJ-yyMMdd-NNNNNN, unique per day. Allocated through the same
    /// deadlock-safe path as carton numbers (see CartonSequenceAllocator).</summary>
    private async Task<string> NextJobNumberAsync(
        System.Data.Common.DbTransaction tx, CancellationToken ct)
    {
        var today = DateTime.UtcNow.ToString("yyMMdd");
        var sequence = await sequences.ReserveAsync($"jobno:{today}", "JobNumber", 1, tx, ct);
        return $"PJ-{today}-{sequence:000000}";
    }

    private static Task StorePayloadAsync(
        MySqlConnection conn, System.Data.Common.DbTransaction tx, long jobId,
        RenderedPayload payload, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO print_job_payloads
                (job_id, requested_at, format, compressed, payload, byte_count, payload_hash, created_at)
            VALUES (@jobId, UTC_TIMESTAMP(3), @format, 0, @data, @length, @hash, UTC_TIMESTAMP(3))
            """,
            new
            {
                jobId, format = payload.Format, data = payload.Data,
                length = payload.Data.Length, hash = payload.Hash,
            }, transaction: tx, cancellationToken: ct));

    /// <summary>One row per carton, inserted as a single multi-row statement —
    /// a 500-carton job costs one round trip, not 500.</summary>
    private static async Task InsertItemsAsync(
        MySqlConnection conn, System.Data.Common.DbTransaction tx, long jobId,
        CartonAllocation allocation, string barcodeValue, CancellationToken ct)
    {
        var numbers = allocation.Numbers.ToList();
        foreach (var chunk in numbers.Chunk(1_000))
        {
            var sb = new StringBuilder(
                "INSERT INTO print_job_items (requested_at, job_id, sequence_no, carton_no, carton_total, barcode_value, status) VALUES ");
            var parameters = new DynamicParameters();
            parameters.Add("jobId", jobId);
            parameters.Add("total", allocation.Total);
            parameters.Add("barcode", barcodeValue);

            for (var i = 0; i < chunk.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append($"(UTC_TIMESTAMP(3), @jobId, @s{i}, @c{i}, @total, @barcode, 'Pending')");
                parameters.Add($"s{i}", numbers.IndexOf(chunk[i]) + 1);
                parameters.Add($"c{i}", chunk[i]);
            }
            await conn.ExecuteAsync(new CommandDefinition(
                sb.ToString(), parameters, transaction: tx, cancellationToken: ct));
        }
    }

    private sealed class PrinterRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string DispatchMode { get; set; } = "";
        public string ConnectionType { get; set; } = "";
        public short? Dpi { get; set; }
        public bool IsActive { get; set; }
    }
    private sealed class PayloadRow
    {
        public string Format { get; set; } = "";
        public byte[] Data { get; set; } = [];
        public string Hash { get; set; } = "";
    }

    // Mutable class, not a positional record: MySQL column types (INT vs
    // BIGINT, DATE vs DATETIME) do not line up with constructor parameters,
    // and Dapper's property mapping converts where constructor mapping will not.
    private sealed class SourceJobRow
    {
        public long Id { get; set; }
        public DateTime RequestedAt { get; set; }
        public string JobNo { get; set; } = "";
        public long PrinterId { get; set; }
        public long TemplateId { get; set; }
        public int TemplateVersion { get; set; }
        public long ProductId { get; set; }
        public string SnapProductCode { get; set; } = "";
        public string SnapDescription { get; set; } = "";
        public string SnapBarcodeValue { get; set; } = "";
        public string? SnapUom { get; set; }
        public string? SnapSize { get; set; }
        public string? SnapColor { get; set; }
        public string? SnapBatch { get; set; }
        public DateTime? SnapProductionDate { get; set; }
        public DateTime? SnapExpiryDate { get; set; }
        public string? SnapQuantityText { get; set; }
        public string? SnapImageHash { get; set; }
        public string? SnapTimestampText { get; set; }
        public long? CartonFrom { get; set; }
        public long? CartonTo { get; set; }
        public long? CartonTotal { get; set; }
        public short CopiesPerLabel { get; set; }
        public int LabelCount { get; set; }
    }
}

public sealed class ProductSnapshot
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public string BarcodeValue { get; set; } = "";
    public string? Uom { get; set; }
    public string? Size { get; set; }
    public string? Color { get; set; }
    public string? DefaultBatch { get; set; }
    public DateOnly? DefaultProductionDate { get; set; }
    public DateOnly? DefaultExpiryDate { get; set; }
    public string? DefaultQuantityText { get; set; }
    public string? ImageHash { get; set; }
    public bool IsActive { get; set; }
}

public sealed record RenderedPayload(string Format, byte[] Data, string Hash);
