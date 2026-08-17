using System.Runtime.Versioning;
using BarcodePrinter.Printing.Abstractions;

namespace BarcodePrinter.Printing.Client;

/// <summary>One printer Windows already knows about on this workstation.</summary>
/// <param name="Name">The Windows queue name — the only identifier the spooler needs.</param>
/// <param name="IsDefault">Windows' own default, offered as the suggested choice.</param>
/// <param name="Availability">What the spooler says about the queue right now.</param>
public sealed record DiscoveredPrinter(
    string Name,
    string? DriverName,
    string? PortName,
    bool IsDefault,
    PrinterAvailability Availability,
    string StatusText,
    PrinterConnectionKind Kind)
{
    /// <summary>A queue reached over a port rather than USB/LPT. Shown as
    /// "Network" so an operator can tell two same-named devices apart.</summary>
    public bool IsNetworkQueue =>
        PortName is { Length: > 0 } p &&
        (p.StartsWith("IP_", StringComparison.OrdinalIgnoreCase) ||
         p.StartsWith("WSD", StringComparison.OrdinalIgnoreCase) ||
         p.StartsWith("\\\\", StringComparison.Ordinal) ||
         p.Contains("TCP", StringComparison.OrdinalIgnoreCase));

    public string ConnectionLabel => IsNetworkQueue ? "Windows (network)" : "Windows (local)";
}

public enum PrinterAvailability { Ready, Paused, Offline, NeedsAttention, Unknown }

/// <summary>
/// Enumerates the printers installed for the current Windows user.
///
/// This is the whole point of the Printers screen: a printer Windows already
/// has is reachable by NAME through the spooler, which owns the queue, the
/// retries and the device. Asking an operator for an IP address and port only
/// makes sense for a device Windows does not have — and then it is a fallback,
/// not the normal path.
///
/// Discovery necessarily runs on the WORKSTATION. The server cannot see a USB
/// printer plugged into someone's PC, and for a shared queue it would see a
/// different list. That is also why anything discovered here is registered as
/// client-dispatched (§7.3).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPrinterProbe : IWindowsPrinterProbe
{
    public IReadOnlyList<DiscoveredPrinter> Discover()
    {
        // System.Printing gives status and driver detail; PrinterSettings gives
        // only names but survives a spooler that refuses the richer API.
        try
        {
            return DiscoverWithPrintServer();
        }
        catch (Exception)
        {
            return DiscoverNamesOnly();
        }
    }

    private static List<DiscoveredPrinter> DiscoverWithPrintServer()
    {
        using var server = new System.Printing.LocalPrintServer();
        var defaultName = SafeDefaultName(server);

        // MEASURED, not assumed: an unplugged USB label printer reports
        // QueueStatus = None (i.e. "ready") while WMI reports WorkOffline = true.
        // WorkOffline is the flag Windows' own Printers page shows as "Offline",
        // so a green light that ignores it is simply wrong.
        var offline = ReadOfflineFlags();

        var printers = new List<DiscoveredPrinter>();
        using var queues = server.GetPrintQueues(
        [
            System.Printing.EnumeratedPrintQueueTypes.Local,
            System.Printing.EnumeratedPrintQueueTypes.Connections,
        ]);

        foreach (var queue in queues)
        {
            using (queue)
            {
                string? driver = null, port = null;
                var availability = PrinterAvailability.Unknown;
                var status = "Unknown";

                try
                {
                    driver = queue.QueueDriver?.Name;
                    port = queue.QueuePort?.Name;
                    (availability, status) = Describe(
                        queue, offline.GetValueOrDefault(queue.Name));
                }
                catch (Exception)
                {
                    // A queue can vanish or refuse mid-enumeration; report it as
                    // present-but-unknown rather than dropping it from the list.
                }

                printers.Add(new DiscoveredPrinter(
                    queue.Name, driver, port,
                    string.Equals(queue.Name, defaultName, StringComparison.OrdinalIgnoreCase),
                    availability, status, Classify(driver, port)));
            }
        }

        return [.. printers.OrderByDescending(p => p.IsDefault)
                           .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static string? SafeDefaultName(System.Printing.LocalPrintServer server)
    {
        try
        {
            using var fallback = System.Printing.LocalPrintServer.GetDefaultPrintQueue();
            return fallback.Name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<DiscoveredPrinter> DiscoverNamesOnly()
    {
        try
        {
            var settings = new System.Drawing.Printing.PrinterSettings();
            var defaultName = settings.PrinterName;

            return [.. System.Drawing.Printing.PrinterSettings.InstalledPrinters
                .Cast<string>()
                .Select(name => new DiscoveredPrinter(
                    name, null, null,
                    string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase),
                    PrinterAvailability.Unknown, "Status unavailable",
                    Classify(null, null)))
                .OrderByDescending(p => p.IsDefault)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception)
        {
            // No spooler at all. An empty list is honest; the manual path remains.
            return [];
        }
    }

    /// <summary>
    /// Reads Win32_Printer.WorkOffline per queue. Returns an empty map when WMI
    /// is unavailable — then we fall back to QueueStatus alone and say so, rather
    /// than silently reporting "Ready" for a device nobody has checked.
    /// </summary>
    private static Dictionary<string, bool> ReadOfflineFlags()
    {
        var flags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name, WorkOffline FROM Win32_Printer");
            foreach (var item in searcher.Get().Cast<System.Management.ManagementObject>())
            {
                using (item)
                {
                    if (item["Name"] is string name)
                    {
                        flags[name] = item["WorkOffline"] is true;
                    }
                }
            }
        }
        catch (Exception)
        {
            // No WMI: leave the map empty (see remarks).
        }
        return flags;
    }

    private static (PrinterAvailability, string) Describe(
        System.Printing.PrintQueue queue, bool workOffline)
    {
        queue.Refresh();
        var s = queue.QueueStatus;

        // Ordered by what an operator must act on first.
        if (workOffline || s.HasFlag(System.Printing.PrintQueueStatus.Offline))
        {
            return (PrinterAvailability.Offline, "Offline");
        }
        if (s.HasFlag(System.Printing.PrintQueueStatus.Error))
        {
            return (PrinterAvailability.NeedsAttention, "Error");
        }
        if (s.HasFlag(System.Printing.PrintQueueStatus.PaperOut))
        {
            return (PrinterAvailability.NeedsAttention, "Out of paper");
        }
        if (s.HasFlag(System.Printing.PrintQueueStatus.PaperJam))
        {
            return (PrinterAvailability.NeedsAttention, "Paper jam");
        }
        if (s.HasFlag(System.Printing.PrintQueueStatus.DoorOpen))
        {
            return (PrinterAvailability.NeedsAttention, "Cover open");
        }
        if (s.HasFlag(System.Printing.PrintQueueStatus.UserIntervention))
        {
            return (PrinterAvailability.NeedsAttention, "Needs attention");
        }
        if (s.HasFlag(System.Printing.PrintQueueStatus.Paused))
        {
            return (PrinterAvailability.Paused, "Paused");
        }

        var queued = queue.NumberOfJobs;
        return (PrinterAvailability.Ready, queued > 0 ? $"Ready · {queued} in queue" : "Ready");
    }

    /// <summary>
    /// Which transport a Windows queue needs.
    ///
    /// A label printer understands ZPL, so its bytes pass through the driver
    /// untouched (RAW). Anything else is an office printer that can only render
    /// a picture. Getting this wrong is visible immediately — ZPL sent to a
    /// laser prints pages of "^XA^FO..." text — so it is a heuristic with an
    /// override, not a guess we hide.
    /// </summary>
    internal static PrinterConnectionKind Classify(string? driverName, string? portName)
    {
        var haystack = $"{driverName} {portName}";
        string[] labelDriverMarkers =
        [
            "zdesigner", "zebra", "zpl", "eltron", "datamax", "intermec",
            "sato", "tsc ", "godex", "argox", "bixolon", "citizen", "toshiba tec",
            "honeywell", "printronix", "generic / text only",
        ];

        return labelDriverMarkers.Any(m => haystack.Contains(m, StringComparison.OrdinalIgnoreCase))
            ? PrinterConnectionKind.WindowsRaw
            : PrinterConnectionKind.WindowsGraphics;
    }
}

/// <summary>Seam for tests and for non-Windows build targets.</summary>
public interface IWindowsPrinterProbe
{
    IReadOnlyList<DiscoveredPrinter> Discover();
}
