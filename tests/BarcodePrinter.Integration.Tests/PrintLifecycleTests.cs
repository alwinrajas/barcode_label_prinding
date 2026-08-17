using System.Net.Http.Headers;
using System.Net.Http.Json;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Auth;
using BarcodePrinter.Contracts.Printing;
using BarcodePrinter.Contracts.Products;
using BarcodePrinter.Contracts.Templates;
using FluentAssertions;
using Xunit;

namespace BarcodePrinter.Integration.Tests;

/// <summary>
/// Covers the redesign's server-side behavior changes: template auto-resolution
/// (§15 — operators never pick a template), the stale-Queued watchdog for
/// client-dispatched jobs, printer status/heartbeat, free-text UOM
/// find-or-create, and honest search truncation.
/// </summary>
[Collection("api")]
public class PrintLifecycleTests(ApiFixture fx) : IAsyncLifetime
{
    private HttpClient _admin = null!;
    private long _productId;
    private long _printerId;

    public async Task InitializeAsync()
    {
        _admin = await LoginAsync("it-admin", ApiFixture.AdminPassword);
        (_productId, _, _printerId) = await PrintScenario.EnsureHistoryAsync(_admin, fx);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- Template auto-resolution -------------------------------------------

    [Fact]
    public async Task Submit_without_template_uses_the_default_template()
    {
        var templates = await _admin.GetFromJsonAsync<List<TemplateSummary>>(ApiRoutes.Templates.Base);
        var expectedDefault = templates!.First(t => t.IsDefault && t.IsActive);

        var response = await _admin.PostAsJsonAsync(ApiRoutes.Print.Jobs, new PrintRequest(
            _productId, TemplateId: null, _printerId, "CONE", null, null, "750[D]",
            5001, 5001, 1, 1, "it-tests"));
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<PrintJobCreatedResponse>())!;

        var job = await _admin.GetFromJsonAsync<PrintJobDto>(ApiRoutes.Print.JobById(created.JobId));
        job!.TemplateCode.Should().Be(expectedDefault.Code,
            "a null TemplateId must resolve to the configured default, not fail");
    }

    [Fact]
    public async Task Preview_without_template_resolves_and_renders()
    {
        var response = await _admin.PostAsJsonAsync(ApiRoutes.Print.Preview, new PrintPreviewRequest(
            _productId, TemplateId: null, "CONE", null, null, "750[D]", 1, 10, _printerId));
        response.EnsureSuccessStatusCode();
        var preview = (await response.Content.ReadFromJsonAsync<PrintPreviewResponse>())!;
        preview.Zpl.Should().NotBeNullOrEmpty("the preview must render against the resolved default template");
    }

    [Fact]
    public async Task Submit_reports_dispatch_mode_so_the_client_can_be_honest()
    {
        var response = await _admin.PostAsJsonAsync(ApiRoutes.Print.Jobs, new PrintRequest(
            _productId, null, _printerId, "CONE", null, null, "750[D]",
            5100, 5100, 1, 1, "it-tests"));
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<PrintJobCreatedResponse>())!;
        created.DispatchMode.Should().Be("Server", "IT-FILE-PRN is a server-dispatched printer");
    }

    // ---- Client-dispatch heartbeat + watchdog -------------------------------

    [Fact]
    public async Task Printer_status_follows_the_workstation_heartbeat()
    {
        var printerId = await EnsureClientPrinterAsync("IT-WS-STATUS");

        var before = await _admin.GetFromJsonAsync<PrinterStatusDto>(ApiRoutes.Printers.Status(printerId));
        before!.Online.Should().BeFalse("the workstation has not polled yet");
        before.Detail.Should().Contain("IT-WS-STATUS",
            "the operator must be told WHICH workstation should be running the app");

        // The workstation's pending-jobs poll doubles as its heartbeat.
        (await _admin.GetAsync($"{ApiRoutes.Print.Pending}?workstation=IT-WS-STATUS"))
            .EnsureSuccessStatusCode();

        var after = await _admin.GetFromJsonAsync<PrinterStatusDto>(ApiRoutes.Printers.Status(printerId));
        after!.Online.Should().BeTrue("the workstation just polled");
        after.LastSeenUtc.Should().NotBeNull();

        var listed = await _admin.GetFromJsonAsync<List<PrinterDto>>(
            $"{ApiRoutes.Printers.Base}/?activeOnly=false");
        listed!.First(p => p.Id == printerId).LastSeenUtc.Should().NotBeNull(
            "the printers grid shows last-seen");
    }

    [Fact]
    public async Task Uncollected_client_job_is_failed_with_an_actionable_message()
    {
        var printerId = await EnsureClientPrinterAsync("IT-WS-GHOST");

        var response = await _admin.PostAsJsonAsync(ApiRoutes.Print.Jobs, new PrintRequest(
            _productId, null, printerId, "CONE", null, null, "750[D]",
            5200, 5200, 1, 1, "it-tests"));
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<PrintJobCreatedResponse>())!;
        created.DispatchMode.Should().Be("Client");
        created.OwnerWorkstation.Should().Be("IT-WS-GHOST");

        // Age the job past the Queued timeout, then wait for a watchdog pass
        // (30 s cadence). The workstation never polls, so nothing collects it.
        await using (var conn = await fx.OpenDbAsync())
        {
            await using var cmd = new MySqlConnector.MySqlCommand(
                "UPDATE print_jobs SET requested_at = requested_at - INTERVAL 10 MINUTE WHERE id = @id", conn);
            cmd.Parameters.AddWithValue("id", created.JobId);
            (await cmd.ExecuteNonQueryAsync()).Should().Be(1);
        }

        PrintJobDto? job = null;
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            job = await _admin.GetFromJsonAsync<PrintJobDto>(ApiRoutes.Print.JobById(created.JobId));
            if (job!.Status == "Failed")
            {
                break;
            }
            await Task.Delay(1000);
        }

        job!.Status.Should().Be("Failed", "a job must never sit in Queued forever");
        job.ErrorCode.Should().Be("WORKSTATION_UNAVAILABLE");
        job.ErrorMessage.Should().Contain("IT-WS-GHOST",
            "the message names the workstation the operator should check");
    }

    [Fact]
    public async Task Sending_label_commands_to_a_windows_language_printer_is_refused()
    {
        // An office printer mis-configured as a raw-spool device would print
        // pages of "^XA^FO40,40…" instead of labels. printers.language records
        // what the device actually speaks, so the mismatch is named up front.
        var response = await _admin.PostAsJsonAsync(ApiRoutes.Printers.Base, new SavePrinterRequest(
            "IT-LANG-MIX", "Mis-configured office printer", null, "WindowsRaw", "Client",
            null, null, "IT Office Printer", "IT-WS-LANG", 203, "Windows", false, true));
        response.EnsureSuccessStatusCode();
        var printerId = (await response.Content.ReadFromJsonAsync<IdResponse>())!.Id;

        var submit = await _admin.PostAsJsonAsync(ApiRoutes.Print.Jobs, new PrintRequest(
            _productId, null, printerId, "CONE", null, null, "750[D]",
            5300, 5300, 1, 1, "it-tests"));

        submit.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        var problem = await submit.Content.ReadFromJsonAsync<ProblemBody>();
        problem!.Code.Should().Be("PRINTER_LANGUAGE_MISMATCH");
        problem.Detail.Should().Contain("administrator",
            "the message must tell the operator who can fix the configuration");
    }

    // ---- Free-text UOM ------------------------------------------------------

    [Fact]
    public async Task Free_text_uom_is_created_once_and_reused_case_insensitively()
    {
        var first = await CreateProductAsync("IT-UOM-A1", uomCode: "Itx9");
        var second = await CreateProductAsync("IT-UOM-A2", uomCode: "ITX9");

        var detailA = await _admin.GetFromJsonAsync<ProductDetail>(ApiRoutes.Products.ById(first));
        var detailB = await _admin.GetFromJsonAsync<ProductDetail>(ApiRoutes.Products.ById(second));

        detailA!.Uom.Should().Be("ITX9", "typed UOM codes are normalised to uppercase");
        detailB!.UomId.Should().Be(detailA.UomId, "the same code must reuse the same UOM row");

        var uoms = await _admin.GetFromJsonAsync<List<UomDto>>(ApiRoutes.Products.Uoms);
        uoms!.Should().ContainSingle(u => u.Code == "ITX9");
    }

    [Fact]
    public async Task Explicit_uom_id_wins_over_a_typed_code()
    {
        var uoms = await _admin.GetFromJsonAsync<List<UomDto>>(ApiRoutes.Products.Uoms);
        var pcs = uoms!.First(u => u.Code == "PCS");

        var id = await CreateProductAsync("IT-UOM-A3", uomCode: "IGNORED", uomId: pcs.Id);
        var detail = await _admin.GetFromJsonAsync<ProductDetail>(ApiRoutes.Products.ById(id));
        detail!.UomId.Should().Be(pcs.Id);
    }

    // ---- Honest search truncation -------------------------------------------

    [Fact]
    public async Task Truncated_search_reports_more_matches_without_a_cursor()
    {
        for (var i = 0; i < 55; i++)
        {
            await CreateProductAsync($"ITTRUNC{i:000}", descriptionSuffix: $" {i}", allowExisting: true);
        }

        var result = await _admin.GetFromJsonAsync<PagedResult<ProductSummary>>(
            $"{ApiRoutes.Products.Base}/?q=ITTRUNC&pageSize=50");

        result!.Items.Should().HaveCount(50, "search is capped at 50 relevance-ranked rows");
        result.HasMore.Should().BeTrue("55 products match — the client must know the list is truncated");
        result.NextCursor.Should().BeNull("relevance-ordered search cannot keyset-page");
    }

    // ---- helpers ------------------------------------------------------------

    private async Task<long> EnsureClientPrinterAsync(string workstation)
    {
        var code = $"IT-CLI-{workstation[^6..]}";
        var printers = await _admin.GetFromJsonAsync<List<PrinterDto>>(
            $"{ApiRoutes.Printers.Base}/?activeOnly=false");
        if (printers!.FirstOrDefault(p => p.Code == code) is { } existing)
        {
            return existing.Id;
        }
        var response = await _admin.PostAsJsonAsync(ApiRoutes.Printers.Base, new SavePrinterRequest(
            code, $"Client printer {workstation}", null, "WindowsRaw", "Client",
            null, null, "IT Test Printer", workstation, 203, "Zpl", false, true));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IdResponse>())!.Id;
    }

    private async Task<long> CreateProductAsync(
        string code, string? uomCode = null, long? uomId = null,
        string descriptionSuffix = "", bool allowExisting = false)
    {
        if (allowExisting)
        {
            var found = await _admin.GetFromJsonAsync<PagedResult<ProductSummary>>(
                $"{ApiRoutes.Products.Base}/?q={code}");
            if (found!.Items.FirstOrDefault(p => p.Code == code) is { } existing)
            {
                return existing.Id;
            }
        }
        var response = await _admin.PostAsJsonAsync(ApiRoutes.Products.Base, new SaveProductRequest(
            code, $"Lifecycle test product{descriptionSuffix}", uomId, "M2", "NATURAL",
            "CONE", 1, "1[D]", 1, 1, null, uomCode));
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

    /// <summary>The ProblemDetails envelope the API middleware emits.</summary>
    private sealed record ProblemBody(string? Code, string? Detail, string? CorrelationId);
}
