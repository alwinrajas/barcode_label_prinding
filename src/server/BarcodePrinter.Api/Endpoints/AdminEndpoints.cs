using System.Security.Claims;
using BarcodePrinter.Api.Auth;
using BarcodePrinter.Api.Middleware;
using BarcodePrinter.Application.Abstractions;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Admin;
using BarcodePrinter.Infrastructure.Admin;
using BarcodePrinter.Infrastructure.Queries;
using BarcodePrinter.Infrastructure.Templates;
using Microsoft.AspNetCore.Mvc;

namespace BarcodePrinter.Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        MapUsers(app);
        MapRoles(app);
        MapSettings(app);
        MapAudit(app);
    }

    private static void MapUsers(WebApplication app)
    {
        var group = app.MapGroup(ApiRoutes.Users.Base);

        group.MapGet("/{id:long}", async (long id, AdminQueries queries, CancellationToken ct) =>
                await queries.GetUserAsync(id, ct) is { } user ? Results.Ok(user) : throw new BarcodePrinter.Domain.NotFoundException("User", id))
            .RequirePermission(PermissionCodes.UserView);

        group.MapPost("/", async (
                CreateUserRequest request, UserAdminService service,
                ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            {
                var id = await service.CreateAsync(request, Actor(user, http), ct);
                return Results.Created(ApiRoutes.Users.ById(id), new UserCreatedResponse(id));
            })
            .RequirePermission(PermissionCodes.UserAdd);

        group.MapPut("/{id:long}", async (
                long id, UpdateUserRequest request, UserAdminService service,
                ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            {
                await service.UpdateAsync(id, request, Actor(user, http), ct);
                return Results.NoContent();
            })
            .RequirePermission(PermissionCodes.UserEdit);

        group.MapPost("/{id:long}/activate", async (
                long id, [FromQuery] bool? active, UserAdminService service,
                ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            {
                await service.SetActiveAsync(id, active ?? true, Actor(user, http), ct);
                return Results.NoContent();
            })
            .RequirePermission(PermissionCodes.UserDeactivate);

        group.MapPost("/{id:long}/reset-password", async (
                long id, ResetPasswordRequest request, UserAdminService service,
                ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            {
                await service.ResetPasswordAsync(id, request.NewPassword, Actor(user, http), ct);
                return Results.NoContent();
            })
            .RequirePermission(PermissionCodes.UserResetPassword);
    }

    private static void MapRoles(WebApplication app)
    {
        var group = app.MapGroup(ApiRoutes.Roles.Base);

        group.MapGet("/", async (AdminQueries queries, CancellationToken ct) =>
                Results.Ok(await queries.ListRolesAsync(ct)))
            .RequirePermission(PermissionCodes.RoleView);

        group.MapGet("/{id:long}", async (long id, AdminQueries queries, CancellationToken ct) =>
                await queries.GetRoleAsync(id, ct) is { } role ? Results.Ok(role) : throw new BarcodePrinter.Domain.NotFoundException("Role", id))
            .RequirePermission(PermissionCodes.RoleView);

        group.MapPost("/", async (
                SaveRoleRequest request, RoleAdminService service,
                ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            {
                var id = await service.CreateAsync(request, Actor(user, http), ct);
                return Results.Created(ApiRoutes.Roles.ById(id), new { id });
            })
            .RequirePermission(PermissionCodes.RoleManage);

        group.MapPut("/{id:long}", async (
                long id, SaveRoleRequest request, RoleAdminService service,
                ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            {
                await service.UpdateAsync(id, request, Actor(user, http), ct);
                return Results.NoContent();
            })
            .RequirePermission(PermissionCodes.RoleManage);

        group.MapDelete("/{id:long}", async (
                long id, RoleAdminService service,
                ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            {
                await service.DeleteAsync(id, Actor(user, http), ct);
                return Results.NoContent();
            })
            .RequirePermission(PermissionCodes.RoleManage);

        app.MapGet(ApiRoutes.Roles.Permissions, async (AdminQueries queries, CancellationToken ct) =>
                Results.Ok(await queries.ListPermissionsAsync(ct)))
            .RequirePermission(PermissionCodes.RoleView);
    }

    private static void MapSettings(WebApplication app)
    {
        app.MapGet(ApiRoutes.Settings.Base, async (AdminQueries queries, CancellationToken ct) =>
                Results.Ok(await queries.ListSettingsAsync(ct)))
            .RequirePermission(PermissionCodes.SettingsView);

        app.MapPut(ApiRoutes.Settings.Base, async (
                SaveSettingsRequest request, SettingsAdminService service,
                ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            {
                await service.SaveAsync(request.Values, Actor(user, http), ct);
                return Results.NoContent();
            })
            .RequirePermission(PermissionCodes.SettingsManage);
    }

    private static void MapAudit(WebApplication app)
    {
        app.MapGet(ApiRoutes.Audit.Base, async (
                [FromQuery] DateTime? from, [FromQuery] DateTime? to,
                [FromQuery] long? userId, [FromQuery] string? action,
                [FromQuery] string? entityType, [FromQuery] string? severity,
                [FromQuery] string? cursor, [FromQuery] int? pageSize,
                AdminQueries queries, CancellationToken ct) =>
                Results.Ok(await queries.QueryAuditAsync(new AuditFilter(
                    from, to, userId, action, entityType, severity, cursor, pageSize ?? 50), ct)))
            .RequirePermission(PermissionCodes.AuditView);

        app.MapGet(ApiRoutes.Audit.Actions, async (AdminQueries queries, CancellationToken ct) =>
                Results.Ok(await queries.ListAuditActionsAsync(ct)))
            .RequirePermission(PermissionCodes.AuditView);

        // The route and the Audit.Export permission already existed but nothing
        // was mapped to them, so the download 404'd.
        app.MapGet(ApiRoutes.Audit.Export, async (
                [FromQuery] DateTime? from, [FromQuery] DateTime? to,
                [FromQuery] long? userId, [FromQuery] string? action,
                [FromQuery] string? entityType, [FromQuery] string? severity,
                BarcodePrinter.Infrastructure.Admin.AuditExport export,
                ClaimsPrincipal user, CancellationToken ct) =>
            {
                var bytes = await export.BuildAsync(
                    new AuditFilter(from, to, userId, action, entityType, severity, null, 200),
                    user.FindFirstValue(AppClaimTypes.Username) ?? "", ct);
                return Results.File(bytes,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    $"audit-{DateTime.Now:yyyyMMdd-HHmm}.xlsx");
            })
            .RequirePermission(PermissionCodes.AuditExport);
    }

    private static ActorInfo Actor(ClaimsPrincipal user, HttpContext http) => new(
        long.Parse(user.FindFirstValue(AppClaimTypes.UserId) ?? "0"),
        user.FindFirstValue(AppClaimTypes.Username) ?? "",
        http.Items[CorrelationIdMiddleware.Header] as string);
}
