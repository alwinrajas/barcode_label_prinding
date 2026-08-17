using System.Collections.Concurrent;
using BarcodePrinter.Contracts.Printing;

namespace BarcodePrinter.Infrastructure.Printing;

/// <summary>
/// Latest state each workstation reported for its own Windows queues.
///
/// Deliberately in memory and not in the database: this is a live reading with a
/// useful life of seconds, and persisting it would only create rows that outlive
/// their truth. After a restart every workstation re-reports within one poll,
/// and until then the status reads "waiting for the workstation" — which is
/// accurate, rather than a stale "Online" recovered from disk.
/// </summary>
public sealed class LocalPrinterStatusCache
{
    /// <summary>A report older than this tells us nothing current.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(45);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private static string Key(string workstation, string printerName) =>
        $"{workstation}{printerName}";

    public void Report(string workstation, IReadOnlyList<WorkstationPrinterStatus> printers)
    {
        var now = DateTime.UtcNow;
        foreach (var printer in printers)
        {
            _entries[Key(workstation, printer.WindowsPrinterName)] =
                new Entry(printer.Availability, printer.StatusText, now);
        }
    }

    /// <summary>Null when that workstation has not reported this queue recently
    /// — the caller must say so rather than assume the printer is fine.</summary>
    public (string Availability, string StatusText)? TryGet(string? workstation, string? printerName)
    {
        if (string.IsNullOrWhiteSpace(workstation) || string.IsNullOrWhiteSpace(printerName))
        {
            return null;
        }

        if (!_entries.TryGetValue(Key(workstation, printerName), out var entry) ||
            DateTime.UtcNow - entry.At > Freshness)
        {
            return null;
        }

        return (entry.Availability, entry.StatusText);
    }

    private sealed record Entry(string Availability, string StatusText, DateTime At);
}
