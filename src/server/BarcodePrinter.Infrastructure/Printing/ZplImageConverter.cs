using System.Text;
using BarcodePrinter.Application.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace BarcodePrinter.Infrastructure.Printing;

/// <summary>
/// Converts a stored product image into a ZPL <c>^GFA</c> raster block.
///
/// Thermal printers have no greyscale: every dot is on or off. So the image is
/// downscaled to the exact dot size the label reserves and then thresholded.
/// Sending a JPEG's worth of pixels and letting the printer cope produces a
/// grey smear, and sending the wrong dot size silently scales the picture.
///
/// Results are cached by (content hash, dot size): the same product printed a
/// thousand times converts once, and the conversion is pure CPU on the render
/// path (§9.1 product_image_renders).
/// </summary>
public sealed class ZplImageConverter(
    IProductImageStore store,
    IMemoryCache cache,
    ILogger<ZplImageConverter> logger)
{
    /// <summary>Above this, a label image is almost certainly a mistake and
    /// would take seconds to transmit at 9600 baud. Public so the preview can
    /// warn about an image the printer would drop — the on-screen rasteriser
    /// has no such limit, so silence here would let preview and print differ.</summary>
    public const int MaxDots = 1_200;

    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);

    /// <summary>
    /// Returns the ^GFA block for an image, or null when there is no image —
    /// a missing picture must degrade to a blank area, never fail the print
    /// (§16: printing continues on a placeholder).
    /// </summary>
    public async Task<string?> TryRenderAsync(
        string? contentHash, int widthDots, int heightDots, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(contentHash) || widthDots <= 0 || heightDots <= 0)
        {
            return null;
        }
        if (widthDots > MaxDots || heightDots > MaxDots)
        {
            logger.LogWarning(
                "Label image {Width}x{Height} dots exceeds the {Max} dot limit; skipping",
                widthDots, heightDots, MaxDots);
            return null;
        }

        var key = $"zplimg:{contentHash}:{widthDots}x{heightDots}";
        if (cache.TryGetValue<string>(key, out var cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            await using var source = await store.OpenAsync(contentHash, ImageVariant.Full, ct);
            if (source is null)
            {
                logger.LogWarning("Product image {Hash} is missing from the store", contentHash);
                return null;
            }

            using var bitmap = SKBitmap.Decode(source);
            if (bitmap is null)
            {
                logger.LogWarning("Product image {Hash} could not be decoded", contentHash);
                return null;
            }

            var zpl = Encode(bitmap, widthDots, heightDots);
            cache.Set(key, zpl, CacheFor);
            return zpl;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A picture is never worth failing a print run for.
            logger.LogWarning(ex, "Could not convert product image {Hash} for printing", contentHash);
            return null;
        }
    }

    /// <summary>Downscale, threshold to 1 bit, pack 8 dots per byte, emit as hex.</summary>
    internal static string Encode(SKBitmap source, int widthDots, int heightDots)
    {
        using var scaled = source.Resize(
            new SKImageInfo(widthDots, heightDots), new SKSamplingOptions(SKFilterMode.Linear))
            ?? throw new InvalidOperationException("Image could not be resized for printing.");

        // ZPL rows are byte-aligned; a 100-dot row occupies 13 bytes.
        var bytesPerRow = (widthDots + 7) / 8;
        var raster = new byte[bytesPerRow * heightDots];

        for (var y = 0; y < heightDots; y++)
        {
            for (var x = 0; x < widthDots; x++)
            {
                var pixel = scaled.GetPixel(x, y);

                // Transparent areas are media, not black; without this an image
                // with an alpha channel prints as a solid rectangle.
                if (pixel.Alpha < 128)
                {
                    continue;
                }

                // Rec. 601 luma, then a mid threshold. Dithering would look
                // better on photographs but smears product line art.
                var luma = (0.299 * pixel.Red) + (0.587 * pixel.Green) + (0.114 * pixel.Blue);
                if (luma >= 128)
                {
                    continue;   // light pixel: leave the dot off
                }

                raster[(y * bytesPerRow) + (x / 8)] |= (byte)(0x80 >> (x % 8));
            }
        }

        var hex = Convert.ToHexString(raster);
        return $"^GFA,{raster.Length},{raster.Length},{bytesPerRow},{hex}";
    }
}
