using System.Text;
using BarcodePrinter.Contracts.Printing;
using BarcodePrinter.Contracts.Products;
using BarcodePrinter.Infrastructure.Services;
using Dapper;

namespace BarcodePrinter.Infrastructure.Printing;

public sealed class PrintQueries(IDbConnectionFactory connections)
{
    // ---- Printers -------------------------------------------------------------

    public async Task<IReadOnlyList<PrinterDto>> ListPrintersAsync(bool activeOnly, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var rows = await conn.QueryAsync<PrinterRow>(new CommandDefinition(
            $"""
            SELECT CAST(id AS SIGNED) AS Id, code AS Code, name AS Name, location AS Location,
                   connection_type AS ConnectionType, dispatch_mode AS DispatchMode,
                   host AS Host, port AS Port, windows_printer_name AS WindowsPrinterName,
                   owner_workstation AS OwnerWorkstation, dpi AS Dpi, language AS Language,
                   supports_status_query AS SupportsStatusQuery,
                   is_active AS IsActive, is_default AS IsDefault
            FROM printers {(activeOnly ? "WHERE is_active = 1" : "")}
            ORDER BY is_default DESC, name
            """, cancellationToken: ct));
        return rows.Select(Map).ToList();
    }

    public async Task<PrinterDto?> GetPrinterAsync(long id, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<PrinterRow>(new CommandDefinition(
            """
            SELECT CAST(id AS SIGNED) AS Id, code AS Code, name AS Name, location AS Location,
                   connection_type AS ConnectionType, dispatch_mode AS DispatchMode,
                   host AS Host, port AS Port, windows_printer_name AS WindowsPrinterName,
                   owner_workstation AS OwnerWorkstation, dpi AS Dpi, language AS Language,
                   supports_status_query AS SupportsStatusQuery,
                   is_active AS IsActive, is_default AS IsDefault
            FROM printers WHERE id = @id
            """, new { id }, cancellationToken: ct));
        return row is null ? null : Map(row);
    }

    private static PrinterDto Map(PrinterRow r) => new(
        r.Id, r.Code, r.Name, r.Location, r.ConnectionType, r.DispatchMode, r.Host, r.Port,
        r.WindowsPrinterName, r.OwnerWorkstation, r.Dpi, r.Language,
        r.SupportsStatusQuery, r.IsActive, r.IsDefault);

    // ---- Print history --------------------------------------------------------

    /// <summary>Keyset-paged, always date-bounded so the partitioned table
    /// prunes (§9.2). No COUNT(*) — totals are opt-in by design.</summary>
    public async Task<PagedResult<PrintJobDto>> QueryHistoryAsync(
        PrintHistoryFilter filter, CancellationToken ct)
    {
        var pageSize = Math.Clamp(filter.PageSize <= 0 ? 50 : filter.PageSize, 1, 200);
        var from = filter.FromUtc ?? DateTime.UtcNow.Date;
        var to = filter.ToUtc ?? DateTime.UtcNow.AddDays(1);

        var where = new StringBuilder("WHERE j.requested_at >= @from AND j.requested_at < @to");
        if (filter.ProductId is not null) where.Append(" AND j.product_id = @ProductId");
        if (filter.UserId is not null) where.Append(" AND j.requested_by_user_id = @UserId");
        if (filter.PrinterId is not null) where.Append(" AND j.printer_id = @PrinterId");
        if (!string.IsNullOrWhiteSpace(filter.Status)) where.Append(" AND j.status = @Status");
        if (filter.ReprintsOnly == true) where.Append(" AND j.is_reprint = 1");
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            where.Append(" AND (j.snap_product_code LIKE @like OR j.job_no LIKE @like OR j.snap_batch LIKE @like)");
        }
        var hasCursor = HistoryCursor.TryDecode(filter.Cursor, out var afterAt, out var afterId);
        if (hasCursor) where.Append(HistoryCursor.Predicate);

        await using var conn = await connections.OpenAsync(ct);
        var rows = (await conn.QueryAsync<JobRow>(new CommandDefinition(
            $"""
            SELECT CAST(j.id AS SIGNED) AS Id, j.job_no AS JobNo, j.requested_at AS RequestedAtUtc,
                   -- Scalar subqueries, NOT joins: a LEFT JOIN here lets the optimizer
                   -- pick a hash join on the tiny printers table, which forces
                   -- "Using temporary; Using filesort" over the whole date range and
                   -- throws away the LIMIT (measured: 725 ms vs 2 ms at 200k rows).
                   -- These run for the page's rows only and cannot alter the plan.
                   COALESCE((SELECT u.username FROM users u
                             WHERE u.id = j.requested_by_user_id), '')  AS RequestedBy,
                   COALESCE((SELECT p.name FROM printers p
                             WHERE p.id = j.printer_id), '')            AS PrinterName,
                   COALESCE((SELECT t.code FROM label_templates t
                             WHERE t.id = j.template_id), '')           AS TemplateCode,
                   j.template_version AS TemplateVersion,
                   j.snap_product_code AS ProductCode, j.snap_description AS Description,
                   j.snap_batch AS Batch, j.snap_production_date AS ProductionDate,
                   j.snap_expiry_date AS ExpiryDate, j.snap_quantity_text AS QuantityText,
                   j.carton_from AS CartonFrom, j.carton_to AS CartonTo,
                   j.label_count AS LabelCount, j.copies_per_label AS CopiesPerLabel,
                   j.status AS Status, j.dispatched_at AS DispatchedAtUtc,
                   j.confirmed_at AS ConfirmedAtUtc, j.labels_confirmed AS LabelsConfirmed,
                   j.error_code AS ErrorCode, j.error_message AS ErrorMessage,
                   j.is_reprint AS IsReprint, CAST(j.source_job_id AS SIGNED) AS SourceJobId,
                   j.reprint_reason AS ReprintReason
            FROM print_jobs j
            {where}
            ORDER BY j.requested_at DESC, j.id DESC
            LIMIT @limit
            """,
            new
            {
                from, to, filter.ProductId, filter.UserId, filter.PrinterId,
                filter.Status, afterAt, afterId, like = $"%{filter.Search}%", limit = pageSize + 1,
            }, cancellationToken: ct))).ToList();

        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var items = rows.Select(r => new PrintJobDto(
            r.Id, r.JobNo, r.RequestedAtUtc, r.RequestedBy, r.PrinterName,
            r.TemplateCode, r.TemplateVersion, r.ProductCode, r.Description,
            r.Batch, ToDateOnly(r.ProductionDate), ToDateOnly(r.ExpiryDate), r.QuantityText,
            r.CartonFrom, r.CartonTo, r.LabelCount, r.CopiesPerLabel,
            r.Status, r.DispatchedAtUtc, r.ConfirmedAtUtc, r.LabelsConfirmed,
            r.ErrorCode, r.ErrorMessage, r.IsReprint, r.SourceJobId, r.ReprintReason)).ToList();

        return new PagedResult<PrintJobDto>(
            items,
            hasMore ? HistoryCursor.Encode(items[^1].RequestedAtUtc, items[^1].Id) : null,
            hasMore);
    }

    /// <summary>Single job by id — a primary-key lookup. This is the print
    /// screen's status-polling path, so it must never touch the date range.</summary>
    public Task<PrintJobDto?> GetJobAsync(long id, CancellationToken ct) =>
        GetJobDirectAsync(id, ct);

    private async Task<PrintJobDto?> GetJobDirectAsync(long id, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition(
            """
            SELECT CAST(j.id AS SIGNED) AS Id, j.job_no AS JobNo, j.requested_at AS RequestedAtUtc,
                   -- Scalar subqueries, NOT joins: a LEFT JOIN here lets the optimizer
                   -- pick a hash join on the tiny printers table, which forces
                   -- "Using temporary; Using filesort" over the whole date range and
                   -- throws away the LIMIT (measured: 725 ms vs 2 ms at 200k rows).
                   -- These run for the page's rows only and cannot alter the plan.
                   COALESCE((SELECT u.username FROM users u
                             WHERE u.id = j.requested_by_user_id), '')  AS RequestedBy,
                   COALESCE((SELECT p.name FROM printers p
                             WHERE p.id = j.printer_id), '')            AS PrinterName,
                   COALESCE((SELECT t.code FROM label_templates t
                             WHERE t.id = j.template_id), '')           AS TemplateCode,
                   j.template_version AS TemplateVersion,
                   j.snap_product_code AS ProductCode, j.snap_description AS Description,
                   j.snap_batch AS Batch, j.snap_production_date AS ProductionDate,
                   j.snap_expiry_date AS ExpiryDate, j.snap_quantity_text AS QuantityText,
                   j.carton_from AS CartonFrom, j.carton_to AS CartonTo,
                   j.label_count AS LabelCount, j.copies_per_label AS CopiesPerLabel,
                   j.status AS Status, j.dispatched_at AS DispatchedAtUtc,
                   j.confirmed_at AS ConfirmedAtUtc, j.labels_confirmed AS LabelsConfirmed,
                   j.error_code AS ErrorCode, j.error_message AS ErrorMessage,
                   j.is_reprint AS IsReprint, CAST(j.source_job_id AS SIGNED) AS SourceJobId,
                   j.reprint_reason AS ReprintReason
            FROM print_jobs j
            LEFT JOIN users u ON u.id = j.requested_by_user_id
            LEFT JOIN printers p ON p.id = j.printer_id
            LEFT JOIN label_templates t ON t.id = j.template_id
            WHERE j.id = @id
            """, new { id }, cancellationToken: ct));

        return row is null ? null : new PrintJobDto(
            row.Id, row.JobNo, row.RequestedAtUtc, row.RequestedBy, row.PrinterName,
            row.TemplateCode, row.TemplateVersion, row.ProductCode, row.Description,
            row.Batch, ToDateOnly(row.ProductionDate), ToDateOnly(row.ExpiryDate), row.QuantityText,
            row.CartonFrom, row.CartonTo, row.LabelCount, row.CopiesPerLabel,
            row.Status, row.DispatchedAtUtc, row.ConfirmedAtUtc, row.LabelsConfirmed,
            row.ErrorCode, row.ErrorMessage, row.IsReprint, row.SourceJobId, row.ReprintReason);
    }

    public async Task<byte[]?> GetPayloadAsync(long jobId, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<byte[]?>(new CommandDefinition(
            "SELECT payload FROM print_job_payloads WHERE job_id = @jobId",
            new { jobId }, cancellationToken: ct));
    }

    private static DateOnly? ToDateOnly(DateTime? d) => d is null ? null : DateOnly.FromDateTime(d.Value);

    private sealed class PrinterRow
    {
        public long Id { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Location { get; set; }
        public string ConnectionType { get; set; } = "";
        public string DispatchMode { get; set; } = "";
        public string? Host { get; set; }
        public int? Port { get; set; }
        public string? WindowsPrinterName { get; set; }
        public string? OwnerWorkstation { get; set; }
        public short? Dpi { get; set; }
        public string Language { get; set; } = "";
        public bool SupportsStatusQuery { get; set; }
        public bool IsActive { get; set; }
        public bool IsDefault { get; set; }
    }

    private sealed class JobRow
    {
        public long Id { get; set; }
        public string JobNo { get; set; } = "";
        public DateTime RequestedAtUtc { get; set; }
        public string RequestedBy { get; set; } = "";
        public string PrinterName { get; set; } = "";
        public string TemplateCode { get; set; } = "";
        public int TemplateVersion { get; set; }
        public string ProductCode { get; set; } = "";
        public string Description { get; set; } = "";
        public string? Batch { get; set; }
        public DateTime? ProductionDate { get; set; }
        public DateTime? ExpiryDate { get; set; }
        public string? QuantityText { get; set; }
        public long? CartonFrom { get; set; }
        public long? CartonTo { get; set; }
        public int LabelCount { get; set; }
        public short CopiesPerLabel { get; set; }
        public string Status { get; set; } = "";
        public DateTime? DispatchedAtUtc { get; set; }
        public DateTime? ConfirmedAtUtc { get; set; }
        public int LabelsConfirmed { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public bool IsReprint { get; set; }
        public long? SourceJobId { get; set; }
        public string? ReprintReason { get; set; }
    }
}
