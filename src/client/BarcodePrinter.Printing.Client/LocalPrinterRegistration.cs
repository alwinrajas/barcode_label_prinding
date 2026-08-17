using System.Text;
using BarcodePrinter.Contracts.Printing;
using BarcodePrinter.Printing.Abstractions;

namespace BarcodePrinter.Printing.Client;

/// <summary>
/// Turns a printer Windows already has into a registration the server can
/// dispatch to, and prints the local test label.
///
/// The routing decision lives here and is not a preference: a queue installed
/// on THIS workstation is reachable only from this workstation, so the job must
/// be client-dispatched through the spooler. The server cannot open a USB
/// printer on someone's PC, and for a shared network queue the spooler already
/// owns retry and ordering — sending raw TCP from the server would duplicate
/// that badly and bypass the driver the device expects (§7.3 / A-19).
/// </summary>
public static class LocalPrinterRegistration
{
    /// <summary>Windows queue names allow characters a printer code does not,
    /// so the code is derived rather than copied.</summary>
    public static string CodeFor(string printerName, string workstation)
    {
        var slug = new string([.. printerName.ToUpperInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')]);

        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }
        slug = slug.Trim('-');

        // Same queue name on two workstations is two different devices.
        var prefix = workstation.ToUpperInvariant();
        var code = $"{prefix}-{slug}";
        return code.Length <= 32 ? code : code[..32].TrimEnd('-');
    }

    public static SavePrinterRequest ToRequest(
        DiscoveredPrinter printer, string workstation, short dpi = 203,
        string? existingCode = null)
    {
        var raw = printer.Kind == PrinterConnectionKind.WindowsRaw;

        return new SavePrinterRequest(
            Code: existingCode ?? CodeFor(printer.Name, workstation),
            Name: printer.Name,
            Location: printer.IsNetworkQueue ? "Network queue" : workstation,
            ConnectionType: printer.Kind.ToString(),
            // Always Client: see the class remarks. Nothing about a Windows
            // queue is reachable from the server.
            DispatchMode: "Client",
            // No host or port. That is the entire point — the spooler resolves
            // the device, so there is nothing for an operator to type.
            Host: null,
            Port: null,
            WindowsPrinterName: printer.Name,
            OwnerWorkstation: workstation,
            Dpi: dpi,
            Language: raw ? "Zpl" : "Windows",
            // Windows reports queue state, not media state; ~HQES is a Zebra
            // conversation we cannot have through the driver.
            SupportsStatusQuery: false,
            IsActive: true);
    }

    /// <summary>
    /// The test label, printed locally through the same transport a real job
    /// uses. Testing through a different path would prove nothing about the
    /// path production jobs take.
    /// </summary>
    public static async Task<PrintOutcome> TestAsync(
        DiscoveredPrinter printer, IEnumerable<IPrintTransport> transports,
        Func<byte[]> rasterTestPage, CancellationToken ct)
    {
        var transport = transports.FirstOrDefault(t => t.Kind == printer.Kind);
        if (transport is null)
        {
            return PrintOutcome.Failed(PrintErrorCodes.SpoolerError,
                $"No transport is available for {printer.Kind} printers.");
        }

        var target = new PrinterTarget(
            0, printer.Name, printer.Kind, null, null, printer.Name, false);

        var payload = printer.Kind == PrinterConnectionKind.WindowsRaw
            ? Encoding.UTF8.GetBytes(ZplTestLabel)
            : rasterTestPage();

        return await transport.SendAsync(target, new PrintPayload("TEST", payload), ct);
    }

    /// <summary>Deliberately small and self-describing: it proves the path to
    /// paper, and an operator who finds one in a stack knows what it is.</summary>
    private const string ZplTestLabel =
        "^XA^CI28" +
        "^FO40,40^A0N,36,36^FDBarcode Label Printing^FS" +
        "^FO40,90^A0N,28,28^FDTest label^FS" +
        "^FO40,140^BY2,3.0,60^BCN,60,Y,N,N^FDTEST-LABEL^FS" +
        "^XZ";
}
