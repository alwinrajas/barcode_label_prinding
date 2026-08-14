using System.Threading.Channels;
using BarcodePrinter.Client.Core;
using BarcodePrinter.Contracts.Printing;
using BarcodePrinter.Printing.Abstractions;
using Microsoft.Extensions.Logging;

namespace BarcodePrinter.Printing.Client;

/// <summary>
/// Client half of the hybrid dispatch model (blueprint §7.3/§8.4). A printer
/// plugged into this PC can only be reached from this PC, so the workstation
/// polls for its own jobs, claims them with a server lease, prints, and reports
/// the outcome back.
///
/// A SINGLE consumer drains the channel, so two jobs for the same local printer
/// can never interleave — the same guarantee the server gives network printers.
/// Everything runs off the UI thread.
/// </summary>
public sealed class ClientPrintDispatcher(
    PrintApi api,
    IEnumerable<IPrintTransport> transports,
    ILogger<ClientPrintDispatcher> logger) : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly Channel<long> _queue =
        Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<PrinterConnectionKind, IPrintTransport> _transports =
        transports.ToDictionary(t => t.Kind);
    private readonly CancellationTokenSource _cts = new();
    private Task? _pollLoop;
    private Task? _consumeLoop;

    public string Workstation { get; } = Environment.MachineName;

    /// <summary>Raised when a job finishes so the UI can refresh without polling.</summary>
    public event EventHandler<long>? JobCompleted;

    public void Start()
    {
        _pollLoop ??= Task.Run(() => PollAsync(_cts.Token));
        _consumeLoop ??= Task.Run(() => ConsumeAsync(_cts.Token));
    }

    private async Task PollAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                foreach (var jobId in await api.GetPendingAsync(Workstation, ct))
                {
                    // Claim before queueing: a second workstation mis-configured
                    // for the same printer must not take the same job.
                    if (await api.TryClaimAsync(jobId, Workstation, ct))
                    {
                        await _queue.Writer.WriteAsync(jobId, ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ApiUnreachableException)
            {
                // Server down: keep polling quietly, the status bar shows it.
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Print poll failed");
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        await foreach (var jobId in _queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                await PrintAsync(jobId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Local print failed for job {JobId}", jobId);
                await TryReportAsync(jobId, "Failed", PrintErrorCodes.SpoolerError, ex.Message);
            }
            finally
            {
                JobCompleted?.Invoke(this, jobId);
            }
        }
    }

    private async Task PrintAsync(long jobId, CancellationToken ct)
    {
        var job = await api.GetJobAsync(jobId, ct);
        var payload = await api.GetPayloadAsync(jobId, ct);
        if (payload is null)
        {
            await TryReportAsync(jobId, "Failed", PrintErrorCodes.SpoolerError,
                "The print data could not be downloaded.");
            return;
        }

        var printers = await api.ListPrintersAsync(activeOnly: false, ct);
        var printer = printers.FirstOrDefault(p => p.Name == job.PrinterName);
        if (printer is null)
        {
            await TryReportAsync(jobId, "Failed", PrintErrorCodes.SpoolerError,
                "This printer is no longer configured.");
            return;
        }

        var kind = printer.ConnectionType switch
        {
            "WindowsRaw" => PrinterConnectionKind.WindowsRaw,
            "WindowsGraphics" => PrinterConnectionKind.WindowsGraphics,
            "NetworkTcp" => PrinterConnectionKind.NetworkTcp,
            _ => PrinterConnectionKind.File,
        };

        if (!_transports.TryGetValue(kind, out var transport))
        {
            await TryReportAsync(jobId, "Failed", PrintErrorCodes.SpoolerError,
                $"This PC cannot print to {printer.ConnectionType} printers.");
            return;
        }

        await TryReportAsync(jobId, "Printing", null, null);

        var outcome = await transport.SendAsync(
            new PrinterTarget(printer.Id, printer.Name, kind, printer.Host, printer.Port,
                printer.WindowsPrinterName, printer.SupportsStatusQuery),
            new PrintPayload(job.JobNo, payload), ct);

        if (outcome.Kind == PrintOutcomeKind.Failed)
        {
            await TryReportAsync(jobId, "Failed", outcome.ErrorCode, outcome.ErrorMessage);
        }
        else
        {
            await TryReportAsync(jobId, "Completed", null, null, job.LabelCount);
        }
    }

    private async Task TryReportAsync(
        long jobId, string status, string? code, string? message, int? labels = null)
    {
        try
        {
            await api.ReportStatusAsync(jobId,
                new UpdateJobStatusRequest(status, labels, code, message), CancellationToken.None);
        }
        catch (Exception ex)
        {
            // If we cannot report, the server's lease watchdog fails the job —
            // the operator still sees a definite outcome.
            logger.LogWarning(ex, "Could not report status {Status} for job {JobId}", status, jobId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _queue.Writer.TryComplete();
        try
        {
            await Task.WhenAll(_pollLoop ?? Task.CompletedTask, _consumeLoop ?? Task.CompletedTask);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        _cts.Dispose();
    }
}
