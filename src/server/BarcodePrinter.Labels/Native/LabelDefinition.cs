using System.Text.Json;
using System.Text.Json.Serialization;
using BarcodePrinter.Labels.Barcodes;
using BarcodePrinter.Labels.Binding;

namespace BarcodePrinter.Labels.Native;

/// <summary>
/// A label described as DATA rather than as printer commands.
///
/// This is the format used when the client has not supplied a printer file of
/// their own. It exists so that label size, resolution, orientation and every
/// element's position, size, font, alignment and visibility are configuration —
/// changing the layout is an edit to one row, never a code change (A-17).
///
/// It is deliberately NOT a second printing engine. A definition is compiled to
/// the same ZPL stored format that a client-supplied artifact produces, so the
/// binder, queue, dispatcher, history and reprint paths never learn it exists
/// (§6.5). When the client's own template arrives, it registers under the `Zpl`
/// format and this model simply stops being used for that template.
///
/// Geometry is in MILLIMETRES, not dots. Dots are a property of the printer;
/// the same definition must print correctly at 203 and 300 dpi, and a client
/// specifying "20 mm from the left" must not have to do arithmetic.
/// </summary>
public sealed record LabelDefinition
{
    /// <summary>Schema version, so a future change can migrate stored
    /// definitions instead of failing to parse them.</summary>
    public int Schema { get; init; } = 1;

    public required decimal WidthMm { get; init; }
    public required decimal HeightMm { get; init; }

    /// <summary>Printer resolution the definition is rendered for. Overridden
    /// per printer at render time when they differ (a 203-dpi definition sent
    /// to a 300-dpi printer must not come out at two-thirds size).</summary>
    public int Dpi { get; init; } = 203;

    public LabelOrientation Orientation { get; init; } = LabelOrientation.Landscape;

    /// <summary>Gap between labels; media tracking is a printer/media property
    /// that belongs with the geometry (C-4).</summary>
    public decimal? GapMm { get; init; }

    /// <summary>Darkness 0-30 and speed in ips, when the media needs them.
    /// Null leaves the printer's own configuration alone, which is the right
    /// default — the shop floor tunes these per printer.</summary>
    public int? Darkness { get; init; }
    public int? PrintSpeedIps { get; init; }

    public required IReadOnlyList<LabelElement> Elements { get; init; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static LabelDefinition Parse(string json)
    {
        var definition = JsonSerializer.Deserialize<LabelDefinition>(json, JsonOptions)
            ?? throw new LabelDefinitionException("The label definition is empty.");
        definition.Validate();
        return definition;
    }

    /// <summary>
    /// Rejects a definition that would print wrongly rather than letting it
    /// reach media. Every failure here is one an operator would otherwise find
    /// on a physical label.
    /// </summary>
    public void Validate()
    {
        if (Schema != 1)
        {
            throw new LabelDefinitionException(
                $"Label definition schema {Schema} is not supported by this version.");
        }
        if (WidthMm <= 0 || HeightMm <= 0)
        {
            throw new LabelDefinitionException("Label width and height must be greater than zero.");
        }
        if (Dpi is not (152 or 203 or 300 or 600))
        {
            throw new LabelDefinitionException(
                $"{Dpi} dpi is not a supported printer resolution (152, 203, 300 or 600).");
        }
        if (Elements.Count == 0)
        {
            throw new LabelDefinitionException("A label with no elements would print blank.");
        }

        var duplicate = Elements.GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new LabelDefinitionException(
                $"Element id '{duplicate.Key}' is used more than once; ids identify field mappings.");
        }

        foreach (var element in Elements)
        {
            element.Validate(this);
        }
    }

    public int MmToDots(decimal mm) => (int)Math.Round(mm * Dpi / 25.4m, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Printable width and feed length in dots.
    ///
    /// WidthMm is ALWAYS the width of the media as it passes the head, and
    /// HeightMm always the feed length — that is how a label is measured and how
    /// a supplier sells it ("4 by 6"). Orientation rotates the CONTENT (^POI); it
    /// must not swap the media dimensions, or a 4x6 portrait label would be sent
    /// a 6-inch print width and come out clipped.
    /// </summary>
    public int WidthDots => MmToDots(WidthMm);
    public int HeightDots => MmToDots(HeightMm);

    /// <summary>The same definition at a different resolution. Used when a
    /// template is sent to a printer whose dpi differs from the design dpi.</summary>
    public LabelDefinition AtDpi(int dpi) => dpi == Dpi ? this : this with { Dpi = dpi };
}

public enum LabelOrientation { Landscape, Portrait }

public enum TextAlignment { Left, Center, Right }

public sealed class LabelDefinitionException(string message) : Exception(message);

// ---- elements -------------------------------------------------------------------

/// <summary>
/// One thing drawn on the label. Position is the top-left corner in millimetres
/// from the top-left of the label, matching how a person measures a printed
/// sample.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TextElement), "text")]
[JsonDerivedType(typeof(BarcodeElement), "barcode")]
[JsonDerivedType(typeof(QrElement), "qr")]
[JsonDerivedType(typeof(ImageElement), "image")]
[JsonDerivedType(typeof(BoxElement), "box")]
public abstract record LabelElement
{
    /// <summary>Stable identifier, and the placeholder reference used by the
    /// field mapping. Renaming an element re-maps it, so ids are chosen once.</summary>
    public required string Id { get; init; }

    public required decimal XMm { get; init; }
    public required decimal YMm { get; init; }

    /// <summary>Hidden elements are kept in the definition but not rendered, so
    /// a field can be switched off without losing its configuration.</summary>
    public bool Visible { get; init; } = true;

    /// <summary>Data this element prints. Null means static content — the
    /// element carries its own text and needs no mapping.</summary>
    public string? DataKey { get; init; }

    /// <summary>Derived from the element type; the JSON discriminator carries
    /// it on the wire, so serialising it too would collide with that.</summary>
    [JsonIgnore]
    public abstract FieldDataKind Kind { get; }

    public virtual void Validate(LabelDefinition label)
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new LabelDefinitionException("Every element needs an id.");
        }
        if (XMm < 0 || YMm < 0)
        {
            throw new LabelDefinitionException($"Element '{Id}' is positioned off the label.");
        }
        if (XMm > label.WidthMm || YMm > label.HeightMm)
        {
            throw new LabelDefinitionException(
                $"Element '{Id}' starts outside the {label.WidthMm}x{label.HeightMm} mm label.");
        }
    }
}

public sealed record TextElement : LabelElement
{
    [JsonIgnore]
    public override FieldDataKind Kind => FieldDataKind.Text;

    /// <summary>Cap height in millimetres. Millimetres rather than points
    /// because the client specifies labels by measurement.</summary>
    public decimal FontHeightMm { get; init; } = 3m;

    /// <summary>Null keeps the printer's default aspect for the height.</summary>
    public decimal? FontWidthMm { get; init; }

    public bool Bold { get; init; }

    public TextAlignment Alignment { get; init; } = TextAlignment.Left;

    /// <summary>Width of the text block. Required for centre/right alignment
    /// and for wrapping; null means "as wide as the text".</summary>
    public decimal? BlockWidthMm { get; init; }

    public int MaxLines { get; init; } = 1;

    /// <summary>Literal text for captions such as "Batch:". Ignored when
    /// DataKey is set.</summary>
    public string? Text { get; init; }

    /// <summary>Format for dates and numbers, e.g. dd/MM/yyyy (C-1).</summary>
    public string? FormatString { get; init; }

    public override void Validate(LabelDefinition label)
    {
        base.Validate(label);
        if (FontHeightMm <= 0)
        {
            throw new LabelDefinitionException($"Text element '{Id}' has a font height of zero.");
        }
        if (DataKey is null && string.IsNullOrEmpty(Text))
        {
            throw new LabelDefinitionException(
                $"Text element '{Id}' has neither static text nor a data key, so it would print nothing.");
        }
        if (Alignment != TextAlignment.Left && BlockWidthMm is null)
        {
            throw new LabelDefinitionException(
                $"Text element '{Id}' is {Alignment}-aligned but has no block width to align within.");
        }
    }
}

public sealed record BarcodeElement : LabelElement
{
    [JsonIgnore]
    public override FieldDataKind Kind => FieldDataKind.Barcode;

    /// <summary>Still TBD with the client (C-6). Configurable precisely because
    /// it is not yet decided.</summary>
    public BarcodeSymbology Symbology { get; init; } = BarcodeSymbology.Code128;

    public decimal HeightMm { get; init; } = 12m;

    /// <summary>Narrow-bar width in dots. 2 is the common default at 203 dpi;
    /// scanners need this tuned to the media, so it is configuration.</summary>
    public int ModuleWidthDots { get; init; } = 2;

    public bool ShowHumanReadable { get; init; } = true;

    public override void Validate(LabelDefinition label)
    {
        base.Validate(label);
        if (DataKey is null)
        {
            throw new LabelDefinitionException($"Barcode '{Id}' has no data key to encode.");
        }
        if (HeightMm <= 0)
        {
            throw new LabelDefinitionException($"Barcode '{Id}' has no height.");
        }
        if (ModuleWidthDots is < 1 or > 10)
        {
            throw new LabelDefinitionException(
                $"Barcode '{Id}' module width {ModuleWidthDots} is outside the printable range 1-10.");
        }
    }
}

public sealed record QrElement : LabelElement
{
    [JsonIgnore]
    public override FieldDataKind Kind => FieldDataKind.QrCode;

    /// <summary>Module magnification 1-10; the QR's printed size is a multiple
    /// of this, so it is how the QR is sized.</summary>
    public int Magnification { get; init; } = 4;

    /// <summary>Error correction: H, Q, M or L. H survives a scuffed label but
    /// encodes less.</summary>
    public string ErrorCorrection { get; init; } = "M";

    public override void Validate(LabelDefinition label)
    {
        base.Validate(label);
        if (Magnification is < 1 or > 10)
        {
            throw new LabelDefinitionException(
                $"QR '{Id}' magnification {Magnification} is outside the printable range 1-10.");
        }
        if (ErrorCorrection is not ("H" or "Q" or "M" or "L"))
        {
            throw new LabelDefinitionException(
                $"QR '{Id}' error correction '{ErrorCorrection}' must be H, Q, M or L.");
        }
    }
}

public sealed record ImageElement : LabelElement
{
    [JsonIgnore]
    public override FieldDataKind Kind => FieldDataKind.Image;

    public required decimal WidthMm { get; init; }
    public required decimal HeightMm { get; init; }

    public override void Validate(LabelDefinition label)
    {
        base.Validate(label);
        if (WidthMm <= 0 || HeightMm <= 0)
        {
            throw new LabelDefinitionException($"Image '{Id}' has no size.");
        }
    }
}

/// <summary>A rule or frame. Zero-thickness fills produce a solid block, which
/// is how ZPL draws a filled rectangle.</summary>
public sealed record BoxElement : LabelElement
{
    [JsonIgnore]
    public override FieldDataKind Kind => FieldDataKind.Text;

    public required decimal WidthMm { get; init; }
    public required decimal HeightMm { get; init; }
    public decimal ThicknessMm { get; init; } = 0.3m;

    public override void Validate(LabelDefinition label)
    {
        base.Validate(label);
        if (WidthMm < 0 || HeightMm < 0)
        {
            throw new LabelDefinitionException($"Box '{Id}' has a negative size.");
        }
    }
}
