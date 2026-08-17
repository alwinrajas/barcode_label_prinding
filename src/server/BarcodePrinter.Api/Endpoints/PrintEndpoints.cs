using System.Security.Claims;
using System.Text;
using BarcodePrinter.Api.Auth;
using BarcodePrinter.Api.Middleware;
using BarcodePrinter.Application.Abstractions;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Printing;
using BarcodePrinter.Infrastructure.Printing;
using Microsoft.AspNetCore.Mvc;

namespace BarcodePrinter.Api.Endpoints;

public static class PrintEndpoints
{
    public static void MapPrintEndpoints(this WebApplication app)
    {
        MapPrinters(app);
        MapJobs(app);
    }

    private static void MapPrinters(WebApplication app)
    {
        var group = app.MapGroup(ApiRoutes.Printers.Base);

        group.MapGet("/", async ([FromQuery] bool? activeOnly, PrintQueries queries, CancellationToken ct) =>
                Results.Ok(await queries.ListPrintersAsync(activeOnly ?? true, ct)))
            .RequirePermission(PermissionCodes.PrintView);

        group.MapGet("/{id:long}", async (long id, PrintQueries queries, CancellationToken ct) =>
                await queries.GetPrinterAsync(id, ct) is { } printer
                    ? Results.Ok(printer) : throw new BarcodePrinter.Domain.NotFoundException("Printer", id))
            .RequirePermission(PermissionCodes.SettingsManagePrinters);

        group.MapPost("/", async (
                SavePrinterRequest request, PrinterAdminService service,
                ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            {
                var id = await service.CreateAsync(request, Actor(user, http), ct);
                return Results.Created(ApiRoutes.Printers.ById(id), new { id });
            })
            .RequirePermission(PermissionCodes.SettingsManagePrinters);

        group.MapPut("/{id:long}", async (
                long id, SavePrinterRequest request, PrinterAdminService service,
                ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            {
                await service.UpdateAsync(id, request, Actor(user, http), ct);
                return Results.NoContent();
            })
            .RequirePermission(PermissionCodes.SettingsManagePrinters);

        group.MapPost("/{id:long}/default", async (
                long id, PrinterAdminService service,
                ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            {
                await service.SetDefaultAsync(id, Actor(user, http), ct);
                return Results.NoContent();
            })
            .RequirePermission(PermissionCodes.SettingsManagePrinters);

        group.MapPost("/{id:long}/test", async (
                long id, PrinterAdminService service, CancellationToken ct) =>
                Results.Ok(await service.TestAsync(id, ct)))
            .RequirePermission(PermissionCodes.SettingsManagePrinters);

        // PrintView (not admin) permission: the print screen shows the selected
        // printer's live status so operators see "offline" before they print.
        group.MapGet("/{id:long}/status", async (
                long id, PrinterAdminService service, CancellationToken ct) =>
                Results.Ok(await service.GetStatusAsync(id, ct)))
            .RequirePermission(PermissionCodes.PrintView);
    }

    private static void MapJobs(WebApplication app)
    {
        var group = app.MapGroup(ApiRoutes.Print.Base);

        group.MapPost("/jobs", async (
                PrintRequest request, PrintJobService service,
                ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            {
                var result = await service.SubmitAsync(request, Actor(user, http), ct);
                return Results.Accepted($"{ApiRoutes.Print.Base}/jobs/{result.JobId}", result);
            })
            .RequirePermission(PermissionCodes.PrintExecute);

        group.MapPost("/jobs/reprint", async (
                ReprintRequest request, PrintJobService service,
                ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            {
                var result = await service.ReprintAsync(request, Actor(user, http), ct);
                return Results.Accepted($"{ApiRoutes.Print.Base}/jobs/{result.JobId}", result);
            })
            .RequirePermission(PermissionCodes.PrintReprint);

        group.MapGet("/jobs/{id:long}", async (long id, PrintQueries queries, CancellationToken ct) =>
                await queries.GetJobAsync(id, ct) is { } job ? Results.Ok(job) : throw new BarcodePrinter.Domain.NotFoundException("Print job", id))
            .RequirePermission(PermissionCodes.PrintView);

        group.MapPost("/jobs/{id:long}/cancel", async (
                long id, PrintJobService service,
                ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            {
                await service.CancelAsync(id, Actor(user, http), ct);
                return Results.NoContent();
            })
            .RequirePermission(PermissionCodes.PrintCancel);

        // Client dispatcher: collect payload, then report status back.
        group.MapGet("/jobs/{id:long}/payload", async (
                long id, PrintQueries queries, CancellationToken ct) =>
                await queries.GetPayloadAsync(id, ct) is { } bytes
                    ? Results.File(bytes, "application/octet-stream")
                    : throw new BarcodePrinter.Domain.NotFoundException("Print job", id))
            .RequirePermission(PermissionCodes.PrintExecute);

        group.MapPut("/jobs/{id:long}/status", async (
                long id, UpdateJobStatusRequest request, ClientDispatchService service,
                ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            {
                await service.UpdateStatusAsync(id, request, Actor(user, http), ct);
                return Results.NoContent();
            })
            .RequirePermission(PermissionCodes.PrintExecute);

        group.MapPost("/jobs/{id:long}/claim", async (
                long id, [FromQuery] string workstation, ClientDispatchService service,
                CancellationToken ct) =>
                await service.ClaimAsync(id, workstation, ct)
                    ? Results.NoContent() : Results.Conflict())
            .RequirePermission(PermissionCodes.PrintExecute);

        group.MapGet("/pending", async (
                [FromQuery] string workstation, ClientDispatchService service, CancellationToken ct) =>
                Results.Ok(await service.GetPendingAsync(workstation, ct)))
            .RequirePermission(PermissionCodes.PrintExecute);

        // History
        group.MapGet("/history", async (
                [FromQuery] DateTime? from, [FromQuery] DateTime? to,
                [FromQuery] long? productId, [FromQuery] long? userId, [FromQuery] long? printerId,
                [FromQuery] string? status, [FromQuery] bool? reprintsOnly, [FromQuery] string? search,
                [FromQuery] string? cursor, [FromQuery] int? pageSize,
                PrintQueries queries, CancellationToken ct) =>
                Results.Ok(await queries.QueryHistoryAsync(new PrintHistoryFilter(
                    from, to, productId, userId, printerId, status, reprintsOnly, search,
                    cursor, pageSize ?? 50), ct)))
            .RequirePermission(PermissionCodes.HistoryView);

        // Live preview for the print screen.
        // Preview NEVER creates a print transaction: no job row, no carton
        // numbers, nothing enqueued (see LabelPreviewService).
        group.MapPost("/preview", async (
                PrintPreviewRequest request,
                BarcodePrinter.Infrastructure.Printing.LabelPreviewService service,
                CancellationToken ct) =>
            {
                var preview = await service.RenderAsync(request, ct);
                return Results.Ok(new PrintPreviewResponse(
                    preview.Png is null ? null : Convert.ToBase64String(preview.Png),
                    preview.Zpl, preview.Format, preview.Unavailable, preview.Warning));
            })
            .RequirePermission(PermissionCodes.PrintView);
    }

    private static ActorInfo Actor(ClaimsPrincipal user, HttpContext http) => new(
        long.Parse(user.FindFirstValue(AppClaimTypes.UserId) ?? "0"),
        user.FindFirstValue(AppClaimTypes.Username) ?? "",
        http.Items[CorrelationIdMiddleware.Header] as string,
        http.Request.Headers["X-Workstation"].FirstOrDefault());
}
