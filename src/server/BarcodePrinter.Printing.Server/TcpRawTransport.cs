using System.Net.Sockets;
using System.Text;
using BarcodePrinter.Printing.Abstractions;
using Microsoft.Extensions.Logging;

namespace BarcodePrinter.Printing.Server;

/// <summary>
/// Raw TCP to port 9100 — Zebra printers with no Windows queue. The whole job
/// goes over ONE socket which is then closed: two jobs can never interleave on
/// the wire (blueprint §8.3). No driver is written; the printer's own firmware
/// interprets the ZPL.
/// </summary>
public sealed class TcpRawTransport(ILogger<TcpRawTransport> logger) : IPrintTransport
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(3);

    public PrinterConnectionKind Kind => PrinterConnectionKind.NetworkTcp;

    public async Task<PrintOutcome> SendAsync(
        PrinterTarget target, PrintPayload payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target.Host))
        {
            return PrintOutcome.Failed(PrintErrorCodes.Unreachable,
                $"Printer '{target.Name}' has no network address configured.");
        }

        try
        {
            using var client = new TcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);

            await client.ConnectAsync(target.Host, target.Port ?? 9100, connectCts.Token);

            await using var stream = client.GetStream();
            using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            writeCts.CancelAfter(WriteTimeout);

            await stream.WriteAsync(payload.Data, writeCts.Token);
            await stream.FlushAsync(writeCts.Token);

            if (target.SupportsStatusQuery)
            {
                var status = await ReadStatusAsync(stream, ct);
                if (status is { IsReady: false })
                {
                    return PrintOutcome.Failed(PrintErrorCodes.NotReady,
                        status.Message ?? "The printer is not ready.");
                }
            }

            return PrintOutcome.Dispatched();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return PrintOutcome.Failed(PrintErrorCodes.Timeout,
                $"Printer '{target.Name}' did not respond in time.");
        }
        catch (SocketException ex)
        {
            logger.LogWarning(ex, "TCP print failed for {Printer}", target.Name);
            return PrintOutcome.Failed(PrintErrorCodes.Unreachable,
                $"Printer '{target.Name}' is not responding. Check that it is switched on and connected.");
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "TCP print I/O error for {Printer}", target.Name);
            return PrintOutcome.Failed(PrintErrorCodes.Unreachable,
                $"The connection to printer '{target.Name}' was lost during printing.");
        }
    }

    public async Task<PrinterStatus> QueryStatusAsync(PrinterTarget target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target.Host) || !target.SupportsStatusQuery)
        {
            return PrinterStatus.Unknown;
        }
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(target.Host, target.Port ?? 9100, cts.Token);

            await using var stream = client.GetStream();
            return await ReadStatusAsync(stream, ct) ?? PrinterStatus.Unknown;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            return new PrinterStatus(false, false, false, false, "Printer is not responding.");
        }
    }

    /// <summary>Issues ~HQES and parses the error-status response. Printers that
    /// do not answer simply leave status Unknown — never a failed print.</summary>
    private static async Task<PrinterStatus?> ReadStatusAsync(NetworkStream stream, CancellationToken ct)
    {
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("~HQES"), ct);
            await stream.FlushAsync(ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(StatusTimeout);

            var buffer = new byte[512];
            var read = await stream.ReadAsync(buffer, cts.Token);
            if (read <= 0)
            {
                return null;
            }

            var text = Encoding.ASCII.GetString(buffer, 0, read);
            return ParseHqes(text);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return null;   // status is best-effort
        }
    }

    /// <summary>~HQES returns "PRINTER STATUS / ERRORS: nnnnnnnn / WARNINGS:…".
    /// Bit 1 of the error nibble group flags media out, bit 2 head open.</summary>
    internal static PrinterStatus ParseHqes(string response)
    {
        var mediaOut = response.Contains("MEDIA OUT", StringComparison.OrdinalIgnoreCase);
        var headOpen = response.Contains("HEAD OPEN", StringComparison.OrdinalIgnoreCase);
        var paused = response.Contains("PAUSED", StringComparison.OrdinalIgnoreCase);

        if (!mediaOut && !headOpen)
        {
            var index = response.IndexOf("ERRORS:", StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var digits = new string(response[(index + 7)..]
                    .TakeWhile(c => char.IsDigit(c) || c == ' ')
                    .Where(char.IsDigit).ToArray());
                if (digits.Length >= 8)
                {
                    mediaOut = digits[^1] == '1';
                    headOpen = digits[^2] == '1';
                }
            }
        }

        var message = (mediaOut, headOpen, paused) switch
        {
            (true, _, _) => "The printer is out of labels.",
            (_, true, _) => "The printer head is open.",
            (_, _, true) => "The printer is paused.",
            _ => null,
        };
        return new PrinterStatus(true, paused, mediaOut, headOpen, message);
    }
}

/// <summary>Writes printer bytes to disk. Development, support and the
/// golden-file path — lets ZPL be inspected without hardware.</summary>
public sealed class FilePrintTransport(string rootPath) : IPrintTransport
{
    public PrinterConnectionKind Kind => PrinterConnectionKind.File;

    public async Task<PrintOutcome> SendAsync(
        PrinterTarget target, PrintPayload payload, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(rootPath);
            var path = Path.Combine(rootPath, $"{payload.JobNo}.zpl");
            await File.WriteAllBytesAsync(path, payload.Data, ct);
            return PrintOutcome.Dispatched();
        }
        catch (IOException ex)
        {
            return PrintOutcome.Failed(PrintErrorCodes.SpoolerError, ex.Message);
        }
    }

    public Task<PrinterStatus> QueryStatusAsync(PrinterTarget target, CancellationToken ct) =>
        Task.FromResult(PrinterStatus.Unknown);
}
