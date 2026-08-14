namespace BarcodePrinter.Labels;

/// <summary>
/// Field-data sanitisation (blueprint §6.2). `^`, `~` and `,` inside ^FD
/// payloads corrupt ZPL — a URL containing a comma silently truncates a QR
/// code, and a product description containing `^` ends the field early.
/// Handled once, centrally, rather than at each call site.
///
/// Strategy: hex-escape via ^FH. The renderer emits ^FH before the field, and
/// unsafe bytes become _xx.
/// </summary>
public static class ZplSanitizer
{
    private const char HexIndicator = '_';

    /// <summary>True when the value needs a ^FH prefix.</summary>
    public static bool NeedsHexEscape(string value) =>
        value.Any(c => c is '^' or '~' or HexIndicator || c > 0x7E);

    /// <summary>Escapes control characters for use inside ^FD with ^FH active.
    /// Non-ASCII is emitted as UTF-8 bytes (paired with ^CI28 in the template).</summary>
    public static string HexEscape(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            if (ch is '^' or '~' or HexIndicator)
            {
                sb.Append(HexIndicator).Append(((int)ch).ToString("X2"));
            }
            else if (ch > 0x7E)
            {
                foreach (var b in System.Text.Encoding.UTF8.GetBytes([ch]))
                {
                    sb.Append(HexIndicator).Append(b.ToString("X2"));
                }
            }
            else
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    /// <summary>Renders one field: `^FN{ref}^FH^FD{escaped}^FS`, or without
    /// ^FH when the value is already safe (keeps output byte-minimal and
    /// readable in golden files).</summary>
    public static string RenderField(string placeholderRef, string value)
    {
        if (!NeedsHexEscape(value))
        {
            return $"^FN{placeholderRef}^FD{value}^FS";
        }
        return $"^FN{placeholderRef}^FH^FD{HexEscape(value)}^FS";
    }

    /// <summary>Inline form (no ^FN) for the full-template fallback path.</summary>
    public static string RenderInlineData(string value) =>
        NeedsHexEscape(value) ? $"^FH^FD{HexEscape(value)}" : $"^FD{value}";
}
