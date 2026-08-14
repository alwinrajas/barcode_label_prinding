using BarcodePrinter.Contracts;
using BarcodePrinter.Infrastructure.Imports;
using BarcodePrinter.Infrastructure.Services;
using Dapper;
using Microsoft.AspNetCore.SignalR;

namespace BarcodePrinter.Api.Imports;

/// <summary>SignalR group per batch: "import-{id}". The client subscribes right
/// after the 202 and renders every push; polling GET /api/imports/{id} is the
/// fallback (B-16).</summary>
public sealed class ImportsHub : Hub
{
    public Task Subscribe(long batchId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(batchId));

    public static string GroupFor(long batchId) => $"import-{batchId}";
}

public sealed class SignalRImportProgressBroadcaster(
    IHubContext<ImportsHub> hub, ImportsQuery query) : IImportProgressBroadcaster
{
    public async Task BatchChangedAsync(long batchId, CancellationToken ct)
    {
        var dto = await query.GetAsync(batchId, ct);
        if (dto is not null)
        {
            await hub.Clients.Group(ImportsHub.GroupFor(batchId))
                .SendAsync("BatchChanged", dto, ct);
        }
    }
}

/// <summary>
/// Hosted consumer of the import queue. Two concurrent imports maximum
/// (§15 guard rails) so a bulk load never starves the print path; a startup
/// sweep fails batches orphaned by a crash and clears their staging rows.
/// </summary>
public sealed class ImportWorker(
    ImportQueue queue,
    IServiceScopeFactory scopes,
    IDbConnectionFactory connections,
    ILogger<ImportWorker> logger) : BackgroundService
{
    private const int MaxConcurrent = 2;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SweepOrphansAsync(stoppingToken);

        var consumers = Enumerable.Range(0, MaxConcurrent)
            .Select(_ => ConsumeAsync(stoppingToken));
        await Task.WhenAll(consumers);
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        await foreach (var batchId in queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                // Pipeline is scoped per batch; the worker itself is a singleton.
                using var scope = scopes.CreateScope();
                var pipeline = scope.ServiceProvider.GetRequiredService<ImportPipeline>();
                await pipeline.ProcessAsync(batchId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // ProcessAsync marks its own batch Failed; this guards the loop.
                logger.LogError(ex, "Import worker error for batch {BatchId}", batchId);
            }
        }
    }

    private async Task SweepOrphansAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await connections.OpenAsync(ct);
            var swept = await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE import_batches
                SET status = 'Failed',
                    error_message = 'Interrupted by a service restart. Please upload the file again.',
                    finished_at = UTC_TIMESTAMP(3)
                WHERE status IN ('Uploaded','Validating','Committing');
                DELETE s FROM product_import_staging s
                LEFT JOIN import_batches b
                    ON b.id = s.batch_id AND b.status IN ('Uploaded','Validating','Committing')
                WHERE b.id IS NULL;
                """, cancellationToken: ct));
            if (swept > 0)
            {
                logger.LogWarning("Startup sweep failed {Count} orphaned import batch(es)", swept);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import orphan sweep failed — continuing");
        }
    }
}
