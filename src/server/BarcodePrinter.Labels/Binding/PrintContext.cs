namespace BarcodePrinter.Labels.Binding;

/// <summary>
/// Everything a label can bind to. `Effective` holds the POST-OVERRIDE values
/// (A-9/A-10) — the confirmed data model made visible in the binding surface,
/// so a mapping cannot accidentally print a master default where the operator's
/// override was intended.
/// </summary>
public sealed record PrintContext(
    ProductValues Product,
    EffectiveValues Effective,
    CartonValues Carton,
    JobValues Job,
    SettingsValues Settings,
    DateTime NowLocal);

public sealed record ProductValues(
    string Code,
    string Description,
    string BarcodeValue,
    string? Uom,
    string? Size,
    string? Color,
    string? PrimaryImageHash);

public sealed record EffectiveValues(
    string? Batch,
    DateOnly? ProductionDate,
    DateOnly? ExpiryDate,
    string? QuantityText);

/// <summary>Carton numbering (C-11). `Text` is produced by the numbering
/// strategy's Format() so "1" vs "1 of 10" is a strategy decision, never a
/// renderer decision (C-10).</summary>
public sealed record CartonValues(
    long? Current,
    long? Total,
    long? From,
    long? To,
    string? Text);

public sealed record JobValues(
    string JobNo,
    string User,
    string PrinterName,
    bool IsReprint);

public sealed record SettingsValues(
    string? FeedbackFormUrl,
    string? CompanyName,
    string DateFormat,
    string TimestampFormat);
