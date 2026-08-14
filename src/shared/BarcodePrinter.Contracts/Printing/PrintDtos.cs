namespace BarcodePrinter.Contracts.Printing;

public sealed record PrinterDto(
    long Id, string Code, string Name, string? Location,
    string ConnectionType, string DispatchMode, string? Host, int? Port,
    string? WindowsPrinterName, string? OwnerWorkstation, short? Dpi,
    string Language, bool SupportsStatusQuery, bool IsActive, bool IsDefault);

public sealed record SavePrinterRequest(
    string Code, string Name, string? Location,
    string ConnectionType, string DispatchMode, string? Host, int? Port,
    string? WindowsPrinterName, string? OwnerWorkstation, short? Dpi,
    string Language, bool SupportsStatusQuery, bool IsActive);

/// <summary>Print request. Effective values are the post-override ones (A-9);
/// carton range is supplied only when the strategy requires it (C-11).</summary>
public sealed record PrintRequest(
    long ProductId,
    long TemplateId,
    long PrinterId,
    string? Batch,
    DateOnly? ProductionDate,
    DateOnly? ExpiryDate,
    string? QuantityText,
    long? CartonFrom,
    long? CartonTo,
    int LabelCount,
    short CopiesPerLabel,
    string? Workstation);

public sealed record PrintJobCreatedResponse(
    long JobId, string JobNo, long CartonFrom, long CartonTo, int LabelCount);

public sealed record PrintJobDto(
    long Id, string JobNo, DateTime RequestedAtUtc, string RequestedBy,
    string PrinterName, string TemplateCode, int TemplateVersion,
    string ProductCode, string Description,
    string? Batch, DateOnly? ProductionDate, DateOnly? ExpiryDate, string? QuantityText,
    long? CartonFrom, long? CartonTo, int LabelCount, short CopiesPerLabel,
    string Status, DateTime? DispatchedAtUtc, DateTime? ConfirmedAtUtc,
    int LabelsConfirmed, string? ErrorCode, string? ErrorMessage,
    bool IsReprint, long? SourceJobId, string? ReprintReason);

public sealed record PrintHistoryFilter(
    DateTime? FromUtc, DateTime? ToUtc, long? ProductId, long? UserId,
    long? PrinterId, string? Status, bool? ReprintsOnly, string? Search,
    string? Cursor, int PageSize);

public sealed record ReprintRequest(long SourceJobId, string? Reason, string? Workstation);

/// <summary>Client dispatcher status callback. The server validates every
/// transition — the client never invents state.</summary>
public sealed record UpdateJobStatusRequest(
    string Status, int? LabelsConfirmed, string? ErrorCode, string? ErrorMessage);

public sealed record PrintPreviewRequest(
    long ProductId, long TemplateId,
    string? Batch, DateOnly? ProductionDate, DateOnly? ExpiryDate,
    string? QuantityText, long? CartonNumber, long? CartonTotal);

/// <summary>
/// Preview result. The PNG is what the operator checks; the ZPL is retained for
/// support and for templates that cannot be drawn faithfully, where
/// <paramref name="Unavailable"/> explains why the picture is absent.
/// </summary>
public sealed record PrintPreviewResponse(
    string? PngBase64,
    string Zpl,
    string Format,
    string? Unavailable);
