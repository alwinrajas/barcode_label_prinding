using System.Text;
using System.Text.Json;
using BarcodePrinter.Application.Abstractions;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Printing;
using BarcodePrinter.Domain;
using BarcodePrinter.Infrastructure.Services;
using BarcodePrinter.Printing.Abstractions;
using Dapper;

namespace BarcodePrinter.Infrastructure.Printing;

public sealed record PrinterTestResult(bool Success, string Message);

public sealed class PrinterAdminService(
    IDbConnectionFactory connections,
    IEnumerable<IPrintTransport> transports,
    IAuditWriter audit,
    LocalPrinterStatusCache localStatus)
{
    public async Task<long> CreateAsync(SavePrinterRequest request, ActorInfo actor, CancellationToken ct)
    {
        Validate(request);
        await using var conn = await connections.OpenAsync(ct);

        if (await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM printers WHERE code = @Code", new { request.Code },
                cancellationToken: ct)) > 0)
        {
            throw new DomainException("PRINTER_CODE_DUPLICATE", "A printer with that code already exists.");
        }

        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO printers
                (code, name, location, connection_type, dispatch_mode, host, port,
                 windows_printer_name, owner_workstation, dpi, language,
                 supports_status_query, is_active, is_default, created_at, created_by)
            VALUES
                (@Code, @Name, @Location, @ConnectionType, @DispatchMode, @Host, @Port,
                 @WindowsPrinterName, @OwnerWorkstation, @Dpi, @Language,
                 @SupportsStatusQuery, @IsActive, 0, UTC_TIMESTAMP(3), @UserId);
            SELECT LAST_INSERT_ID();
            """,
            new
            {
                request.Code, request.Name, request.Location, request.ConnectionType,
                request.DispatchMode, request.Host, request.Port, request.WindowsPrinterName,
                request.OwnerWorkstation, request.Dpi, request.Language,
                request.SupportsStatusQuery, request.IsActive, actor.UserId,
            }, cancellationToken: ct));

        await audit.WriteAsync(new AuditEntry("PrinterCreated", "Info",
            actor.UserId, actor.Username, "Printer", request.Code,
            AfterJson: JsonSerializer.Serialize(request), CorrelationId: actor.CorrelationId), ct);
        return id;
    }

    public async Task UpdateAsync(long id, SavePrinterRequest request, ActorInfo actor, CancellationToken ct)
    {
        Validate(request);
        await using var conn = await connections.OpenAsync(ct);

        var changed = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE printers SET code = @Code, name = @Name, location = @Location,
                connection_type = @ConnectionType, dispatch_mode = @DispatchMode,
                host = @Host, port = @Port, windows_printer_name = @WindowsPrinterName,
                owner_workstation = @OwnerWorkstation, dpi = @Dpi, language = @Language,
                supports_status_query = @SupportsStatusQuery, is_active = @IsActive,
                updated_at = UTC_TIMESTAMP(3), updated_by = @UserId
            WHERE id = @id
            """,
            new
            {
                id, request.Code, request.Name, request.Location, request.ConnectionType,
                request.DispatchMode, request.Host, request.Port, request.WindowsPrinterName,
                request.OwnerWorkstation, request.Dpi, request.Language,
                request.SupportsStatusQuery, request.IsActive, actor.UserId,
            }, cancellationToken: ct));
        if (changed == 0)
        {
            throw new NotFoundException("Printer", id);
        }

        await audit.WriteAsync(new AuditEntry("PrinterUpdated", "Info",
            actor.UserId, actor.Username, "Printer", request.Code,
            AfterJson: JsonSerializer.Serialize(request), CorrelationId: actor.CorrelationId), ct);
    }

    public async Task SetDefaultAsync(long id, ActorInfo actor, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE printers SET is_default = (id = @id)", new { id }, cancellationToken: ct));
        await audit.WriteAsync(new AuditEntry("PrinterDefaultChanged", "Info",
            actor.UserId, actor.Username, "Printer", id.ToString(),
            CorrelationId: actor.CorrelationId), ct);
    }

    /// <summary>Sends a real ZPL test label so commissioning failures surface
    /// at setup rather than on the first production run.</summary>
    public async Task<PrinterTestResult> TestAsync(long id, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<TargetRow>(new CommandDefinition(
            """
            SELECT CAST(id AS SIGNED) AS Id, name AS Name, connection_type AS ConnectionType,
                   dispatch_mode AS DispatchMode, host AS Host, port AS Port,
                   windows_printer_name AS WindowsPrinterName,
                   supports_status_query AS SupportsStatusQuery
            FROM printers WHERE id = @id
            """, new { id }, cancellationToken: ct)) ?? throw new NotFoundException("Printer", id);

        if (row.DispatchMode == "Client")
        {
            return new PrinterTestResult(false,
                "This printer is connected to a workstation. Run the test from that PC.");
        }

        var kind = ParseKind(row.ConnectionType);
        var transport = transports.FirstOrDefault(t => t.Kind == kind);
        if (transport is null)
        {
            return new PrinterTestResult(false,
                $"The server cannot reach {row.ConnectionType} printers directly.");
        }

        var target = new PrinterTarget(row.Id, row.Name, kind, row.Host, row.Port,
            row.WindowsPrinterName, row.SupportsStatusQuery);

        var zpl = Encoding.UTF8.GetBytes(
            "^XA^CI28^FO40,40^A0N,40,40^FDTest label^FS" +
            $"^FO40,100^A0N,28,28^FD{row.Name}^FS" +
            $"^FO40,150^A0N,28,28^FD{DateTime.Now:dd/MM/yyyy HH:mm}^FS^XZ");

        var outcome = await transport.SendAsync(target, new PrintPayload("TEST", zpl), ct);
        return outcome.Kind == PrintOutcomeKind.Failed
            ? new PrinterTestResult(false, outcome.ErrorMessage ?? "The test print failed.")
            : new PrinterTestResult(true, "Test label sent. Check the printer.");
    }

    /// <summary>Live reachability. Network printers get a real TCP probe;
    /// client-dispatched printers are judged by their workstation's poll
    /// heartbeat (every ~3 s while the app runs); File printers are always
    /// reachable. Never throws for an unreachable device — offline is data.</summary>
    public async Task<PrinterStatusDto> GetStatusAsync(long id, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<StatusRow>(new CommandDefinition(
            """
            SELECT CAST(id AS SIGNED) AS Id, connection_type AS ConnectionType,
                   dispatch_mode AS DispatchMode, host AS Host, port AS Port,
                   windows_printer_name AS WindowsPrinterName,
                   owner_workstation AS OwnerWorkstation, last_seen_at AS LastSeenUtc
            FROM printers WHERE id = @id
            """, new { id }, cancellationToken: ct)) ?? throw new NotFoundException("Printer", id);

        if (row.DispatchMode == "Client")
        {
            // Two separate facts, reported separately. Collapsing them is how an
            // unplugged printer ends up with a green light: the workstation was
            // running, so "Online" was true — of the PC, not of the printer.
            var workstationRunning =
                row.LastSeenUtc is { } seen && DateTime.UtcNow - seen < TimeSpan.FromSeconds(15);

            if (!workstationRunning)
            {
                return new PrinterStatusDto(id, false,
                    $"Workstation '{row.OwnerWorkstation ?? "not configured"}' is not running the application.",
                    row.LastSeenUtc);
            }

            var reported = localStatus.TryGet(row.OwnerWorkstation, row.WindowsPrinterName);
            if (reported is null)
            {
                // The PC is up but has not told us about this queue. Saying so is
                // better than guessing either way.
                return new PrinterStatusDto(id, false,
                    "Waiting for the workstation to report this printer.", row.LastSeenUtc);
            }

            var (availability, statusText) = reported.Value;
            var ready = string.Equals(availability, "Ready", StringComparison.OrdinalIgnoreCase);
            return new PrinterStatusDto(id, ready, ready ? null : statusText, row.LastSeenUtc);
        }

        if (row.ConnectionType == "NetworkTcp")
        {
            if (string.IsNullOrWhiteSpace(row.Host))
            {
                return new PrinterStatusDto(id, false, "No IP address or host name is configured.", row.LastSeenUtc);
            }
            try
            {
                using var probe = new System.Net.Sockets.TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await probe.ConnectAsync(row.Host, row.Port ?? 9100, timeout.Token);

                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE printers SET last_seen_at = UTC_TIMESTAMP(3) WHERE id = @id",
                    new { id }, cancellationToken: ct));
                return new PrinterStatusDto(id, true, null, DateTime.UtcNow);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return new PrinterStatusDto(id, false,
                    $"No response from {row.Host}:{row.Port ?? 9100}. Check power and network.",
                    row.LastSeenUtc);
            }
        }

        return new PrinterStatusDto(id, true, null, row.LastSeenUtc);
    }

    private static void Validate(SavePrinterRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Code) || string.IsNullOrWhiteSpace(r.Name))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Printer code and name are required.");
        }
        if (r.ConnectionType == "NetworkTcp" && string.IsNullOrWhiteSpace(r.Host))
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "Network printers need an IP address or host name.");
        }
        if (r.ConnectionType is "WindowsRaw" or "WindowsGraphics" &&
            string.IsNullOrWhiteSpace(r.WindowsPrinterName))
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "Windows printers need the printer name exactly as it appears in Windows.");
        }
        // A Windows-queue printer can only be reached from the PC it is installed
        // on, so it must be client-dispatched (§7.3).
        if (r.ConnectionType is "WindowsRaw" or "WindowsGraphics" && r.DispatchMode == "Server")
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "Windows printers must be dispatched from the workstation they are installed on.");
        }
        if (r.DispatchMode == "Client" && string.IsNullOrWhiteSpace(r.OwnerWorkstation))
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "Enter the workstation name that owns this printer.");
        }
    }

    internal static PrinterConnectionKind ParseKind(string connectionType) => connectionType switch
    {
        "NetworkTcp" => PrinterConnectionKind.NetworkTcp,
        "WindowsRaw" => PrinterConnectionKind.WindowsRaw,
        "WindowsGraphics" => PrinterConnectionKind.WindowsGraphics,
        _ => PrinterConnectionKind.File,
    };

    private sealed class TargetRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string ConnectionType { get; set; } = "";
        public string DispatchMode { get; set; } = "";
        public string? Host { get; set; }
        public int? Port { get; set; }
        public string? WindowsPrinterName { get; set; }
        public bool SupportsStatusQuery { get; set; }
    }

    private sealed class StatusRow
    {
        public long Id { get; set; }
        public string ConnectionType { get; set; } = "";
        public string DispatchMode { get; set; } = "";
        public string? Host { get; set; }
        public int? Port { get; set; }
        public string? WindowsPrinterName { get; set; }
    public string? OwnerWorkstation { get; set; }
        public DateTime? LastSeenUtc { get; set; }
    }
}

/// <summary>
/// Server side of client-dispatched printing (§8.4). The workstation polls for
/// its jobs, claims them with a lease, prints, and reports back — the server
/// validates every transition so the client can never invent state.
/// </summary>
public sealed class ClientDispatchService(
    IDbConnectionFactory connections,
    IPrintJobStatusBroadcaster jobStatus)
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);

    public async Task<IReadOnlyList<long>> GetPendingAsync(string workstation, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);

        // The poll doubles as a heartbeat: it is the only signal that a
        // client-dispatched printer's workstation is alive, and it feeds the
        // online/last-seen display on the Printers screen.
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE printers SET last_seen_at = UTC_TIMESTAMP(3)
            WHERE dispatch_mode = 'Client' AND owner_workstation = @workstation
            """, new { workstation }, cancellationToken: ct));

        return (await conn.QueryAsync<long>(new CommandDefinition(
            """
            SELECT CAST(j.id AS SIGNED) FROM print_jobs j
            JOIN printers p ON p.id = j.printer_id
            WHERE j.status = 'Queued' AND p.dispatch_mode = 'Client'
              AND p.owner_workstation = @workstation
            ORDER BY j.requested_at
            LIMIT 20
            """, new { workstation }, cancellationToken: ct))).ToList();
    }

    /// <summary>Atomic claim + lease. A second workstation configured for the
    /// same printer cannot take a job that is already claimed.</summary>
    public async Task<bool> ClaimAsync(long jobId, string workstation, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var claimed = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE print_jobs
            SET status = 'Dispatching', lease_owner = @workstation,
                lease_expires_at = UTC_TIMESTAMP(3) + INTERVAL @seconds SECOND,
                attempt_count = attempt_count + 1
            WHERE id = @jobId AND status = 'Queued'
            """,
            new { jobId, workstation, seconds = (int)LeaseDuration.TotalSeconds },
            cancellationToken: ct));

        if (claimed > 0)
        {
            await jobStatus.JobChangedAsync(jobId, ct);
        }
        return claimed > 0;
    }

    public async Task UpdateStatusAsync(
        long jobId, UpdateJobStatusRequest request, ActorInfo actor, CancellationToken ct)
    {
        // Only these transitions are legal; anything else is rejected.
        var (sql, terminal) = request.Status switch
        {
            "Printing" => ("""
                UPDATE print_jobs SET status = 'Printing', dispatched_at = UTC_TIMESTAMP(3),
                    lease_expires_at = UTC_TIMESTAMP(3) + INTERVAL 60 SECOND
                WHERE id = @jobId AND status IN ('Dispatching','Printing')
                """, false),
            "Completed" => ("""
                UPDATE print_jobs SET status = 'Completed',
                    dispatched_at = COALESCE(dispatched_at, UTC_TIMESTAMP(3)),
                    labels_confirmed = COALESCE(@LabelsConfirmed, labels_confirmed),
                    completed_at = UTC_TIMESTAMP(3), lease_expires_at = NULL
                WHERE id = @jobId AND status IN ('Dispatching','Printing')
                """, true),
            "Failed" => ("""
                UPDATE print_jobs SET status = 'Failed', error_code = @ErrorCode,
                    error_message = @ErrorMessage, completed_at = UTC_TIMESTAMP(3),
                    lease_expires_at = NULL
                WHERE id = @jobId AND status IN ('Dispatching','Printing')
                """, true),
            _ => throw new DomainException(ErrorCodes.ValidationFailed,
                $"'{request.Status}' is not a valid print status transition."),
        };

        await using var conn = await connections.OpenAsync(ct);
        var changed = await conn.ExecuteAsync(new CommandDefinition(
            sql, new { jobId, request.LabelsConfirmed, request.ErrorCode, request.ErrorMessage },
            cancellationToken: ct));

        if (changed == 0)
        {
            throw new DomainException("PRINT_STATUS_INVALID",
                "This job is no longer in a state that accepts that update.");
        }

        if (terminal)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE print_job_items SET status = @itemStatus, printed_at = UTC_TIMESTAMP(3)
                WHERE job_id = @jobId AND status = 'Pending'
                """,
                new { jobId, itemStatus = request.Status == "Completed" ? "Dispatched" : "Failed" },
                cancellationToken: ct));
        }

        // Client-dispatched printing reports its own progress; those transitions
        // must reach the other screens exactly like the server-side ones.
        await jobStatus.JobChangedAsync(jobId, ct);
    }
}

/// <summary>Live single-label preview for the print screen.</summary>
public sealed class PrintPreviewService(
    IDbConnectionFactory connections, TemplateRenderService renderer)
{
    public async Task<string> RenderAsync(PrintPreviewRequest request, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var product = await conn.QuerySingleOrDefaultAsync<ProductSnapshot>(new CommandDefinition(
            """
            SELECT CAST(p.id AS SIGNED) AS Id, p.code AS Code, p.description AS Description,
                   COALESCE(NULLIF(p.barcode_value, ''), p.code) AS BarcodeValue,
                   u.code AS Uom, p.size AS Size, p.color AS Color,
                   p.default_batch AS DefaultBatch,
                   p.default_production_date AS DefaultProductionDate,
                   p.default_expiry_date AS DefaultExpiryDate,
                   p.default_quantity_text AS DefaultQuantityText,
                   pi.content_hash AS ImageHash, p.is_active AS IsActive
            FROM products p
            LEFT JOIN uoms u ON u.id = p.uom_id
            LEFT JOIN product_images pi ON pi.id = p.primary_image_id
            WHERE p.id = @ProductId
            """, new { request.ProductId }, cancellationToken: ct))
            ?? throw new NotFoundException("Product", request.ProductId);

        return await renderer.RenderPreviewAsync(
            await TemplateResolver.ResolveAsync(
                conn, request.TemplateId, request.ProductId, request.PrinterId, ct),
            product,
            request.Batch ?? product.DefaultBatch,
            request.ProductionDate ?? product.DefaultProductionDate,
            request.ExpiryDate ?? product.DefaultExpiryDate,
            request.QuantityText ?? product.DefaultQuantityText,
            request.CartonNumber ?? 1, request.CartonTotal ?? 1, conn, ct);
    }
}
