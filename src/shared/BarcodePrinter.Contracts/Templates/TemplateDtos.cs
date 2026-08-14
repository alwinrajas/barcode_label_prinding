namespace BarcodePrinter.Contracts.Templates;

public sealed record TemplateSummary(
    long Id, string Code, string Name, string TemplateFormat,
    decimal? WidthMm, decimal? HeightMm, short? Dpi,
    int CurrentVersion, bool IsActive, bool IsDefault, int MappedFieldCount);

public sealed record TemplateDetail(
    long Id, string Code, string Name, string? Description, string TemplateFormat,
    decimal? WidthMm, decimal? HeightMm, short? Dpi, decimal? GapMm,
    string? Orientation, string? LayoutType, string? MediaType, string? MediaTracking,
    int CurrentVersion, bool IsActive, bool IsDefault,
    long VersionId, string ArtifactFileName, string ArtifactHash,
    IReadOnlyList<TemplateFieldDto> Fields,
    IReadOnlyList<DetectedFieldDto> DetectedFields);

/// <summary>A variable field found in the client's artifact, offered to the
/// admin for mapping. Unmapped fields stay literal on the label.</summary>
public sealed record DetectedFieldDto(
    int CommandIndex, string InferredKind, string SampleValue,
    int? X, int? Y, string Context);

public sealed record TemplateFieldDto(
    long Id, string PlaceholderRef, string FieldLabel, string DataKey, string DataKind,
    string? FormatString, string Transform, int? MaxLength, string Overflow,
    bool IsRequired, string? FallbackValue, string? SampleValue, int CommandIndex);

public sealed record RegisterTemplateRequest(
    string Code, string Name, string? Description, string TemplateFormat,
    decimal? WidthMm, decimal? HeightMm, short? Dpi, decimal? GapMm,
    string? Orientation, string? LayoutType, string? MediaType, string? MediaTracking);

public sealed record SaveFieldMappingRequest(IReadOnlyList<FieldMappingInput> Fields);

public sealed record FieldMappingInput(
    int CommandIndex, string PlaceholderRef, string FieldLabel,
    string DataKey, string DataKind, string? FormatString,
    string Transform, int? MaxLength, string Overflow,
    bool IsRequired, string? FallbackValue, string? SampleValue);

/// <summary>Vocabulary served to the mapping UI so it can offer only valid
/// keys (blueprint §5.2 closed vocabulary).</summary>
public sealed record TemplateVocabularyDto(
    IReadOnlyList<string> DataKeys,
    IReadOnlyList<string> DataKinds,
    IReadOnlyList<string> Transforms,
    IReadOnlyList<string> Overflows,
    IReadOnlyList<string> Symbologies,
    string QrOnlyKey);
