using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Auth;
using BarcodePrinter.Contracts.Printing;
using BarcodePrinter.Contracts.Products;
using BarcodePrinter.Contracts.Templates;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarcodePrinter.Integration.Tests;

[Collection("api")]
public class PrintApiTests(ApiFixture fx) : IAsyncLifetime
{
    private HttpClient _admin = null!;
    private long _productId;
    private long _templateId;
    private long _printerId;

    private const string TemplateZpl = """
        ^XA
        ^CI28
        ^PW812
        ^FO232,16^BY2,3.0,56^BCN,56,Y,N,N^FD5GCAPM2N^FS
        ^FO264,112^A0N,26,26^FDProduct^FS
        ^FO452,112^A0N,26,26^FD5G M2 CAP^FS
        ^FO452,220^A0N,26,26^FDCONE^FS
        ^FO452,392^A0N,26,26^FD1^FS
        ^FO660,470^BQN,2,5^FDLA,https://forms.gle/EXAMPLE^FS
        ^XZ
        """;

    public async Task InitializeAsync()
    {
        _admin = await LoginAsync("it-admin", ApiFixture.AdminPassword);
        _productId = await EnsureProductAsync();
        _templateId = await EnsureTemplateAsync();
        _printerId = await EnsurePrinterAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- Submit ----------------------------------------------------------------

    [Fact]
    public async Task Submit_snapshots_effective_values_and_renders_every_label()
    {
        var response = await _admin.PostAsJsonAsync(ApiRoutes.Print.Jobs, new PrintRequest(
            _productId, _templateId, _printerId,
            Batch: "RUN-OVERRIDE", ProductionDate: new DateOnly(2026, 8, 1),
            ExpiryDate: new DateOnly(2027, 8, 1), QuantityText: "500[D]",
            CartonFrom: 41, CartonTo: 45, LabelCount: 5, CopiesPerLabel: 1, Workstation: "it"));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var created = (await response.Content.ReadFromJsonAsync<PrintJobCreatedResponse>())!;
        created.CartonFrom.Should().Be(41);
        created.CartonTo.Should().Be(45);
        created.LabelCount.Should().Be(5);
        created.JobNo.Should().StartWith("PJ-");

        var job = (await _admin.GetFromJsonAsync<PrintJobDto>(ApiRoutes.Print.JobById(created.JobId)))!;
        job.Batch.Should().Be("RUN-OVERRIDE", "the operator's override is what gets printed and stored (A-9/A-10)");
        job.QuantityText.Should().Be("500[D]");
        job.ProductCode.Should().Be("IT-PRINT-01");

        // The payload contains the layout ONCE and one recall per carton.
        var payload = Encoding.UTF8.GetString(
            await _admin.GetByteArrayAsync(ApiRoutes.Print.Payload(created.JobId)));
        CountOf(payload, "^DFR:").Should().Be(1, "layout is transmitted once per job (§6.2)");
        CountOf(payload, "^XFR:").Should().Be(5, "one recall per label");
        payload.Should().Contain("^FDRUN-OVERRIDE^FS");
        payload.Should().Contain("^FDLA,https://forms.gle/EXAMPLE^FS", "QR prefix must survive");
        // Carton number differs per label.
        payload.Should().Contain("^FD41^FS").And.Contain("^FD45^FS");
    }

    [Fact]
    public async Task Job_items_are_written_one_per_carton()
    {
        var created = await SubmitAsync(cartonFrom: 100, cartonTo: 109);

        await using var conn = await fx.OpenDbAsync();
        await using var cmd = new MySqlConnector.MySqlCommand(
            "SELECT COUNT(*) FROM print_job_items WHERE job_id = @id", conn);
        cmd.Parameters.AddWithValue("@id", created.JobId);
        Convert.ToInt64(await cmd.ExecuteScalarAsync()).Should().Be(10);
    }

    [Fact]
    public async Task Invalid_carton_range_and_copies_are_rejected()
    {
        var backwards = await _admin.PostAsJsonAsync(ApiRoutes.Print.Jobs, new PrintRequest(
            _productId, _templateId, _printerId, null, null, null, null,
            CartonFrom: 50, CartonTo: 40, LabelCount: 1, CopiesPerLabel: 1, Workstation: "it"));
        backwards.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await backwards.Content.ReadAsStringAsync()).Should().Contain("carton start");

        var badCopies = await _admin.PostAsJsonAsync(ApiRoutes.Print.Jobs, new PrintRequest(
            _productId, _templateId, _printerId, null, null, null, null,
            1, 1, 1, CopiesPerLabel: 0, Workstation: "it"));
        badCopies.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- Carton numbering concurrency (blueprint §11.6) ---------------------------

    [Fact]
    public async Task Twenty_concurrent_allocations_never_duplicate_a_carton_number()
    {
        await SetSettingAsync("Printing:CartonStrategy", "ContinuousPerProduct");
        try
        {
            var tasks = Enumerable.Range(0, 20).Select(_ => _admin.PostAsJsonAsync(
                ApiRoutes.Print.Jobs, new PrintRequest(
                    _productId, _templateId, _printerId, "CONC", null, null, null,
                    null, null, LabelCount: 5, CopiesPerLabel: 1, Workstation: "it")));

            var responses = await Task.WhenAll(tasks);
            responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Accepted);

            var jobs = await Task.WhenAll(responses.Select(r =>
                r.Content.ReadFromJsonAsync<PrintJobCreatedResponse>()));

            var allNumbers = jobs.SelectMany(j =>
                Enumerable.Range((int)j!.CartonFrom, (int)(j.CartonTo - j.CartonFrom + 1))).ToList();

            allNumbers.Should().HaveCount(100);
            allNumbers.Should().OnlyHaveUniqueItems(
                "two operators must never be given the same carton number (§11.2)");
            // Blocks are contiguous overall — no numbers lost or skipped.
            allNumbers.Distinct().Count().Should().Be(allNumbers.Max() - allNumbers.Min() + 1);
        }
        finally
        {
            await SetSettingAsync("Printing:CartonStrategy", "ManualRange");
        }
    }

    // ---- Reprint (§14.2) -------------------------------------------------------------

    [Fact]
    public async Task Reprint_replays_identical_bytes_and_reuses_carton_numbers()
    {
        var original = await SubmitAsync(cartonFrom: 200, cartonTo: 204);
        var originalPayload = await _admin.GetByteArrayAsync(ApiRoutes.Print.Payload(original.JobId));

        var response = await _admin.PostAsJsonAsync(ApiRoutes.Print.Reprint,
            new ReprintRequest(original.JobId, "Label damaged in transit", "it"));
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var reprint = (await response.Content.ReadFromJsonAsync<PrintJobCreatedResponse>())!;

        reprint.CartonFrom.Should().Be(200, "a reprint replaces a damaged label — it is not a new carton");
        reprint.CartonTo.Should().Be(204);

        var reprintPayload = await _admin.GetByteArrayAsync(ApiRoutes.Print.Payload(reprint.JobId));
        reprintPayload.Should().BeEquivalentTo(originalPayload,
            "reprint replays stored bytes; re-rendering would pick up today's template and data");

        var job = (await _admin.GetFromJsonAsync<PrintJobDto>(ApiRoutes.Print.JobById(reprint.JobId)))!;
        job.IsReprint.Should().BeTrue();
        job.SourceJobId.Should().Be(original.JobId);
        job.ReprintReason.Should().Be("Label damaged in transit");
    }

    [Fact]
    public async Task Reprint_reproduces_the_original_even_after_the_product_changes()
    {
        var original = await SubmitAsync(cartonFrom: 300, cartonTo: 301, batch: "ORIGINAL-BATCH");
        var originalPayload = await _admin.GetByteArrayAsync(ApiRoutes.Print.Payload(original.JobId));

        // Change the product master AFTER printing.
        var detail = (await _admin.GetFromJsonAsync<ProductDetail>(ApiRoutes.Products.ById(_productId)))!;
        (await _admin.PutAsJsonAsync(ApiRoutes.Products.ById(_productId), new SaveProductRequest(
            detail.Code, "RENAMED AFTER PRINTING", detail.UomId, "XX", "CHANGED",
            "NEW-BATCH", null, null, null, null, detail.ConcurrencyStamp)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var reprint = (await (await _admin.PostAsJsonAsync(ApiRoutes.Print.Reprint,
            new ReprintRequest(original.JobId, "after product edit", "it")))
            .Content.ReadFromJsonAsync<PrintJobCreatedResponse>())!;

        var reprintPayload = await _admin.GetByteArrayAsync(ApiRoutes.Print.Payload(reprint.JobId));
        reprintPayload.Should().BeEquivalentTo(originalPayload);
        Encoding.UTF8.GetString(reprintPayload).Should().Contain("ORIGINAL-BATCH")
            .And.NotContain("NEW-BATCH", "history records what was printed, not what the product says now");
    }

    [Fact]
    public async Task Reprint_requires_the_reprint_permission()
    {
        var original = await SubmitAsync(cartonFrom: 400, cartonTo: 400);

        var user = await LoginAsync("it-user", ApiFixture.UserPassword);
        (await user.PostAsJsonAsync(ApiRoutes.Print.Reprint,
            new ReprintRequest(original.JobId, "nope", "it")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "Print.Reprint is separate from Print.Execute (A-22)");
    }

    // ---- Dispatch + history ------------------------------------------------------------

    [Fact]
    public async Task Job_reaches_a_terminal_state_and_writes_the_label_file()
    {
        var created = await SubmitAsync(cartonFrom: 500, cartonTo: 502);

        var status = await WaitForTerminalAsync(created.JobId);
        status.Should().Be("Completed", "the File transport always succeeds");

        var job = (await _admin.GetFromJsonAsync<PrintJobDto>(ApiRoutes.Print.JobById(created.JobId)))!;
        job.DispatchedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task History_filters_and_pages_by_keyset()
    {
        await SubmitAsync(cartonFrom: 600, cartonTo: 601);
        await SubmitAsync(cartonFrom: 602, cartonTo: 603);

        var page = (await _admin.GetFromJsonAsync<PagedResult<PrintJobDto>>(
            $"{ApiRoutes.Print.History}?pageSize=1&from={DateTime.UtcNow.AddDays(-1):O}"))!;
        page.Items.Should().HaveCount(1);
        page.HasMore.Should().BeTrue();

        var next = (await _admin.GetFromJsonAsync<PagedResult<PrintJobDto>>(
            $"{ApiRoutes.Print.History}?pageSize=1&from={DateTime.UtcNow.AddDays(-1):O}&cursor={page.NextCursor}"))!;
        next.Items[0].Id.Should().NotBe(page.Items[0].Id);

        var reprintsOnly = (await _admin.GetFromJsonAsync<PagedResult<PrintJobDto>>(
            $"{ApiRoutes.Print.History}?reprintsOnly=true&from={DateTime.UtcNow.AddDays(-1):O}&pageSize=50"))!;
        reprintsOnly.Items.Should().OnlyContain(j => j.IsReprint);
    }

    [Fact]
    public async Task Cancel_only_works_before_dispatch()
    {
        var created = await SubmitAsync(cartonFrom: 700, cartonTo: 700);
        await WaitForTerminalAsync(created.JobId);

        var late = await _admin.PostAsync(ApiRoutes.Print.Cancel(created.JobId), null);
        late.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await late.Content.ReadAsStringAsync()).Should().Contain("PRINT_CANCEL_TOO_LATE");
    }

    // ---- Printer configuration validation -------------------------------------------------

    [Fact]
    public async Task Windows_printers_cannot_be_server_dispatched()
    {
        var response = await _admin.PostAsJsonAsync(ApiRoutes.Printers.Base, new SavePrinterRequest(
            "IT-BAD-WIN", "Bad Windows printer", null, "WindowsRaw", "Server",
            null, null, "Zebra ZT230", null, 203, "Zpl", false, true));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync())
            .Should().Contain("workstation they are installed on",
                "a Windows queue is only reachable from its own PC (§7.3)");
    }

    [Fact]
    public async Task Network_printer_without_host_is_rejected()
    {
        var response = await _admin.PostAsJsonAsync(ApiRoutes.Printers.Base, new SavePrinterRequest(
            "IT-BAD-TCP", "Bad TCP printer", null, "NetworkTcp", "Server",
            null, 9100, null, null, 203, "Zpl", false, true));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unreachable_network_printer_fails_the_job_with_a_clear_message()
    {
        // Port 9 (discard) is closed on the test host, so connect fails fast.
        var printer = await CreatePrinterAsync(
            $"IT-DEAD-{Guid.NewGuid():N}"[..12], "Dead printer", "NetworkTcp", "Server",
            host: "127.0.0.1", port: 9);
        var created = await SubmitAsync(cartonFrom: 800, cartonTo: 800, printerId: printer);

        var status = await WaitForTerminalAsync(created.JobId, timeoutSeconds: 90);
        status.Should().Be("Failed");

        var job = (await _admin.GetFromJsonAsync<PrintJobDto>(ApiRoutes.Print.JobById(created.JobId)))!;
        job.ErrorCode.Should().Be("PRINTER_UNREACHABLE");
        job.ErrorMessage.Should().Contain("not responding");
    }

    // ---- Preview ----------------------------------------------------------------------------

    [Fact]
    public async Task Preview_renders_one_label_with_the_entered_values()
    {
        var response = await _admin.PostAsJsonAsync(ApiRoutes.Print.Preview, new PrintPreviewRequest(
            _productId, _templateId, "PREVIEW-BATCH", new DateOnly(2026, 9, 1),
            new DateOnly(2027, 9, 1), "999[D]", 7, 10));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var zpl = await response.Content.ReadAsStringAsync();
        zpl.Should().Contain("^FDPREVIEW-BATCH^FS");
        zpl.Should().Contain("^FD7^FS");
        CountOf(zpl, "^XFR:").Should().Be(1, "preview is a single label");
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static int CountOf(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private async Task<PrintJobCreatedResponse> SubmitAsync(
        long cartonFrom, long cartonTo, string? batch = "CONE", long? printerId = null)
    {
        var response = await _admin.PostAsJsonAsync(ApiRoutes.Print.Jobs, new PrintRequest(
            _productId, _templateId, printerId ?? _printerId, batch, null, null, null,
            cartonFrom, cartonTo, (int)(cartonTo - cartonFrom + 1), 1, "it"));
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PrintJobCreatedResponse>())!;
    }

    private async Task<string> WaitForTerminalAsync(long jobId, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var job = await _admin.GetFromJsonAsync<PrintJobDto>(ApiRoutes.Print.JobById(jobId));
            if (job!.Status is "Completed" or "Failed" or "Cancelled" or "PartiallyCompleted")
            {
                return job.Status;
            }
            await Task.Delay(250);
        }
        throw new TimeoutException($"Job {jobId} did not reach a terminal state.");
    }

    private async Task SetSettingAsync(string key, string value)
    {
        await using var conn = await fx.OpenDbAsync();
        await using var cmd = new MySqlConnector.MySqlCommand(
            "UPDATE app_settings SET setting_value = @v WHERE setting_key = @k", conn);
        cmd.Parameters.AddWithValue("@v", value);
        cmd.Parameters.AddWithValue("@k", key);
        await cmd.ExecuteNonQueryAsync();
        fx.Factory.Services.GetRequiredService<IMemoryCache>().Remove($"setting:{key}");
    }

    private async Task<long> EnsureProductAsync()
    {
        var existing = await _admin.GetFromJsonAsync<PagedResult<ProductSummary>>(
            $"{ApiRoutes.Products.Base}/?q=IT-PRINT-01");
        if (existing!.Items.FirstOrDefault(p => p.Code == "IT-PRINT-01") is { } found)
        {
            return found.Id;
        }

        var response = await _admin.PostAsJsonAsync(ApiRoutes.Products.Base, new SaveProductRequest(
            "IT-PRINT-01", "5G M2 CAP", null, "M2", "NATURAL",
            "CONE", 750, "750[D]", 750, 10, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IdResponse>())!.Id;
    }

    private async Task<long> EnsureTemplateAsync()
    {
        var templates = await _admin.GetFromJsonAsync<List<TemplateSummary>>(ApiRoutes.Templates.Base);
        if (templates!.FirstOrDefault(t => t.Code == "IT-PRINT-TPL") is { } found)
        {
            return found.Id;
        }

        using var content = new MultipartFormDataContent
        {
            { new StringContent("IT-PRINT-TPL"), "code" },
            { new StringContent("Print test template"), "name" },
            { new StringContent("Zpl"), "templateFormat" },
            { new StringContent("203"), "dpi" },
        };
        var file = new StringContent(TemplateZpl);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", "template.prn");

        var created = await _admin.PostAsync($"{ApiRoutes.Templates.Base}/", content);
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<IdResponse>())!.Id;

        var detail = (await _admin.GetFromJsonAsync<TemplateDetail>(ApiRoutes.Templates.ById(id)))!;
        int IndexOf(string sample) => detail.DetectedFields.First(f => f.SampleValue == sample).CommandIndex;
        var qr = detail.DetectedFields.First(f => f.InferredKind == "QrCode").CommandIndex;

        (await _admin.PutAsJsonAsync(ApiRoutes.Templates.Fields(id), new SaveFieldMappingRequest([
            new(IndexOf("5GCAPM2N"), "1", "Barcode", "Product.BarcodeValue", "Barcode", null, "None", null, "Error", true, null, null),
            new(IndexOf("5G M2 CAP"), "2", "Product", "Product.Description", "Text", null, "None", null, "Error", false, null, null),
            new(IndexOf("CONE"), "3", "Batch", "Effective.Batch", "Text", null, "None", null, "Error", false, null, null),
            new(IndexOf("1"), "4", "Carton", "Carton.Text", "Text", null, "None", null, "Error", false, null, null),
            new(qr, "5", "QR", "Settings.FeedbackFormUrl", "QrCode", null, "None", null, "Error", false, null, null),
        ]))).EnsureSuccessStatusCode();

        (await _admin.PostAsync(ApiRoutes.Templates.Activate(id), null)).EnsureSuccessStatusCode();
        await SetSettingAsync("Label:FeedbackFormUrl", "https://forms.gle/EXAMPLE");
        return id;
    }

    private Task<long> EnsurePrinterAsync() =>
        CreatePrinterAsync("IT-FILE-PRN", "Test file printer", "File", "Server", null, null);

    private async Task<long> CreatePrinterAsync(
        string code, string name, string connectionType, string dispatchMode, string? host, int? port)
    {
        var printers = await _admin.GetFromJsonAsync<List<PrinterDto>>(
            $"{ApiRoutes.Printers.Base}/?activeOnly=false");
        if (printers!.FirstOrDefault(p => p.Code == code) is { } found)
        {
            return found.Id;
        }

        var response = await _admin.PostAsJsonAsync(ApiRoutes.Printers.Base, new SavePrinterRequest(
            code, name, null, connectionType, dispatchMode, host, port, null, null, 203, "Zpl", false, true));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IdResponse>())!.Id;
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
