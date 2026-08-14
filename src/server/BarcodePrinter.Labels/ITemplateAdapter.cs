using BarcodePrinter.Labels.Binding;

namespace BarcodePrinter.Labels;

/// <summary>A variable field discovered in a client-supplied template.</summary>
public sealed record DetectedField(
    int CommandIndex,
    FieldDataKind InferredKind,
    string SampleValue,
    int? X,
    int? Y,
    string Context);

/// <summary>Result of registering a template version: the prepared payload
/// plus what the admin must map.</summary>
/// <param name="PlaceholderPrefixes">
/// Data prefixes that are part of the ZPL field syntax rather than the value —
/// notably a QR code's `LA,` error-correction/input-mode indicator. These are
/// captured at Prepare time and re-applied at render time; dropping them
/// produces a corrupt symbol that only shows up on physical media.
/// </param>
public sealed record PreparedTemplate(
    string StoredFormatName,
    string DefinePayload,
    IReadOnlyList<DetectedField> Fields,
    IReadOnlyDictionary<string, string> PlaceholderPrefixes);

public sealed record RenderRequest(
    PreparedTemplate Template,
    IReadOnlyList<FieldMapping> Mappings,
    IReadOnlyDictionary<string, string> BoundValues,
    int Copies = 1);

/// <summary>
/// Per-format adapter (blueprint §4.2 / B-4). The unknown template format
/// (C-2) is a plug-in choice: register a new adapter, not a redesign.
/// </summary>
public interface ITemplateAdapter
{
    /// <summary>Format this adapter handles, matching label_templates.template_format.</summary>
    string Format { get; }

    /// <summary>Parses a client-supplied artifact and lists its variable fields
    /// so an admin can map them. Never mutates the artifact.</summary>
    IReadOnlyList<DetectedField> Inspect(string artifact);

    /// <summary>Rewrites the marked fields to placeholders and wraps the
    /// artifact as a reusable stored format. The client's geometry, fonts and
    /// static text pass through byte-for-byte (A-15).</summary>
    PreparedTemplate Prepare(
        string artifact, string storedFormatName,
        IReadOnlyDictionary<int, string> fieldPlaceholders);

    /// <summary>Per-label output: recall the stored format and supply values.</summary>
    string RenderRecall(RenderRequest request);

    /// <summary>Full inline output for printers without stored-format support
    /// (risk R-13). Same result, larger payload.</summary>
    string RenderInline(string artifact,
        IReadOnlyDictionary<int, string> fieldPlaceholders,
        IReadOnlyDictionary<string, string> boundValues, int copies = 1);
}
