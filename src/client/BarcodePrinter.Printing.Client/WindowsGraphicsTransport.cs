using System.Drawing;
using System.IO;
using System.Drawing.Printing;
using System.Runtime.Versioning;
using BarcodePrinter.Printing.Abstractions;

namespace BarcodePrinter.Printing.Client;

/// <summary>
/// Prints to an ordinary Windows printer — an office laser or inkjet — through
/// the installed driver and the spooler (§7.2 / A-19).
///
/// Such a printer cannot interpret ZPL, so a job aimed at one is rendered to one
/// image per label at submit time and arrives here as a raster payload. Nothing
/// else in the pipeline changes: the same job row, the same status lifecycle,
/// the same stored payload for byte-identical reprint.
///
/// We write no driver and do no device work; the spooler owns the queue and the
/// vendor driver owns the hardware.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsGraphicsTransport : IPrintTransport
{
    public PrinterConnectionKind Kind => PrinterConnectionKind.WindowsGraphics;

    public Task<PrintOutcome> SendAsync(
        PrinterTarget target, PrintPayload payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target.WindowsPrinterName))
        {
            return Task.FromResult(PrintOutcome.Failed(PrintErrorCodes.SpoolerError,
                $"Printer '{target.Name}' has no Windows printer name configured."));
        }

        // GDI printing is blocking and must never run on the UI thread (A-20).
        var printerName = target.WindowsPrinterName;
        return Task.Run(() => SendCore(target, printerName, payload), ct);
    }

    private static PrintOutcome SendCore(
        PrinterTarget target, string printerName, PrintPayload payload)
    {
        List<byte[]> labels;
        try
        {
            labels = [.. RasterLabelPayload.Unpack(payload.Data)];
        }
        catch (InvalidDataException ex)
        {
            // Almost always a ZPL template pointed at a GDI printer. Say which
            // configuration is wrong rather than reporting a driver fault.
            return PrintOutcome.Failed(PrintErrorCodes.SpoolerError,
                $"This job was not prepared for a standard Windows printer. {ex.Message}");
        }

        if (labels.Count == 0)
        {
            return PrintOutcome.Failed(PrintErrorCodes.SpoolerError, "The job contains no labels.");
        }

        var images = new List<Image>(labels.Count);
        try
        {
            foreach (var bytes in labels)
            {
                images.Add(Image.FromStream(new MemoryStream(bytes)));
            }

            var index = 0;
            using var document = new PrintDocument();
            document.DocumentName = payload.JobNo;
            document.PrinterSettings.PrinterName = printerName;
            document.PrintController = new StandardPrintController();   // no progress dialog

            if (!document.PrinterSettings.IsValid)
            {
                return PrintOutcome.Failed(PrintErrorCodes.SpoolerError,
                    $"Windows does not have a printer named '{printerName}'.");
            }

            document.PrintPage += (_, e) =>
            {
                var image = images[index++];

                // One label per page, fitted to the printable area with its
                // aspect preserved — stretching a barcode stops it scanning.
                var area = e.PageBounds;
                if (e.Graphics is { } g)
                {
                    var scale = Math.Min(
                        area.Width / (double)image.Width, area.Height / (double)image.Height);
                    var width = (int)(image.Width * scale);
                    var height = (int)(image.Height * scale);
                    g.DrawImage(image,
                        new Rectangle(
                            area.Left + ((area.Width - width) / 2),
                            area.Top + ((area.Height - height) / 2),
                            width, height));
                }

                e.HasMorePages = index < images.Count;
            };

            document.Print();

            // The spooler has accepted the job. Whether it reached paper is the
            // Confirmed semantic, which GDI printers do not report (C-17).
            return PrintOutcome.Dispatched();
        }
        catch (InvalidPrinterException ex)
        {
            return PrintOutcome.Failed(PrintErrorCodes.SpoolerError, ex.Message);
        }
        catch (Exception ex)
        {
            return PrintOutcome.Failed(PrintErrorCodes.SpoolerError,
                $"Windows could not print to '{printerName}': {ex.Message}");
        }
        finally
        {
            foreach (var image in images)
            {
                image.Dispose();
            }
        }
    }

    /// <summary>GDI printers report no media state; the spooler either accepts
    /// the job or it does not.</summary>
    public Task<PrinterStatus> QueryStatusAsync(PrinterTarget target, CancellationToken ct) =>
        Task.FromResult(PrinterStatus.Unknown);
}
