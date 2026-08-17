using BarcodePrinter.Printing.Abstractions;
using BarcodePrinter.Printing.Client;
using FluentAssertions;
using Xunit;

namespace BarcodePrinter.Printing.Tests;

/// <summary>
/// Printer discovery exists so an operator never types an IP address for a
/// printer Windows already has. These pin the decisions that make that work:
/// which transport a queue needs, that discovered printers are dispatched from
/// the workstation, and that an unavailable printer is reported rather than
/// turned into a configuration task.
/// </summary>
public class WindowsPrinterDiscoveryTests
{
    // ---- transport classification --------------------------------------------------

    /// <summary>
    /// A label printer takes ZPL through the driver untouched. Sending ZPL to an
    /// office printer instead prints pages of "^XA^FO…" text, so this decision is
    /// visible on paper the moment it is wrong.
    /// </summary>
    [Theory]
    [InlineData("ZDesigner ZT230-200dpi ZPL", "USB001")]
    [InlineData("Zebra  ZT411-300dpi", "IP_192.168.1.50")]
    [InlineData("Generic / Text Only", "LPT1:")]
    [InlineData("SATO CL4NX plus", "USB002")]
    [InlineData("TSC TTP-244 Pro", "USB003")]
    [InlineData("Honeywell PC42t", "USB004")]
    public void A_label_printer_driver_is_sent_raw(string driver, string port) =>
        WindowsPrinterProbe.Classify(driver, port)
            .Should().Be(PrinterConnectionKind.WindowsRaw);

    [Theory]
    [InlineData("HP Universal Printing PCL 6", "IP_10.0.0.4")]
    [InlineData("Microsoft Print to PDF", "PORTPROMPT:")]
    [InlineData("Canon Generic Plus UFR II", "USB005")]
    [InlineData("Brother MFC-L2750DW series", "WSD-abc")]
    public void An_office_printer_is_sent_a_rendered_page(string driver, string port) =>
        WindowsPrinterProbe.Classify(driver, port)
            .Should().Be(PrinterConnectionKind.WindowsGraphics);

    /// <summary>Fallback enumeration yields no driver detail. Rendering a page
    /// to a label printer wastes a label; sending ZPL to a laser wastes a tree —
    /// so the safer default is the rendered page.</summary>
    [Fact]
    public void An_unknown_driver_defaults_to_the_rendered_page() =>
        WindowsPrinterProbe.Classify(null, null)
            .Should().Be(PrinterConnectionKind.WindowsGraphics);

    // ---- routing: this is the architectural decision --------------------------------

    /// <summary>
    /// A queue installed on this workstation is reachable ONLY from this
    /// workstation. The server cannot open a USB device on someone's PC, so a
    /// discovered printer is always client-dispatched — never raw TCP from the
    /// server (§7.3 / A-19).
    /// </summary>
    [Theory]
    [InlineData("USB001", false)]
    [InlineData("IP_192.168.1.50", true)]
    public void A_discovered_printer_is_always_dispatched_from_its_workstation(
        string port, bool networkQueue)
    {
        var request = LocalPrinterRegistration.ToRequest(
            Printer("Line 2 Zebra", "ZDesigner ZT230", port), "WS-14");

        request.DispatchMode.Should().Be("Client");
        request.OwnerWorkstation.Should().Be("WS-14");
        request.WindowsPrinterName.Should().Be("Line 2 Zebra");

        // The whole point: nothing for an operator to type, network queue or not.
        request.Host.Should().BeNull();
        request.Port.Should().BeNull();

        Printer("Line 2 Zebra", "ZDesigner ZT230", port).IsNetworkQueue.Should().Be(networkQueue);
    }

    [Fact]
    public void A_label_queue_registers_as_raw_and_an_office_queue_as_graphics()
    {
        LocalPrinterRegistration.ToRequest(Printer("Zebra", "ZDesigner ZT230", "USB001"), "WS-14")
            .Should().BeEquivalentTo(new { ConnectionType = "WindowsRaw", Language = "Zpl" },
                o => o.ExcludingMissingMembers());

        LocalPrinterRegistration.ToRequest(Printer("Office", "HP LaserJet PCL 6", "IP_10.0.0.4"), "WS-14")
            .Should().BeEquivalentTo(new { ConnectionType = "WindowsGraphics", Language = "Windows" },
                o => o.ExcludingMissingMembers());
    }

    /// <summary>Windows reports the QUEUE, not the media. Claiming otherwise
    /// would make the app wait for a confirmation that never arrives (C-17).</summary>
    [Fact]
    public void A_windows_queue_never_claims_to_report_media_status() =>
        LocalPrinterRegistration.ToRequest(Printer("Zebra", "ZDesigner", "USB001"), "WS-14")
            .SupportsStatusQuery.Should().BeFalse();

    // ---- codes -----------------------------------------------------------------------

    /// <summary>The same queue name on two PCs is two different devices, so the
    /// workstation is part of the identity.</summary>
    [Fact]
    public void The_same_queue_name_on_two_workstations_gets_two_codes()
    {
        var a = LocalPrinterRegistration.CodeFor("Zebra ZT230", "WS-14");
        var b = LocalPrinterRegistration.CodeFor("Zebra ZT230", "WS-22");
        a.Should().NotBe(b);
    }

    [Theory]
    [InlineData("Zebra ZT230", "WS-14-ZEBRA-ZT230")]
    [InlineData("HP LaserJet (Copy 1)", "WS-14-HP-LASERJET-COPY-1")]
    [InlineData("\\\\srv\\Line2", "WS-14-SRV-LINE2")]
    public void A_queue_name_becomes_a_usable_printer_code(string name, string expected) =>
        LocalPrinterRegistration.CodeFor(name, "WS-14").Should().Be(expected);

    [Fact]
    public void A_very_long_queue_name_is_truncated_to_a_storable_code()
    {
        var code = LocalPrinterRegistration.CodeFor(new string('X', 80), "WS-14");
        code.Length.Should().BeLessThanOrEqualTo(32);
        code.Should().NotEndWith("-", "a trailing separator reads as a truncation bug");
    }

    // ---- unavailable printers --------------------------------------------------------

    /// <summary>An offline printer is a fact to report, not a reason to demand
    /// an IP address. It stays selectable so the operator can queue work for it.</summary>
    [Theory]
    [InlineData(PrinterAvailability.Offline, "Offline")]
    [InlineData(PrinterAvailability.NeedsAttention, "Out of paper")]
    [InlineData(PrinterAvailability.Paused, "Paused")]
    public void An_unavailable_printer_still_registers_and_reports_why(
        PrinterAvailability availability, string status)
    {
        var printer = Printer("Line 2", "ZDesigner", "USB001") with
        {
            Availability = availability, StatusText = status,
        };

        var request = LocalPrinterRegistration.ToRequest(printer, "WS-14");

        request.IsActive.Should().BeTrue("an offline printer is still the right printer");
        request.Host.Should().BeNull("being offline is not a reason to ask for an address");
        printer.StatusText.Should().Be(status);
    }

    [Fact]
    public void A_ready_printer_is_reported_as_ready()
    {
        var printer = Printer("Line 2", "ZDesigner", "USB001");
        printer.Availability.Should().Be(PrinterAvailability.Ready);
        printer.ConnectionLabel.Should().Be("Windows (local)");
    }

    [Fact]
    public void A_network_queue_is_labelled_so_two_similar_devices_can_be_told_apart() =>
        Printer("Line 2", "ZDesigner", "IP_192.168.1.50").ConnectionLabel
            .Should().Be("Windows (network)");

    // ---- test print ------------------------------------------------------------------

    /// <summary>The test must travel the SAME transport a real job uses, or it
    /// proves nothing about the path production jobs take.</summary>
    [Fact]
    public async Task A_test_label_goes_through_the_transport_the_printer_will_really_use()
    {
        var raw = new RecordingTransport(PrinterConnectionKind.WindowsRaw);
        var graphics = new RecordingTransport(PrinterConnectionKind.WindowsGraphics);

        await LocalPrinterRegistration.TestAsync(
            Printer("Zebra", "ZDesigner", "USB001"), [raw, graphics],
            () => [1, 2, 3], CancellationToken.None);

        raw.Sent.Should().HaveCount(1);
        graphics.Sent.Should().BeEmpty();

        var (target, payload) = raw.Sent[0];
        target.WindowsPrinterName.Should().Be("Zebra");
        target.Host.Should().BeNull("a spooler job needs no address");
        System.Text.Encoding.UTF8.GetString(payload.Data).Should().StartWith("^XA");
    }

    [Fact]
    public async Task An_office_printer_is_tested_with_a_rendered_page()
    {
        var raw = new RecordingTransport(PrinterConnectionKind.WindowsRaw);
        var graphics = new RecordingTransport(PrinterConnectionKind.WindowsGraphics);

        await LocalPrinterRegistration.TestAsync(
            Printer("Office", "HP LaserJet", "IP_10.0.0.4"), [raw, graphics],
            () => [0x89, 0x50], CancellationToken.None);

        graphics.Sent.Should().HaveCount(1);
        raw.Sent.Should().BeEmpty();
        graphics.Sent[0].Payload.Data.Should().Equal([0x89, 0x50]);
    }

    [Fact]
    public async Task A_missing_transport_is_reported_rather_than_throwing()
    {
        var outcome = await LocalPrinterRegistration.TestAsync(
            Printer("Zebra", "ZDesigner", "USB001"), [], () => [], CancellationToken.None);

        outcome.Kind.Should().Be(PrintOutcomeKind.Failed);
        outcome.ErrorMessage.Should().Contain("WindowsRaw");
    }

    // ---- refresh ---------------------------------------------------------------------

    /// <summary>Refresh re-reads the spooler: a printer switched on, or removed,
    /// must show up on the next refresh without restarting the application.</summary>
    [Fact]
    public void Refresh_reflects_what_the_spooler_reports_now()
    {
        var probe = new FakeProbe
        {
            Result = [Printer("Line 2", "ZDesigner", "USB001")],
        };

        probe.Discover().Should().ContainSingle().Which.Name.Should().Be("Line 2");

        probe.Result =
        [
            Printer("Line 2", "ZDesigner", "USB001") with
            {
                Availability = PrinterAvailability.Offline, StatusText = "Offline",
            },
            Printer("Line 3", "ZDesigner", "USB002"),
        ];

        var second = probe.Discover();
        second.Should().HaveCount(2);
        second[0].StatusText.Should().Be("Offline");
        probe.Calls.Should().Be(2, "refresh must re-enumerate, not replay a cache");
    }

    // ---- helpers ---------------------------------------------------------------------

    private static DiscoveredPrinter Printer(string name, string driver, string port) =>
        new(name, driver, port, IsDefault: false, PrinterAvailability.Ready, "Ready",
            WindowsPrinterProbe.Classify(driver, port));

    private sealed class FakeProbe : IWindowsPrinterProbe
    {
        public IReadOnlyList<DiscoveredPrinter> Result { get; set; } = [];
        public int Calls { get; private set; }

        public IReadOnlyList<DiscoveredPrinter> Discover()
        {
            Calls++;
            return Result;
        }
    }

    private sealed class RecordingTransport(PrinterConnectionKind kind) : IPrintTransport
    {
        public PrinterConnectionKind Kind => kind;

        public List<(PrinterTarget Target, PrintPayload Payload)> Sent { get; } = [];

        public Task<PrintOutcome> SendAsync(
            PrinterTarget target, PrintPayload payload, CancellationToken ct)
        {
            Sent.Add((target, payload));
            return Task.FromResult(PrintOutcome.Dispatched());
        }

        public Task<PrinterStatus> QueryStatusAsync(PrinterTarget target, CancellationToken ct) =>
            Task.FromResult(PrinterStatus.Unknown);
    }
}
