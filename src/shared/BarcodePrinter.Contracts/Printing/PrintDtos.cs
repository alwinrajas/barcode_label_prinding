namespace BarcodePrinter.Contracts.Printing;

public sealed record PrinterDto(
    long Id, string Code, string Name, string? Location,
    string ConnectionType, string DispatchMode, string? Host, int? Port,
    string? WindowsPrinterName, string? OwnerWorkstation, short? Dpi,
    string Language, bool SupportsStatusQuery, bool IsActive, bool IsDefault,
    DateTime? LastSeenUtc = null);

/// <summary>Live reachability of one printer. For network printers this is a
/// real connection probe; for client-dispatched printers it reflects whether
/// the owning workstation has polled recently.</summary>
public sealed record PrinterStatusDto(
    long PrinterId, bool Online, string? Detail, DateTime? LastSeenUtc);

public sealed record SavePrinterRequest(
    string Code, string Name, string? Location,
    string ConnectionType, string DispatchMode, string? Host, int? Port,
    string? WindowsPrinterName, string? OwnerWorkstation, short? Dpi,
    string Language, bool SupportsStatusQuery, bool IsActive);

/// <summary>Print request. Effective values are the post-override ones (A-9);
/// carton range is supplied only when the strategy requires it (C-11).
/// TemplateId null → the server resolves it: product default, then printer
/// default, then the global default template (§15 — operators never pick one).</summary>
public sealed record PrintRequest(
    long ProductId,
    long? TemplateId,
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

/// <summary>DispatchMode/OwnerWorkstation let the client say honestly where
/// the job goes next: "sent to printer" for server dispatch versus "waiting
/// for workstation X to collect it" for client dispatch.</summary>
public sealed record PrintJobCreatedResponse(
    long JobId, string JobNo, long CartonFrom, long CartonTo, int LabelCount,
    string? DispatchMode = null, string? OwnerWorkstation = null);

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
    long ProductId, long? TemplateId,
    string? Batch, DateOnly? ProductionDate, DateOnly? ExpiryDate,
    string? QuantityText, long? CartonNumber, long? CartonTotal,
    long? PrinterId = null);

/// <summary>
/// Preview result. The PNG is what the operator checks; the ZPL is retained for
/// support and for templates that cannot be drawn faithfully, where
/// <paramref name="Unavailable"/> explains why the picture is absent.
/// </summary>
public sealed record PrintPreviewResponse(
    string? PngBase64,
    string Zpl,
    string Format,
    string? Unavailable,
    string? Warning = null);

/// <summary>
/// What a workstation reports about one of its own Windows queues.
///
/// The server cannot see a printer attached to somebody's PC, so "is this
/// printer available?" can only be answered by that PC. Without this the server
/// can only say whether the WORKSTATION is running — which is a different
/// question, and answering it as though it were the printer's state shows a
/// green light next to an unplugged printer.
/// </summary>
public sealed record WorkstationPrinterStatus(
    string WindowsPrinterName, string Availability, string StatusText);

public sealed record ReportLocalPrintersRequest(
    string Workstation, IReadOnlyList<WorkstationPrinterStatus> Printers);
