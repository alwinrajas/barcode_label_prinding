using System.Globalization;
using System.Text;
using BarcodePrinter.Labels.Barcodes;
using BarcodePrinter.Labels.Binding;

namespace BarcodePrinter.Labels.Native;

/// <summary>
/// Compiles a <see cref="LabelDefinition"/> into the same ZPL stored format a
/// client-supplied artifact produces.
///
/// This is what keeps the definition model from becoming a second printing
/// engine: everything downstream — binder, payload store, queue, dispatcher,
/// transports, history, byte-replay reprint — sees an ordinary prepared
/// template and cannot tell which adapter produced it. Registering the client's
/// own file later swaps the adapter and changes nothing else (§6.5 / A-18).
///
/// The QR mode prefix is handled exactly as the ZPL adapter handles it: ZPL
/// carries `LA,` (or similar) as part of the field DATA, not the command, so it
/// is recorded as a placeholder prefix and re-applied at render time. Dropping
/// it yields a symbol that looks right and does not scan.
/// </summary>
public sealed class NativeTemplateAdapter : ITemplateAdapter
{
    public const string FormatName = "Native";

    public string Format => FormatName;

    public IReadOnlyList<DetectedField> Inspect(string artifact)
    {
        var label = LabelDefinition.Parse(artifact);
        var detected = new List<DetectedField>();
        var index = 0;

        foreach (var element in Bindable(label))
        {
            detected.Add(new DetectedField(
                index++,
                element.Kind,
                element.Id,
                label.MmToDots(element.XMm),
                label.MmToDots(element.YMm),
                Describe(element)));
        }

        return detected;
    }

    public PreparedTemplate Prepare(
        string artifact, string storedFormatName,
        IReadOnlyDictionary<int, string> fieldPlaceholders)
    {
        var label = LabelDefinition.Parse(artifact);

        // The caller may supply its own numbering; otherwise fields are numbered
        // in declaration order, which is what Inspect reported.
        var bindable = Bindable(label).ToList();
        var placeholderByElementId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < bindable.Count; i++)
        {
            placeholderByElementId[bindable[i].Id] =
                fieldPlaceholders.TryGetValue(i, out var supplied) ? supplied : (i + 1).ToString();
        }

        var prefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var define = new StringBuilder(2_048);

        define.Append("^XA");
        define.Append($"^DF{storedFormatName}^FS");
        define.Append("^CI28");                                  // UTF-8 in, so ° and é survive
        define.Append($"^PW{label.WidthDots}");
        define.Append($"^LL{label.HeightDots}");
        define.Append("^LH0,0");
        if (label.Darkness is { } darkness)
        {
            define.Append($"~SD{darkness:00}");
        }
        if (label.PrintSpeedIps is { } speed)
        {
            define.Append($"^PR{speed}");
        }
        if (label.Orientation == LabelOrientation.Portrait)
        {
            define.Append("^POI");                               // invert: portrait media
        }

        foreach (var element in label.Elements.Where(e => e.Visible))
        {
            placeholderByElementId.TryGetValue(element.Id, out var placeholder);
            AppendElement(define, label, element, placeholder, prefixes);
        }

        define.Append("^XZ");

        var fields = new List<DetectedField>();
        for (var i = 0; i < bindable.Count; i++)
        {
            fields.Add(new DetectedField(
                i, bindable[i].Kind, placeholderByElementId[bindable[i].Id],
                label.MmToDots(bindable[i].XMm), label.MmToDots(bindable[i].YMm),
                Describe(bindable[i])));
        }

        return new PreparedTemplate(storedFormatName, define.ToString(), fields, prefixes);
    }

    public string RenderRecall(RenderRequest request)
    {
        var sb = new StringBuilder(256);
        sb.Append("^XA");
        sb.Append($"^XF{request.Template.StoredFormatName}^FS");

        foreach (var mapping in request.Mappings)
        {
            if (!request.BoundValues.TryGetValue(mapping.PlaceholderRef, out var value))
            {
                continue;
            }

            // An image resolves to raster data that is substituted downstream;
            // emitting the hash as text would print the hash on the label.
            if (mapping.DataKind == FieldDataKind.Image)
            {
                continue;
            }

            if (request.Template.PlaceholderPrefixes.TryGetValue(mapping.PlaceholderRef, out var prefix))
            {
                value = prefix + value;
            }
            sb.Append(ZplSanitizer.RenderField(mapping.PlaceholderRef, value));
        }

        if (request.Copies > 1)
        {
            sb.Append($"^PQ{request.Copies}");
        }
        sb.Append("^XZ");
        return sb.ToString();
    }

    /// <summary>Full layout per label, for firmware without stored-format
    /// support (R-13). Same marks on the media, larger payload.</summary>
    public string RenderInline(
        string artifact,
        IReadOnlyDictionary<int, string> fieldPlaceholders,
        IReadOnlyDictionary<string, string> boundValues,
        int copies = 1)
    {
        var prepared = Prepare(artifact, "INLINE", fieldPlaceholders);

        // Turn the define block into a plain label: drop the ^DF and inline the
        // values where the ^FN placeholders sit.
        var body = prepared.DefinePayload
            .Replace("^XA", "", StringComparison.Ordinal)
            .Replace("^XZ", "", StringComparison.Ordinal)
            .Replace($"^DF{prepared.StoredFormatName}^FS", "", StringComparison.Ordinal);

        foreach (var field in prepared.Fields)
        {
            var placeholder = field.SampleValue;
            var value = boundValues.TryGetValue(placeholder, out var bound) ? bound : "";
            if (prepared.PlaceholderPrefixes.TryGetValue(placeholder, out var prefix))
            {
                value = prefix + value;
            }
            body = body.Replace(
                $"^FN{placeholder}^FS",
                value.Length == 0 ? "^FS" : ZplSanitizer.RenderInlineData(value) + "^FS",
                StringComparison.Ordinal);
        }

        var sb = new StringBuilder(body.Length + 32);
        sb.Append("^XA").Append(body);
        if (copies > 1)
        {
            sb.Append($"^PQ{copies}");
        }
        sb.Append("^XZ");
        return sb.ToString();
    }

    // ---- element emitters ---------------------------------------------------------

    private static void AppendElement(
        StringBuilder zpl, LabelDefinition label, LabelElement element,
        string? placeholder, Dictionary<string, string> prefixes)
    {
        var x = label.MmToDots(element.XMm);
        var y = label.MmToDots(element.YMm);

        switch (element)
        {
            case TextElement text:
                AppendText(zpl, label, text, x, y, placeholder);
                break;

            case BarcodeElement barcode:
                zpl.Append($"^FO{x},{y}");
                zpl.Append($"^BY{barcode.ModuleWidthDots},3.0,{label.MmToDots(barcode.HeightMm)}");
                zpl.Append(BarcodeEncoder.Shared.ZplCommand(
                    barcode.Symbology, label.MmToDots(barcode.HeightMm), barcode.ShowHumanReadable));
                zpl.Append($"^FN{placeholder}^FS");
                break;

            case QrElement qr:
                zpl.Append($"^FO{x},{y}");
                zpl.Append($"^BQN,2,{qr.Magnification}");
                zpl.Append($"^FN{placeholder}^FS");
                // Mode indicator travels in the DATA (see class remarks).
                if (placeholder is not null)
                {
                    prefixes[placeholder] = $"{qr.ErrorCorrection}A,";
                }
                break;

            case ImageElement image:
                // Reserved position only. The raster block is substituted at
                // render time, when the product's image bytes are available.
                zpl.Append($"^FO{x},{y}");
                zpl.Append($"^FN{placeholder}^FS");
                break;

            case BoxElement box:
                zpl.Append($"^FO{x},{y}");
                zpl.Append($"^GB{label.MmToDots(box.WidthMm)},{label.MmToDots(box.HeightMm)}," +
                           $"{Math.Max(1, label.MmToDots(box.ThicknessMm))}^FS");
                break;
        }
    }

    private static void AppendText(
        StringBuilder zpl, LabelDefinition label, TextElement text, int x, int y, string? placeholder)
    {
        zpl.Append($"^FO{x},{y}");

        var height = label.MmToDots(text.FontHeightMm);
        var width = text.FontWidthMm is { } w ? label.MmToDots(w) : height;
        // ^A0 is the scalable font; bold is emulated by widening, because ZPL's
        // resident fonts have no weight axis.
        zpl.Append($"^A0N,{height},{(text.Bold ? (int)(width * 1.15) : width)}");

        if (text.BlockWidthMm is { } blockWidth)
        {
            var justify = text.Alignment switch
            {
                TextAlignment.Center => "C",
                TextAlignment.Right => "R",
                _ => "L",
            };
            zpl.Append($"^FB{label.MmToDots(blockWidth)},{Math.Max(1, text.MaxLines)},0,{justify},0");
        }

        if (placeholder is not null)
        {
            zpl.Append($"^FN{placeholder}^FS");
        }
        else
        {
            zpl.Append(ZplSanitizer.RenderInlineData(text.Text ?? "")).Append("^FS");
        }
    }

    // ---- helpers ------------------------------------------------------------------

    /// <summary>
    /// Elements that need a value at print time, in the order the adapter
    /// numbers them. Static text and boxes are drawn into the stored format once
    /// and never bound.
    ///
    /// Public because the preview rasteriser draws the same elements and must
    /// agree with this ordering; deriving it twice is how the two would drift.
    /// </summary>
    public static IReadOnlyList<LabelElement> BindableElements(LabelDefinition label) =>
        label.Elements.Where(e => e.Visible && e.DataKey is not null).ToList();

    private static IEnumerable<LabelElement> Bindable(LabelDefinition label) =>
        BindableElements(label);

    private static string Describe(LabelElement element) => element switch
    {
        TextElement t => $"text {t.FontHeightMm}mm {t.Alignment}".ToLower(CultureInfo.InvariantCulture),
        BarcodeElement b => $"barcode {b.Symbology} {b.HeightMm}mm",
        QrElement q => $"qr x{q.Magnification} ec={q.ErrorCorrection}",
        ImageElement i => $"image {i.WidthMm}x{i.HeightMm}mm",
        _ => element.Kind.ToString(),
    };
}
