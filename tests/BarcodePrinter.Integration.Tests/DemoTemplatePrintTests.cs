using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Auth;
using BarcodePrinter.Contracts.Printing;
using BarcodePrinter.Contracts.Products;
using BarcodePrinter.Contracts.Templates;
using BarcodePrinter.Printing.Abstractions;
using FluentAssertions;
using MySqlConnector;
using Xunit;

namespace BarcodePrinter.Integration.Tests;

/// <summary>
/// The seeded DEMO template must carry a fresh installation all the way from
/// product to printed label without anyone registering anything first. This is
/// the flow the client will be shown, and the one that must keep working when
/// their real template replaces the demo.
/// </summary>
[Collection("api")]
public class DemoTemplatePrintTests(ApiFixture fx) : IAsyncLifetime
{
    private HttpClient _admin = null!;
    private long _productId;
    private long _demoTemplateId;
    private long _filePrinterId;

    public async Task InitializeAsync()
    {
        _admin = await LoginAsync("it-admin", ApiFixture.AdminPassword);

        var templates = await _admin.GetFromJsonAsync<List<TemplateSummary>>(ApiRoutes.Templates.Base);
        _demoTemplateId = templates!.Single(t => t.Code == "DEMO-CARTON").Id;

        (_productId, _, _filePrinterId) = await PrintScenario.EnsureHistoryAsync(_admin, fx);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- the template is there, and is honest about what it is ----------------------

    [Fact]
    public async Task A_fresh_installation_has_a_usable_template_and_says_it_is_a_demo()
    {
        var templates = (await _admin.GetFromJsonAsync<List<TemplateSummary>>(ApiRoutes.Templates.Base))!;
        var demo = templates.Single(t => t.Code == "DEMO-CARTON");

        demo.IsActive.Should().BeTrue("nothing can be printed without an active template");
        demo.Name.Should().Contain("DEMO", "an operator must never mistake it for the client's label");

        var detail = (await _admin.GetFromJsonAsync<TemplateDetail>(
            ApiRoutes.Templates.ById(_demoTemplateId)))!;
        detail.TemplateFormat.Should().Be("Native");
        detail.Description.Should().Contain("Replace with the client");
    }

    /// <summary>Geometry, fonts and positions live in the definition, not in
    /// code. If this is not JSON we can read and change, the "configurable"
    /// claim is not true.</summary>
    [Fact]
    public async Task The_layout_is_stored_as_editable_configuration()
    {
        await using var conn = await fx.OpenDbAsync();
        await using var cmd = new MySqlCommand(
            """
            SELECT v.artifact_blob FROM label_template_versions v
            JOIN label_templates t ON t.id = v.template_id
            WHERE t.code = 'DEMO-CARTON' AND v.version = t.current_version
            """, conn);
        var json = Encoding.UTF8.GetString((byte[])(await cmd.ExecuteScalarAsync())!);

        var definition = BarcodePrinter.Labels.Native.LabelDefinition.Parse(json);
        definition.WidthMm.Should().Be(100m);
        definition.HeightMm.Should().Be(50m);
        definition.Dpi.Should().Be(203);
        definition.Elements.Should().Contain(e => e.Id == "barcode");
        definition.Elements.Should().Contain(e => e.Id == "feedbackQr");
        definition.Elements.Should().Contain(e => e.Id == "productImage");
        definition.Elements.Should().Contain(e => e.Id == "carton");
    }

    // ---- preview --------------------------------------------------------------------

    [Fact]
    public async Task Preview_returns_a_picture_and_creates_no_print_transaction()
    {
        var before = await CountJobsAsync();

        var response = await _admin.PostAsJsonAsync(ApiRoutes.Print.Preview, new PrintPreviewRequest(
            _productId, _demoTemplateId, "CONE", null, null, "750[D]", 7, 20));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var preview = (await response.Content.ReadFromJsonAsync<PrintPreviewResponse>())!;
        preview.Unavailable.Should().BeNull();
        preview.Format.Should().Be("Native");
        preview.PngBase64.Should().NotBeNullOrEmpty();

        var png = Convert.FromBase64String(preview.PngBase64!);
        png.Should().StartWith([0x89, 0x50, 0x4E, 0x47], "that is the PNG signature");
        png.Length.Should().BeGreaterThan(1_000, "a blank image would mean nothing was drawn");

        // The run values must reach the preview, or it is checking nothing.
        preview.Zpl.Should().Contain("CONE");
        preview.Zpl.Should().Contain("750[D]");

        (await CountJobsAsync()).Should().Be(before,
            "a preview must never allocate carton numbers or create a job");
    }

    [Fact]
    public async Task Preview_never_shows_a_job_number_that_was_not_issued()
    {
        var preview = await PreviewAsync();
        preview.Zpl.Should().NotContain("PJ-", "no job exists, so no job number may be shown");
    }

    // ---- print ----------------------------------------------------------------------

    [Fact]
    public async Task The_demo_template_prints_a_real_job_end_to_end()
    {
        var response = await _admin.PostAsJsonAsync(ApiRoutes.Print.Jobs, new PrintRequest(
            _productId, _demoTemplateId, _filePrinterId, "CONE", null, null, "750[D]",
            9100, 9104, 5, 1, "it-demo"));
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<PrintJobCreatedResponse>())!;

        var payload = await LoadPayloadAsync(created.JobId);
        var zpl = Encoding.UTF8.GetString(payload.Data);

        payload.Format.Should().Be("Zpl");

        // Layout once, then one recall per carton (§6.2) — this is the property
        // that keeps a 500-carton run off the network's critical path.
        zpl.Should().Contain("^DFR:DEMO-CARTON.ZPL^FS");
        System.Text.RegularExpressions.Regex.Matches(zpl, @"\^DF").Should().HaveCount(1);
        System.Text.RegularExpressions.Regex.Matches(zpl, @"\^XFR:DEMO-CARTON\.ZPL")
            .Should().HaveCount(5, "one recall per carton");

        // Every carton number in the range appears exactly once.
        foreach (var carton in Enumerable.Range(9100, 5))
        {
            zpl.Should().Contain($"^FD{carton}^FS");
        }

        zpl.Should().Contain("CONE");

        // The define block holds placeholders by design; every RECALL must carry
        // resolved data, or the printer would emit blank fields.
        var recalls = zpl.Split("^XFR:DEMO-CARTON.ZPL^FS").Skip(1).ToList();
        recalls.Should().HaveCount(5);
        recalls.Should().OnlyContain(r => r.Contains("^FD"), "each label must carry its own values");
        recalls.Should().OnlyContain(r => !r.Contains("^FN1^FS"), "no field may be left unbound");
    }

    /// <summary>The QR mode indicator is invisible in a ZPL dump and fatal on
    /// media: without it the symbol prints and will not scan.</summary>
    [Fact]
    public async Task A_printed_label_carries_a_scannable_qr()
    {
        var configuredUrl = await FeedbackUrlAsync();
        configuredUrl.Should().NotBeNullOrWhiteSpace("the QR has nothing to encode otherwise");

        var response = await _admin.PostAsJsonAsync(ApiRoutes.Print.Jobs, new PrintRequest(
            _productId, _demoTemplateId, _filePrinterId, "CONE", null, null, null,
            9200, 9200, 1, 1, "it-demo"));
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<PrintJobCreatedResponse>())!;

        var zpl = Encoding.UTF8.GetString((await LoadPayloadAsync(created.JobId)).Data);
        zpl.Should().Contain("^BQN,2,3", "the QR is sized by magnification");
        zpl.Should().Contain($"^FDMA,{configuredUrl}^FS",
            "the mode indicator travels in the field data");
    }

    // ---- office printers -------------------------------------------------------------

    /// <summary>A laser or inkjet cannot interpret ZPL, so the same job is
    /// rendered to pictures. Everything else about it is unchanged.</summary>
    [Fact]
    public async Task A_job_for_a_standard_windows_printer_is_rendered_as_pictures()
    {
        var printerId = await EnsureWindowsGraphicsPrinterAsync();

        var response = await _admin.PostAsJsonAsync(ApiRoutes.Print.Jobs, new PrintRequest(
            _productId, _demoTemplateId, printerId, "CONE", null, null, "750[D]",
            9300, 9302, 3, 2, "it-demo"));
        response.StatusCode.Should().Be(HttpStatusCode.Accepted,
            await response.Content.ReadAsStringAsync());
        var created = (await response.Content.ReadFromJsonAsync<PrintJobCreatedResponse>())!;

        var payload = await LoadPayloadAsync(created.JobId);
        payload.Format.Should().Be("Raster");

        var pages = RasterLabelPayload.Unpack(payload.Data);
        pages.Should().HaveCount(6, "three cartons at two copies each is six pages");
        pages.Should().OnlyContain(p => p.Length > 1_000);
        pages[0].Should().StartWith([0x89, 0x50, 0x4E, 0x47]);
    }

    private async Task<long> EnsureWindowsGraphicsPrinterAsync()
    {
        var printers = await _admin.GetFromJsonAsync<List<PrinterDto>>(
            $"{ApiRoutes.Printers.Base}/?activeOnly=false");
        if (printers!.FirstOrDefault(p => p.Code == "IT-OFFICE") is { } existing)
        {
            return existing.Id;
        }

        var created = await _admin.PostAsJsonAsync(ApiRoutes.Printers.Base, new SavePrinterRequest(
            "IT-OFFICE", "Office laser", null, "WindowsGraphics", "Client",
            null, null, "Microsoft Print to PDF", "it-office-ws", 300, "Windows", false, true));
        created.StatusCode.Should().Be(HttpStatusCode.Created,
            await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<IdResponse>())!.Id;
    }

    // ---- helpers ---------------------------------------------------------------------

    private async Task<PrintPreviewResponse> PreviewAsync()
    {
        var response = await _admin.PostAsJsonAsync(ApiRoutes.Print.Preview, new PrintPreviewRequest(
            _productId, _demoTemplateId, "CONE", null, null, "750[D]", 1, 1));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PrintPreviewResponse>())!;
    }

    private async Task<(string Format, byte[] Data)> LoadPayloadAsync(long jobId)
    {
        await using var conn = await fx.OpenDbAsync();
        await using var cmd = new MySqlCommand(
            "SELECT format, payload FROM print_job_payloads WHERE job_id = @jobId", conn);
        cmd.Parameters.AddWithValue("@jobId", jobId);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("the job must have stored its payload");
        return (reader.GetString(0), (byte[])reader.GetValue(1));
    }

    private async Task<long> CountJobsAsync()
    {
        await using var conn = await fx.OpenDbAsync();
        await using var cmd = new MySqlCommand("SELECT COUNT(*) FROM print_jobs", conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<string?> FeedbackUrlAsync()
    {
        await using var conn = await fx.OpenDbAsync();
        await using var cmd = new MySqlCommand(
            "SELECT setting_value FROM app_settings WHERE setting_key = 'Label:FeedbackFormUrl'", conn);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private async Task<HttpClient> LoginAsync(string username, string password)
    {
        var client = fx.CreateClient();
        var response = await client.PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest(username, password, "it-tests"));
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return client;
    }

    private sealed record IdResponse(long Id);
}
