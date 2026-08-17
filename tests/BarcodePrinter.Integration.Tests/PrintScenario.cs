using System.Net.Http.Headers;
using System.Net.Http.Json;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Printing;
using BarcodePrinter.Contracts.Products;
using BarcodePrinter.Contracts.Templates;

namespace BarcodePrinter.Integration.Tests;

/// <summary>
/// Creates the product + template + printer + print history a suite needs, so
/// tests do not depend on another suite having run first. Every step is
/// idempotent, so repeated calls across suites are cheap.
/// </summary>
internal static class PrintScenario
{
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

    /// <summary>Ensures at least <paramref name="minimumJobs"/> print jobs plus
    /// one reprint exist, and returns the ids used.</summary>
    public static async Task<(long ProductId, long TemplateId, long PrinterId)> EnsureHistoryAsync(
        HttpClient admin, ApiFixture fx, int minimumJobs = 3)
    {
        var productId = await EnsureProductAsync(admin);
        var templateId = await EnsureTemplateAsync(admin, fx);
        var printerId = await EnsurePrinterAsync(admin);

        var existing = await admin.GetFromJsonAsync<PagedResult<PrintJobDto>>(
            $"{ApiRoutes.Print.History}?pageSize=50&from={Uri.EscapeDataString(DateTime.UtcNow.AddDays(-1).ToString("O"))}");

        if (existing!.Items.Count >= minimumJobs && existing.Items.Any(j => j.IsReprint))
        {
            return (productId, templateId, printerId);
        }

        long lastJobId = 0;
        for (var i = 0; i < minimumJobs; i++)
        {
            var response = await admin.PostAsJsonAsync(ApiRoutes.Print.Jobs, new PrintRequest(
                productId, templateId, printerId, "CONE", null, null, "750[D]",
                900 + (i * 5), 904 + (i * 5), 5, 1, "it-scenario"));
            response.EnsureSuccessStatusCode();
            lastJobId = (await response.Content.ReadFromJsonAsync<PrintJobCreatedResponse>())!.JobId;
        }

        if (!existing.Items.Any(j => j.IsReprint) && lastJobId > 0)
        {
            await admin.PostAsJsonAsync(ApiRoutes.Print.Reprint,
                new ReprintRequest(lastJobId, "scenario reprint", "it-scenario"));
        }

        return (productId, templateId, printerId);
    }

    private static async Task<long> EnsureProductAsync(HttpClient admin)
    {
        var found = await admin.GetFromJsonAsync<PagedResult<ProductSummary>>(
            $"{ApiRoutes.Products.Base}/?q=IT-PRINT-01");
        if (found!.Items.FirstOrDefault(p => p.Code == "IT-PRINT-01") is { } product)
        {
            return product.Id;
        }

        var response = await admin.PostAsJsonAsync(ApiRoutes.Products.Base, new SaveProductRequest(
            "IT-PRINT-01", "5G M2 CAP", null, "M2", "NATURAL",
            "CONE", 750, "750[D]", 750, 10, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IdResponse>())!.Id;
    }

    private static async Task<long> EnsureTemplateAsync(HttpClient admin, ApiFixture fx)
    {
        var templates = await admin.GetFromJsonAsync<List<TemplateSummary>>(ApiRoutes.Templates.Base);
        if (templates!.FirstOrDefault(t => t.Code == "IT-PRINT-TPL") is { } existing)
        {
            return existing.Id;
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

        var created = await admin.PostAsync($"{ApiRoutes.Templates.Base}/", content);
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<IdResponse>())!.Id;

        var detail = (await admin.GetFromJsonAsync<TemplateDetail>(ApiRoutes.Templates.ById(id)))!;
        int IndexOf(string sample) => detail.DetectedFields.First(f => f.SampleValue == sample).CommandIndex;
        var qr = detail.DetectedFields.First(f => f.InferredKind == "QrCode").CommandIndex;

        (await admin.PutAsJsonAsync(ApiRoutes.Templates.Fields(id), new SaveFieldMappingRequest([
            new(IndexOf("5GCAPM2N"), "1", "Barcode", "Product.BarcodeValue", "Barcode", null, "None", null, "Error", true, null, null),
            new(IndexOf("5G M2 CAP"), "2", "Product", "Product.Description", "Text", null, "None", null, "Error", false, null, null),
            new(IndexOf("CONE"), "3", "Batch", "Effective.Batch", "Text", null, "None", null, "Error", false, null, null),
            new(IndexOf("1"), "4", "Carton", "Carton.Text", "Text", null, "None", null, "Error", false, null, null),
            new(qr, "5", "QR", "Settings.FeedbackFormUrl", "QrCode", null, "None", null, "Error", false, null, null),
        ]))).EnsureSuccessStatusCode();

        (await admin.PostAsync(ApiRoutes.Templates.Activate(id), null)).EnsureSuccessStatusCode();

        await using var conn = await fx.OpenDbAsync();
        await using var cmd = new MySqlConnector.MySqlCommand(
            "UPDATE app_settings SET setting_value = 'https://forms.gle/EXAMPLE' WHERE setting_key = 'Label:FeedbackFormUrl'",
            conn);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task<long> EnsurePrinterAsync(HttpClient admin)
    {
        var printers = await admin.GetFromJsonAsync<List<PrinterDto>>(
            $"{ApiRoutes.Printers.Base}/?activeOnly=false");
        if (printers!.FirstOrDefault(p => p.Code == "IT-FILE-PRN") is { } existing)
        {
            return existing.Id;
        }

        var response = await admin.PostAsJsonAsync(ApiRoutes.Printers.Base, new SavePrinterRequest(
            "IT-FILE-PRN", "Test file printer", null, "File", "Server",
            null, null, null, null, 203, "Zpl", false, true));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IdResponse>())!.Id;
    }

    private sealed record IdResponse(long Id);
}
