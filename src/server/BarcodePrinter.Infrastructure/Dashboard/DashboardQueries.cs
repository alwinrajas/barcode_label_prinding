using BarcodePrinter.Contracts.Dashboard;
using BarcodePrinter.Infrastructure.Services;
using Dapper;

namespace BarcodePrinter.Infrastructure.Dashboard;

/// <summary>
/// Home screen data (FRD §1). One multi-statement round trip, every query
/// date-bounded so the partitioned print tables prune, and every result set
/// hard-capped — a landing page must stay fast no matter how large history
/// grows (A-25).
///
/// Alerts are DERIVED from existing tables (F-4 in the readiness review): there
/// is no notifications table to maintain, drift or clean up.
/// </summary>
public sealed class DashboardQueries(IDbConnectionFactory connections, BackupStatusReader backups)
{
    private const int RecentJobCount = 8;

    public async Task<DashboardDto> GetAsync(CancellationToken ct)
    {
        var todayUtc = DateTime.UtcNow.Date;
        var tomorrowUtc = todayUtc.AddDays(1);
        var yesterdayUtc = todayUtc.AddDays(-1);
        var weekStartUtc = todayUtc.AddDays(-6);

        await using var conn = await connections.OpenAsync(ct);

        // One command, several result sets: the dashboard costs a single
        // network round trip rather than six.
        await using var multi = await conn.QueryMultipleAsync(new CommandDefinition(
            """
            SELECT
                COALESCE(SUM(CASE WHEN j.requested_at >= @today THEN j.label_count END), 0)      AS LabelsToday,
                COALESCE(SUM(j.requested_at >= @today), 0)                                       AS JobsToday,
                COALESCE(SUM(j.requested_at >= @today AND j.status = 'Failed'), 0)               AS FailedToday,
                COALESCE(SUM(j.requested_at >= @today AND j.is_reprint = 1), 0)                  AS ReprintsToday,
                COALESCE(SUM(CASE WHEN j.requested_at < @today THEN j.label_count END), 0)       AS LabelsYesterday,
                COUNT(DISTINCT CASE WHEN j.requested_at >= @today THEN j.requested_by_user_id END) AS ActiveUsersToday
            FROM print_jobs j
            WHERE j.requested_at >= @yesterday AND j.requested_at < @tomorrow;

            SELECT COUNT(*) FROM products WHERE is_active = 1;

            SELECT CAST(j.id AS SIGNED) AS Id, j.job_no AS JobNo,
                   j.snap_product_code AS ProductCode, j.snap_description AS Description,
                   j.label_count AS LabelCount,
                   -- Scalar subqueries, not joins: joining here lets the optimizer
                   -- hash-join the small printers table, which costs the ordered
                   -- index scan and makes the LIMIT worthless (§11.4).
                   COALESCE((SELECT p.name FROM printers p
                             WHERE p.id = j.printer_id), '')            AS PrinterName,
                   COALESCE((SELECT u.username FROM users u
                             WHERE u.id = j.requested_by_user_id), '')  AS RequestedBy,
                   j.status AS Status,
                   j.requested_at AS RequestedAtUtc, j.is_reprint AS IsReprint
            FROM print_jobs j
            -- Bounded at BOTH ends: an open upper bound reads every future
            -- partition and defeats pruning.
            WHERE j.requested_at >= @weekStart AND j.requested_at < @tomorrow
            ORDER BY j.requested_at DESC, j.id DESC
            LIMIT @recentCount;

            SELECT CAST(p.id AS SIGNED) AS Id, p.name AS Name, p.location AS Location,
                   p.dispatch_mode AS DispatchMode, p.is_active AS IsActive,
                   COALESCE(SUM(j.requested_at >= @today), 0)                        AS JobsToday,
                   COALESCE(SUM(j.requested_at >= @today AND j.status = 'Failed'), 0) AS FailedToday,
                   MAX(j.requested_at)                                                AS LastJobUtc,
                   SUBSTRING_INDEX(GROUP_CONCAT(
                       CASE WHEN j.status = 'Failed' THEN j.error_message END
                       ORDER BY j.requested_at DESC SEPARATOR '||'), '||', 1)         AS LastError
            FROM printers p
            LEFT JOIN print_jobs j
                   ON j.printer_id = p.id AND j.requested_at >= @weekStart AND j.requested_at < @tomorrow
            WHERE p.is_active = 1
            GROUP BY p.id, p.name, p.location, p.dispatch_mode, p.is_active
            ORDER BY p.is_default DESC, p.name;

            SELECT DATE(j.requested_at)                             AS `Date`,
                   COALESCE(SUM(j.label_count), 0)                  AS Labels,
                   COALESCE(SUM(j.status = 'Failed'), 0)            AS Failed
            FROM print_jobs j
            WHERE j.requested_at >= @weekStart AND j.requested_at < @tomorrow
            GROUP BY DATE(j.requested_at)
            ORDER BY `Date`;

            SELECT COUNT(*) FROM label_templates WHERE is_active = 1;

            SELECT COUNT(*) FROM import_batches
            WHERE status = 'Failed' AND uploaded_at >= @weekStart;
            """,
            new
            {
                today = todayUtc, tomorrow = tomorrowUtc, yesterday = yesterdayUtc,
                weekStart = weekStartUtc, recentCount = RecentJobCount,
            }, cancellationToken: ct));

        var kpiRow = await multi.ReadSingleAsync<KpiRow>();
        var activeProducts = await multi.ReadSingleAsync<int>();
        var recent = (await multi.ReadAsync<RecentRow>()).ToList();
        var printers = (await multi.ReadAsync<PrinterRow>()).ToList();
        var daily = (await multi.ReadAsync<DailyRow>()).ToList();
        var activeTemplates = await multi.ReadSingleAsync<int>();
        var failedImports = await multi.ReadSingleAsync<int>();

        var kpis = new DashboardKpis(
            (int)kpiRow.LabelsToday, (int)kpiRow.JobsToday, (int)kpiRow.FailedToday,
            (int)kpiRow.ReprintsToday, (int)kpiRow.LabelsYesterday,
            activeProducts, kpiRow.ActiveUsersToday);

        return new DashboardDto(
            kpis,
            recent.Select(r => new RecentJobDto(
                r.Id, r.JobNo, r.ProductCode, r.Description, r.LabelCount,
                r.PrinterName, r.RequestedBy, r.Status, r.RequestedAtUtc, r.IsReprint)).ToList(),
            printers.Select(p => new PrinterHealthDto(
                p.Id, p.Name, p.Location, p.DispatchMode, p.IsActive,
                (int)p.JobsToday, (int)p.FailedToday, p.LastJobUtc, p.LastError)).ToList(),
            BuildAlerts(kpis, printers, activeProducts, activeTemplates, failedImports, backups.Read()),
            BuildWeek(daily, weekStartUtc));
    }

    /// <summary>Alerts the FRD asks for, derived from the same data the tiles
    /// use. Each carries a nav key so the card is actionable, not decorative.</summary>
    private static List<DashboardAlertDto> BuildAlerts(
        DashboardKpis kpis, List<PrinterRow> printers,
        int activeProducts, int activeTemplates, int failedImports, BackupStatus backup)
    {
        var alerts = new List<DashboardAlertDto>();

        if (kpis.FailedToday > 0)
        {
            alerts.Add(new DashboardAlertDto("Error",
                $"{kpis.FailedToday} print job(s) failed today",
                "Open Print History to see the reason and retry or reprint.", "history"));
        }

        foreach (var printer in printers.Where(p => p.FailedToday > 0))
        {
            alerts.Add(new DashboardAlertDto("Warning",
                $"Printer '{printer.Name}' reported a problem",
                printer.LastError ?? "One or more jobs failed on this printer today.", "printers"));
        }

        if (activeTemplates == 0)
        {
            alerts.Add(new DashboardAlertDto("Warning",
                "No label template is active",
                "Register and activate a label template before printing.", "settings"));
        }

        if (activeProducts == 0)
        {
            alerts.Add(new DashboardAlertDto("Warning",
                "No products yet",
                "Add products manually or import them from Excel.", "import"));
        }

        if (failedImports > 0)
        {
            alerts.Add(new DashboardAlertDto("Warning",
                $"{failedImports} Excel import(s) failed this week",
                "Open Excel Import to download the error report.", "import"));
        }

        if (printers.Count == 0)
        {
            alerts.Add(new DashboardAlertDto("Warning",
                "No printers configured",
                "Add a printer before labels can be printed.", "printers"));
        }

        // Backup age (§16). Status only — the application never offers a restore,
        // and the nav key points at Settings so an operator is not led to expect one.
        if (backup.Configured && backup.IsStale)
        {
            alerts.Add(new DashboardAlertDto("Warning",
                backup.LastSuccessUtc is null
                    ? "No successful backup has been recorded"
                    : $"Last backup was {(int)(DateTime.UtcNow - backup.LastSuccessUtc.Value).TotalHours} hours ago",
                backup.LastError ?? "Check the scheduled backup task on the server. See RUNBOOK.md.",
                "settings"));
        }

        return alerts;
    }

    /// <summary>Fills gaps so the trend always covers seven days — a missing
    /// day is meaningful (nothing printed), not an absent bar.</summary>
    private static List<DailyPointDto> BuildWeek(List<DailyRow> daily, DateTime weekStartUtc)
    {
        var byDate = daily.ToDictionary(d => DateOnly.FromDateTime(d.Date), d => d);
        return Enumerable.Range(0, 7)
            .Select(offset =>
            {
                var date = DateOnly.FromDateTime(weekStartUtc.AddDays(offset));
                return byDate.TryGetValue(date, out var row)
                    ? new DailyPointDto(date, (int)row.Labels, (int)row.Failed)
                    : new DailyPointDto(date, 0, 0);
            })
            .ToList();
    }

    // Mutable rows: MySQL SUM() is DECIMAL and COUNT() is BIGINT.
    private sealed class KpiRow
    {
        public decimal LabelsToday { get; set; }
        public decimal JobsToday { get; set; }
        public decimal FailedToday { get; set; }
        public decimal ReprintsToday { get; set; }
        public decimal LabelsYesterday { get; set; }
        public int ActiveUsersToday { get; set; }
    }

    private sealed class RecentRow
    {
        public long Id { get; set; }
        public string JobNo { get; set; } = "";
        public string ProductCode { get; set; } = "";
        public string Description { get; set; } = "";
        public int LabelCount { get; set; }
        public string PrinterName { get; set; } = "";
        public string RequestedBy { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime RequestedAtUtc { get; set; }
        public bool IsReprint { get; set; }
    }

    private sealed class PrinterRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string? Location { get; set; }
        public string DispatchMode { get; set; } = "";
        public bool IsActive { get; set; }
        public decimal JobsToday { get; set; }
        public decimal FailedToday { get; set; }
        public DateTime? LastJobUtc { get; set; }
        public string? LastError { get; set; }
    }

    private sealed class DailyRow
    {
        public DateTime Date { get; set; }
        public decimal Labels { get; set; }
        public decimal Failed { get; set; }
    }
}
