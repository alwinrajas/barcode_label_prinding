using System.Security.Claims;
using BarcodePrinter.Api.Auth;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Reports;
using BarcodePrinter.Infrastructure.Reports;
using Microsoft.AspNetCore.Mvc;

namespace BarcodePrinter.Api.Endpoints;

public static class ReportEndpoints
{
    public static void MapReportEndpoints(this WebApplication app)
    {
        app.MapGet(ApiRoutes.Reports.Base, async (
                [FromQuery] string? type, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
                [FromQuery] long? productId, [FromQuery] long? userId, [FromQuery] long? printerId,
                [FromQuery] string? status, [FromQuery] string? search,
                [FromQuery] int? pageSize, [FromQuery] string? cursor,
                ReportQueries queries, CancellationToken ct) =>
                Results.Ok(await queries.RunAsync(new ReportFilter(
                    type ?? nameof(ReportType.PrintLog), from, to, productId, userId,
                    printerId, status, search, pageSize ?? 100, cursor), ct)))
            .RequirePermission(PermissionCodes.ReportView);

        app.MapGet(ApiRoutes.Reports.Export, async (
                [FromQuery] string? type, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
                [FromQuery] long? productId, [FromQuery] long? userId, [FromQuery] long? printerId,
                [FromQuery] string? status, [FromQuery] string? search,
                ReportExport export, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var filter = new ReportFilter(type ?? nameof(ReportType.PrintLog), from, to,
                    productId, userId, printerId, status, search, 500, null);
                var bytes = await export.BuildAsync(
                    filter, user.FindFirstValue(AppClaimTypes.Username) ?? "", ct);
                return Results.File(bytes,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    $"{filter.Type}-{DateTime.Now:yyyyMMdd-HHmm}.xlsx");
            })
            .RequirePermission(PermissionCodes.ReportExport);
    }
}
