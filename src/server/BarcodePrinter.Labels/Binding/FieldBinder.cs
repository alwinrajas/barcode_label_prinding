using System.Globalization;

namespace BarcodePrinter.Labels.Binding;

public enum FieldDataKind { Text, Barcode, QrCode, Image, DateTime, Number }

public enum FieldTransform { None, Upper, Lower, Trim }

public enum OverflowBehaviour { Truncate, Error, Shrink }

/// <summary>One row of label_template_fields, in engine terms.</summary>
public sealed record FieldMapping(
    string PlaceholderRef,
    string DataKey,
    FieldDataKind DataKind,
    string? FormatString = null,
    FieldTransform Transform = FieldTransform.None,
    int? MaxLength = null,
    OverflowBehaviour Overflow = OverflowBehaviour.Error,
    bool IsRequired = false,
    string? FallbackValue = null,
    string? SampleValue = null);

public sealed class FieldBindingException(string message) : Exception(message);

/// <summary>
/// The closed data-key vocabulary (blueprint §5.2). A fixed, validated set —
/// NOT an expression language: admins map fields, they cannot write logic, so
/// mappings stay statically validatable and cannot be abused.
/// </summary>
public static class TokenVocabulary
{
    public const string FeedbackUrlKey = "Settings.FeedbackFormUrl";

    public static readonly IReadOnlyList<string> All =
    [
        "Product.Code", "Product.Description", "Product.BarcodeValue",
        "Product.Uom", "Product.Size", "Product.Color", "Product.PrimaryImage",
        "Effective.Batch", "Effective.ProductionDate", "Effective.ExpiryDate",
        "Effective.QuantityText",
        "Carton.Current", "Carton.Total", "Carton.From", "Carton.To", "Carton.Text",
        "Job.JobNo", "Job.PrintedAt", "Job.User", "Job.PrinterName", "Job.IsReprint",
        FeedbackUrlKey, "Settings.CompanyName",
        "Now",
    ];

    public static bool IsKnown(string dataKey) => All.Contains(dataKey);

    /// <summary>
    /// A-14 enforced, not merely documented: a QR field may bind ONLY to the
    /// static feedback URL. Product or job data can never reach a QR code, so
    /// the confirmed "static URL, no parameters" rule cannot be eroded later by
    /// a template edit.
    /// </summary>
    public static bool IsAllowedForKind(string dataKey, FieldDataKind kind) => kind switch
    {
        FieldDataKind.QrCode => dataKey == FeedbackUrlKey,
        FieldDataKind.Image => dataKey == "Product.PrimaryImage",
        _ => IsKnown(dataKey) && dataKey != FeedbackUrlKey,
    };
}

/// <summary>Resolves mappings against a PrintContext and produces the final,
/// formatted, length-checked strings the renderer emits.</summary>
public sealed class FieldBinder
{
    public IReadOnlyDictionary<string, string> Bind(
        IReadOnlyList<FieldMapping> mappings, PrintContext context)
    {
        var result = new Dictionary<string, string>(mappings.Count, StringComparer.Ordinal);
        foreach (var mapping in mappings)
        {
            var raw = Resolve(mapping, context);

            if (string.IsNullOrEmpty(raw))
            {
                raw = mapping.FallbackValue ?? string.Empty;
            }
            if (mapping.IsRequired && string.IsNullOrEmpty(raw))
            {
                throw new FieldBindingException(
                    $"Field '{mapping.PlaceholderRef}' ({mapping.DataKey}) is required but has no value.");
            }

            raw = mapping.Transform switch
            {
                FieldTransform.Upper => raw.ToUpperInvariant(),
                FieldTransform.Lower => raw.ToLowerInvariant(),
                FieldTransform.Trim => raw.Trim(),
                _ => raw,
            };

            if (mapping.MaxLength is { } max && raw.Length > max)
            {
                raw = mapping.Overflow switch
                {
                    OverflowBehaviour.Truncate => raw[..max],
                    // Shrink is a renderer concern (smaller font); the value is
                    // passed through intact and the renderer decides.
                    OverflowBehaviour.Shrink => raw,
                    _ => throw new FieldBindingException(
                        $"Field '{mapping.PlaceholderRef}' ({mapping.DataKey}) is {raw.Length} characters " +
                        $"but the label allows {max}."),
                };
            }

            result[mapping.PlaceholderRef] = raw;
        }
        return result;
    }

    private static string Resolve(FieldMapping mapping, PrintContext c)
    {
        if (!TokenVocabulary.IsAllowedForKind(mapping.DataKey, mapping.DataKind))
        {
            throw new FieldBindingException(
                $"'{mapping.DataKey}' cannot be bound to a {mapping.DataKind} field. " +
                (mapping.DataKind == FieldDataKind.QrCode
                    ? "QR codes carry the static feedback URL only (confirmed requirement A-14)."
                    : "Unknown or disallowed data key."));
        }

        return mapping.DataKey switch
        {
            "Product.Code" => c.Product.Code,
            "Product.Description" => c.Product.Description,
            "Product.BarcodeValue" => c.Product.BarcodeValue,
            "Product.Uom" => c.Product.Uom ?? "",
            "Product.Size" => c.Product.Size ?? "",
            "Product.Color" => c.Product.Color ?? "",
            "Product.PrimaryImage" => c.Product.PrimaryImageHash ?? "",

            "Effective.Batch" => c.Effective.Batch ?? "",
            "Effective.ProductionDate" => FormatDate(c.Effective.ProductionDate, mapping, c),
            "Effective.ExpiryDate" => FormatDate(c.Effective.ExpiryDate, mapping, c),
            "Effective.QuantityText" => c.Effective.QuantityText ?? "",

            "Carton.Current" => c.Carton.Current?.ToString(CultureInfo.InvariantCulture) ?? "",
            "Carton.Total" => c.Carton.Total?.ToString(CultureInfo.InvariantCulture) ?? "",
            "Carton.From" => c.Carton.From?.ToString(CultureInfo.InvariantCulture) ?? "",
            "Carton.To" => c.Carton.To?.ToString(CultureInfo.InvariantCulture) ?? "",
            "Carton.Text" => c.Carton.Text ?? "",

            "Job.JobNo" => c.Job.JobNo,
            "Job.User" => c.Job.User,
            "Job.PrinterName" => c.Job.PrinterName,
            "Job.IsReprint" => c.Job.IsReprint ? "REPRINT" : "",
            "Job.PrintedAt" => c.NowLocal.ToString(
                mapping.FormatString ?? c.Settings.TimestampFormat, CultureInfo.InvariantCulture),

            TokenVocabulary.FeedbackUrlKey => c.Settings.FeedbackFormUrl ?? "",
            "Settings.CompanyName" => c.Settings.CompanyName ?? "",

            "Now" => c.NowLocal.ToString(
                mapping.FormatString ?? c.Settings.TimestampFormat, CultureInfo.InvariantCulture),

            _ => throw new FieldBindingException($"Unknown data key '{mapping.DataKey}'."),
        };
    }

    private static string FormatDate(DateOnly? value, FieldMapping mapping, PrintContext c) =>
        value?.ToString(mapping.FormatString ?? c.Settings.DateFormat, CultureInfo.InvariantCulture) ?? "";
}
