using System.Collections.Concurrent;
using System.Threading.Channels;
using BarcodePrinter.Infrastructure.Printing;
using BarcodePrinter.Infrastructure.Services;
using BarcodePrinter.Printing.Abstractions;
using Dapper;

namespace BarcodePrinter.Api.Printing;

/// <summary>In-process queue. Server-dispatch jobs go to a per-printer channel;
/// client-dispatch jobs are collected by the owning workstation instead.</summary>
public sealed class PrintJobQueue : IPrintJobQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>();
    public ChannelWriter<long> Writer => _channel.Writer;
    public ChannelReader<long> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(long jobId, CancellationToken ct) =>
        _channel.Writer.WriteAsync(jobId, ct);
}

/// <summary>
/// Server-side dispatch (blueprint §8.3). ONE consumer per printer, fed by its
/// own channel: two jobs for the same printer are physically serialised, so
/// their ZPL can never interleave. Different printers run in parallel.
/// </summary>
public sealed class PrintDispatchWorker(
    PrintJobQueue queue,
    IDbConnectionFactory connections,
    IEnumerable<IPrintTransport> transports,
    IPrintJobStatusBroadcaster status,
    ILogger<PrintDispatchWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<long, Channel<long>> _perPrinter = new();
    private readonly Dictionary<PrinterConnectionKind, IPrintTransport> _transports =
        transports.ToDictionary(t => t.Kind);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverOrphansAsync(stoppingToken);

        await foreach (var jobId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var target = await LoadTargetAsync(jobId, stoppingToken);
                if (target is null || target.DispatchMode != "Server")
                {
                    continue;   // client-dispatched jobs are collected by the workstation
                }
                GetChannel(target.PrinterId, stoppingToken).Writer.TryWrite(jobId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to route print job {JobId}", jobId);
            }
        }
    }

    private Channel<long> GetChannel(long printerId, CancellationToken ct) =>
        _perPrinter.GetOrAdd(printerId, id =>
        {
            var channel = Channel.CreateUnbounded<long>(
                new UnboundedChannelOptions { SingleReader = true });
            _ = ConsumeAsync(id, channel, ct);
            return channel;
        });

    private async Task ConsumeAsync(long printerId, Channel<long> channel, CancellationToken ct)
    {
        await foreach (var jobId in channel.Reader.ReadAllAsync(ct))
        {
            try
            {
                await DispatchAsync(jobId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Dispatch failed for job {JobId} on printer {PrinterId}", jobId, printerId);
                await FailAsync(jobId, "DISPATCH_ERROR", ex.Message, CancellationToken.None);
            }
        }
    }

    private async Task DispatchAsync(long jobId, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);

        // Claim: only a Queued job may be taken, and only once.
        var claimed = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE print_jobs SET status = 'Dispatching', attempt_count = attempt_count + 1
            WHERE id = @jobId AND status = 'Queued'
            """, new { jobId }, cancellationToken: ct));
        if (claimed == 0)
        {
            return;   // cancelled or already taken
        }

        // Every transition is pushed as it happens (B-16), so the operator sees
        // the job move without touching anything.
        await status.JobChangedAsync(jobId, ct);

        var target = await LoadTargetAsync(jobId, ct);
        var payload = await conn.ExecuteScalarAsync<byte[]?>(new CommandDefinition(
            "SELECT payload FROM print_job_payloads WHERE job_id = @jobId",
            new { jobId }, cancellationToken: ct));
        var jobNo = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT job_no FROM print_jobs WHERE id = @jobId", new { jobId }, cancellationToken: ct));

        if (target is null || payload is null)
        {
            await FailAsync(jobId, "PAYLOAD_MISSING", "The print data could not be loaded.", ct);
            return;
        }

        if (!_transports.TryGetValue(target.Kind, out var transport))
        {
            await FailAsync(jobId, "TRANSPORT_UNAVAILABLE",
                $"No transport is available for {target.Kind} printers on the server.", ct);
            return;
        }

        // Retry only transient transport failures; the payload is fixed, so a
        // retry re-sends identical bytes and never re-allocates carton numbers.
        PrintOutcome outcome = PrintOutcome.Failed("UNSET", "not attempted");
        var delays = new[] { 2, 6, 15 };
        for (var attempt = 0; attempt <= delays.Length; attempt++)
        {
            outcome = await transport.SendAsync(
                new PrinterTarget(target.PrinterId, target.Name, target.Kind, target.Host,
                    target.Port, target.WindowsPrinterName, target.SupportsStatusQuery),
                new PrintPayload(jobNo ?? jobId.ToString(), payload), ct);

            if (outcome.Kind != PrintOutcomeKind.Failed ||
                outcome.ErrorCode is not (PrintErrorCodes.Unreachable or PrintErrorCodes.Timeout) ||
                attempt == delays.Length)
            {
                break;
            }
            logger.LogWarning("Print job {JobId} retry {Attempt} after {Error}",
                jobId, attempt + 1, outcome.ErrorCode);
            await Task.Delay(TimeSpan.FromSeconds(delays[attempt]), ct);
        }

        await ApplyOutcomeAsync(conn, jobId, outcome, ct);
    }

    private async Task ApplyOutcomeAsync(
        MySqlConnector.MySqlConnection conn, long jobId, PrintOutcome outcome, CancellationToken ct)
    {
        if (outcome.Kind == PrintOutcomeKind.Failed)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE print_jobs
                SET status = 'Failed', error_code = @code, error_message = @message,
                    completed_at = UTC_TIMESTAMP(3)
                WHERE id = @jobId;
                UPDATE print_job_items SET status = 'Failed' WHERE job_id = @jobId AND status = 'Pending';
                """,
                new { jobId, code = outcome.ErrorCode, message = outcome.ErrorMessage },
                cancellationToken: ct));
            await status.JobChangedAsync(jobId, ct);
            return;
        }

        // C-17: both facts recorded. Which one means "Completed" is a setting.
        var confirmed = outcome.Kind == PrintOutcomeKind.Confirmed;
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE print_jobs
            SET status = 'Completed',
                dispatched_at = UTC_TIMESTAMP(3),
                confirmed_at = CASE WHEN @confirmed THEN UTC_TIMESTAMP(3) ELSE confirmed_at END,
                labels_confirmed = CASE WHEN @confirmed THEN @labels ELSE labels_confirmed END,
                completed_at = UTC_TIMESTAMP(3)
            WHERE id = @jobId;
            UPDATE print_job_items SET status = @itemStatus, printed_at = UTC_TIMESTAMP(3)
            WHERE job_id = @jobId AND status = 'Pending';
            """,
            new
            {
                jobId, confirmed,
                labels = outcome.LabelsConfirmed ?? 0,
                itemStatus = confirmed ? "Confirmed" : "Dispatched",
            }, cancellationToken: ct));

        await status.JobChangedAsync(jobId, ct);
    }

    private async Task FailAsync(long jobId, string code, string message, CancellationToken ct)
    {
        try
        {
            await using var conn = await connections.OpenAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE print_jobs SET status = 'Failed', error_code = @code,
                    error_message = @message, completed_at = UTC_TIMESTAMP(3)
                WHERE id = @jobId AND status IN ('Queued','Dispatching','Printing')
                """, new { jobId, code, message = Truncate(message) }, cancellationToken: ct));
            await status.JobChangedAsync(jobId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not record failure for job {JobId}", jobId);
        }
    }

    private async Task<TargetRow?> LoadTargetAsync(long jobId, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<TargetRow>(new CommandDefinition(
            """
            SELECT CAST(p.id AS SIGNED) AS PrinterId, p.name AS Name,
                   p.connection_type AS ConnectionType, p.dispatch_mode AS DispatchMode,
                   p.host AS Host, p.port AS Port,
                   p.windows_printer_name AS WindowsPrinterName,
                   p.supports_status_query AS SupportsStatusQuery
            FROM print_jobs j JOIN printers p ON p.id = j.printer_id
            WHERE j.id = @jobId
            """, new { jobId }, cancellationToken: ct));
        return row;
    }

    /// <summary>A crash leaves jobs stuck mid-flight; re-queue them at startup
    /// so nothing is silently lost (§8.2).</summary>
    private async Task RecoverOrphansAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await connections.OpenAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE print_jobs SET status = 'Queued', lease_owner = NULL, lease_expires_at = NULL
                WHERE status = 'Dispatching'
                """, cancellationToken: ct));

            var queued = await conn.QueryAsync<long>(new CommandDefinition(
                """
                SELECT CAST(j.id AS SIGNED) FROM print_jobs j
                JOIN printers p ON p.id = j.printer_id AND p.dispatch_mode = 'Server'
                WHERE j.status = 'Queued'
                ORDER BY j.requested_at
                """, cancellationToken: ct));

            foreach (var jobId in queued)
            {
                await queue.EnqueueAsync(jobId, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Print job recovery sweep failed — continuing");
        }
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];

    private sealed class TargetRow
    {
        public long PrinterId { get; set; }
        public string Name { get; set; } = "";
        public string ConnectionType { get; set; } = "";
        public string DispatchMode { get; set; } = "";
        public string? Host { get; set; }
        public int? Port { get; set; }
        public string? WindowsPrinterName { get; set; }
        public bool SupportsStatusQuery { get; set; }

        public PrinterConnectionKind Kind => ConnectionType switch
        {
            "NetworkTcp" => PrinterConnectionKind.NetworkTcp,
            "WindowsRaw" => PrinterConnectionKind.WindowsRaw,
            "WindowsGraphics" => PrinterConnectionKind.WindowsGraphics,
            _ => PrinterConnectionKind.File,
        };
    }
}

/// <summary>
/// Fails jobs whose client dispatcher died (§7.4). The lease is the only thing
/// that distinguishes "printing slowly" from "the workstation is gone".
/// </summary>
public sealed class PrintLeaseWatchdog(
    IDbConnectionFactory connections,
    IPrintJobStatusBroadcaster status,
    ILogger<PrintLeaseWatchdog> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var conn = await connections.OpenAsync(stoppingToken);

                // Collected before the update, because afterwards they no longer
                // match the predicate and the operator would never be told.
                var losing = (await conn.QueryAsync<long>(new CommandDefinition(
                    """
                    SELECT CAST(id AS SIGNED) FROM print_jobs
                    WHERE status IN ('Dispatching','Printing')
                      AND lease_expires_at IS NOT NULL
                      AND lease_expires_at < UTC_TIMESTAMP(3)
                    """, cancellationToken: stoppingToken))).ToList();

                var expired = await conn.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE print_jobs
                    SET status = 'Failed', error_code = 'CLIENT_LOST',
                        error_message = 'The workstation stopped responding while printing.',
                        completed_at = UTC_TIMESTAMP(3)
                    WHERE status IN ('Dispatching','Printing')
                      AND lease_expires_at IS NOT NULL
                      AND lease_expires_at < UTC_TIMESTAMP(3)
                    """, cancellationToken: stoppingToken));
                if (expired > 0)
                {
                    logger.LogWarning("{Count} print job(s) failed on expired client lease", expired);
                    foreach (var jobId in losing)
                    {
                        await status.JobChangedAsync(jobId, stoppingToken);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Lease watchdog pass failed");
            }
        }
    }
}
