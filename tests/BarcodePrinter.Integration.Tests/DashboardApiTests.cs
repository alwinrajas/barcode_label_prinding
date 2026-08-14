using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Auth;
using BarcodePrinter.Contracts.Dashboard;
using BarcodePrinter.Contracts.Printing;
using BarcodePrinter.Contracts.Reports;
using FluentAssertions;
using Xunit;

namespace BarcodePrinter.Integration.Tests;

[Collection("api")]
public class DashboardApiTests(ApiFixture fx) : IAsyncLifetime
{
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _admin = await LoginAsync("it-admin", ApiFixture.AdminPassword);
        await PrintScenario.EnsureHistoryAsync(_admin, fx);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Dashboard_returns_every_section_in_one_call()
    {
        var dashboard = await GetAsync();

        dashboard.Kpis.Should().NotBeNull();
        dashboard.Kpis.LabelsToday.Should().BeGreaterThan(0, "the scenario printed today");
        dashboard.Kpis.JobsToday.Should().BeGreaterThan(0);
        dashboard.Kpis.ActiveProducts.Should().BeGreaterThan(0);
        dashboard.Kpis.ActiveUsersToday.Should().BeGreaterThan(0);

        dashboard.RecentJobs.Should().NotBeEmpty();
        dashboard.RecentJobs.Should().HaveCountLessThanOrEqualTo(8, "the recent list is capped");
        dashboard.RecentJobs.Should().BeInDescendingOrder(j => j.RequestedAtUtc);
        dashboard.RecentJobs.Should().OnlyContain(j =>
            !string.IsNullOrEmpty(j.JobNo) && !string.IsNullOrEmpty(j.ProductCode));

        dashboard.Printers.Should().NotBeEmpty();
        dashboard.LastSevenDays.Should().HaveCount(7, "gaps are filled so the trend is always a full week");
        dashboard.LastSevenDays.Select(d => d.Date).Should().BeInAscendingOrder();
        dashboard.LastSevenDays.Last().Date.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow));
    }

    /// <summary>The dashboard must agree with the reports; two different
    /// numbers for "labels today" would destroy trust in both screens.</summary>
    [Fact]
    public async Task Kpis_agree_with_the_report_for_the_same_period()
    {
        var dashboard = await GetAsync();

        var from = Uri.EscapeDataString(DateTime.UtcNow.Date.ToString("O"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddDays(1).ToString("O"));
        var report = (await _admin.GetFromJsonAsync<ReportResult>(
            $"{ApiRoutes.Reports.Base}?type=PrintLog&from={from}&to={to}&pageSize=500"))!;

        dashboard.Kpis.LabelsToday.Should().Be(report.Totals.Labels);
        dashboard.Kpis.JobsToday.Should().Be(report.Totals.Jobs);
        dashboard.Kpis.FailedToday.Should().Be(report.Totals.Failed);
        dashboard.Kpis.ReprintsToday.Should().Be(report.Totals.Reprints);
    }

    [Fact]
    public async Task Trend_totals_match_the_weekly_report()
    {
        var dashboard = await GetAsync();

        var from = Uri.EscapeDataString(DateTime.UtcNow.Date.AddDays(-6).ToString("O"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddDays(1).ToString("O"));
        var report = (await _admin.GetFromJsonAsync<ReportResult>(
            $"{ApiRoutes.Reports.Base}?type=ByDate&from={from}&to={to}"))!;

        dashboard.LastSevenDays.Sum(d => d.Labels).Should().Be(report.Totals.Labels);
    }

    [Fact]
    public async Task Failed_job_raises_an_actionable_alert()
    {
        // A printer pointed at a closed port fails fast and deterministically.
        var printers = await _admin.GetFromJsonAsync<List<PrinterDto>>(
            $"{ApiRoutes.Printers.Base}/?activeOnly=false");
        var dead = printers!.FirstOrDefault(p => p.Code == "IT-DASH-DEAD");
        long deadId;
        if (dead is null)
        {
            var created = await _admin.PostAsJsonAsync(ApiRoutes.Printers.Base, new SavePrinterRequest(
                "IT-DASH-DEAD", "Dashboard dead printer", null, "NetworkTcp", "Server",
                "127.0.0.1", 9, null, null, 203, "Zpl", false, true));
            created.EnsureSuccessStatusCode();
            deadId = (await created.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        }
        else
        {
            deadId = dead.Id;
        }

        var (productId, templateId, _) = await PrintScenario.EnsureHistoryAsync(_admin, fx);
        var job = await _admin.PostAsJsonAsync(ApiRoutes.Print.Jobs, new PrintRequest(
            productId, templateId, deadId, "CONE", null, null, null,
            8000, 8000, 1, 1, "it-dash"));
        job.EnsureSuccessStatusCode();
        var jobId = (await job.Content.ReadFromJsonAsync<PrintJobCreatedResponse>())!.JobId;

        // Wait for the dispatcher to exhaust its retries.
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            var status = (await _admin.GetFromJsonAsync<PrintJobDto>(ApiRoutes.Print.JobById(jobId)))!;
            if (status.Status is "Failed" or "Completed")
            {
                break;
            }
            await Task.Delay(250);
        }

        var dashboard = await GetAsync();
        dashboard.Kpis.FailedToday.Should().BeGreaterThan(0);
        dashboard.Alerts.Should().Contain(a => a.Severity == "Error" && a.NavigateTo == "history");
        dashboard.Alerts.Should().Contain(a => a.NavigateTo == "printers",
            "the failing printer is called out so the operator knows which one to check");
        dashboard.Printers.Should().Contain(p => p.Name == "Dashboard dead printer" && p.FailedToday > 0);
    }

    [Fact]
    public async Task Dashboard_requires_the_dashboard_permission()
    {
        var user = await LoginAsync("it-user", ApiFixture.UserPassword);
        (await user.GetAsync(ApiRoutes.Dashboard.Base)).StatusCode.Should().Be(HttpStatusCode.OK);

        var noAccess = fx.CreateClient();
        (await noAccess.GetAsync(ApiRoutes.Dashboard.Base)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<DashboardDto> GetAsync()
    {
        var response = await _admin.GetAsync(ApiRoutes.Dashboard.Base);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<DashboardDto>())!;
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

    private sealed record IdResponse(long Id);
}
