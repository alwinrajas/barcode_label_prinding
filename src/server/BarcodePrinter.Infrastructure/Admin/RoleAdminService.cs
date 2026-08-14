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
/// Role and permission-matrix management (A-5: custom roles are a requirement).
/// Changing a role's permissions must take effect for everyone holding it, so
/// every affected user's security stamp is bumped and their cache entry evicted.
/// </summary>
public sealed class RoleAdminService(
    IDbConnectionFactory connections, IAuditWriter audit, IMemoryCache cache)
{
    public async Task<long> CreateAsync(SaveRoleRequest request, ActorInfo actor, CancellationToken ct)
    {
        Validate(request);
        await using var conn = await connections.OpenAsync(ct);

        if (await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM roles WHERE code = @Code", new { request.Code },
                cancellationToken: ct)) > 0)
        {
            throw new DomainException("ROLE_CODE_DUPLICATE", "A role with that code already exists.");
        }

        await using var tx = await conn.BeginTransactionAsync(ct);
        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO roles (code, name, description, is_system, created_at, created_by)
            VALUES (@Code, @Name, @Description, 0, UTC_TIMESTAMP(3), @UserId);
            SELECT LAST_INSERT_ID();
            """, new { request.Code, request.Name, request.Description, actor.UserId },
            transaction: tx, cancellationToken: ct));

        await ReplacePermissionsAsync(conn, tx, id, request.PermissionIds, ct);
        await tx.CommitAsync(ct);

        await audit.WriteAsync(new AuditEntry("RoleCreated", "Security",
            actor.UserId, actor.Username, "Role", request.Code,
            AfterJson: JsonSerializer.Serialize(new { request.Name, permissions = request.PermissionIds.Count }),
            CorrelationId: actor.CorrelationId), ct);
        return id;
    }

    public async Task UpdateAsync(long id, SaveRoleRequest request, ActorInfo actor, CancellationToken ct)
    {
        Validate(request);
        await using var conn = await connections.OpenAsync(ct);

        var role = await conn.QuerySingleOrDefaultAsync<RoleRow?>(new CommandDefinition(
            "SELECT code AS Code, name AS Name, is_system AS IsSystem FROM roles WHERE id = @id",
            new { id }, cancellationToken: ct)) ?? throw new NotFoundException("Role", id);

        // System role codes are referenced by seed data and RBAC tests; the
        // permission set stays editable, the identity does not.
        if (role.IsSystem && !string.Equals(role.Code, request.Code, StringComparison.Ordinal))
        {
            throw new DomainException("ROLE_SYSTEM_CODE",
                $"The '{role.Code}' role is a system role — its code cannot be changed.");
        }

        var before = await GetPermissionIdsAsync(conn, id, ct);

        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE roles SET code = @Code, name = @Name, description = @Description,
                             updated_at = UTC_TIMESTAMP(3), updated_by = @UserId
            WHERE id = @id
            """, new { id, request.Code, request.Name, request.Description, actor.UserId },
            transaction: tx, cancellationToken: ct));

        await ReplacePermissionsAsync(conn, tx, id, request.PermissionIds, ct);

        // Permission change = authorization change: revoke live tokens for
        // everyone in this role.
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE users u JOIN user_roles ur ON ur.user_id = u.id
            SET u.security_stamp = UUID(), u.concurrency_stamp = UUID()
            WHERE ur.role_id = @id
            """, new { id }, transaction: tx, cancellationToken: ct));

        var affected = (await conn.QueryAsync<long>(new CommandDefinition(
            "SELECT CAST(user_id AS SIGNED) FROM user_roles WHERE role_id = @id",
            new { id }, transaction: tx, cancellationToken: ct))).ToList();

        await tx.CommitAsync(ct);
        foreach (var userId in affected)
        {
            cache.Remove($"sstamp:{userId}");
        }

        await audit.WriteAsync(new AuditEntry("RolePermissionsChanged", "Security",
            actor.UserId, actor.Username, "Role", request.Code,
            BeforeJson: JsonSerializer.Serialize(new { permissionIds = before }),
            AfterJson: JsonSerializer.Serialize(new { permissionIds = request.PermissionIds, affectedUsers = affected.Count }),
            CorrelationId: actor.CorrelationId), ct);
    }

    public async Task DeleteAsync(long id, ActorInfo actor, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var role = await conn.QuerySingleOrDefaultAsync<RoleRow?>(new CommandDefinition(
            "SELECT code AS Code, name AS Name, is_system AS IsSystem FROM roles WHERE id = @id",
            new { id }, cancellationToken: ct)) ?? throw new NotFoundException("Role", id);

        if (role.IsSystem)
        {
            throw new DomainException("ROLE_SYSTEM_DELETE",
                $"The '{role.Code}' role is a system role and cannot be deleted.");
        }

        var inUse = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM user_roles WHERE role_id = @id", new { id }, cancellationToken: ct));
        if (inUse > 0)
        {
            throw new DomainException("ROLE_IN_USE",
                $"{inUse} user(s) still have this role. Reassign them first.");
        }

        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM role_permissions WHERE role_id = @id", new { id },
            transaction: tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM roles WHERE id = @id", new { id },
            transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);

        await audit.WriteAsync(new AuditEntry("RoleDeleted", "Security",
            actor.UserId, actor.Username, "Role", role.Code,
            CorrelationId: actor.CorrelationId), ct);
    }

    private static void Validate(SaveRoleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 32)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "Role code is required (max 32 characters).");
        }
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Role name is required.");
        }
    }

    private static async Task ReplacePermissionsAsync(
        MySqlConnector.MySqlConnection conn, System.Data.Common.DbTransaction tx,
        long roleId, IReadOnlyList<long> permissionIds, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM role_permissions WHERE role_id = @roleId",
            new { roleId }, transaction: tx, cancellationToken: ct));

        var ids = permissionIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var known = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM permissions WHERE id IN @ids", new { ids },
            transaction: tx, cancellationToken: ct));
        if (known != ids.Count)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "One or more permissions do not exist.");
        }

        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO role_permissions (role_id, permission_id) VALUES (@roleId, @permissionId)",
            ids.Select(permissionId => new { roleId, permissionId }),
            transaction: tx, cancellationToken: ct));
    }

    private static async Task<List<long>> GetPermissionIdsAsync(
        MySqlConnector.MySqlConnection conn, long roleId, CancellationToken ct) =>
        (await conn.QueryAsync<long>(new CommandDefinition(
            "SELECT CAST(permission_id AS SIGNED) FROM role_permissions WHERE role_id = @roleId",
            new { roleId }, cancellationToken: ct))).ToList();

    private sealed record RoleRow(string Code, string Name, bool IsSystem);
}
