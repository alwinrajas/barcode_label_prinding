using BarcodePrinter.Contracts;
using Dapper;
using BarcodePrinter.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;

namespace BarcodePrinter.Api.Auth;

/// <summary>
/// Permission-per-endpoint authorization (blueprint §13/§19.2).
/// Usage: app.MapGet(...).RequirePermission(PermissionCodes.UserView)
/// No endpoint is unprotected by default — the fallback policy demands an
/// authenticated user, and endpoints opt OUT via AllowAnonymous, never in.
/// </summary>
public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.HasClaim(AppClaimTypes.Permission, requirement.Permission))
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Security-stamp validation (§19.3): every authenticated request compares the
/// token's stamp against the user's current stamp (cached 60 s). Deactivation,
/// role change or password reset therefore revokes access within ~60 s without
/// waiting for token expiry.
/// </summary>
public sealed class SecurityStampValidator(IDbConnectionFactory connections, IMemoryCache cache)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    public async Task<bool> IsCurrentAsync(long userId, string stamp, CancellationToken ct)
    {
        var current = await ReadCachedAsync(userId, ct);
        if (current is not null && string.Equals(current, stamp, StringComparison.Ordinal))
        {
            return true;
        }

        // Mismatch may mean the CACHE is stale, not the token: a user who just
        // changed their password logs back in with the NEW stamp while the old
        // one is still cached. Re-read once before rejecting — false rejections
        // disappear, and the ≤60 s revocation window for genuinely stale tokens
        // is unchanged (matching tokens never reach this path).
        cache.Remove($"sstamp:{userId}");
        current = await ReadCachedAsync(userId, ct);
        return current is not null && string.Equals(current, stamp, StringComparison.Ordinal);
    }

    private Task<string?> ReadCachedAsync(long userId, CancellationToken ct) =>
        cache.GetOrCreateAsync($"sstamp:{userId}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = CacheFor;
            await using var conn = await connections.OpenAsync(ct);
            return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT security_stamp FROM users WHERE id = @userId AND is_active = 1",
                new { userId }, cancellationToken: ct));
        });
}

/// <summary>
/// Named policies, for the places that cannot use the inline
/// <see cref="PermissionEndpointExtensions.RequirePermission"/> builder —
/// notably [Authorize] on a SignalR hub, where the policy name must be a
/// compile-time constant.
/// </summary>
public static class PermissionPolicy
{
    public const string Prefix = "perm:";

    public const string PrintView = Prefix + PermissionCodes.PrintView;
    public const string ProductImport = Prefix + PermissionCodes.ProductImport;

    public static string For(string permission) => Prefix + permission;
}

public static class PermissionEndpointExtensions
{
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(policy => policy.AddRequirements(new PermissionRequirement(permission)));
}
