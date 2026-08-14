using System.Security.Claims;
using BarcodePrinter.Api.Auth;
using BarcodePrinter.Api.Middleware;
using BarcodePrinter.Application.Auth;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Auth;
using BarcodePrinter.Infrastructure.Queries;

namespace BarcodePrinter.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var minimumClientVersion = app.Configuration["MinimumClientVersion"] ?? "1.0.0";

        app.MapPost(ApiRoutes.Auth.Login, async (
                LoginRequest request, AuthService auth, JwtTokenService jwt,
                HttpContext http, CancellationToken ct) =>
            {
                var result = await auth.LoginAsync(
                    request.Username, request.Password, request.Workstation,
                    http.Connection.RemoteIpAddress?.ToString(),
                    http.Items[CorrelationIdMiddleware.Header] as string, ct);

                var (token, expires) = jwt.Issue(result);
                return Results.Ok(new LoginResponse(
                    token, expires,
                    result.RefreshTokenPlain, result.RefreshTokenExpiresUtc,
                    new UserInfo(result.User.Id, result.User.Username, result.User.FullName,
                        result.Roles, result.Permissions),
                    result.User.MustChangePassword,
                    minimumClientVersion));
            })
            .AllowAnonymous()
            .RequireRateLimiting("login");

        app.MapPost(ApiRoutes.Auth.Refresh, async (
                RefreshRequest request, AuthService auth, JwtTokenService jwt,
                HttpContext http, CancellationToken ct) =>
            {
                var result = await auth.RefreshAsync(
                    request.RefreshToken, request.Workstation,
                    http.Connection.RemoteIpAddress?.ToString(), ct);

                var (token, expires) = jwt.Issue(result);
                return Results.Ok(new RefreshResponse(
                    token, expires, result.RefreshTokenPlain, result.RefreshTokenExpiresUtc));
            })
            .AllowAnonymous()
            .RequireRateLimiting("login");

        app.MapPost(ApiRoutes.Auth.Logout, async (
                LogoutRequest request, AuthService auth, HttpContext http, CancellationToken ct) =>
            {
                await auth.LogoutAsync(request.RefreshToken,
                    http.Items[CorrelationIdMiddleware.Header] as string, ct);
                return Results.NoContent();
            })
            .RequireAuthorization();

        app.MapPost(ApiRoutes.Auth.ChangePassword, async (
                ChangePasswordRequest request, AuthService auth, JwtTokenService jwt,
                ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            {
                await auth.ChangePasswordAsync(
                    GetUserId(user), request.CurrentPassword, request.NewPassword,
                    http.Items[CorrelationIdMiddleware.Header] as string, ct);

                // The change rotated the security stamp, so the token the caller
                // used to make this request is now revoked. Re-authenticate with
                // the NEW password and return a working session; otherwise the
                // user is dropped into a shell where every request 401s.
                var result = await auth.LoginAsync(
                    user.FindFirstValue(AppClaimTypes.Username) ?? "",
                    request.NewPassword, request.Workstation,
                    http.Connection.RemoteIpAddress?.ToString(),
                    http.Items[CorrelationIdMiddleware.Header] as string, ct);

                var (token, expires) = jwt.Issue(result);
                return Results.Ok(new LoginResponse(
                    token, expires,
                    result.RefreshTokenPlain, result.RefreshTokenExpiresUtc,
                    new UserInfo(result.User.Id, result.User.Username, result.User.FullName,
                        result.Roles, result.Permissions),
                    result.User.MustChangePassword,
                    minimumClientVersion));
            })
            .RequireAuthorization();

        app.MapGet(ApiRoutes.Auth.Me, (ClaimsPrincipal user) =>
            {
                var info = new UserInfo(
                    GetUserId(user),
                    user.FindFirstValue(AppClaimTypes.Username) ?? "",
                    user.FindFirstValue(AppClaimTypes.Username) ?? "",
                    [.. user.FindAll(AppClaimTypes.Role).Select(c => c.Value)],
                    [.. user.FindAll(AppClaimTypes.Permission).Select(c => c.Value)]);
                return Results.Ok(info);
            })
            .RequireAuthorization();

        // First concrete permission-protected endpoint — the RBAC bypass test
        // target (§19.2: a valid lower-role token must receive 403 here).
        app.MapGet(ApiRoutes.Users.List, async (UsersQuery users, CancellationToken ct) =>
                Results.Ok(await users.ListAsync(ct)))
            .RequirePermission(PermissionCodes.UserView);
    }

    private static long GetUserId(ClaimsPrincipal user) =>
        long.Parse(
            user.FindFirstValue(AppClaimTypes.UserId)
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Token has no user id claim."));
}
