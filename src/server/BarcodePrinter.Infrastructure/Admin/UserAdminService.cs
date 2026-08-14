using System.Text.Json;
using BarcodePrinter.Application.Abstractions;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Admin;
using BarcodePrinter.Domain;
using BarcodePrinter.Infrastructure.Services;
using Dapper;
using Microsoft.Extensions.Caching.Memory;

namespace BarcodePrinter.Infrastructure.Admin;

/// <summary>
/// User lifecycle (A-5 / §19). Every mutation that changes what a user may do
/// bumps `security_stamp`, which revokes their live JWTs within the validator's
/// cache window — the cache entry is evicted here so revocation is immediate.
/// </summary>
public sealed class UserAdminService(
    IDbConnectionFactory connections,
    IPasswordService passwords,
    IAuditWriter audit,
    IMemoryCache cache,
    ISettingsProvider settings)
{
    public async Task<long> CreateAsync(CreateUserRequest request, ActorInfo actor, CancellationToken ct)
    {
        var username = (request.Username ?? "").Trim();
        if (username.Length is < 3 or > 64)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "Username must be between 3 and 64 characters.");
        }
        if (string.IsNullOrWhiteSpace(request.FullName))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Full name is required.");
        }
        await ValidatePasswordAsync(request.InitialPassword, ct);
        if (request.RoleIds.Count == 0)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "Assign at least one role — a user with no role cannot use the application.");
        }

        await using var conn = await connections.OpenAsync(ct);

        if (await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM users WHERE username = @username",
                new { username }, cancellationToken: ct)) > 0)
        {
            throw new DomainException("USERNAME_DUPLICATE", "That username is already taken.");
        }

        await using var tx = await conn.BeginTransactionAsync(ct);
        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO users (username, full_name, email, password_hash, security_stamp,
                               is_active, must_change_password, concurrency_stamp, created_at, created_by)
            VALUES (@username, @FullName, @Email, @hash, UUID(), 1, 1, UUID(), UTC_TIMESTAMP(3), @UserId);
            SELECT LAST_INSERT_ID();
            """,
            new
            {
                username, request.FullName, request.Email,
                hash = passwords.Hash(request.InitialPassword), actor.UserId,
            }, transaction: tx, cancellationToken: ct));

        await ReplaceRolesAsync(conn, tx, id, request.RoleIds, ct);
        await tx.CommitAsync(ct);

        await audit.WriteAsync(new AuditEntry("UserCreated", "Security",
            actor.UserId, actor.Username, "User", username,
            AfterJson: JsonSerializer.Serialize(new { username, request.FullName, request.Email, request.RoleIds }),
            CorrelationId: actor.CorrelationId), ct);
        return id;
    }

    public async Task UpdateAsync(long id, UpdateUserRequest request, ActorInfo actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.FullName))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Full name is required.");
        }
        if (request.RoleIds.Count == 0)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Assign at least one role.");
        }

        await using var conn = await connections.OpenAsync(ct);
        var before = await LoadForAuditAsync(conn, id, ct)
            ?? throw new NotFoundException("User", id);

        await using var tx = await conn.BeginTransactionAsync(ct);

        // Optimistic concurrency + stamp bump in one statement: role changes
        // must invalidate the user's existing tokens.
        var updated = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE users
            SET full_name = @FullName, email = @Email,
                security_stamp = UUID(), concurrency_stamp = UUID(),
                updated_at = UTC_TIMESTAMP(3), updated_by = @ActorId
            WHERE id = @id AND concurrency_stamp = @ConcurrencyStamp
            """,
            new { id, request.FullName, request.Email, request.ConcurrencyStamp, ActorId = actor.UserId },
            transaction: tx, cancellationToken: ct));
        if (updated == 0)
        {
            throw new ConcurrencyException("user");
        }

        await ReplaceRolesAsync(conn, tx, id, request.RoleIds, ct);
        await tx.CommitAsync(ct);
        EvictUser(id);

        await audit.WriteAsync(new AuditEntry("UserUpdated", "Security",
            actor.UserId, actor.Username, "User", before.Username,
            BeforeJson: JsonSerializer.Serialize(before),
            AfterJson: JsonSerializer.Serialize(new { request.FullName, request.Email, request.RoleIds }),
            CorrelationId: actor.CorrelationId), ct);
    }

    public async Task SetActiveAsync(long id, bool active, ActorInfo actor, CancellationToken ct)
    {
        if (!active && id == actor.UserId)
        {
            throw new DomainException("SELF_DEACTIVATION",
                "You cannot deactivate your own account.");
        }

        await using var conn = await connections.OpenAsync(ct);
        var user = await LoadForAuditAsync(conn, id, ct) ?? throw new NotFoundException("User", id);

        if (!active && await IsLastActiveAdminAsync(conn, id, ct))
        {
            throw new DomainException("LAST_ADMIN",
                "This is the last active administrator. Promote another user first.");
        }

        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE users SET is_active = @active, security_stamp = UUID(),
                             concurrency_stamp = UUID(), locked_until = NULL,
                             failed_login_count = 0,
                             updated_at = UTC_TIMESTAMP(3), updated_by = @ActorId
            WHERE id = @id
            """, new { id, active, ActorId = actor.UserId }, cancellationToken: ct));

        // Deactivation must also kill refresh tokens, not just access tokens.
        if (!active)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE refresh_tokens SET revoked_at = UTC_TIMESTAMP(3) WHERE user_id = @id AND revoked_at IS NULL",
                new { id }, cancellationToken: ct));
        }
        EvictUser(id);

        await audit.WriteAsync(new AuditEntry(active ? "UserActivated" : "UserDeactivated", "Security",
            actor.UserId, actor.Username, "User", user.Username,
            CorrelationId: actor.CorrelationId), ct);
    }

    public async Task ResetPasswordAsync(long id, string newPassword, ActorInfo actor, CancellationToken ct)
    {
        await ValidatePasswordAsync(newPassword, ct);

        await using var conn = await connections.OpenAsync(ct);
        var user = await LoadForAuditAsync(conn, id, ct) ?? throw new NotFoundException("User", id);

        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE users SET password_hash = @hash, must_change_password = 1,
                             security_stamp = UUID(), concurrency_stamp = UUID(),
                             failed_login_count = 0, locked_until = NULL,
                             updated_at = UTC_TIMESTAMP(3), updated_by = @ActorId
            WHERE id = @id;
            UPDATE refresh_tokens SET revoked_at = UTC_TIMESTAMP(3)
            WHERE user_id = @id AND revoked_at IS NULL;
            """,
            new { id, hash = passwords.Hash(newPassword), ActorId = actor.UserId },
            cancellationToken: ct));
        EvictUser(id);

        await audit.WriteAsync(new AuditEntry("UserPasswordReset", "Security",
            actor.UserId, actor.Username, "User", user.Username,
            CorrelationId: actor.CorrelationId), ct);
    }

    // ---- helpers ---------------------------------------------------------------

    private async Task ValidatePasswordAsync(string? password, CancellationToken ct)
    {
        var minLength = await settings.GetIntAsync("Auth:PasswordMinLength", 8, ct);
        if (string.IsNullOrWhiteSpace(password) || password.Length < minLength)
        {
            throw new DomainException(ErrorCodes.PasswordPolicyViolation,
                $"Password must be at least {minLength} characters.");
        }
    }

    private static async Task ReplaceRolesAsync(
        MySqlConnector.MySqlConnection conn, System.Data.Common.DbTransaction tx,
        long userId, IReadOnlyList<long> roleIds, CancellationToken ct)
    {
        var valid = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM roles WHERE id IN @roleIds",
            new { roleIds }, transaction: tx, cancellationToken: ct));
        if (valid != roleIds.Distinct().Count())
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "One or more roles do not exist.");
        }

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM user_roles WHERE user_id = @userId",
            new { userId }, transaction: tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO user_roles (user_id, role_id) VALUES (@userId, @roleId)",
            roleIds.Distinct().Select(roleId => new { userId, roleId }),
            transaction: tx, cancellationToken: ct));
    }

    /// <summary>Guards the "locked out of your own system" failure mode.</summary>
    private static async Task<bool> IsLastActiveAdminAsync(
        MySqlConnector.MySqlConnection conn, long candidateId, CancellationToken ct) =>
        await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT COUNT(DISTINCT u.id) FROM users u
            JOIN user_roles ur ON ur.user_id = u.id
            JOIN roles r ON r.id = ur.role_id AND r.code = 'Admin'
            WHERE u.is_active = 1 AND u.id <> @candidateId
            """, new { candidateId }, cancellationToken: ct)) == 0;

    private static Task<UserAuditRow?> LoadForAuditAsync(
        MySqlConnector.MySqlConnection conn, long id, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<UserAuditRow?>(new CommandDefinition(
            "SELECT username AS Username, full_name AS FullName, email AS Email, is_active AS IsActive FROM users WHERE id = @id",
            new { id }, cancellationToken: ct));

    private void EvictUser(long id) => cache.Remove($"sstamp:{id}");

    private sealed record UserAuditRow(string Username, string FullName, string? Email, bool IsActive);
}
