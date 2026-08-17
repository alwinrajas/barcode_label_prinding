using BarcodePrinter.Application.Abstractions;
using BarcodePrinter.Labels.Barcodes;
using BarcodePrinter.Labels.Binding;
using BarcodePrinter.Labels.Native;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using ZXing;
using ZXing.Common;
using ZXing.SkiaSharp.Rendering;

namespace BarcodePrinter.Infrastructure.Printing;

/// <summary>
/// Draws a <see cref="LabelDefinition"/> with real data as a picture, for the
/// on-screen preview (§6.4 option (a)).
///
/// Preview is the strongest error-prevention mechanism the print screen has: it
/// is where an operator notices the wrong batch, a truncated description or a
/// missing carton number BEFORE 500 labels come off the printer. A dump of ZPL
/// source cannot do that job — nobody proofreads `^FO240,160`.
///
/// This renders the DEFINITION, not the ZPL, and is therefore an approximation
/// of the printer's own rasteriser: fonts are the host's, not the printer's
/// resident set. It is accurate about what is on the label, where it sits and
/// whether it fits — which is what the check is for. Exact typeface fidelity is
/// settled against physical media during template sign-off.
/// </summary>
public sealed class LabelRasterizer(ILogger<LabelRasterizer> logger)
{
    /// <summary>Screen preview is rendered above label resolution so text stays
    /// legible when the operator is checking it.</summary>
    private const float PreviewScale = 2.0f;

    private const int MaxPixels = 4000;

    public byte[] RenderPng(
        LabelDefinition label,
        IReadOnlyDictionary<string, string> valuesByElementId,
        Func<string, SKBitmap?>? imageLoader = null)
    {
        var width = (int)Math.Min(MaxPixels, label.WidthDots * PreviewScale);
        var height = (int)Math.Min(MaxPixels, label.HeightDots * PreviewScale);

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;

        // Media, not transparency: a preview on a dark UI theme must still look
        // like a label.
        canvas.Clear(SKColors.White);
        canvas.Scale(PreviewScale);

        foreach (var element in label.Elements.Where(e => e.Visible))
        {
            try
            {
                Draw(canvas, label, element, valuesByElementId, imageLoader);
            }
            catch (Exception ex)
            {
                // One bad element must not blank the whole preview — the rest of
                // the label is still worth showing.
                logger.LogWarning(ex, "Could not draw preview element {ElementId}", element.Id);
            }
        }

        canvas.Flush();
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static void Draw(
        SKCanvas canvas, LabelDefinition label, LabelElement element,
        IReadOnlyDictionary<string, string> values, Func<string, SKBitmap?>? imageLoader)
    {
        var x = label.MmToDots(element.XMm);
        var y = label.MmToDots(element.YMm);

        switch (element)
        {
            case TextElement text:
                DrawText(canvas, label, text, x, y, Value(values, element, text.Text));
                break;

            case BarcodeElement barcode:
                DrawBarcode(canvas, label, barcode, x, y, Value(values, element, null));
                break;

            case QrElement qr:
                DrawQr(canvas, label, qr, x, y, Value(values, element, null));
                break;

            case ImageElement image:
                DrawImage(canvas, label, image, x, y, Value(values, element, null), imageLoader);
                break;

            case BoxElement box:
                DrawBox(canvas, label, box, x, y);
                break;
        }
    }

    private static string Value(
        IReadOnlyDictionary<string, string> values, LabelElement element, string? staticText) =>
        element.DataKey is null
            ? staticText ?? ""
            : values.TryGetValue(element.Id, out var bound) ? bound : "";

    private static void DrawText(
        SKCanvas canvas, LabelDefinition label, TextElement text, int x, int y, string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        var size = label.MmToDots(text.FontHeightMm);
        using var font = new SKFont(
            SKTypeface.FromFamilyName(
                "Segoe UI",
                text.Bold ? SKFontStyleWeight.SemiBold : SKFontStyleWeight.Normal,
                SKFontStyleWidth.Normal, SKFontStyleSlant.Upright),
            size);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };

        var measured = font.MeasureText(value);
        var blockWidth = text.BlockWidthMm is { } w ? label.MmToDots(w) : measured;
        var offset = text.Alignment switch
        {
            TextAlignment.Center => (blockWidth - measured) / 2,
            TextAlignment.Right => blockWidth - measured,
            _ => 0f,
        };

        // ZPL positions text by its TOP edge; Skia draws from the baseline.
        canvas.DrawText(value, x + Math.Max(0, offset), y + size, font, paint);
    }

    private static void DrawBarcode(
        SKCanvas canvas, LabelDefinition label, BarcodeElement barcode, int x, int y, string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        var heightDots = label.MmToDots(barcode.HeightMm);
        var writer = new BarcodeWriter<SKBitmap>
        {
            Format = ToZXing(barcode.Symbology),
            Renderer = new SKBitmapRenderer(),
            Options = new EncodingOptions
            {
                Height = heightDots,
                // The real width comes from the module width; ZXing needs a hint,
                // and it scales the symbol to fit whatever it is given.
                Width = Math.Max(1, value.Length * 11 * barcode.ModuleWidthDots),
                Margin = 0,
                PureBarcode = !barcode.ShowHumanReadable,
            },
        };

        using var bitmap = writer.Write(value);
        canvas.DrawBitmap(bitmap, new SKRect(x, y, x + bitmap.Width, y + bitmap.Height));
    }

    private static void DrawQr(
        SKCanvas canvas, LabelDefinition label, QrElement qr, int x, int y, string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        // ZPL sizes a QR by module magnification, not by an overall dimension,
        // so the preview must do the same or it will lie about the printed size.
        var writer = new BarcodeWriter<SKBitmap>
        {
            Format = BarcodeFormat.QR_CODE,
            Renderer = new SKBitmapRenderer(),
            Options = new EncodingOptions { Margin = 0, Width = 1, Height = 1 },
        };

        using var bitmap = writer.Write(value);
        var side = bitmap.Width * qr.Magnification;
        canvas.DrawBitmap(bitmap, new SKRect(x, y, x + side, y + side));
    }

    private static void DrawImage(
        SKCanvas canvas, LabelDefinition label, ImageElement image,
        int x, int y, string hash, Func<string, SKBitmap?>? loader)
    {
        var width = label.MmToDots(image.WidthMm);
        var height = label.MmToDots(image.HeightMm);
        var box = new SKRect(x, y, x + width, y + height);

        var bitmap = hash.Length > 0 ? loader?.Invoke(hash) : null;
        if (bitmap is null)
        {
            // Show the reserved area rather than nothing: an operator needs to
            // see that a picture is MISSING, not that the label has no picture.
            using var outline = new SKPaint
            {
                Color = SKColors.LightGray,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
                PathEffect = SKPathEffect.CreateDash([4f, 4f], 0),
            };
            canvas.DrawRect(box, outline);
            return;
        }

        // The LOADER owns the bitmap's lifetime — a multi-label job hands the
        // same decoded instance to every label, so disposing it here would
        // corrupt every label after the first.
        canvas.DrawBitmap(bitmap, box);
    }

    private static void DrawBox(SKCanvas canvas, LabelDefinition label, BoxElement box, int x, int y)
    {
        var thickness = Math.Max(1, label.MmToDots(box.ThicknessMm));
        using var paint = new SKPaint
        {
            Color = SKColors.Black,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = thickness,
        };
        canvas.DrawRect(
            new SKRect(x, y, x + label.MmToDots(box.WidthMm), y + label.MmToDots(box.HeightMm)),
            paint);
    }

    private static BarcodeFormat ToZXing(BarcodeSymbology symbology) => symbology switch
    {
        BarcodeSymbology.Code128 => BarcodeFormat.CODE_128,
        BarcodeSymbology.Code39 => BarcodeFormat.CODE_39,
        BarcodeSymbology.Ean13 => BarcodeFormat.EAN_13,
        BarcodeSymbology.UpcA => BarcodeFormat.UPC_A,
        BarcodeSymbology.Itf14 => BarcodeFormat.ITF,
        _ => BarcodeFormat.CODE_128,
    };
}
