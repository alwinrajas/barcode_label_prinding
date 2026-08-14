using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Auth;
using BarcodePrinter.Contracts.Imports;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using MiniExcelLibs;
using Xunit;

namespace BarcodePrinter.Integration.Tests;

[Collection("api")]
public class ImportApiTests(ApiFixture fx) : IAsyncLifetime
{
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        var response = await fx.CreateClient().PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest("it-admin", ApiFixture.AdminPassword, "it-tests"));
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        _admin = fx.CreateClient();
        _admin.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.AccessToken);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- THE phase-4 exit criterion -----------------------------------------

    [Fact]
    public async Task Import_20k_rows_completes_under_25_seconds()
    {
        var file = MakeWorkbook(Enumerable.Range(1, 20_000).Select(i => Row(
            $"IMP20K-{i:00000}", $"Imported widget {i}", "PCS")));

        var sw = Stopwatch.StartNew();
        var batch = await UploadAndAwaitAsync(file, "bulk-20k.xlsx");
        sw.Stop();

        batch.Status.Should().Be("Completed",
            $"error: {batch.ErrorMessage} — invalid rows: {batch.InvalidRows}");
        batch.TotalRows.Should().Be(20_000);
        batch.ValidRows.Should().Be(20_000);
        batch.InsertedRows.Should().Be(20_000);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(25),
            $"§15.4 budget — actual: {sw.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public async Task Reimport_updates_instead_of_duplicating()
    {
        var file = MakeWorkbook(Enumerable.Range(1, 50).Select(i => Row(
            $"IMP-UPD-{i:00}", $"Original {i}", "PCS")));
        (await UploadAndAwaitAsync(file, "first.xlsx")).InsertedRows.Should().Be(50);

        var updated = MakeWorkbook(Enumerable.Range(1, 50).Select(i => Row(
            $"IMP-UPD-{i:00}", $"Renamed {i}", "PCS")));
        var second = await UploadAndAwaitAsync(updated, "second.xlsx");

        second.InsertedRows.Should().Be(0);
        second.UpdatedRows.Should().Be(50, "the upsert updates by unique code (A-13 semantics)");
    }

    // ---- Commit policies (C-13: both) -----------------------------------------

    [Fact]
    public async Task AllOrNothing_rejects_whole_file_when_any_row_fails()
    {
        var rows = Enumerable.Range(1, 20).Select(i => Row(
            $"IMP-AON-{i:00}", $"Widget {i}", "PCS")).ToList();
        rows.Add(Row("", "Missing code — invalid", "PCS"));   // 1 bad row

        var batch = await UploadAndAwaitAsync(MakeWorkbook(rows), "aon.xlsx");

        batch.Status.Should().Be("Failed");
        batch.InvalidRows.Should().Be(1);
        batch.InsertedRows.Should().Be(0, "all-or-nothing means NOTHING was imported");

        // Prove the DB agrees (exact-code check — ngram search is deliberately
        // fuzzy, so "no fuzzy hits" would be the wrong assertion).
        var search = await _admin.GetFromJsonAsync<BarcodePrinter.Contracts.Products.PagedResult<
            BarcodePrinter.Contracts.Products.ProductSummary>>(
            $"{ApiRoutes.Products.Base}/?q=IMP-AON-01");
        search!.Items.Should().NotContain(p => p.Code.StartsWith("IMP-AON"));
    }

    [Fact]
    public async Task PartialCommit_imports_valid_rows_and_reports_the_rest()
    {
        await SetCommitPolicyAsync("PartialCommit");
        try
        {
            var rows = Enumerable.Range(1, 20).Select(i => Row(
                $"IMP-PC-{i:00}", $"Widget {i}", "PCS")).ToList();
            rows.Add(Row("IMP-PC-BAD", "Bad UOM row", "NOT-A-UOM"));

            var batch = await UploadAndAwaitAsync(MakeWorkbook(rows), "partial.xlsx");

            batch.Status.Should().Be("Completed");
            batch.ValidRows.Should().Be(20);
            batch.InvalidRows.Should().Be(1);
            batch.InsertedRows.Should().Be(20);
            batch.HasErrorReport.Should().BeTrue();
        }
        finally
        {
            await SetCommitPolicyAsync("AllOrNothing");
        }
    }

    [Fact]
    public async Task Duplicate_codes_within_the_file_are_all_rejected()
    {
        await SetCommitPolicyAsync("PartialCommit");
        try
        {
            var batch = await UploadAndAwaitAsync(MakeWorkbook(
            [
                Row("IMP-DUPF-01", "First", "PCS"),
                Row("IMP-DUPF-02", "Unique", "PCS"),
                Row("IMP-DUPF-01", "Second occurrence", "PCS"),
            ]), "dups.xlsx");

            batch.InvalidRows.Should().Be(2, "EVERY occurrence of a duplicated code is rejected — the system must not guess which row wins");
            batch.InsertedRows.Should().Be(1);
        }
        finally
        {
            await SetCommitPolicyAsync("AllOrNothing");
        }
    }

    [Fact]
    public async Task Phantom_empty_rows_from_template_ranges_are_skipped()
    {
        // A template's data-validation range makes readers see thousands of
        // empty rows — a user who fills 5 rows must import exactly 5.
        var rows = Enumerable.Range(1, 5).Select(i => Row(
            $"IMP-EMPTY-{i:00}", $"Real row {i}", "PCS")).ToList();
        rows.AddRange(Enumerable.Range(1, 500).Select(_ => new Dictionary<string, object?>
        {
            ["Code"] = null, ["Description"] = null, ["UOM"] = null,
            ["Size"] = null, ["Color"] = null, ["Batch"] = null,
            ["Production Date"] = null, ["Expiry Date"] = null,
            ["Quantity"] = null, ["Carton Quantity"] = null, ["Category"] = null,
        }));

        var batch = await UploadAndAwaitAsync(MakeWorkbook(rows), "phantom.xlsx");

        batch.Status.Should().Be("Completed");
        batch.TotalRows.Should().Be(5, "empty phantom rows are not data and not errors");
        batch.InsertedRows.Should().Be(5);
        batch.InvalidRows.Should().Be(0);
    }

    // ---- Error report -----------------------------------------------------------

    [Fact]
    public async Task Error_report_returns_failed_rows_with_messages()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            Row("IMP-ERR-01", "Good row", "PCS"),
            Row("", "No code", "PCS"),
            Row("IMP-ERR-03", "Bad date", "PCS", prodDate: "31/31/2026"),
        };
        var batch = await UploadAndAwaitAsync(MakeWorkbook(rows), "errors.xlsx");
        batch.InvalidRows.Should().Be(2);

        var report = await _admin.GetAsync(ApiRoutes.Imports.Errors(batch.Id));
        report.StatusCode.Should().Be(HttpStatusCode.OK);
        var bytes = await report.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(500);

        // The workbook contains exactly the failed rows + an Error column.
        using var ms = new MemoryStream(bytes);
        var reportRows = MiniExcel.Query(ms, useHeaderRow: true)
            .Cast<IDictionary<string, object?>>().ToList();
        reportRows.Should().HaveCount(2);
        reportRows.Should().OnlyContain(r =>
            r.ContainsKey("Error") && !string.IsNullOrEmpty(r["Error"] as string));
    }

    // ---- Template + export ---------------------------------------------------------

    [Fact]
    public async Task Template_and_export_download_as_xlsx()
    {
        var template = await _admin.GetAsync(ApiRoutes.Imports.Template);
        template.StatusCode.Should().Be(HttpStatusCode.OK);
        (await template.Content.ReadAsByteArrayAsync()).Length.Should().BeGreaterThan(1_000);

        var export = await _admin.GetAsync(ApiRoutes.Products.Export);
        export.StatusCode.Should().Be(HttpStatusCode.OK);
        export.Content.Headers.ContentType!.MediaType
            .Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }

    [Fact]
    public async Task User_role_cannot_import()
    {
        var login = await fx.CreateClient().PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest("it-user", ApiFixture.UserPassword, "it-tests"));
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        var user = fx.CreateClient();
        user.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        (await user.GetAsync(ApiRoutes.Imports.Template))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- helpers --------------------------------------------------------------------

    private async Task<ImportBatchDto> UploadAndAwaitAsync(byte[] xlsx, string fileName)
    {
        using var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(xlsx);
        part.Headers.ContentType = new MediaTypeHeaderValue(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(part, "file", fileName);

        var response = await _admin.PostAsync($"{ApiRoutes.Imports.Base}/", content);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted,
            await response.Content.ReadAsStringAsync());
        var accepted = (await response.Content.ReadFromJsonAsync<ImportAcceptedResponse>())!;

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var batch = await _admin.GetFromJsonAsync<ImportBatchDto>(
                ApiRoutes.Imports.ById(accepted.BatchId));
            if (batch!.Status is "Completed" or "Failed" or "Cancelled")
            {
                return batch;
            }
            await Task.Delay(200);
        }
        throw new TimeoutException("Import did not finish within 60 s.");
    }

    private async Task SetCommitPolicyAsync(string policy)
    {
        await using var conn = await fx.OpenDbAsync();
        await using var cmd = new MySqlConnector.MySqlCommand(
            "UPDATE app_settings SET setting_value = @p WHERE setting_key = 'Import:CommitPolicy'", conn);
        cmd.Parameters.AddWithValue("@p", policy);
        await cmd.ExecuteNonQueryAsync();
        // The provider caches settings for 60 s — evict so the change is live.
        fx.Factory.Services.GetRequiredService<IMemoryCache>()
            .Remove("setting:Import:CommitPolicy");
    }

    private static Dictionary<string, object?> Row(
        string code, string description, string uom, string? prodDate = "21/07/2026") => new()
    {
        ["Code"] = code,
        ["Description"] = description,
        ["UOM"] = uom,
        ["Size"] = "M2",
        ["Color"] = "NATURAL",
        ["Batch"] = "CONE",
        ["Production Date"] = prodDate,
        ["Expiry Date"] = "21/07/2027",
        ["Quantity"] = 750,
        ["Carton Quantity"] = 750,
        ["Category"] = null,
    };

    private static byte[] MakeWorkbook(IEnumerable<Dictionary<string, object?>> rows)
    {
        using var ms = new MemoryStream();
        MiniExcel.SaveAs(ms, rows);
        return ms.ToArray();
    }
}
