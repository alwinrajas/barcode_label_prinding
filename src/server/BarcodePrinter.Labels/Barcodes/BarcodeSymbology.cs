using System.Text.RegularExpressions;

namespace BarcodePrinter.Labels.Barcodes;

/// <summary>Candidate symbologies (C-6, still TBD). The observed product codes
/// (`5GCAPM2N`, `5GCAPM3NOSW`) are alphanumeric and variable-length, which
/// rules out the numeric fixed-length options — recorded here as validation
/// rules so choosing one is a configuration change, not a rewrite.</summary>
public enum BarcodeSymbology
{
    Code128,
    Code39,
    Ean13,
    UpcA,
    Itf14,
}

public sealed record BarcodeValidation(bool IsValid, string? Error)
{
    public static readonly BarcodeValidation Ok = new(true, null);
    public static BarcodeValidation Fail(string error) => new(false, error);
}

/// <summary>Maps a symbology to its ZPL command and validates payloads
/// against that symbology's rules (blueprint §6.2 / R-8).</summary>
public interface IBarcodeEncoder
{
    /// <summary>The ZPL command that opens this symbology, e.g. `^BCN,{height},Y,N,N`.</summary>
    string ZplCommand(BarcodeSymbology symbology, int heightDots, bool humanReadable);

    BarcodeValidation Validate(BarcodeSymbology symbology, string value);
}

public sealed partial class BarcodeEncoder : IBarcodeEncoder
{
    /// <summary>Stateless: one instance serves the template compiler, which has
    /// no DI container of its own.</summary>
    public static readonly BarcodeEncoder Shared = new();

    public string ZplCommand(BarcodeSymbology symbology, int heightDots, bool humanReadable)
    {
        var hri = humanReadable ? "Y" : "N";
        return symbology switch
        {
            BarcodeSymbology.Code128 => $"^BCN,{heightDots},{hri},N,N",
            BarcodeSymbology.Code39 => $"^B3N,N,{heightDots},{hri},N",
            BarcodeSymbology.Ean13 => $"^BEN,{heightDots},{hri},N",
            BarcodeSymbology.UpcA => $"^BUN,{heightDots},{hri},N,N",
            BarcodeSymbology.Itf14 => $"^B2N,{heightDots},{hri},N,N",
            _ => throw new ArgumentOutOfRangeException(nameof(symbology)),
        };
    }

    public BarcodeValidation Validate(BarcodeSymbology symbology, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return BarcodeValidation.Fail("Barcode value is empty.");
        }

        return symbology switch
        {
            // Code 128 encodes all of ASCII 0–127.
            BarcodeSymbology.Code128 => value.All(c => c <= 0x7F)
                ? BarcodeValidation.Ok
                : BarcodeValidation.Fail("Code 128 accepts ASCII characters only."),

            // Code 39 (without full-ASCII mode): 0-9 A-Z and - . $ / + % space.
            BarcodeSymbology.Code39 => Code39Pattern().IsMatch(value)
                ? BarcodeValidation.Ok
                : BarcodeValidation.Fail(
                    "Code 39 accepts uppercase letters, digits and - . $ / + % and space only."),

            BarcodeSymbology.Ean13 => NumericOfLength(value, 12, 13)
                ? BarcodeValidation.Ok
                : BarcodeValidation.Fail(
                    "EAN-13 requires exactly 12 digits (check digit calculated) or 13 digits."),

            BarcodeSymbology.UpcA => NumericOfLength(value, 11, 12)
                ? BarcodeValidation.Ok
                : BarcodeValidation.Fail(
                    "UPC-A requires exactly 11 digits (check digit calculated) or 12 digits."),

            BarcodeSymbology.Itf14 => NumericOfLength(value, 13, 14)
                ? BarcodeValidation.Ok
                : BarcodeValidation.Fail("ITF-14 requires 13 or 14 digits."),

            _ => BarcodeValidation.Fail($"Unsupported symbology '{symbology}'."),
        };
    }

    private static bool NumericOfLength(string value, params int[] lengths) =>
        value.All(char.IsAsciiDigit) && lengths.Contains(value.Length);

    [GeneratedRegex(@"^[0-9A-Z\-\.\$\/\+% ]+$")]
    private static partial Regex Code39Pattern();
}
