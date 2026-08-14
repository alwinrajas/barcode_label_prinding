namespace BarcodePrinter.Infrastructure.Printing;

/// <summary>
/// Port for live print-job status (B-16), implemented in the Api layer over
/// SignalR so the print pipeline stays transport-agnostic.
///
/// Implementations MUST NOT throw. A transition has already happened — and by
/// the time most of these fire, a label has physically been printed — so a
/// failed notification is a lost update, never a failed job. The client polls
/// as a fallback.
/// </summary>
public interface IPrintJobStatusBroadcaster
{
    Task JobChangedAsync(long jobId, CancellationToken ct);
}

/// <summary>Used wherever no transport is wired (tests, client-side dispatch).</summary>
public sealed class NullPrintJobStatusBroadcaster : IPrintJobStatusBroadcaster
{
    public Task JobChangedAsync(long jobId, CancellationToken ct) => Task.CompletedTask;
}
