using BarcodePrinter.Contracts.Auth;
using BarcodePrinter.Infrastructure.Services;
using Dapper;

namespace BarcodePrinter.Infrastructure.Queries;

/// <summary>Read-side query for the user list (Dapper, explicit columns,
/// no tracking — blueprint §5.3). Grows filters/paging with phase 8's
/// user-management screen.</summary>
public sealed class UsersQuery(IDbConnectionFactory connections)
{
    public async Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);

        // Note for every Dapper query in this codebase: BIGINT UNSIGNED keys
        // materialise as ulong, which fails record-constructor mapping —
        // always CAST(id AS SIGNED) in the projection.
        var rows = await conn.QueryAsync<UserRow>(new CommandDefinition(
            """
            SELECT CAST(u.id AS SIGNED) AS id,
                   u.username, u.full_name, u.email, u.is_active,
                   u.last_login_at,
                   GROUP_CONCAT(r.code ORDER BY r.code SEPARATOR ',') AS role_codes
            FROM users u
            LEFT JOIN user_roles ur ON ur.user_id = u.id
            LEFT JOIN roles r ON r.id = ur.role_id
            GROUP BY u.id, u.username, u.full_name, u.email, u.is_active, u.last_login_at
            ORDER BY u.username
            LIMIT 200
            """, cancellationToken: ct));

        return rows.Select(r => new UserSummary(
            r.Id, r.Username, r.Full_Name, r.Email, r.Is_Active,
            string.IsNullOrEmpty(r.Role_Codes) ? [] : r.Role_Codes.Split(','),
            r.Last_Login_At)).ToList();
    }

    private sealed record UserRow(
        long Id, string Username, string Full_Name, string? Email,
        bool Is_Active, DateTime? Last_Login_At, string? Role_Codes);
}
