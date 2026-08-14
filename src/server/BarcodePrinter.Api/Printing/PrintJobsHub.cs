using BarcodePrinter.Api.Auth;
using BarcodePrinter.Contracts.Printing;
using BarcodePrinter.Infrastructure.Printing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace BarcodePrinter.Api.Printing;

/// <summary>
/// Live print-job status (B-16).
///
/// Two group shapes, because the two screens want different things: the print
/// screen follows the one job it just submitted, while the history and dashboard
/// views want anything that moves. Both are cheap — a group per job is created
/// and dropped with the subscription.
///
/// Authorized: a job carries the product, batch, operator and printer. Anonymous
/// subscription would hand that to anyone who can reach the port.
/// </summary>
[Authorize(Policy = PermissionPolicy.PrintView)]
public sealed class PrintJobsHub : Hub
{
    /// <summary>Every job transition, for the recent list and the dashboard.</summary>
    public const string AllGroup = "print-jobs";

    public Task SubscribeToJob(long jobId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(jobId));

    public Task UnsubscribeFromJob(long jobId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(jobId));

    public Task SubscribeToAll() =>
        Groups.AddToGroupAsync(Context.ConnectionId, AllGroup);

    public static string GroupFor(long jobId) => $"print-job-{jobId}";
}

/// <summary>
/// Publishes a transition to both groups. Deliberately swallows its own
/// failures: a broken notification must never fail the print that already
/// physically happened. The client polls as a fallback (B-16), so the worst
/// case of a dropped push is a late update, not a wrong one.
/// </summary>
public sealed class SignalRPrintJobStatusBroadcaster(
    IHubContext<PrintJobsHub> hub,
    IServiceScopeFactory scopes,
    ILogger<SignalRPrintJobStatusBroadcaster> logger) : IPrintJobStatusBroadcaster
{
    public async Task JobChangedAsync(long jobId, CancellationToken ct)
    {
        try
        {
            // Singleton, so that the singleton dispatch worker and the scoped
            // request services can share one instance; the scoped query is
            // resolved per notification.
            await using var scope = scopes.CreateAsyncScope();
            var job = await scope.ServiceProvider
                .GetRequiredService<PrintQueries>()
                .GetJobAsync(jobId, ct);
            if (job is null)
            {
                return;
            }

            await hub.Clients.Groups(PrintJobsHub.GroupFor(jobId), PrintJobsHub.AllGroup)
                .SendAsync("JobChanged", job, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not broadcast the status of print job {JobId}", jobId);
        }
    }
}
