using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Reports;

namespace BarcodePrinter.Client.Core;

public sealed class ReportsApi(ApiClient api)
{
    public Task<ReportResult> RunAsync(
        string type, DateTime from, DateTime to, string? search,
        string? cursor, int pageSize, CancellationToken ct) =>
        api.GetAsync<ReportResult>($"{ApiRoutes.Reports.Base}?{Query(type, from, to, search)}" +
            $"&pageSize={pageSize}" +
            (string.IsNullOrWhiteSpace(cursor) ? "" : $"&cursor={cursor}"), ct);

    public Task<byte[]?> ExportAsync(
        string type, DateTime from, DateTime to, string? search, CancellationToken ct) =>
        api.GetBytesAsync($"{ApiRoutes.Reports.Export}?{Query(type, from, to, search)}", ct);

    private static string Query(string type, DateTime from, DateTime to, string? search)
    {
        var parts = new List<string>
        {
            $"type={type}",
            $"from={Uri.EscapeDataString(from.ToString("O"))}",
            $"to={Uri.EscapeDataString(to.ToString("O"))}",
        };
        if (!string.IsNullOrWhiteSpace(search))
        {
            parts.Add($"search={Uri.EscapeDataString(search)}");
        }
        return string.Join("&", parts);
    }
}
