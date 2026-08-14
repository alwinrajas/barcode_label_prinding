using System.Security.Cryptography;
using System.Text;
using BarcodePrinter.Application.Abstractions;
using BarcodePrinter.Contracts;
using BarcodePrinter.Domain;
using BarcodePrinter.Domain.Identity;

namespace BarcodePrinter.Application.Auth;

/// <summary>Result of a successful authentication — the API layer turns this
/// into a JWT + LoginResponse. Application never sees JWT concerns.</summary>
public sealed record AuthResult(
    User User,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    string RefreshTokenPlain,
    DateTime RefreshTokenExpiresUtc);

public sealed class AuthService(
    IUserStore users,
    IRefreshTokenStore refreshTokens,
    IPasswordService passwords,
    IAuditWriter audit,
    ISettingsProvider settings,
    TimeProvider clock)
{
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromHours(8);

    public async Task<AuthResult> LoginAsync(
        string username, string password, string? workstation, string? ip,
        string? correlationId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var user = await users.FindByUsernameAsync(username.Trim(), ct);

        // Uniform failure for unknown user and wrong password (§19.3 — no
        // account enumeration). Hash verification still runs on a dummy hash
        // for unknown users so response timing does not differ.
        if (user is null)
        {
            passwords.Verify(DummyHash, password);
            await AuditFailureAsync("LoginFailed", username, workstation, ip, correlationId, ct);
            throw new DomainException(ErrorCodes.LoginFailed, "Invalid username or password.");
        }

        if (user.IsLockedOut(now))
        {
            await AuditFailureAsync("LoginLockedOut", username, workstation, ip, correlationId, ct);
            throw new DomainException(ErrorCodes.AccountLocked,
                "The account is temporarily locked after repeated failed attempts.");
        }

        if (!user.IsActive)
        {
            await AuditFailureAsync("LoginInactive", username, workstation, ip, correlationId, ct);
            throw new DomainException(ErrorCodes.LoginFailed, "Invalid username or password.");
        }

        var verdict = passwords.Verify(user.PasswordHash, password);
        if (verdict == PasswordVerdict.Failed)
        {
            var threshold = await settings.GetIntAsync("Auth:LockoutThreshold", 5, ct);
            var minutes = await settings.GetIntAsync("Auth:LockoutMinutes", 15, ct);
            user.RegisterFailedLogin(threshold, TimeSpan.FromMinutes(minutes), now);
            await users.SaveChangesAsync(ct);
            await AuditFailureAsync("LoginFailed", username, workstation, ip, correlationId, ct);
            throw new DomainException(ErrorCodes.LoginFailed, "Invalid username or password.");
        }

        if (verdict == PasswordVerdict.SuccessRehashNeeded)
        {
            user.PasswordHash = passwords.Hash(password);
        }

        user.RegisterSuccessfulLogin(now);
        var (plain, entity) = CreateRefreshToken(user.Id, now, workstation, ip);
        await refreshTokens.AddAsync(entity, ct);
        await users.SaveChangesAsync(ct);

        await audit.WriteAsync(new AuditEntry("LoginSucceeded", "Security",
            user.Id, user.Username, Workstation: workstation, Ip: ip,
            CorrelationId: correlationId), ct);

        return await BuildResultAsync(user, plain, entity.ExpiresAt, ct);
    }

    public async Task<AuthResult> RefreshAsync(
        string refreshTokenPlain, string? workstation, string? ip, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var existing = await refreshTokens.FindByHashAsync(HashToken(refreshTokenPlain), ct);

        if (existing is null)
        {
            throw new DomainException(ErrorCodes.RefreshTokenInvalid, "The session is no longer valid.");
        }

        // Reuse detection (§19.3): a replayed (already rotated/revoked) token
        // means the token leaked — revoke the user's entire chain.
        if (!existing.IsActive(now))
        {
            await refreshTokens.RevokeAllForUserAsync(existing.UserId, now, ct);
            await refreshTokens.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("RefreshTokenReuseDetected", "Security",
                existing.UserId, existing.User?.Username ?? "", Workstation: workstation, Ip: ip), ct);
            throw new DomainException(ErrorCodes.RefreshTokenInvalid, "The session is no longer valid.");
        }

        var user = await users.FindByIdAsync(existing.UserId, ct)
            ?? throw new DomainException(ErrorCodes.RefreshTokenInvalid, "The session is no longer valid.");

        if (!user.IsActive || user.IsLockedOut(now))
        {
            throw new DomainException(ErrorCodes.RefreshTokenInvalid, "The session is no longer valid.");
        }

        // Rotation: single-use tokens. Save the replacement first so its
        // generated Id exists before we link the chain.
        var (plain, replacement) = CreateRefreshToken(user.Id, now, workstation, ip);
        await refreshTokens.AddAsync(replacement, ct);
        await refreshTokens.SaveChangesAsync(ct);
        existing.RevokedAt = now;
        existing.ReplacedById = replacement.Id;
        await refreshTokens.SaveChangesAsync(ct);

        return await BuildResultAsync(user, plain, replacement.ExpiresAt, ct);
    }

    public async Task LogoutAsync(string refreshTokenPlain, string? correlationId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var existing = await refreshTokens.FindByHashAsync(HashToken(refreshTokenPlain), ct);
        if (existing is not null && existing.IsActive(now))
        {
            existing.RevokedAt = now;
            await refreshTokens.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("Logout", "Security",
                existing.UserId, existing.User?.Username ?? "", CorrelationId: correlationId), ct);
        }
    }

    public async Task ChangePasswordAsync(
        long userId, string currentPassword, string newPassword,
        string? correlationId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId, ct)
            ?? throw new NotFoundException("User", userId);

        if (passwords.Verify(user.PasswordHash, currentPassword) == PasswordVerdict.Failed)
        {
            throw new DomainException(ErrorCodes.CurrentPasswordIncorrect,
                "The current password is incorrect.");
        }

        var minLength = await settings.GetIntAsync("Auth:PasswordMinLength", 8, ct);
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < minLength)
        {
            throw new DomainException(ErrorCodes.PasswordPolicyViolation,
                $"The new password must be at least {minLength} characters.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        user.PasswordHash = passwords.Hash(newPassword);
        user.MustChangePassword = false;
        user.SecurityStamp = Guid.NewGuid().ToString();   // invalidates existing JWTs ≤60 s
        user.UpdatedAt = now;
        user.UpdatedBy = userId;

        // Password change ends all other sessions.
        await refreshTokens.RevokeAllForUserAsync(userId, now, ct);
        await users.SaveChangesAsync(ct);

        await audit.WriteAsync(new AuditEntry("PasswordChanged", "Security",
            user.Id, user.Username, CorrelationId: correlationId), ct);
    }

    private async Task<AuthResult> BuildResultAsync(
        User user, string refreshPlain, DateTime refreshExpires, CancellationToken ct)
    {
        var roles = await users.GetRoleCodesAsync(user.Id, ct);
        var permissions = await users.GetPermissionCodesAsync(user.Id, ct);
        return new AuthResult(user, roles, permissions, refreshPlain, refreshExpires);
    }

    private (string Plain, RefreshToken Entity) CreateRefreshToken(
        long userId, DateTime now, string? workstation, string? ip)
    {
        var plain = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var entity = new RefreshToken
        {
            UserId = userId,
            TokenHash = HashToken(plain),
            IssuedAt = now,
            ExpiresAt = now.Add(RefreshTokenLifetime),
            Workstation = workstation,
            Ip = ip,
        };
        return (plain, entity);
    }

    internal static string HashToken(string plain) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plain)));

    private Task AuditFailureAsync(
        string action, string username, string? workstation, string? ip,
        string? correlationId, CancellationToken ct) =>
        audit.WriteAsync(new AuditEntry(action, "Security",
            UsernameSnapshot: username, Workstation: workstation, Ip: ip,
            CorrelationId: correlationId), ct);

    // Constant dummy hash so unknown-user logins cost the same as wrong-password
    // logins (PBKDF2 timing). Generated once from an unguessable value.
    private const string DummyHash =
        "AQAAAAIAAzQgAAAAEDl0K1qkzXicKk9O0eGm4uV1YfF0S1cAqf3T8H0dY0j5nZfW0m8m4m4d5o5m7Qkq9g==";
}
