using System.IO;
using System.Net.Http;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Imports;
using Microsoft.AspNetCore.SignalR.Client;

namespace BarcodePrinter.Client.Core;

public sealed class ImportsApi(ApiClient api)
{
    public Task<byte[]?> DownloadTemplateAsync(CancellationToken ct) =>
        api.GetBytesAsync(ApiRoutes.Imports.Template, ct);

    public Task<byte[]?> DownloadErrorsAsync(long batchId, CancellationToken ct) =>
        api.GetBytesAsync(ApiRoutes.Imports.Errors(batchId), ct);

    public Task<byte[]?> ExportProductsAsync(CancellationToken ct) =>
        api.GetBytesAsync(ApiRoutes.Products.Export, ct);

    public Task<ImportBatchDto> GetAsync(long batchId, CancellationToken ct) =>
        api.GetAsync<ImportBatchDto>(ApiRoutes.Imports.ById(batchId), ct);

    public Task<IReadOnlyList<ImportBatchDto>> RecentAsync(CancellationToken ct) =>
        api.GetAsync<IReadOnlyList<ImportBatchDto>>(ApiRoutes.Imports.Recent, ct);

    public Task CancelAsync(long batchId, CancellationToken ct) =>
        api.PostAsync(ApiRoutes.Imports.Cancel(batchId), ct);

    /// <summary>Content factory per attempt so ApiClient can refresh an
    /// expired token and retry — a fresh stream is opened each time and the
    /// upload progress restarts from zero on the (rare) retry.</summary>
    public async Task<long> UploadAsync(string filePath, IProgress<double>? progress, CancellationToken ct)
    {
        var length = new FileInfo(filePath).Length;
        var accepted = await api.PostMultipartAsync<ImportAcceptedResponse>($"{ApiRoutes.Imports.Base}/", () =>
        {
            var content = new MultipartFormDataContent();
            var part = new StreamContent(new ProgressStream(File.OpenRead(filePath), length, progress));
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            content.Add(part, "file", Path.GetFileName(filePath));
            return content;
        }, ct);
        return accepted.BatchId;
    }

    /// <summary>Live progress subscription (B-16). Returns the connection so
    /// the caller controls its lifetime; on any connection failure the caller
    /// falls back to polling GetAsync.</summary>
    public async Task<HubConnection> SubscribeAsync(
        long batchId, Action<ImportBatchDto> onChanged, CancellationToken ct)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(api.BaseAddress, ApiRoutes.Imports.Hub), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult(api.AccessToken);
            })
            .WithAutomaticReconnect()
            .Build();

        connection.On<ImportBatchDto>("BatchChanged", onChanged);
        await connection.StartAsync(ct);
        await connection.InvokeAsync("Subscribe", batchId, ct);
        return connection;
    }
}
