using System.Security.Cryptography;
using BarcodePrinter.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using SkiaSharp;

namespace BarcodePrinter.Infrastructure.Services;

/// <summary>
/// Content-addressed product image store on the server filesystem (B-9 /
/// §9.4 — recommended over BLOBs so mysqldump stays small and the InnoDB
/// buffer pool stays free for indexes).
///
/// Every upload is RE-ENCODED via SkiaSharp — this normalises the format,
/// strips EXIF/embedded payloads (§13 security), and produces:
///   {root}/{h[0..1]}/{h[2..3]}/{hash}.jpg        full  (≤1200 px long edge)
///   {root}/{h[0..1]}/{h[2..3]}/{hash}_thumb.jpg  thumb (128 px, for grids)
/// Writes are idempotent by construction: same content → same hash → same path.
/// Orphans (file written, row not committed) are swept by a scheduled job.
/// </summary>
public sealed class FileSystemImageStore : IProductImageStore
{
    private const int FullMaxEdge = 1200;
    private const int ThumbEdge = 128;
    private const int JpegQuality = 85;

    private readonly string _root;

    public FileSystemImageStore(IConfiguration configuration)
    {
        _root = configuration["Images:RootPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "data", "images");
        Directory.CreateDirectory(_root);
    }

    public async Task<StoredImage> SaveAsync(Stream content, CancellationToken ct)
    {
        // Buffer the upload (endpoint enforces the size cap before we get here).
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        using var original = SKBitmap.Decode(buffer);
        if (original is null)
        {
            throw new Domain.DomainException("IMAGE_INVALID",
                "The file is not a readable image. Use JPEG or PNG.");
        }

        using var full = Resize(original, FullMaxEdge);
        using var fullData = full.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        var fullBytes = fullData.ToArray();

        using var thumb = Resize(original, ThumbEdge);
        using var thumbData = thumb.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);

        // Hash of the CANONICAL (re-encoded) full image — the store key and
        // the client cache key are the same value.
        var hash = Convert.ToHexStringLower(SHA256.HashData(fullBytes));
        var dir = Path.Combine(_root, hash[..2], hash[2..4]);
        Directory.CreateDirectory(dir);

        var fullPath = Path.Combine(dir, $"{hash}.jpg");
        var thumbPath = Path.Combine(dir, $"{hash}_thumb.jpg");
        if (!File.Exists(fullPath))
        {
            await File.WriteAllBytesAsync(fullPath, fullBytes, ct);
            await File.WriteAllBytesAsync(thumbPath, thumbData.ToArray(), ct);
        }

        return new StoredImage(
            hash,
            StorageKey: Path.Combine(hash[..2], hash[2..4], $"{hash}.jpg"),
            Mime: "image/jpeg",
            WidthPx: full.Width,
            HeightPx: full.Height,
            ByteSize: fullBytes.Length);
    }

    public Task<Stream?> OpenAsync(string hash, ImageVariant variant, CancellationToken ct)
    {
        if (hash.Length < 4 || !hash.All(char.IsAsciiHexDigitLower))
        {
            return Task.FromResult<Stream?>(null);   // path-traversal guard
        }
        var suffix = variant == ImageVariant.Thumb ? "_thumb" : "";
        var path = Path.Combine(_root, hash[..2], hash[2..4], $"{hash}{suffix}.jpg");
        return Task.FromResult<Stream?>(
            File.Exists(path) ? File.OpenRead(path) : null);
    }

    private static SKBitmap Resize(SKBitmap source, int maxEdge)
    {
        var scale = Math.Min(1.0, maxEdge / (double)Math.Max(source.Width, source.Height));
        var w = Math.Max(1, (int)Math.Round(source.Width * scale));
        var h = Math.Max(1, (int)Math.Round(source.Height * scale));
        return source.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888),
            new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new InvalidOperationException("Image resize failed.");
    }
}
