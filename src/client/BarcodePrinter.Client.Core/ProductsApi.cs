using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Products;

namespace BarcodePrinter.Client.Core;

/// <summary>Product endpoints for the WPF client. Same ApiClient pipeline —
/// bearer, refresh-on-401, ProblemDetails mapping.</summary>
public sealed class ProductsApi(ApiClient api)
{
    public Task<PagedResult<ProductSummary>> ListAsync(
        string? term, string? cursor, int pageSize, bool includeInactive, CancellationToken ct) =>
        api.GetAsync<PagedResult<ProductSummary>>(
            $"{ApiRoutes.Products.Base}/?q={Uri.EscapeDataString(term ?? "")}" +
            $"&cursor={Uri.EscapeDataString(cursor ?? "")}" +
            $"&pageSize={pageSize}&includeInactive={includeInactive}", ct);

    public Task<ProductDetail> GetAsync(long id, CancellationToken ct) =>
        api.GetAsync<ProductDetail>(ApiRoutes.Products.ById(id), ct);

    public async Task<long> CreateAsync(SaveProductRequest request, CancellationToken ct) =>
        (await api.PostAsync<SaveProductRequest, CreatedResponse>(
            ApiRoutes.Products.Base, request, ct)).Id;

    public Task UpdateAsync(long id, SaveProductRequest request, CancellationToken ct) =>
        api.PutAsync(ApiRoutes.Products.ById(id), request, ct);

    public Task DeactivateAsync(long id, CancellationToken ct) =>
        api.DeleteAsync(ApiRoutes.Products.ById(id), ct);

    public Task ActivateAsync(long id, CancellationToken ct) =>
        api.PostAsync($"{ApiRoutes.Products.ById(id)}/activate", ct);

    public Task<IReadOnlyList<UomDto>> UomsAsync(CancellationToken ct) =>
        api.GetAsync<IReadOnlyList<UomDto>>(ApiRoutes.Products.Uoms, ct);

    /// <summary>Uploads a product image. The caller is expected to have run
    /// <see cref="ImageFileValidator.Validate"/> first; the sniffed content
    /// type is passed in so a renamed extension can never mislabel the part.
    /// The content factory lets ApiClient retry transparently after a token
    /// refresh — each attempt opens a fresh stream.</summary>
    public async Task<string> UploadImageAsync(
        long id, string filePath, string contentType, IProgress<double>? progress, CancellationToken ct)
    {
        var length = new FileInfo(filePath).Length;
        var result = await api.PostMultipartAsync<HashResponse>(ApiRoutes.Products.Image(id), () =>
        {
            var content = new MultipartFormDataContent();
            var part = new StreamContent(new ProgressStream(File.OpenRead(filePath), length, progress));
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            content.Add(part, "file", Path.GetFileName(filePath));
            return content;
        }, ct);
        return result.Hash;
    }

    /// <summary>Thumb/full image bytes with a content-addressed disk cache —
    /// a hash can never serve stale content, so cache hits skip the network
    /// entirely (§11.2 "large images never in grids").</summary>
    public async Task<byte[]?> GetImageAsync(long id, string hash, bool thumb, CancellationToken ct)
    {
        var cached = ImageCache.TryRead(hash, thumb);
        if (cached is not null)
        {
            return cached;
        }

        var bytes = await api.GetBytesAsync(
            $"{ApiRoutes.Products.Image(id)}?variant={(thumb ? "thumb" : "full")}", ct);
        if (bytes is not null)
        {
            ImageCache.Write(hash, thumb, bytes);
        }
        return bytes;
    }

    private sealed record CreatedResponse(long Id);
    private sealed record HashResponse(string Hash);
}

/// <summary>%LocalAppData% disk cache keyed by content hash.</summary>
internal static class ImageCache
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BarcodePrinter", "imagecache");

    public static byte[]? TryRead(string hash, bool thumb)
    {
        var path = PathFor(hash, thumb);
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static void Write(string hash, bool thumb, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(Root);
            File.WriteAllBytes(PathFor(hash, thumb), bytes);
        }
        catch (IOException)
        {
            // Cache is best-effort; never fail the UI over it.
        }
    }

    private static string PathFor(string hash, bool thumb) =>
        Path.Combine(Root, $"{hash}{(thumb ? "_t" : "")}.jpg");
}
