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

    public async Task DisposeAsync()
    {
        await SetCommitPolicyAsync("AllOrNothing");
    }

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
            ["Quantity"] = null, ["Carton Quantity"] = null,
            ["Cartons per Pallet"] = null,
            ["Production Date"] = null, ["Expiry Date"] = null,
            ["Category"] = null, ["Barcode"] = null,
        }));

        var batch = await UploadAndAwaitAsync(MakeWorkbook(rows), "phantom.xlsx");

        batch.Status.Should().Be("Completed");
        batch.TotalRows.Should().Be(5, "empty phantom rows are not data and not errors");
        batch.InsertedRows.Should().Be(5);
        batch.InvalidRows.Should().Be(0);
    }

    // ---- The import contract: extra columns are IGNORED, never rejected ---------

    [Fact]
    public async Task A_file_with_a_category_column_imports_instead_of_failing_every_row()
    {
        // The regression that started this: product_categories is never populated,
        // so a customer file carrying "GENERAL" in every row produced one error
        // per row and, under AllOrNothing, imported nothing at all.
        var rows = Enumerable.Range(1, 25).Select(i => Row(
            $"IMP-CAT-{i:00}", $"Widget {i}", "PCS", category: "GENERAL")).ToList();

        var batch = await UploadAndAwaitAsync(MakeWorkbook(rows), "with-category.xlsx");

        batch.Status.Should().Be("Completed", $"error: {batch.ErrorMessage}");
        batch.InvalidRows.Should().Be(0, "Category is not part of the import contract");
        batch.InsertedRows.Should().Be(25);

        // Ignored means IGNORED: nothing was written to category_id.
        (await ScalarAsync(
            "SELECT COUNT(*) FROM products WHERE code LIKE 'IMP-CAT-%' AND category_id IS NOT NULL"))
            .Should().Be(0L);
    }

    [Fact]
    public async Task Legacy_columns_import_cleanly_even_when_their_values_are_nonsense()
    {
        // Category / Production Date / Expiry Date / Barcode are never parsed, so
        // even values that could never validate cannot invalidate a row.
        var rows = Enumerable.Range(1, 10).Select(i =>
        {
            var row = Row($"IMP-LEG-{i:00}", $"Legacy widget {i}", "PCS",
                prodDate: "31/31/2026", category: "NO-SUCH-CATEGORY", barcode: "not-a-barcode");
            row["Expiry Date"] = "yesterday";
            return row;
        }).ToList();

        var batch = await UploadAndAwaitAsync(MakeWorkbook(rows), "legacy-columns.xlsx");

        batch.Status.Should().Be("Completed", $"error: {batch.ErrorMessage}");
        batch.InvalidRows.Should().Be(0);
        batch.InsertedRows.Should().Be(10);
        (await ScalarAsync(
            """
            SELECT COUNT(*) FROM products
            WHERE code LIKE 'IMP-LEG-%'
              AND (category_id IS NOT NULL
                   OR default_production_date IS NOT NULL
                   OR default_expiry_date IS NOT NULL)
            """)).Should().Be(0L, "none of those columns is written by the importer");
    }

    [Fact]
    public async Task Reimport_leaves_fields_outside_the_contract_untouched()
    {
        const string code = "IMP-KEEP-01";
        (await UploadAndAwaitAsync(MakeWorkbook([Row(code, "Original", "PCS")]), "keep-1.xlsx"))
            .Status.Should().Be("Completed");

        // Values set OUTSIDE the import (category by an admin, dates by a print run).
        await ExecuteAsync(
            """
            INSERT INTO product_categories (code, name, is_active) VALUES ('KEEPCAT', 'Keep me', 1)
            ON DUPLICATE KEY UPDATE name = VALUES(name);
            """);
        await ExecuteAsync(
            $"""
            UPDATE products
            SET category_id = (SELECT id FROM product_categories WHERE code = 'KEEPCAT'),
                default_production_date = '2020-01-01',
                default_expiry_date     = '2030-01-01'
            WHERE code = '{code}';
            """);

        var second = await UploadAndAwaitAsync(MakeWorkbook(
            [Row(code, "Renamed", "PCS", prodDate: "01/01/1999", category: "SOMETHING ELSE")]), "keep-2.xlsx");
        second.Status.Should().Be("Completed");
        second.UpdatedRows.Should().Be(1);

        (await ScalarAsync($"SELECT description FROM products WHERE code = '{code}'"))
            .Should().Be("Renamed", "columns inside the contract ARE updated");
        (await ScalarAsync($"SELECT category_id FROM products WHERE code = '{code}'"))
            .Should().NotBeNull("the importer must not null a value it does not own");
        (await ScalarAsync($"SELECT default_production_date FROM products WHERE code = '{code}'"))
            .Should().Be(new DateTime(2020, 1, 1));
        (await ScalarAsync($"SELECT default_expiry_date FROM products WHERE code = '{code}'"))
            .Should().Be(new DateTime(2030, 1, 1));
    }

    [Fact]
    public async Task A_file_with_only_the_contract_columns_imports()
    {
        // Exactly the generated template's headers — nothing else.
        var rows = Enumerable.Range(1, 5).Select(i => new Dictionary<string, object?>
        {
            ["Code"] = $"IMP-MIN-{i:00}",
            ["Description"] = $"Minimal widget {i}",
            ["UOM"] = "PCS",
            ["Size"] = "M2",
            ["Color"] = "NATURAL",
            ["Batch"] = "CONE",
            ["Quantity"] = 750,
            ["Carton Quantity"] = 750,
            ["Cartons per Pallet"] = 40,
        }).ToList();

        var batch = await UploadAndAwaitAsync(MakeWorkbook(rows), "minimal.xlsx");

        batch.Status.Should().Be("Completed", $"error: {batch.ErrorMessage}");
        batch.InvalidRows.Should().Be(0);
        batch.InsertedRows.Should().Be(5);
    }

    [Fact]
    public async Task Cartons_per_pallet_round_trips()
    {
        const string code = "IMP-CPP-01";
        var batch = await UploadAndAwaitAsync(MakeWorkbook(
            [Row(code, "Pallet widget", "PCS", cartonsPerPallet: 42)]), "cpp.xlsx");
        batch.Status.Should().Be("Completed", $"error: {batch.ErrorMessage}");

        (await ScalarAsync($"SELECT cartons_per_pallet FROM products WHERE code = '{code}'"))
            .Should().Be(42);

        // ...and the export carries it back out, so an export re-imports intact.
        var export = await _admin.GetAsync(ApiRoutes.Products.Export);
        export.StatusCode.Should().Be(HttpStatusCode.OK);
        using var ms = new MemoryStream(await export.Content.ReadAsByteArrayAsync());
        MiniExcel.Query(ms, useHeaderRow: false).Cast<IDictionary<string, object?>>()
            .SelectMany(r => r.Values).Should().Contain(v => (v as string) == "Cartons per Pallet");
    }

    [Fact]
    public async Task Unknown_uom_error_names_the_row_and_the_product_code()
    {
        const string code = "IMP-UOMERR-01";
        var batch = await UploadAndAwaitAsync(MakeWorkbook(
            [Row(code, "Bad unit", "NOT-A-UOM")]), "bad-uom.xlsx");

        batch.InvalidRows.Should().Be(1);
        var message = (string?)await ScalarAsync(
            $"SELECT message FROM import_errors WHERE batch_id = {batch.Id} ORDER BY id LIMIT 1");

        // "Row 2" — the first DATA row is row 2 in the user's spreadsheet.
        // (The message then lists the valid UOMs, which other tests may add to.)
        message.Should().StartWith($"Row 2 ({code}): UOM 'NOT-A-UOM' does not exist. Valid values: ");
    }

    // ---- Error report -----------------------------------------------------------

    [Fact]
    public async Task Error_report_returns_failed_rows_with_messages()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            Row("IMP-ERR-01", "Good row", "PCS"),
            Row("", "No code", "PCS"),
            Row("IMP-ERR-03", "Bad UOM", "NOT-A-UOM"),
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

    /// <summary>The shape of a REAL customer file: the nine contract columns
    /// PLUS the columns that used to be part of the contract. Category,
    /// Production Date and Expiry Date stay in this fixture deliberately —
    /// customers hold files that carry them and those files must import cleanly,
    /// with the extra columns ignored rather than rejected.</summary>
    private static Dictionary<string, object?> Row(
        string code, string description, string uom,
        string? prodDate = "21/07/2026", object? cartonsPerPallet = null,
        string? category = null, string? barcode = null) => new()
    {
        ["Code"] = code,
        ["Description"] = description,
        ["UOM"] = uom,
        ["Size"] = "M2",
        ["Color"] = "NATURAL",
        ["Batch"] = "CONE",
        ["Quantity"] = 750,
        ["Carton Quantity"] = 750,
        ["Cartons per Pallet"] = cartonsPerPallet,
        // ---- outside the import contract: read by nobody -------------------
        ["Production Date"] = prodDate,
        ["Expiry Date"] = "21/07/2027",
        ["Category"] = category,
        ["Barcode"] = barcode,
    };

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = await fx.OpenDbAsync();
        await using var cmd = new MySqlConnector.MySqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = await fx.OpenDbAsync();
        await using var cmd = new MySqlConnector.MySqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static byte[] MakeWorkbook(IEnumerable<Dictionary<string, object?>> rows)
    {
        using var ms = new MemoryStream();
        MiniExcel.SaveAs(ms, rows);
        return ms.ToArray();
    }
}
