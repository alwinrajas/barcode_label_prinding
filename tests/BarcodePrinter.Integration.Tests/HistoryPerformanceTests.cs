using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Auth;
using BarcodePrinter.Contracts.Printing;
using BarcodePrinter.Contracts.Products;
using BarcodePrinter.Contracts.Reports;
using FluentAssertions;
using MySqlConnector;
using Xunit;
using Xunit.Abstractions;

namespace BarcodePrinter.Integration.Tests;

/// <summary>
/// Phase 8 performance exit criteria for print history and reports (§11.1):
/// a keyset page must stay under 300 ms at volume, and it must stay CORRECT at
/// depth — a fast page that skips rows is worse than a slow one.
///
/// Seeded rows are dated FORWARD from 2026-09-01 on purpose. Every other suite
/// queries today, the last 7 days or the last 30 days, so this volume is
/// invisible to them, and the spread exercises eleven of the monthly partitions
/// rather than piling into one.
///
/// The seeded date is deliberately DECORRELATED from the auto-increment id
/// (a stride permutation). Real history decorrelates the same way — reprints of
/// old jobs, an Oracle backfill, a clock correction — and any keyset cursor that
/// silently assumes "higher id means later timestamp" breaks there.
/// </summary>
[Collection("api")]
public class HistoryPerformanceTests(ApiFixture fx, ITestOutputHelper output) : IAsyncLifetime
{
    private const int SeededJobs = 200_000;
    private const int SpreadDays = 250;
    private static readonly DateTime SeedStart = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string From = Uri.EscapeDataString(SeedStart.ToString("O"));
    private static readonly string To =
        Uri.EscapeDataString(SeedStart.AddDays(SpreadDays + 1).ToString("O"));

    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _admin = await LoginAsync("it-admin", ApiFixture.AdminPassword);
        await SeedVolumeAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- Correctness at depth ------------------------------------------------------

    /// <summary>Walks the history a page at a time and asserts every job is seen
    /// exactly once. This is the property keyset pagination exists to provide;
    /// an id-only cursor over a (requested_at, id) ordering does not have it.</summary>
    [Fact]
    public async Task Keyset_walk_visits_each_job_exactly_once()
    {
        const int pageSize = 50;
        var seen = new HashSet<long>();
        var duplicates = new List<long>();
        string? cursor = null;
        DateTime previous = DateTime.MaxValue;

        for (var page = 0; page < 20; page++)
        {
            var url = $"{ApiRoutes.Print.History}?from={From}&to={To}&pageSize={pageSize}" +
                      (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var result = (await _admin.GetFromJsonAsync<PagedResult<PrintJobDto>>(url))!;

            result.Items.Should().NotBeEmpty($"page {page} of a 200k history should be full");

            foreach (var job in result.Items)
            {
                if (!seen.Add(job.Id))
                {
                    duplicates.Add(job.Id);
                }
                job.RequestedAtUtc.Should().BeOnOrBefore(previous,
                    "the walk must stay in descending time order across page boundaries");
                previous = job.RequestedAtUtc;
            }

            result.HasMore.Should().BeTrue("20 pages of 50 is far short of 200k rows");
            cursor = result.NextCursor;
            cursor.Should().NotBeNull();
        }

        duplicates.Should().BeEmpty("a keyset walk must never re-serve a row");
        seen.Should().HaveCount(20 * pageSize, "and must never skip one either");
    }

    [Fact]
    public async Task Report_keyset_walk_visits_each_job_exactly_once()
    {
        var seen = new HashSet<long>();
        string? cursor = null;

        for (var page = 0; page < 10; page++)
        {
            var url = $"{ApiRoutes.Reports.Base}?type=PrintLog&from={From}&to={To}&pageSize=100" +
                      (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var result = (await _admin.GetFromJsonAsync<ReportResult>(url))!;

            result.Rows.Should().NotBeEmpty();
            foreach (var row in result.Rows)
            {
                seen.Add(row.JobId!.Value);
            }
            cursor = result.NextCursor;
            cursor.Should().NotBeNull();
        }

        seen.Should().HaveCount(1_000, "10 pages of 100 distinct jobs");
    }

    // ---- Throughput ----------------------------------------------------------------

    [Fact]
    public async Task History_page_p95_under_300ms_at_volume()
    {
        var p95 = await MeasureAsync("history", 20, i =>
            $"{ApiRoutes.Print.History}?from={From}&to={To}&pageSize=50&status=" +
            (i % 3 == 0 ? "Failed" : "Completed"));

        p95.Should().BeLessThan(300, $"§11.1 exit criterion at {SeededJobs:N0} jobs");
    }

    /// <summary>Page 40 must cost the same as page 1 — that is the whole point of
    /// seek pagination, and the thing OFFSET cannot do.</summary>
    [Fact]
    public async Task Deep_page_costs_the_same_as_the_first_page()
    {
        var cursor = await WalkToPageAsync(40);

        var first = await MeasureAsync("page-1", 10,
            _ => $"{ApiRoutes.Print.History}?from={From}&to={To}&pageSize=50");
        var deep = await MeasureAsync("page-40", 10,
            _ => $"{ApiRoutes.Print.History}?from={From}&to={To}&pageSize=50" +
                 $"&cursor={Uri.EscapeDataString(cursor)}");

        deep.Should().BeLessThan(Math.Max(first * 3, 300),
            $"deep paging must not degrade (page 1 p95 {first:F0} ms, page 40 p95 {deep:F0} ms)");
    }

    [Theory]
    [InlineData("PrintLog")]
    [InlineData("ByProduct")]
    [InlineData("ByUser")]
    [InlineData("ByPrinter")]
    [InlineData("ByDate")]
    [InlineData("Reprints")]
    public async Task Report_p95_under_300ms_at_volume(string type)
    {
        // A month is the realistic reporting window; the full 250-day range is
        // covered by the aggregate test below.
        var from = Uri.EscapeDataString(SeedStart.ToString("O"));
        var to = Uri.EscapeDataString(SeedStart.AddDays(30).ToString("O"));

        var p95 = await MeasureAsync($"report:{type}", 15,
            _ => $"{ApiRoutes.Reports.Base}?type={type}&from={from}&to={to}&pageSize=100");

        p95.Should().BeLessThan(300, $"§11.1 exit criterion at {SeededJobs:N0} jobs");
    }

    [Fact]
    public async Task Dashboard_stays_fast_with_a_large_history_table()
    {
        var p95 = await MeasureAsync("dashboard", 15, _ => ApiRoutes.Dashboard.Base);
        p95.Should().BeLessThan(300, "the landing page is loaded by every user at shift start");
    }

    // ---- Structure -----------------------------------------------------------------

    /// <summary>Targets are met by structure, not by a warm buffer pool: assert
    /// the plan itself. A date-bounded history page must prune to the partitions
    /// it actually needs and range-scan an index — never scan the table.</summary>
    [Fact]
    public async Task Date_bounded_history_prunes_partitions_and_range_scans_an_index()
    {
        await using var conn = await fx.OpenDbAsync();
        await using var cmd = new MySqlCommand(
            """
            EXPLAIN SELECT j.id, j.job_no
            FROM print_jobs j
            WHERE j.requested_at >= @from AND j.requested_at < @to
            ORDER BY j.requested_at DESC, j.id DESC
            LIMIT 50
            """, conn);
        cmd.Parameters.AddWithValue("@from", SeedStart.AddDays(10));
        cmd.Parameters.AddWithValue("@to", SeedStart.AddDays(20));

        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        var partitions = reader["partitions"] as string ?? "";
        var type = reader["type"] as string ?? "";
        var key = reader["key"] as string;
        output.WriteLine($"partitions={partitions} type={type} key={key}");

        partitions.Split(',').Should().HaveCountLessThanOrEqualTo(2,
            "a 10-day window spans at most two monthly partitions");
        type.Should().NotBe("ALL", "a date-bounded query must never scan the whole table");
        key.Should().NotBeNull("the requested_at index must be chosen");
    }

    // ---- helpers -------------------------------------------------------------------

    private async Task<double> MeasureAsync(string label, int runs, Func<int, string> url)
    {
        // Warm the pool and the plan cache; the target is steady-state.
        for (var i = 0; i < 3; i++)
        {
            (await _admin.GetAsync(url(i))).EnsureSuccessStatusCode();
        }

        var samples = new List<double>(runs);
        for (var i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            var response = await _admin.GetAsync(url(i));
            sw.Stop();
            response.EnsureSuccessStatusCode();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        var p95 = samples[Math.Max(0, (int)Math.Ceiling(samples.Count * 0.95) - 1)];
        output.WriteLine(
            $"{label}: min={samples[0]:F0} p50={samples[samples.Count / 2]:F0} p95={p95:F0} ms");
        return p95;
    }

    private async Task<string> WalkToPageAsync(int pages)
    {
        string? cursor = null;
        for (var i = 0; i < pages; i++)
        {
            var url = $"{ApiRoutes.Print.History}?from={From}&to={To}&pageSize=50" +
                      (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = (await _admin.GetFromJsonAsync<PagedResult<PrintJobDto>>(url))!;
            cursor = page.NextCursor;
            cursor.Should().NotBeNull($"the history must still be pageable at page {i}");
        }
        return cursor!;
    }

    private async Task SeedVolumeAsync()
    {
        var (productId, templateId, printerId) = await PrintScenario.EnsureHistoryAsync(_admin, fx);

        await using var conn = await fx.OpenDbAsync();
        var already = Convert.ToInt64(await new MySqlCommand(
            "SELECT COUNT(*) FROM print_jobs WHERE job_no LIKE 'PJ-PERF-%'", conn)
            .ExecuteScalarAsync());
        if (already >= SeededJobs)
        {
            return;
        }

        var userId = Convert.ToInt64(await new MySqlCommand(
            "SELECT id FROM users WHERE username = 'it-admin'", conn).ExecuteScalarAsync());

        var sw = Stopwatch.StartNew();
        const int chunk = 25_000;
        for (var offset = 0; offset < SeededJobs; offset += chunk)
        {
            await using var cmd = new MySqlCommand(
                $"""
                INSERT IGNORE INTO print_jobs (
                    requested_at, job_no, requested_by_user_id, printer_id,
                    template_id, template_version, product_id,
                    snap_product_code, snap_description, snap_barcode_value, snap_batch,
                    carton_from, carton_to, carton_total, copies_per_label, label_count,
                    status, labels_confirmed, attempt_count, is_reprint,
                    correlation_id, concurrency_stamp)
                SELECT
                    TIMESTAMP('{SeedStart:yyyy-MM-dd}')
                        + INTERVAL ((n * 97) MOD {SpreadDays}) DAY
                        + INTERVAL (n MOD 1440) MINUTE,
                    CONCAT('PJ-PERF-', LPAD(n, 8, '0')),
                    @userId, @printerId, @templateId, 1, @productId,
                    CONCAT('PERF-', LPAD(n MOD 500, 4, '0')),
                    'Perf seeded job', CONCAT('PERF-', LPAD(n MOD 500, 4, '0')),
                    CONCAT('B', n MOD 90),
                    n, n, 1, 1, (n MOD 20) + 1,
                    CASE WHEN n MOD 37 = 0 THEN 'Failed' ELSE 'Completed' END,
                    0, 1, (n MOD 23 = 0),
                    '00000000-0000-0000-0000-000000000000',
                    '00000000-0000-0000-0000-000000000000'
                FROM (
                    SELECT d0.n + d1.n * 10 + d2.n * 100
                         + d3.n * 1000 + d4.n * 10000 + d5.n * 100000 AS n
                    FROM {Digits("d0")}, {Digits("d1")}, {Digits("d2")},
                         {Digits("d3")}, {Digits("d4")}, {Digits("d5")}
                    ORDER BY n
                    LIMIT {chunk} OFFSET {offset}
                ) numbers
                """, conn)
            {
                CommandTimeout = 300,
            };
            cmd.Parameters.AddWithValue("@userId", userId);
            cmd.Parameters.AddWithValue("@printerId", printerId);
            cmd.Parameters.AddWithValue("@templateId", templateId);
            cmd.Parameters.AddWithValue("@productId", productId);
            await cmd.ExecuteNonQueryAsync();
        }

        // The optimizer needs current statistics, or it may reject the index it
        // should be using and the measurement tests fail for the wrong reason.
        await new MySqlCommand("ANALYZE TABLE print_jobs", conn) { CommandTimeout = 300 }
            .ExecuteNonQueryAsync();

        await SettleAsync(conn);
        output.WriteLine($"seeded {SeededJobs:N0} print jobs in {sw.Elapsed.TotalSeconds:F0}s");
    }

    /// <summary>
    /// §11.1 targets are steady-state figures for a long-running server with a
    /// buffer pool sized to the data (§16). Immediately after a 200k bulk load
    /// InnoDB is still flushing what the load dirtied, and a query timed in that
    /// window measures the loader, not the query — the first run here was 883 ms
    /// against 0.8 ms for the identical statement once flushing finished. So:
    /// pull the date index into the pool, then wait for the flush to drain.
    /// </summary>
    private async Task SettleAsync(MySqlConnection conn)
    {
        await new MySqlCommand(
            "SELECT COUNT(*) FROM print_jobs WHERE requested_at > '2000-01-01'", conn)
            { CommandTimeout = 300 }.ExecuteScalarAsync();

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(90))
        {
            await using var status = new MySqlCommand(
                "SHOW GLOBAL STATUS LIKE 'Innodb_buffer_pool_pages_dirty'", conn);
            await using var reader = await status.ExecuteReaderAsync();
            var dirty = await reader.ReadAsync() ? Convert.ToInt64(reader.GetString(1)) : 0;
            await reader.CloseAsync();

            if (dirty < 200)
            {
                output.WriteLine($"settled after {sw.Elapsed.TotalSeconds:F0}s ({dirty} dirty pages)");
                return;
            }
            await Task.Delay(500);
        }

        output.WriteLine("WARNING: buffer pool never settled; timings may be pessimistic");
    }

    /// <summary>A 0–9 derived table; six of them cross-joined generate a million
    /// row numbers without a recursive CTE or a helper table.</summary>
    private static string Digits(string alias) =>
        "(SELECT 0 AS n UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 " +
        "UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 " +
        $"UNION ALL SELECT 8 UNION ALL SELECT 9) {alias}";

    private async Task<HttpClient> LoginAsync(string username, string password)
    {
        var client = fx.CreateClient();
        var response = await client.PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest(username, password, "it-tests"));
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return client;
    }
}
