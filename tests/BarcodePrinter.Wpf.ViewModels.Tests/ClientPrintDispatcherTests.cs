using System.Net.Http;
using BarcodePrinter.Client.Core;
using BarcodePrinter.Printing.Abstractions;
using BarcodePrinter.Printing.Client;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarcodePrinter.Wpf.ViewModels.Tests;

/// <summary>
/// The workstation half of hybrid dispatch. These cover the outcomes an
/// operator actually feels: a job that prints, a job whose printer is gone, and
/// a job another workstation claimed first — every one of which must end in a
/// definite reported status rather than a job stuck mid-flight.
/// </summary>
public sealed class ClientPrintDispatcherTests
{
    private sealed class FakeTransport(PrintOutcome outcome) : IPrintTransport
    {
        public PrinterConnectionKind Kind => PrinterConnectionKind.WindowsRaw;
        public List<PrintPayload> Sent { get; } = [];

        public Task<PrintOutcome> SendAsync(
            PrinterTarget target, PrintPayload payload, CancellationToken ct)
        {
            Sent.Add(payload);
            return Task.FromResult(outcome);
        }

        public Task<PrinterStatus> QueryStatusAsync(PrinterTarget target, CancellationToken ct) =>
            Task.FromResult(PrinterStatus.Unknown);
    }

    private static object Printer(string connectionType = "WindowsRaw") => new
    {
        id = 1L, code = "LOCAL", name = "Local Zebra", location = (string?)null,
        connectionType, dispatchMode = "Client", host = (string?)null, port = (int?)null,
        windowsPrinterName = "Zebra ZT230", ownerWorkstation = Environment.MachineName,
        dpi = 203, language = "Zpl", supportsStatusQuery = false,
        isActive = true, isDefault = true, lastSeenUtc = (DateTime?)null,
    };

    private static object Job() => new
    {
        id = 10L, jobNo = "PJ-260816-000001", requestedAtUtc = DateTime.UtcNow, requestedBy = "op",
        printerName = "Local Zebra", templateCode = "TPL", templateVersion = 1,
        productCode = "5GCAPM2N", description = "5G M2 CAP", batch = "CONE",
        productionDate = (string?)null, expiryDate = (string?)null, quantityText = "750[D]",
        cartonFrom = 1L, cartonTo = 5L, labelCount = 5, copiesPerLabel = (short)1,
        status = "Dispatching", dispatchedAtUtc = (DateTime?)null, confirmedAtUtc = (DateTime?)null,
        labelsConfirmed = 0, errorCode = (string?)null, errorMessage = (string?)null,
        isReprint = false, sourceJobId = (long?)null, reprintReason = (string?)null,
    };

    /// <summary>Drives one poll cycle to completion without waiting on the
    /// dispatcher's own 3-second timer.</summary>
    private static async Task<(RoutingHandler Handler, FakeTransport Transport)> RunOneJobAsync(
        PrintOutcome outcome, bool claimSucceeds = true, string connectionType = "WindowsRaw",
        bool payloadMissing = false)
    {
        // Later registrations are matched first, so the general job route is
        // registered BEFORE its /claim, /payload and /status siblings —
        // otherwise it would swallow all three.
        var handler = new RoutingHandler();
        handler.Route("/api/printers", new[] { Printer(connectionType) });
        handler.Route("/api/print/pending", new[] { 10L });
        handler.Route("/api/print/jobs/10", Job());
        handler.Route("/api/print/jobs/10/claim", _ => new HttpResponseMessage(
            claimSucceeds ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.Conflict));
        handler.Route("/api/print/jobs/10/payload", _ => payloadMissing
            ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("^XA^XZ"u8.ToArray()),
            });
        handler.Route("/api/print/jobs/10/status", new { });

        var api = await handler.LoggedInClientAsync();
        var transport = new FakeTransport(outcome);
        await using var dispatcher = new ClientPrintDispatcher(
            new PrintApi(api), [transport], new NoPrintersProbe(),
            NullLogger<ClientPrintDispatcher>.Instance);

        var finished = new TaskCompletionSource();
        dispatcher.JobCompleted += (_, _) => finished.TrySetResult();
        dispatcher.Start();

        // The claim decides whether anything is queued at all, so a rejected
        // claim legitimately never raises JobCompleted.
        await Task.WhenAny(finished.Task, Task.Delay(claimSucceeds ? 15_000 : 6_000));
        return (handler, transport);
    }

    private static IEnumerable<string> StatusBodies(RoutingHandler handler) =>
        handler.Requests.Where(r => r.Path.EndsWith("/status")).Select(r => r.Body);

    [Fact]
    public async Task A_successful_local_print_reports_Printing_then_Completed()
    {
        var (handler, transport) = await RunOneJobAsync(
            PrintOutcome.Dispatched());

        transport.Sent.Should().ContainSingle("the job's stored bytes go to the local printer once");
        var statuses = StatusBodies(handler).ToList();
        statuses.Should().Contain(b => b.Contains("Printing"));
        statuses.Should().Contain(b => b.Contains("Completed"));
    }

    [Fact]
    public async Task A_transport_failure_is_reported_with_its_code_not_swallowed()
    {
        var (handler, _) = await RunOneJobAsync(
            new PrintOutcome(PrintOutcomeKind.Failed, "PRINTER_UNREACHABLE", "The printer is offline."));

        StatusBodies(handler).Should().Contain(b =>
            b.Contains("Failed") && b.Contains("PRINTER_UNREACHABLE"),
            "a failed print must reach the server so the operator sees a definite outcome");
    }

    [Fact]
    public async Task A_job_claimed_by_another_workstation_is_never_printed_here()
    {
        var (_, transport) = await RunOneJobAsync(
            PrintOutcome.Dispatched(), claimSucceeds: false);

        transport.Sent.Should().BeEmpty(
            "losing the claim race is how duplicate labels are prevented");
    }

    [Fact]
    public async Task A_printer_this_pc_cannot_drive_fails_the_job_with_an_explanation()
    {
        // The dispatcher only holds a WindowsRaw transport in this test.
        var (handler, transport) = await RunOneJobAsync(
            PrintOutcome.Dispatched(), connectionType: "NetworkTcp");

        transport.Sent.Should().BeEmpty();
        StatusBodies(handler).Should().Contain(b => b.Contains("Failed") && b.Contains("NetworkTcp"));
    }

    [Fact]
    public async Task A_payload_that_cannot_be_downloaded_fails_the_job()
    {
        var (handler, transport) = await RunOneJobAsync(
            PrintOutcome.Dispatched(), payloadMissing: true);

        transport.Sent.Should().BeEmpty();
        StatusBodies(handler).Should().Contain(b => b.Contains("Failed"));
    }

    [Fact]
    public void The_dispatcher_identifies_itself_by_machine_name()
    {
        // The server matches this against printers.owner_workstation; a mismatch
        // is what used to leave jobs Queued forever.
        var handler = new RoutingHandler();
        var api = new PrintApi(new ApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://server.test") },
            new ConnectionStatus()));

        var dispatcher = new ClientPrintDispatcher(
            api, [], new NoPrintersProbe(), NullLogger<ClientPrintDispatcher>.Instance);

        dispatcher.Workstation.Should().Be(Environment.MachineName);
    }

    /// <summary>These tests exercise job dispatch, not discovery: reporting
    /// nothing keeps the status heartbeat out of the way.</summary>
    private sealed class NoPrintersProbe : BarcodePrinter.Printing.Client.IWindowsPrinterProbe
    {
        public IReadOnlyList<BarcodePrinter.Printing.Client.DiscoveredPrinter> Discover() => [];
    }
}
