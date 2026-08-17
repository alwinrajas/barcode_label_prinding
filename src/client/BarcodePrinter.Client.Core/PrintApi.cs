using BarcodePrinter.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using BarcodePrinter.Contracts.Printing;
using BarcodePrinter.Contracts.Products;
using BarcodePrinter.Contracts.Templates;

namespace BarcodePrinter.Client.Core;

public sealed class PrintApi(ApiClient api)
{
    public Task<IReadOnlyList<PrinterDto>> ListPrintersAsync(bool activeOnly, CancellationToken ct) =>
        api.GetAsync<IReadOnlyList<PrinterDto>>(
            $"{ApiRoutes.Printers.Base}/?activeOnly={activeOnly.ToString().ToLowerInvariant()}", ct);

    public Task<IReadOnlyList<TemplateSummary>> ListTemplatesAsync(CancellationToken ct) =>
        api.GetAsync<IReadOnlyList<TemplateSummary>>(ApiRoutes.Templates.Base, ct);

    public Task<PrintJobCreatedResponse> SubmitAsync(PrintRequest request, CancellationToken ct) =>
        api.PostAsync<PrintRequest, PrintJobCreatedResponse>(ApiRoutes.Print.Jobs, request, ct);

    public Task<PrintJobCreatedResponse> ReprintAsync(ReprintRequest request, CancellationToken ct) =>
        api.PostAsync<ReprintRequest, PrintJobCreatedResponse>(ApiRoutes.Print.Reprint, request, ct);

    public Task<PrintJobDto> GetJobAsync(long id, CancellationToken ct) =>
        api.GetAsync<PrintJobDto>(ApiRoutes.Print.JobById(id), ct);

    public Task CancelAsync(long id, CancellationToken ct) =>
        api.PostAsync(ApiRoutes.Print.Cancel(id), ct);

    /// <summary>Preview never creates a print transaction; it is safe to call
    /// on every keystroke (debounced).</summary>
    public Task<PrintPreviewResponse> PreviewAsync(PrintPreviewRequest request, CancellationToken ct) =>
        api.PostAsync<PrintPreviewRequest, PrintPreviewResponse>(ApiRoutes.Print.Preview, request, ct);

    private Task<string> PreviewZplAsync(PrintPreviewRequest request, CancellationToken ct) =>
        api.PostForTextAsync(ApiRoutes.Print.Preview, request, ct);

    public Task<PagedResult<PrintJobDto>> HistoryAsync(
        DateTime? from, DateTime? to, string? status, bool reprintsOnly,
        string? search, string? cursor, int pageSize, CancellationToken ct)
    {
        var query = new List<string> { $"pageSize={pageSize}" };
        if (from is not null) query.Add($"from={Uri.EscapeDataString(from.Value.ToString("O"))}");
        if (to is not null) query.Add($"to={Uri.EscapeDataString(to.Value.ToString("O"))}");
        if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={status}");
        if (reprintsOnly) query.Add("reprintsOnly=true");
        if (!string.IsNullOrWhiteSpace(search)) query.Add($"search={Uri.EscapeDataString(search)}");
        if (!string.IsNullOrWhiteSpace(cursor)) query.Add($"cursor={cursor}");
        return api.GetAsync<PagedResult<PrintJobDto>>(
            $"{ApiRoutes.Print.History}?{string.Join("&", query)}", ct);
    }

    // ---- client dispatcher ----
    public Task<IReadOnlyList<long>> GetPendingAsync(string workstation, CancellationToken ct) =>
        api.GetAsync<IReadOnlyList<long>>(
            $"{ApiRoutes.Print.Pending}?workstation={Uri.EscapeDataString(workstation)}", ct);

    public Task<bool> TryClaimAsync(long jobId, string workstation, CancellationToken ct) =>
        api.PostForStatusAsync(
            $"{ApiRoutes.Print.Claim(jobId)}?workstation={Uri.EscapeDataString(workstation)}", ct);

    public Task<byte[]?> GetPayloadAsync(long jobId, CancellationToken ct) =>
        api.GetBytesAsync(ApiRoutes.Print.Payload(jobId), ct);

    public Task ReportStatusAsync(long jobId, UpdateJobStatusRequest request, CancellationToken ct) =>
        api.PutAsync(ApiRoutes.Print.Status(jobId), request, ct);

    // ---- printer admin ----
    public Task<long> CreatePrinterAsync(SavePrinterRequest request, CancellationToken ct) =>
        api.PostAsync<SavePrinterRequest, IdResponse>(ApiRoutes.Printers.Base, request, ct)
            .ContinueWith(t => t.Result.Id, ct,
                TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

    public Task UpdatePrinterAsync(long id, SavePrinterRequest request, CancellationToken ct) =>
        api.PutAsync(ApiRoutes.Printers.ById(id), request, ct);

    public Task SetDefaultPrinterAsync(long id, CancellationToken ct) =>
        api.PostAsync(ApiRoutes.Printers.SetDefault(id), ct);

    public Task<PrinterTestResultDto> TestPrinterAsync(long id, CancellationToken ct) =>
        api.PostAsync<object, PrinterTestResultDto>(ApiRoutes.Printers.Test(id), new { }, ct);

    /// <summary>Live reachability of one printer — TCP probe for network
    /// printers, workstation heartbeat for client-dispatched ones.</summary>
    public Task<PrinterStatusDto> GetPrinterStatusAsync(long id, CancellationToken ct) =>
        api.GetAsync<PrinterStatusDto>(ApiRoutes.Printers.Status(id), ct);

    /// <summary>Live job status (B-16). Returns the connection so the caller
    /// owns its lifetime; if it cannot be established the caller keeps its
    /// existing refresh-on-demand behaviour and simply loses the live updates.
    /// A print is never blocked by the notification channel.</summary>
    public async Task<HubConnection> SubscribeToJobsAsync(
        Action<PrintJobDto> onJobChanged, CancellationToken ct)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(api.BaseAddress, ApiRoutes.Print.Hub), options =>
            {
                // The hub is authorized like every endpoint; the access token
                // is re-read on each (re)connect so a rotated token is used (F-5).
                options.AccessTokenProvider = () => Task.FromResult(api.AccessToken);
            })
            .WithAutomaticReconnect()
            .Build();

        connection.On<PrintJobDto>("JobChanged", onJobChanged);
        await connection.StartAsync(ct);
        await connection.InvokeAsync("SubscribeToAll", ct);
        return connection;
    }

    private sealed record IdResponse(long Id);
}

public sealed record PrinterTestResultDto(bool Success, string Message);
