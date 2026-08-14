using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Auth;
using BarcodePrinter.Contracts.Reports;
using FluentAssertions;
using Xunit;

namespace BarcodePrinter.Integration.Tests;

/// <summary>
/// Reports run over whatever print history the other suites created, so these
/// assert shape, filtering and totals-consistency rather than fixed numbers.
/// </summary>
[Collection("api")]
public class ReportApiTests(ApiFixture fx) : IAsyncLifetime
{
    private HttpClient _admin = null!;
    private string _from = "";
    private string _to = "";

    public async Task InitializeAsync()
    {
        _admin = await LoginAsync("it-admin", ApiFixture.AdminPassword);
        // Reports need history to report on; seed our own so the suite does not
        // depend on another suite having run first.
        await PrintScenario.EnsureHistoryAsync(_admin, fx);
        _from = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-30).ToString("O"));
        _to = Uri.EscapeDataString(DateTime.UtcNow.AddDays(1).ToString("O"));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("PrintLog", "Barcode printing log")]
    [InlineData("ByProduct", "Product-wise printing")]
    [InlineData("ByUser", "User-wise printing")]
    [InlineData("ByPrinter", "Printer-wise printing")]
    [InlineData("ByDate", "Date-wise printing")]
    [InlineData("Reprints", "Reprint history")]
    public async Task Every_report_type_runs_and_returns_its_shape(string type, string title)
    {
        var result = await RunAsync(type);

        result.Type.Should().Be(type);
        result.Title.Should().Be(title);
        result.Columns.Should().NotBeEmpty();
        result.Totals.Should().NotBeNull();
    }

    [Fact]
    public async Task Totals_reflect_the_whole_filtered_set_not_the_returned_page()
    {
        // One row per page, but totals must still describe everything matched.
        var paged = await RunAsync("PrintLog", pageSize: 1);
        var full = await RunAsync("PrintLog", pageSize: 500);

        paged.Rows.Should().HaveCount(1);
        paged.Totals.Jobs.Should().Be(full.Totals.Jobs);
        paged.Totals.Labels.Should().Be(full.Totals.Labels);
        paged.Totals.Jobs.Should().BeGreaterThan(1, "earlier suites printed several jobs");
    }

    [Fact]
    public async Task Aggregate_totals_match_the_detail_log_over_the_same_period()
    {
        var detail = await RunAsync("PrintLog", pageSize: 500);
        var byProduct = await RunAsync("ByProduct");
        var byUser = await RunAsync("ByUser");

        byProduct.Totals.Jobs.Should().Be(detail.Totals.Jobs);
        byProduct.Totals.Labels.Should().Be(detail.Totals.Labels);
        byUser.Totals.Labels.Should().Be(detail.Totals.Labels,
            "the same jobs grouped differently must still sum to the same labels");
    }

    [Fact]
    public async Task Aggregations_group_and_rank_by_volume()
    {
        var byProduct = await RunAsync("ByProduct");

        byProduct.Rows.Should().NotBeEmpty();
        byProduct.Rows.Select(r => r.Key).Should().OnlyHaveUniqueItems("each product appears once");
        byProduct.Rows.Select(r => r.Labels).Should().BeInDescendingOrder();
        byProduct.Rows.Should().OnlyContain(r => r.Jobs > 0 && r.Labels > 0);
    }

    [Fact]
    public async Task Reprint_report_returns_only_reprints()
    {
        var reprints = await RunAsync("Reprints", pageSize: 200);
        reprints.Rows.Should().OnlyContain(r => r.Reprints == 1);
        // And it is a strict subset of the full log.
        var log = await RunAsync("PrintLog", pageSize: 500);
        reprints.Totals.Jobs.Should().BeLessThanOrEqualTo(log.Totals.Jobs);
    }

    [Fact]
    public async Task Detail_report_pages_by_keyset_without_overlap()
    {
        var page1 = await RunAsync("PrintLog", pageSize: 2);
        page1.HasMore.Should().BeTrue();

        var page2 = await RunAsync("PrintLog", pageSize: 2, cursor: page1.NextCursor);
        page2.Rows.Select(r => r.JobId).Should().NotIntersectWith(page1.Rows.Select(r => r.JobId));
    }

    [Fact]
    public async Task Search_filter_narrows_the_result()
    {
        var all = await RunAsync("PrintLog", pageSize: 500);
        var filtered = await RunAsync("PrintLog", pageSize: 500, search: "IT-PRINT-01");

        filtered.Totals.Jobs.Should().BeLessThanOrEqualTo(all.Totals.Jobs);
        filtered.Rows.Should().OnlyContain(r => r.Key.Contains("IT-PRINT-01"));
    }

    [Fact]
    public async Task Period_outside_any_activity_returns_empty_with_zero_totals()
    {
        var from = Uri.EscapeDataString(DateTime.UtcNow.AddYears(-5).ToString("O"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddYears(-5).AddDays(1).ToString("O"));
        var result = await _admin.GetFromJsonAsync<ReportResult>(
            $"{ApiRoutes.Reports.Base}?type=PrintLog&from={from}&to={to}");

        result!.Rows.Should().BeEmpty();
        result.Totals.Jobs.Should().Be(0);
        result.Totals.Labels.Should().Be(0);
    }

    // ---- Export ------------------------------------------------------------------

    [Fact]
    public async Task Export_produces_a_readable_workbook_for_every_report()
    {
        foreach (var type in new[] { "PrintLog", "ByProduct", "ByUser", "ByPrinter", "ByDate", "Reprints" })
        {
            var response = await _admin.GetAsync(
                $"{ApiRoutes.Reports.Export}?type={type}&from={_from}&to={_to}");

            response.StatusCode.Should().Be(HttpStatusCode.OK, $"export of {type} must succeed");
            response.Content.Headers.ContentType!.MediaType
                .Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

            var bytes = await response.Content.ReadAsByteArrayAsync();
            bytes.Length.Should().BeGreaterThan(1_000);

            using var stream = new MemoryStream(bytes);
            using var workbook = new ClosedXML.Excel.XLWorkbook(stream);
            var sheet = workbook.Worksheet(1);
            sheet.Cell(1, 1).GetString().Should().NotBeEmpty("the export names the report");
            sheet.Cell(3, 1).GetString().Should().Contain("it-admin",
                "the export records who generated it");
        }
    }

    // ---- RBAC ---------------------------------------------------------------------

    [Fact]
    public async Task Report_access_requires_the_report_permissions()
    {
        var user = await LoginAsync("it-user", ApiFixture.UserPassword);

        // The User role has Report.View but not Report.Export.
        (await user.GetAsync($"{ApiRoutes.Reports.Base}?type=PrintLog&from={_from}&to={_to}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await user.GetAsync($"{ApiRoutes.Reports.Export}?type=PrintLog&from={_from}&to={_to}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- helpers -------------------------------------------------------------------

    private async Task<ReportResult> RunAsync(
        string type, int pageSize = 100, string? cursor = null, string? search = null)
    {
        var url = $"{ApiRoutes.Reports.Base}?type={type}&from={_from}&to={_to}&pageSize={pageSize}";
        if (cursor is not null) url += $"&cursor={cursor}";
        if (search is not null) url += $"&search={Uri.EscapeDataString(search)}";

        var response = await _admin.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ReportResult>())!;
    }

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
