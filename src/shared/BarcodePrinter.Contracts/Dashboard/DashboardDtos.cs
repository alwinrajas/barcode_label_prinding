namespace BarcodePrinter.Contracts.Dashboard;

/// <summary>Everything the home screen shows, in ONE round trip — a dashboard
/// that fires six requests is the classic N+1 of landing pages.</summary>
public sealed record DashboardDto(
    DashboardKpis Kpis,
    IReadOnlyList<RecentJobDto> RecentJobs,
    IReadOnlyList<PrinterHealthDto> Printers,
    IReadOnlyList<DashboardAlertDto> Alerts,
    IReadOnlyList<DailyPointDto> LastSevenDays);

public sealed record DashboardKpis(
    int LabelsToday,
    int JobsToday,
    int FailedToday,
    int ReprintsToday,
    int LabelsYesterday,
    int ActiveProducts,
    int ActiveUsersToday);

public sealed record RecentJobDto(
    long Id, string JobNo, string ProductCode, string Description,
    int LabelCount, string PrinterName, string RequestedBy,
    string Status, DateTime RequestedAtUtc, bool IsReprint);

/// <summary>Printer health is derived from recent job outcomes rather than by
/// polling every device — polling on a dashboard load would stall the UI and
/// hammer the printers (§7.4).</summary>
public sealed record PrinterHealthDto(
    long Id, string Name, string? Location, string DispatchMode,
    bool IsActive, int JobsToday, int FailedToday,
    DateTime? LastJobUtc, string? LastError);

public sealed record DashboardAlertDto(string Severity, string Title, string Detail, string? NavigateTo);

public sealed record DailyPointDto(DateOnly Date, int Labels, int Failed);
