using System.Text;
using BarcodePrinter.Labels.Binding;

namespace BarcodePrinter.Labels.Zpl;

/// <summary>
/// ZPL adapter — the expected best case for C-2 (blueprint §4.2). A captured
/// `.prn` from the client's existing printer contains their complete layout
/// plus one set of live data; we detect the data fields, replace them with
/// `^FN` placeholders, and store the result as a `^DF` stored format.
///
/// Per-label output then collapses to `^XF` + values: roughly 95 % less payload
/// on batch runs, and the printed geometry is the client's own, unmodified.
/// </summary>
public sealed class ZplTemplateAdapter : ITemplateAdapter
{
    public string Format => "Zpl";

    /// <summary>Commands that establish what the NEXT ^FD means.</summary>
    private static readonly Dictionary<string, FieldDataKind> KindByCommand = new(StringComparer.Ordinal)
    {
        ["BC"] = FieldDataKind.Barcode,
        ["B3"] = FieldDataKind.Barcode,
        ["BE"] = FieldDataKind.Barcode,
        ["BU"] = FieldDataKind.Barcode,
        ["B2"] = FieldDataKind.Barcode,
        ["BQ"] = FieldDataKind.QrCode,
        ["GF"] = FieldDataKind.Image,
        ["XG"] = FieldDataKind.Image,
    };

    public IReadOnlyList<DetectedField> Inspect(string artifact)
    {
        var document = ZplDocument.Parse(artifact);
        var detected = new List<DetectedField>();

        for (var i = 0; i < document.Commands.Count; i++)
        {
            var command = document.Commands[i];
            if (command.Name != "FD" || command.Data is null)
            {
                continue;
            }

            var (kind, context) = InferKind(document, i);
            var (x, y) = FindPosition(document, i);

            detected.Add(new DetectedField(
                CommandIndex: i,
                InferredKind: kind,
                SampleValue: command.Data,
                X: x, Y: y,
                Context: context));
        }
        return detected;
    }

    public PreparedTemplate Prepare(
        string artifact, string storedFormatName,
        IReadOnlyDictionary<int, string> fieldPlaceholders)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storedFormatName);

        var document = ZplDocument.Parse(artifact);

        // Only mapped fields become placeholders; everything else — including
        // the static captions "Product", "Size", ":" — stays literal.
        var replacements = fieldPlaceholders.ToDictionary(
            kv => kv.Key,
            kv => $"^FN{kv.Value}");

        var body = document.ReplaceData(replacements);
        body = StripFormatWrapper(body);

        var define = new StringBuilder()
            .Append("^XA")
            .Append($"^DF{storedFormatName}^FS")
            .Append(body)
            .Append("^XZ")
            .ToString();

        return new PreparedTemplate(
            storedFormatName, define, Inspect(artifact),
            CollectPrefixes(document, fieldPlaceholders));
    }

    /// <summary>Captures syntax prefixes that live inside ^FD but are not part
    /// of the value (QR mode indicators). They must be re-applied on recall.</summary>
    private static Dictionary<string, string> CollectPrefixes(
        ZplDocument document, IReadOnlyDictionary<int, string> fieldPlaceholders)
    {
        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (commandIndex, placeholderRef) in fieldPlaceholders)
        {
            if (commandIndex >= document.Commands.Count)
            {
                continue;
            }
            var data = document.Commands[commandIndex].Data;
            if (data is not null && TrySplitQrPrefix(data, out var prefix, out _))
            {
                prefixes[placeholderRef] = prefix;
            }
        }
        return prefixes;
    }

    /// <summary>`^BQ` payloads start with `&lt;error correction&gt;&lt;input mode&gt;,`
    /// — e.g. `LA,` or `QM,` — before the encoded data.</summary>
    internal static bool TrySplitQrPrefix(string data, out string prefix, out string payload)
    {
        if (data.Length >= 3 &&
            data[2] == ',' &&
            "HQML".Contains(char.ToUpperInvariant(data[0])) &&
            "AM".Contains(char.ToUpperInvariant(data[1])))
        {
            prefix = data[..3];
            payload = data[3..];
            return true;
        }
        prefix = string.Empty;
        payload = data;
        return false;
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
            // Re-apply any ZPL syntax prefix (QR mode indicator) that Prepare
            // stripped along with the sample value.
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

    public string RenderInline(
        string artifact,
        IReadOnlyDictionary<int, string> fieldPlaceholders,
        IReadOnlyDictionary<string, string> boundValues,
        int copies = 1)
    {
        var document = ZplDocument.Parse(artifact);

        var replacements = new Dictionary<int, string>();
        foreach (var (commandIndex, placeholderRef) in fieldPlaceholders)
        {
            var value = boundValues.TryGetValue(placeholderRef, out var v) ? v : string.Empty;

            // Same prefix rule as the recall path (QR mode indicator).
            if (commandIndex < document.Commands.Count &&
                document.Commands[commandIndex].Data is { } original &&
                TrySplitQrPrefix(original, out var prefix, out _))
            {
                value = prefix + value;
            }
            replacements[commandIndex] = ZplSanitizer.RenderInlineData(value);
        }

        var output = document.ReplaceData(replacements);
        if (copies > 1)
        {
            output = ReplaceQuantity(output, copies);
        }
        return output;
    }

    // ---- internals -----------------------------------------------------------

    /// <summary>Walks backwards from a ^FD to the command that defines its type.
    /// Falls back to Text, which is what an ^A font command implies anyway.</summary>
    private static (FieldDataKind Kind, string Context) InferKind(ZplDocument document, int fdIndex)
    {
        for (var i = fdIndex - 1; i >= 0; i--)
        {
            var command = document.Commands[i];

            if (KindByCommand.TryGetValue(command.Name, out var kind))
            {
                return (kind, command.ToString());
            }
            // A font command settles it as text; a new field origin means we
            // have left the current field without finding a type command.
            if (command.Name is "A0" or "A@" or "CF")
            {
                return (FieldDataKind.Text, command.ToString());
            }
            if (command.Name is "FO" or "FT")
            {
                // Keep scanning back only through the field-origin itself.
                continue;
            }
            if (command.Name == "FS")
            {
                break;   // previous field ended — no type command in this one
            }
        }
        return (FieldDataKind.Text, "^FD");
    }

    private static (int? X, int? Y) FindPosition(ZplDocument document, int fdIndex)
    {
        for (var i = fdIndex - 1; i >= 0; i--)
        {
            var command = document.Commands[i];
            if (command.Name is "FO" or "FT")
            {
                var parts = command.Parameters.Split(',');
                if (parts.Length >= 2 &&
                    int.TryParse(parts[0], out var x) &&
                    int.TryParse(parts[1], out var y))
                {
                    return (x, y);
                }
                return (null, null);
            }
            if (command.Name == "FS" && i < fdIndex - 1)
            {
                break;
            }
        }
        return (null, null);
    }

    /// <summary>A stored format supplies its own ^XA/^XZ, so the captured
    /// wrapper (and its ^PQ, which belongs to the recall) must come out.</summary>
    private static string StripFormatWrapper(string body)
    {
        var span = body.AsSpan().Trim();

        if (span.StartsWith("^XA", StringComparison.OrdinalIgnoreCase))
        {
            span = span[3..];
        }
        if (span.EndsWith("^XZ", StringComparison.OrdinalIgnoreCase))
        {
            span = span[..^3];
        }

        var text = span.ToString();
        return RemoveCommand(text, "^PQ").Trim();
    }

    private static string RemoveCommand(string text, string command)
    {
        var index = text.IndexOf(command, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return text;
        }
        var end = index + command.Length;
        while (end < text.Length && text[end] is not ('^' or '~'))
        {
            end++;
        }
        return text.Remove(index, end - index);
    }

    private static string ReplaceQuantity(string text, int copies)
    {
        var stripped = RemoveCommand(text, "^PQ");
        var xzIndex = stripped.LastIndexOf("^XZ", StringComparison.OrdinalIgnoreCase);
        return xzIndex < 0
            ? stripped + $"^PQ{copies}"
            : stripped.Insert(xzIndex, $"^PQ{copies}");
    }
}
