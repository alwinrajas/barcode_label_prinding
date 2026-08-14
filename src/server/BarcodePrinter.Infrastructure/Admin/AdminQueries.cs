using System.Text;
using BarcodePrinter.Contracts.Admin;
using BarcodePrinter.Contracts.Products;
using BarcodePrinter.Infrastructure.Services;
using Dapper;

namespace BarcodePrinter.Infrastructure.Admin;

public sealed class AdminQueries(IDbConnectionFactory connections)
{
    // ---- Users ---------------------------------------------------------------

    public async Task<UserDetail?> GetUserAsync(long id, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            """
            SELECT CAST(id AS SIGNED) AS Id, username AS Username, full_name AS FullName,
                   email AS Email, is_active AS IsActive,
                   must_change_password AS MustChangePassword, locked_until AS LockedUntilUtc,
                   last_login_at AS LastLoginAtUtc, concurrency_stamp AS ConcurrencyStamp
            FROM users WHERE id = @id
            """, new { id }, cancellationToken: ct));
        if (row is null)
        {
            return null;
        }

        var roles = (await conn.QueryAsync<(long Id, string Code)>(new CommandDefinition(
            """
            SELECT CAST(r.id AS SIGNED), r.code FROM roles r
            JOIN user_roles ur ON ur.role_id = r.id WHERE ur.user_id = @id ORDER BY r.code
            """, new { id }, cancellationToken: ct))).ToList();

        return new UserDetail(row.Id, row.Username, row.FullName, row.Email,
            row.IsActive, row.MustChangePassword, row.LockedUntilUtc, row.LastLoginAtUtc,
            roles.Select(r => r.Id).ToList(), roles.Select(r => r.Code).ToList(),
            row.ConcurrencyStamp);
    }

    // ---- Roles / permissions -----------------------------------------------------

    public async Task<IReadOnlyList<RoleSummary>> ListRolesAsync(CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        // Aggregate subqueries, not joins: avoids the cartesian blow-up that a
        // double LEFT JOIN across role_permissions × user_roles would produce.
        var rows = await conn.QueryAsync<RoleSummaryRow>(new CommandDefinition(
            """
            SELECT CAST(r.id AS SIGNED) AS Id, r.code AS Code, r.name AS Name,
                   r.description AS Description, r.is_system AS IsSystem,
                   (SELECT COUNT(*) FROM role_permissions rp WHERE rp.role_id = r.id) AS PermissionCount,
                   (SELECT COUNT(*) FROM user_roles ur WHERE ur.role_id = r.id) AS UserCount
            FROM roles r ORDER BY r.is_system DESC, r.code
            """, cancellationToken: ct));
        return rows.Select(r => new RoleSummary(
            r.Id, r.Code, r.Name, r.Description, r.IsSystem, r.PermissionCount, r.UserCount)).ToList();
    }

    public async Task<RoleDetail?> GetRoleAsync(long id, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<RoleSummaryRow>(new CommandDefinition(
            """
            SELECT CAST(r.id AS SIGNED) AS Id, r.code AS Code, r.name AS Name,
                   r.description AS Description, r.is_system AS IsSystem,
                   0 AS PermissionCount,
                   (SELECT COUNT(*) FROM user_roles ur WHERE ur.role_id = r.id) AS UserCount
            FROM roles r WHERE r.id = @id
            """, new { id }, cancellationToken: ct));
        if (row is null)
        {
            return null;
        }

        var permissionIds = (await conn.QueryAsync<long>(new CommandDefinition(
            "SELECT CAST(permission_id AS SIGNED) FROM role_permissions WHERE role_id = @id",
            new { id }, cancellationToken: ct))).ToList();

        return new RoleDetail(row.Id, row.Code, row.Name, row.Description,
            row.IsSystem, permissionIds, row.UserCount);
    }

    public async Task<IReadOnlyList<PermissionDto>> ListPermissionsAsync(CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var rows = await conn.QueryAsync<PermissionRow>(new CommandDefinition(
            """
            SELECT CAST(id AS SIGNED) AS Id, code AS Code, module AS Module,
                   action AS Action, display_name AS DisplayName, sort_order AS SortOrder
            FROM permissions ORDER BY sort_order, code
            """, cancellationToken: ct));
        return rows.Select(r => new PermissionDto(
            r.Id, r.Code, r.Module, r.Action, r.DisplayName, r.SortOrder)).ToList();
    }

    // ---- Settings -------------------------------------------------------------------

    /// <summary>Secrets are returned with a null value — the UI shows a masked
    /// box and only sends a replacement when the admin types one (§19.4).</summary>
    public async Task<IReadOnlyList<SettingDto>> ListSettingsAsync(CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var rows = await conn.QueryAsync<SettingRow>(new CommandDefinition(
            """
            SELECT setting_key AS `Key`, setting_value AS Value, value_type AS ValueType,
                   description AS Description, is_secret AS IsSecret
            FROM app_settings WHERE scope = 'Global' ORDER BY setting_key
            """, cancellationToken: ct));
        return rows.Select(r => new SettingDto(
            r.Key, r.IsSecret ? null : r.Value, r.ValueType, r.Description, r.IsSecret)).ToList();
    }

    // ---- Audit ------------------------------------------------------------------------

    /// <summary>Keyset-paged audit viewer. Always date-bounded so the partitioned
    /// table prunes (§9.2); descending id is the cursor.</summary>
    public async Task<PagedResult<AuditEntryDto>> QueryAuditAsync(AuditFilter filter, CancellationToken ct)
    {
        var pageSize = Math.Clamp(filter.PageSize <= 0 ? 50 : filter.PageSize, 1, 200);
        var from = filter.FromUtc ?? DateTime.UtcNow.AddDays(-7);
        var to = filter.ToUtc ?? DateTime.UtcNow.AddDays(1);

        var where = new StringBuilder("WHERE a.occurred_at >= @from AND a.occurred_at < @to");
        if (filter.UserId is not null) where.Append(" AND a.user_id = @UserId");
        if (!string.IsNullOrWhiteSpace(filter.Action)) where.Append(" AND a.action = @Action");
        if (!string.IsNullOrWhiteSpace(filter.EntityType)) where.Append(" AND a.entity_type = @EntityType");
        if (!string.IsNullOrWhiteSpace(filter.Severity)) where.Append(" AND a.severity = @Severity");
        var hasCursor = HistoryCursor.TryDecode(filter.Cursor, out var afterAt, out var afterId);
        if (hasCursor)
        {
            where.Append(" AND (a.occurred_at < @afterAt OR (a.occurred_at = @afterAt AND a.id < @afterId))");
        }

        await using var conn = await connections.OpenAsync(ct);
        var rows = (await conn.QueryAsync<AuditRow>(new CommandDefinition(
            $"""
            SELECT CAST(a.id AS SIGNED) AS Id, a.occurred_at AS OccurredAtUtc,
                   CAST(a.user_id AS SIGNED) AS UserId, a.username_snapshot AS Username,
                   a.action AS Action, a.entity_type AS EntityType, a.entity_id AS EntityId,
                   a.before_json AS BeforeJson, a.after_json AS AfterJson,
                   a.workstation AS Workstation, a.ip AS Ip,
                   a.correlation_id AS CorrelationId, a.severity AS Severity
            FROM audit_logs a
            {where}
            ORDER BY a.occurred_at DESC, a.id DESC
            LIMIT @limit
            """,
            new
            {
                from, to, filter.UserId, filter.Action, filter.EntityType,
                filter.Severity, afterAt, afterId, limit = pageSize + 1,
            }, cancellationToken: ct))).ToList();

        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var items = rows.Select(r => new AuditEntryDto(
            r.Id, r.OccurredAtUtc, r.UserId, r.Username, r.Action, r.EntityType, r.EntityId,
            r.BeforeJson, r.AfterJson, r.Workstation, r.Ip, r.CorrelationId, r.Severity)).ToList();

        return new PagedResult<AuditEntryDto>(
            items,
            hasMore ? HistoryCursor.Encode(items[^1].OccurredAtUtc, items[^1].Id) : null,
            hasMore);
    }

    public async Task<IReadOnlyList<string>> ListAuditActionsAsync(CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        return (await conn.QueryAsync<string>(new CommandDefinition(
            """
            SELECT DISTINCT action FROM audit_logs
            WHERE occurred_at >= UTC_TIMESTAMP(3) - INTERVAL 90 DAY
            ORDER BY action
            """, cancellationToken: ct))).ToList();
    }

    private sealed class UserRow
    {
        public long Id { get; set; }
        public string Username { get; set; } = "";
        public string FullName { get; set; } = "";
        public string? Email { get; set; }
        public bool IsActive { get; set; }
        public bool MustChangePassword { get; set; }
        public DateTime? LockedUntilUtc { get; set; }
        public DateTime? LastLoginAtUtc { get; set; }
        public string ConcurrencyStamp { get; set; } = "";
    }

    private sealed class RoleSummaryRow
    {
        public long Id { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public bool IsSystem { get; set; }
        public int PermissionCount { get; set; }
        public int UserCount { get; set; }
    }

    private sealed class PermissionRow
    {
        public long Id { get; set; }
        public string Code { get; set; } = "";
        public string Module { get; set; } = "";
        public string Action { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public int SortOrder { get; set; }
    }

    private sealed class SettingRow
    {
        public string Key { get; set; } = "";
        public string? Value { get; set; }
        public string ValueType { get; set; } = "String";
        public string? Description { get; set; }
        public bool IsSecret { get; set; }
    }

    private sealed class AuditRow
    {
        public long Id { get; set; }
        public DateTime OccurredAtUtc { get; set; }
        public long? UserId { get; set; }
        public string Username { get; set; } = "";
        public string Action { get; set; } = "";
        public string? EntityType { get; set; }
        public string? EntityId { get; set; }
        public string? BeforeJson { get; set; }
        public string? AfterJson { get; set; }
        public string? Workstation { get; set; }
        public string? Ip { get; set; }
        public string? CorrelationId { get; set; }
        public string Severity { get; set; } = "Info";
    }
}
