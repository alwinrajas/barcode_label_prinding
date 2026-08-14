using System.Text;
using BarcodePrinter.Contracts.Reports;
using BarcodePrinter.Infrastructure.Services;
using Dapper;

namespace BarcodePrinter.Infrastructure.Reports;

/// <summary>
/// Reporting over print history (blueprint §24 / A-24). Every query is
/// date-bounded so the monthly partitions prune, projects explicit columns,
/// and returns bounded result sets — the client never receives an unbounded
/// history (A-25).
///
/// Aggregations are capped and ordered by volume: a report is for reading, so
/// "top N by labels" is more useful than an unbounded alphabetical dump, and it
/// keeps the payload predictable regardless of history size.
/// </summary>
public sealed class ReportQueries(IDbConnectionFactory connections)
{
    private const int MaxAggregateRows = 500;

    public async Task<ReportResult> RunAsync(ReportFilter filter, CancellationToken ct)
    {
        var type = Enum.TryParse<ReportType>(filter.Type, ignoreCase: true, out var parsed)
            ? parsed : ReportType.PrintLog;

        return type switch
        {
            ReportType.ByProduct => await AggregateAsync(filter, type,
                "Product-wise printing", ["Product", "Description"],
                "j.snap_product_code", "MAX(j.snap_description)", ct),
            ReportType.ByUser => await AggregateAsync(filter, type,
                "User-wise printing", ["User", ""],
                "COALESCE(u.username, '(deleted user)')", "NULL", ct),
            ReportType.ByPrinter => await AggregateAsync(filter, type,
                "Printer-wise printing", ["Printer", "Location"],
                "COALESCE(p.name, '(removed printer)')", "MAX(p.location)", ct),
            ReportType.ByDate => await AggregateAsync(filter, type,
                "Date-wise printing", ["Date", ""],
                "DATE_FORMAT(j.requested_at, '%Y-%m-%d')", "NULL", ct),
            ReportType.Reprints => await DetailAsync(filter, type, "Reprint history", reprintsOnly: true, ct),
            _ => await DetailAsync(filter, type, "Barcode printing log", reprintsOnly: false, ct),
        };
    }

    // ---- aggregations ----------------------------------------------------------

    private async Task<ReportResult> AggregateAsync(
        ReportFilter filter, ReportType type, string title, string[] keyColumns,
        string groupExpression, string secondaryExpression, CancellationToken ct)
    {
        var (where, parameters) = BuildWhere(filter, reprintsOnly: false);

        await using var conn = await connections.OpenAsync(ct);
        var rows = (await conn.QueryAsync<AggregateRow>(new CommandDefinition(
            $"""
            SELECT {groupExpression}                                     AS `Key`,
                   {secondaryExpression}                                 AS Secondary,
                   COUNT(*)                                              AS Jobs,
                   COALESCE(SUM(j.label_count), 0)                       AS Labels,
                   COALESCE(SUM(j.carton_total), 0)                      AS Cartons,
                   COALESCE(SUM(j.status = 'Failed'), 0)                 AS Failed,
                   COALESCE(SUM(j.is_reprint), 0)                        AS Reprints,
                   MAX(j.requested_at)                                   AS LastPrintedUtc
            FROM print_jobs j
            {JoinsFor(groupExpression, secondaryExpression)}
            {where}
            GROUP BY {groupExpression}
            ORDER BY Labels DESC, `Key`
            LIMIT @limit
            """, parameters.With("limit", MaxAggregateRows + 1), cancellationToken: ct))).ToList();

        var hasMore = rows.Count > MaxAggregateRows;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var mapped = rows.Select(r => new ReportRow(
            r.Key ?? "(none)", r.Secondary, r.Jobs, (int)r.Labels, (int)r.Cartons,
            (int)r.Failed, (int)r.Reprints,
            r.LastPrintedUtc, null, null, null, null, null, null, null)).ToList();

        // Totals come from the same filtered set, computed in SQL — never by
        // summing a truncated page in memory.
        var totals = await TotalsAsync(conn, where, parameters, ct);

        return new ReportResult(type.ToString(), title,
            [.. keyColumns, "Jobs", "Labels", "Cartons", "Failed", "Reprints", "Last printed"],
            mapped, totals, null, hasMore);
    }

    /// <summary>Only the lookup a grouping key actually reads. A report grouped
    /// by product has no reason to join users and printers over the whole
    /// filtered set.</summary>
    private static string JoinsFor(string groupExpression, string secondaryExpression)
    {
        var referenced = groupExpression + secondaryExpression;
        var joins = new StringBuilder();
        if (referenced.Contains("u."))
        {
            joins.Append("LEFT JOIN users u ON u.id = j.requested_by_user_id");
        }
        if (referenced.Contains("p."))
        {
            joins.AppendLine().Append("LEFT JOIN printers p ON p.id = j.printer_id");
        }
        return joins.ToString();
    }

    // ---- detail ------------------------------------------------------------------

    private async Task<ReportResult> DetailAsync(
        ReportFilter filter, ReportType type, string title, bool reprintsOnly, CancellationToken ct)
    {
        var pageSize = Math.Clamp(filter.PageSize <= 0 ? 100 : filter.PageSize, 1, 500);
        var (where, parameters) = BuildWhere(filter, reprintsOnly);

        var keyset = where;
        if (HistoryCursor.TryDecode(filter.Cursor, out var afterAt, out var afterId))
        {
            keyset += HistoryCursor.Predicate;
            parameters.Add("afterAt", afterAt);
            parameters.Add("afterId", afterId);
        }

        await using var conn = await connections.OpenAsync(ct);
        var rows = (await conn.QueryAsync<DetailRow>(new CommandDefinition(
            $"""
            SELECT CAST(j.id AS SIGNED)                 AS JobId,
                   j.job_no                             AS JobNo,
                   j.snap_product_code                  AS `Key`,
                   j.snap_description                   AS Secondary,
                   j.snap_batch                         AS Batch,
                   -- Scalar subqueries, not joins — see PrintQueries: a join here
                   -- costs the ordered-index-scan plan and with it the LIMIT.
                   COALESCE((SELECT u.username FROM users u
                             WHERE u.id = j.requested_by_user_id), '') AS `User`,
                   COALESCE((SELECT p.name FROM printers p
                             WHERE p.id = j.printer_id), '')           AS Printer,
                   j.status                             AS Status,
                   j.label_count                        AS Labels,
                   COALESCE(j.carton_total, 0)          AS Cartons,
                   j.is_reprint                         AS IsReprint,
                   j.requested_at                       AS RequestedAtUtc
            FROM print_jobs j
            {keyset}
            ORDER BY j.requested_at DESC, j.id DESC
            LIMIT @limit
            """, parameters.With("limit", pageSize + 1), cancellationToken: ct))).ToList();

        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var mapped = rows.Select(r => new ReportRow(
            r.Key, r.Secondary, 1, r.Labels, r.Cartons,
            r.Status == "Failed" ? 1 : 0, r.IsReprint ? 1 : 0,
            r.RequestedAtUtc, r.JobId, r.JobNo, r.Batch, r.User, r.Printer,
            r.Status, r.RequestedAtUtc)).ToList();

        var totals = await TotalsAsync(conn, where, parameters, ct);

        return new ReportResult(type.ToString(), title,
            ["Job", "When", "Product", "Description", "Batch", "User", "Printer", "Labels", "Status"],
            mapped, totals,
            hasMore ? HistoryCursor.Encode(mapped[^1].RequestedAtUtc!.Value, mapped[^1].JobId!.Value) : null,
            hasMore);
    }

    private static async Task<ReportTotals> TotalsAsync(
        MySqlConnector.MySqlConnection conn, string where, DynamicParameters parameters,
        CancellationToken ct)
    {
        // COUNT returns BIGINT and SUM returns DECIMAL in MySQL, so this maps
        // into a mutable row and converts (constructor mapping cannot).
        var row = await conn.QuerySingleAsync<TotalsRow>(new CommandDefinition(
            $"""
            SELECT COUNT(*)                              AS Jobs,
                   COALESCE(SUM(j.label_count), 0)       AS Labels,
                   COALESCE(SUM(j.carton_total), 0)      AS Cartons,
                   COALESCE(SUM(j.status = 'Failed'), 0) AS Failed,
                   COALESCE(SUM(j.is_reprint), 0)        AS Reprints
            FROM print_jobs j
            {where}
            """, parameters, cancellationToken: ct));

        return new ReportTotals(row.Jobs, row.Labels, row.Cartons, row.Failed, row.Reprints);
    }

    private sealed class TotalsRow
    {
        public int Jobs { get; set; }
        public int Labels { get; set; }
        public int Cartons { get; set; }
        public int Failed { get; set; }
        public int Reprints { get; set; }
    }

    /// <summary>Shared, parameterised predicate. Filters are never string-
    /// concatenated from user input (§19.5).</summary>
    private static (string Where, DynamicParameters Parameters) BuildWhere(
        ReportFilter filter, bool reprintsOnly)
    {
        var from = filter.FromUtc ?? DateTime.UtcNow.AddDays(-7);
        var to = filter.ToUtc ?? DateTime.UtcNow.AddDays(1);

        var sql = new StringBuilder("WHERE j.requested_at >= @from AND j.requested_at < @to");
        var parameters = new DynamicParameters();
        parameters.Add("from", from);
        parameters.Add("to", to);

        if (filter.ProductId is not null)
        {
            sql.Append(" AND j.product_id = @productId");
            parameters.Add("productId", filter.ProductId);
        }
        if (filter.UserId is not null)
        {
            sql.Append(" AND j.requested_by_user_id = @userId");
            parameters.Add("userId", filter.UserId);
        }
        if (filter.PrinterId is not null)
        {
            sql.Append(" AND j.printer_id = @printerId");
            parameters.Add("printerId", filter.PrinterId);
        }
        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            sql.Append(" AND j.status = @status");
            parameters.Add("status", filter.Status);
        }
        if (reprintsOnly)
        {
            sql.Append(" AND j.is_reprint = 1");
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            sql.Append(" AND (j.snap_product_code LIKE @like OR j.job_no LIKE @like OR j.snap_batch LIKE @like)");
            parameters.Add("like", $"%{filter.Search}%");
        }

        return (sql.ToString(), parameters);
    }

    private sealed class AggregateRow
    {
        public string? Key { get; set; }
        public string? Secondary { get; set; }
        public int Jobs { get; set; }
        public decimal Labels { get; set; }
        public decimal Cartons { get; set; }
        public decimal Failed { get; set; }
        public decimal Reprints { get; set; }
        public DateTime? LastPrintedUtc { get; set; }
    }

    private sealed class DetailRow
    {
        public long JobId { get; set; }
        public string JobNo { get; set; } = "";
        public string Key { get; set; } = "";
        public string? Secondary { get; set; }
        public string? Batch { get; set; }
        public string User { get; set; } = "";
        public string Printer { get; set; } = "";
        public string Status { get; set; } = "";
        public int Labels { get; set; }
        public int Cartons { get; set; }
        public bool IsReprint { get; set; }
        public DateTime RequestedAtUtc { get; set; }
    }
}

internal static class DynamicParametersExtensions
{
    /// <summary>Adds a parameter and returns the same instance, so a query can
    /// append its own limit without mutating the shared filter set twice.</summary>
    public static DynamicParameters With(this DynamicParameters parameters, string name, object value)
    {
        if (!parameters.ParameterNames.Contains(name))
        {
            parameters.Add(name, value);
        }
        return parameters;
    }
}
