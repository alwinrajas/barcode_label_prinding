using System.Runtime.InteropServices;
using BarcodePrinter.Printing.Abstractions;

namespace BarcodePrinter.Printing.Client;

/// <summary>
/// Sends ZPL to a printer installed in Windows, using the spooler's RAW
/// datatype (blueprint §7.2 / A-19). The vendor driver and the spooler do all
/// the device work — we write no driver and touch no USB stack; the bytes are
/// simply passed through untouched.
/// </summary>
public sealed class WindowsRawTransport : IPrintTransport
{
    public PrinterConnectionKind Kind => PrinterConnectionKind.WindowsRaw;

    public Task<PrintOutcome> SendAsync(
        PrinterTarget target, PrintPayload payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target.WindowsPrinterName))
        {
            return Task.FromResult(PrintOutcome.Failed(PrintErrorCodes.SpoolerError,
                $"Printer '{target.Name}' has no Windows printer name configured."));
        }

        // Blocking Win32 calls: run off the UI thread so printing never freezes
        // the app (A-20).
        return Task.Run(() => SendCore(target, payload), ct);
    }

    private static PrintOutcome SendCore(PrinterTarget target, PrintPayload payload)
    {
        var printerName = target.WindowsPrinterName!;
        var printerHandle = IntPtr.Zero;
        var unmanaged = IntPtr.Zero;

        try
        {
            if (!NativeMethods.OpenPrinter(printerName, out printerHandle, IntPtr.Zero))
            {
                return FromWin32(Marshal.GetLastWin32Error(), target.Name,
                    $"Windows could not open printer '{printerName}'.");
            }

            var docInfo = new NativeMethods.DocInfo1
            {
                DocName = $"Label {payload.JobNo}",
                DataType = "RAW",   // pass ZPL straight through — no rendering
                OutputFile = null,
            };

            if (!NativeMethods.StartDocPrinter(printerHandle, 1, docInfo))
            {
                return FromWin32(Marshal.GetLastWin32Error(), target.Name,
                    "Windows rejected the print job.");
            }

            try
            {
                if (!NativeMethods.StartPagePrinter(printerHandle))
                {
                    return FromWin32(Marshal.GetLastWin32Error(), target.Name,
                        "Windows rejected the print page.");
                }

                unmanaged = Marshal.AllocCoTaskMem(payload.Data.Length);
                Marshal.Copy(payload.Data, 0, unmanaged, payload.Data.Length);

                if (!NativeMethods.WritePrinter(
                        printerHandle, unmanaged, payload.Data.Length, out var written) ||
                    written != payload.Data.Length)
                {
                    return FromWin32(Marshal.GetLastWin32Error(), target.Name,
                        "The printer connection was lost while sending the labels.");
                }

                NativeMethods.EndPagePrinter(printerHandle);
            }
            finally
            {
                NativeMethods.EndDocPrinter(printerHandle);
            }

            // The spooler accepted the bytes: Dispatched, not Confirmed (§8.5).
            return PrintOutcome.Dispatched();
        }
        catch (Exception ex)
        {
            return PrintOutcome.Failed(PrintErrorCodes.SpoolerError, ex.Message);
        }
        finally
        {
            if (unmanaged != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(unmanaged);
            }
            if (printerHandle != IntPtr.Zero)
            {
                NativeMethods.ClosePrinter(printerHandle);
            }
        }
    }

    /// <summary>Maps Win32 codes to the operator-facing message and the stable
    /// error code the history screen filters on.</summary>
    private static PrintOutcome FromWin32(int error, string printerName, string fallback) => error switch
    {
        1801 => PrintOutcome.Failed(PrintErrorCodes.SpoolerError,   // ERROR_INVALID_PRINTER_NAME
            $"Windows does not have a printer called '{printerName}'. Check the printer setup."),
        1722 or 1723 => PrintOutcome.Failed(PrintErrorCodes.SpoolerError,   // RPC unavailable
            "The Windows Print Spooler service is not running on this PC."),
        2 or 3 or 1167 => PrintOutcome.Failed(PrintErrorCodes.UsbFault,   // not found / device not connected
            $"Printer '{printerName}' is not connected. Check the cable and that it is switched on."),
        1784 or 6 => PrintOutcome.Failed(PrintErrorCodes.UsbFault,
            $"The connection to printer '{printerName}' was lost."),
        _ => PrintOutcome.Failed(PrintErrorCodes.SpoolerError, $"{fallback} (Windows error {error})"),
    };

    /// <summary>Windows exposes no reliable status for RAW jobs; the spooler
    /// accepting bytes is all we can assert (C-17).</summary>
    public Task<PrinterStatus> QueryStatusAsync(PrinterTarget target, CancellationToken ct) =>
        Task.FromResult(PrinterStatus.Unknown);

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public sealed class DocInfo1
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string? DocName;
            [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string? DataType;
        }

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool OpenPrinter(string printerName, out IntPtr handle, IntPtr defaults);

        [DllImport("winspool.drv", SetLastError = true)]
        public static extern bool ClosePrinter(IntPtr handle);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool StartDocPrinter(IntPtr handle, int level, DocInfo1 docInfo);

        [DllImport("winspool.drv", SetLastError = true)]
        public static extern bool EndDocPrinter(IntPtr handle);

        [DllImport("winspool.drv", SetLastError = true)]
        public static extern bool StartPagePrinter(IntPtr handle);

        [DllImport("winspool.drv", SetLastError = true)]
        public static extern bool EndPagePrinter(IntPtr handle);

        [DllImport("winspool.drv", SetLastError = true)]
        public static extern bool WritePrinter(IntPtr handle, IntPtr buffer, int count, out int written);
    }
}
