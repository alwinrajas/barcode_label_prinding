namespace BarcodePrinter.Contracts.Reports;

/// <summary>The report types the FRD requires. Detail rows come from the print
/// history query; the rest are aggregations.</summary>
public enum ReportType
{
    PrintLog,
    ByProduct,
    ByUser,
    ByPrinter,
    ByDate,
    Reprints,
}

public sealed record ReportFilter(
    string Type,
    DateTime? FromUtc,
    DateTime? ToUtc,
    long? ProductId,
    long? UserId,
    long? PrinterId,
    string? Status,
    string? Search,
    int PageSize,
    string? Cursor);

/// <summary>One row of any report. Aggregations fill the summary fields;
/// detail rows fill the rest. A single shape keeps the grid, the export and
/// the print path identical for every report type.</summary>
public sealed record ReportRow(
    string Key,
    string? Secondary,
    int Jobs,
    int Labels,
    int Cartons,
    int Failed,
    int Reprints,
    DateTime? LastPrintedUtc,
    // Detail-only
    long? JobId,
    string? JobNo,
    string? Batch,
    string? User,
    string? Printer,
    string? Status,
    DateTime? RequestedAtUtc);

public sealed record ReportResult(
    string Type,
    string Title,
    IReadOnlyList<string> Columns,
    IReadOnlyList<ReportRow> Rows,
    ReportTotals Totals,
    string? NextCursor,
    bool HasMore);

public sealed record ReportTotals(int Jobs, int Labels, int Cartons, int Failed, int Reprints);
