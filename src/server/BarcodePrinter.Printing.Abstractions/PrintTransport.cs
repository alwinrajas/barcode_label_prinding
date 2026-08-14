namespace BarcodePrinter.Printing.Abstractions;

public enum PrinterConnectionKind { NetworkTcp, WindowsRaw, WindowsGraphics, File }

public sealed record PrinterTarget(
    long PrinterId,
    string Name,
    PrinterConnectionKind Kind,
    string? Host,
    int? Port,
    string? WindowsPrinterName,
    bool SupportsStatusQuery);

public sealed record PrintPayload(string JobNo, byte[] Data);

public enum PrintOutcomeKind { Dispatched, Confirmed, Failed }

public sealed record PrintOutcome(
    PrintOutcomeKind Kind, string? ErrorCode = null, string? ErrorMessage = null,
    int? LabelsConfirmed = null)
{
    public static PrintOutcome Dispatched() => new(PrintOutcomeKind.Dispatched);
    public static PrintOutcome Confirmed(int labels) =>
        new(PrintOutcomeKind.Confirmed, LabelsConfirmed: labels);
    public static PrintOutcome Failed(string code, string message) =>
        new(PrintOutcomeKind.Failed, code, message);
}

public sealed record PrinterStatus(
    bool IsOnline, bool IsPaused, bool IsMediaOut, bool IsHeadOpen, string? Message)
{
    public static PrinterStatus Unknown => new(true, false, false, false, null);
    public bool IsReady => IsOnline && !IsPaused && !IsMediaOut && !IsHeadOpen;
}

/// <summary>
/// How bytes physically reach a printer. Implementations live server-side
/// (TCP/File) and client-side (Windows spooler) — the job model and status
/// lifecycle are identical for both (blueprint §7.2/§7.3).
/// </summary>
public interface IPrintTransport
{
    PrinterConnectionKind Kind { get; }
    Task<PrintOutcome> SendAsync(PrinterTarget target, PrintPayload payload, CancellationToken ct);
    Task<PrinterStatus> QueryStatusAsync(PrinterTarget target, CancellationToken ct);
}

public static class PrintErrorCodes
{
    public const string Unreachable = "PRINTER_UNREACHABLE";
    public const string UsbFault = "PRINTER_USB_FAULT";
    public const string SpoolerError = "PRINTER_SPOOLER_ERROR";
    public const string Timeout = "PRINT_TIMEOUT";
    public const string ClientLost = "CLIENT_LOST";
    public const string NotReady = "PRINTER_NOT_READY";
}
