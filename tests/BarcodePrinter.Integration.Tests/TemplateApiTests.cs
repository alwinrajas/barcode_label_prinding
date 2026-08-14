using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Auth;
using BarcodePrinter.Contracts.Templates;
using FluentAssertions;
using Xunit;

namespace BarcodePrinter.Integration.Tests;

/// <summary>
/// Drives the real admin flow a technician will follow when the client's
/// template arrives: upload → inspect → map → activate → preview.
/// Uses the synthetic capture from Labels.Tests/Fixtures (blocker BQ-2).
/// </summary>
[Collection("api")]
public class TemplateApiTests(ApiFixture fx) : IAsyncLifetime
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

    private const string Fixture = """
        ^XA
        ^CI28
        ^PW812
        ^LL0609
        ^FO232,16^BY2,3.0,56^BCN,56,Y,N,N^FD5GCAPM2N^FS
        ^FO264,112^A0N,26,26^FDProduct^FS
        ^FO452,112^A0N,26,26^FD5G M2 CAP^FS
        ^FO264,220^A0N,26,26^FDBatch^FS
        ^FO452,220^A0N,26,26^FDCONE^FS
        ^FO452,392^A0N,26,26^FD1^FS
        ^FO660,470^BQN,2,5^FDLA,https://forms.gle/EXAMPLE^FS
        ^PQ1
        ^XZ
        """;

    [Fact]
    public async Task Full_onboarding_flow_register_map_activate_preview()
    {
        var id = await RegisterAsync("IT-TPL-FLOW", "Standard carton label");

        // 1. The upload is inspected and its fields offered for mapping.
        var detail = (await _admin.GetFromJsonAsync<TemplateDetail>(ApiRoutes.Templates.ById(id)))!;
        detail.CurrentVersion.Should().Be(1);
        detail.IsActive.Should().BeFalse("a template is inactive until its fields are mapped");
        detail.DetectedFields.Should().NotBeEmpty();
        detail.DetectedFields.Should().Contain(f => f.InferredKind == "Barcode" && f.SampleValue == "5GCAPM2N");
        detail.DetectedFields.Should().Contain(f => f.InferredKind == "QrCode");

        // 2. Activation is refused while nothing is mapped.
        var premature = await _admin.PostAsync(ApiRoutes.Templates.Activate(id), null);
        premature.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await premature.Content.ReadAsStringAsync()).Should().Contain("TEMPLATE_NOT_MAPPED");

        // 3. Map the variable fields; captions stay unmapped and literal.
        (await MapDefaultFieldsAsync(id, detail)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // 4. Now it activates, and can be made the default.
        (await _admin.PostAsync(ApiRoutes.Templates.Activate(id), null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _admin.PostAsync(ApiRoutes.Templates.SetDefault(id), null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var activated = (await _admin.GetFromJsonAsync<TemplateDetail>(ApiRoutes.Templates.ById(id)))!;
        activated.IsActive.Should().BeTrue();
        activated.IsDefault.Should().BeTrue();
        activated.Fields.Should().HaveCount(5);

        // 5. Preview renders real ZPL with the client's geometry and our values.
        var preview = await _admin.GetStringAsync(ApiRoutes.Templates.PreviewZpl(id));
        preview.Should().Contain("^DFR:IT-TPL-FLOW.ZPL");
        preview.Should().Contain("^PW812");            // client geometry preserved
        preview.Should().Contain("^FDProduct^FS");     // caption still literal
        preview.Should().Contain("^XFR:IT-TPL-FLOW.ZPL");
        preview.Should().Contain("^FD5GCAPM2N^FS");    // bound sample value
        preview.Should().Contain("^FDLA,https://forms.gle/EXAMPLE^FS",
            "the QR mode prefix must survive registration and rendering");
    }

    [Fact]
    public async Task Re_uploading_the_same_code_creates_a_new_immutable_version()
    {
        var id = await RegisterAsync("IT-TPL-VER", "Versioned label");
        var v1 = (await _admin.GetFromJsonAsync<TemplateDetail>(ApiRoutes.Templates.ById(id)))!;

        var changed = Fixture.Replace("^PW812", "^PW820");
        var id2 = await RegisterAsync("IT-TPL-VER", "Versioned label", changed);
        id2.Should().Be(id, "the same code updates the same template");

        var v2 = (await _admin.GetFromJsonAsync<TemplateDetail>(ApiRoutes.Templates.ById(id)))!;
        v2.CurrentVersion.Should().Be(2);
        v2.ArtifactHash.Should().NotBe(v1.ArtifactHash);
        v2.VersionId.Should().NotBe(v1.VersionId,
            "prior versions stay intact so a reprint reproduces the layout it used");
    }

    /// <summary>A-14 enforced at the API boundary, not only in the engine.</summary>
    [Fact]
    public async Task Mapping_product_data_into_a_qr_field_is_rejected()
    {
        var id = await RegisterAsync("IT-TPL-QR", "QR guard");
        var detail = (await _admin.GetFromJsonAsync<TemplateDetail>(ApiRoutes.Templates.ById(id)))!;
        var qr = detail.DetectedFields.First(f => f.InferredKind == "QrCode");

        var response = await _admin.PutAsJsonAsync(ApiRoutes.Templates.Fields(id),
            new SaveFieldMappingRequest([
                new FieldMappingInput(qr.CommandIndex, "1", "QR", "Product.Code", "QrCode",
                    null, "None", null, "Error", false, null, null),
            ]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync())
            .Should().Contain("static feedback URL");
    }

    [Fact]
    public async Task Unreadable_upload_is_rejected_at_registration_not_at_print_time()
    {
        using var content = BuildForm("IT-TPL-BAD", "Not a label", "this file contains no ZPL at all");
        var response = await _admin.PostAsync($"{ApiRoutes.Templates.Base}/", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("TEMPLATE_NO_FIELDS");
    }

    [Fact]
    public async Task Vocabulary_lists_only_valid_keys_and_names_the_qr_restriction()
    {
        var vocabulary = (await _admin.GetFromJsonAsync<TemplateVocabularyDto>(
            ApiRoutes.Templates.Vocabulary))!;

        vocabulary.DataKeys.Should().Contain("Effective.Batch");
        vocabulary.DataKeys.Should().Contain("Carton.Text");
        vocabulary.QrOnlyKey.Should().Be("Settings.FeedbackFormUrl");
        vocabulary.Symbologies.Should().Contain("Code128").And.Contain("Ean13");
    }

    [Fact]
    public async Task Managing_templates_requires_the_template_permission()
    {
        var login = await fx.CreateClient().PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest("it-manager", ApiFixture.ManagerPassword, "it-tests"));
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        var manager = fx.CreateClient();
        manager.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var content = BuildForm("IT-TPL-RBAC", "Should fail", Fixture);
        (await manager.PostAsync($"{ApiRoutes.Templates.Base}/", content))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "Settings.ManageTemplates is an Admin-only permission");
    }

    // ---- helpers ---------------------------------------------------------------

    private async Task<long> RegisterAsync(string code, string name, string? artifact = null)
    {
        using var content = BuildForm(code, name, artifact ?? Fixture);
        var response = await _admin.PostAsync($"{ApiRoutes.Templates.Base}/", content);
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CreatedResponse>())!.Id;
    }

    private static MultipartFormDataContent BuildForm(string code, string name, string artifact)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(code), "code" },
            { new StringContent(name), "name" },
            { new StringContent("Zpl"), "templateFormat" },
            // Dimensions are C-4 (TBD) — the fixture's own values, recorded as
            // metadata so nothing is hardcoded in C#.
            { new StringContent("101.6"), "widthMm" },
            { new StringContent("76.2"), "heightMm" },
            { new StringContent("203"), "dpi" },
        };
        var file = new StringContent(artifact);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", "captured-label.prn");
        return content;
    }

    private Task<HttpResponseMessage> MapDefaultFieldsAsync(long id, TemplateDetail detail)
    {
        int IndexOf(string sample) =>
            detail.DetectedFields.First(f => f.SampleValue == sample).CommandIndex;
        var qrIndex = detail.DetectedFields.First(f => f.InferredKind == "QrCode").CommandIndex;

        return _admin.PutAsJsonAsync(ApiRoutes.Templates.Fields(id),
            new SaveFieldMappingRequest([
                new(IndexOf("5GCAPM2N"), "1", "Barcode", "Product.BarcodeValue", "Barcode",
                    null, "None", null, "Error", true, null, null),
                new(IndexOf("5G M2 CAP"), "2", "Product", "Product.Description", "Text",
                    null, "None", null, "Error", false, null, null),
                new(IndexOf("CONE"), "3", "Batch", "Effective.Batch", "Text",
                    null, "None", null, "Error", false, null, null),
                new(IndexOf("1"), "4", "Carton", "Carton.Text", "Text",
                    null, "None", null, "Error", false, null, null),
                new(qrIndex, "5", "Feedback QR", "Settings.FeedbackFormUrl", "QrCode",
                    null, "None", null, "Error", false, null, null),
            ]));
    }

    private sealed record CreatedResponse(long Id);
}
