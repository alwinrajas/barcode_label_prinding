using BarcodePrinter.Application.Abstractions;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using MySqlConnector;

namespace BarcodePrinter.Infrastructure.Services;

/// <summary>Opens Dapper read connections. Only the API tier holds this —
/// clients never see a connection string (A-28).</summary>
public interface IDbConnectionFactory
{
    Task<MySqlConnection> OpenAsync(CancellationToken ct);
}

public static class ConnectionStrings
{
    /// <summary>Central normalisation applied to every DB connection:
    /// CHAR(36) columns hold string stamps, not GUIDs, so MySqlConnector's
    /// default CHAR(36)→Guid mapping must be off — enforced here rather than
    /// remembered per deployment.</summary>
    public static string Normalize(string raw) =>
        new MySqlConnectionStringBuilder(raw) { GuidFormat = MySqlGuidFormat.None }.ConnectionString;
}

public sealed class MySqlConnectionFactory(IConfiguration configuration) : IDbConnectionFactory
{
    private readonly string _connectionString = ConnectionStrings.Normalize(
        configuration.GetConnectionString("BarcodePrinter")
        ?? throw new InvalidOperationException("Connection string 'BarcodePrinter' is not configured."));

    public async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}

/// <summary>PasswordHasher&lt;T&gt; = PBKDF2-HMAC-SHA512, versioned format,
/// automatic rehash-on-verify when parameters change (B-11).</summary>
public sealed class PasswordService : IPasswordService
{
    private readonly PasswordHasher<object> _hasher = new();
    private static readonly object Dummy = new();

    public string Hash(string password) => _hasher.HashPassword(Dummy, password);

    public PasswordVerdict Verify(string hash, string password) =>
        _hasher.VerifyHashedPassword(Dummy, hash, password) switch
        {
            PasswordVerificationResult.Success => PasswordVerdict.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordVerdict.SuccessRehashNeeded,
            _ => PasswordVerdict.Failed,
        };
}

/// <summary>Direct Dapper insert into audit_logs — audit writes must not
/// depend on an EF unit of work that a failure might roll back with the
/// business change it is meant to record (login failures have no transaction).</summary>
public sealed class AuditWriter(IDbConnectionFactory connections) : IAuditWriter
{
    public async Task WriteAsync(AuditEntry entry, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO audit_logs
                (occurred_at, user_id, username_snapshot, action, entity_type,
                 entity_id, before_json, after_json, workstation, ip,
                 correlation_id, severity)
            VALUES
                (UTC_TIMESTAMP(3), @UserId, @UsernameSnapshot, @Action, @EntityType,
                 @EntityId, @BeforeJson, @AfterJson, @Workstation, @Ip,
                 @CorrelationId, @Severity)
            """,
            entry, cancellationToken: ct));
    }
}

/// <summary>app_settings reader with a 60 s cache; invalidated on write from
/// the settings admin (phase 8).</summary>
public sealed class SettingsProvider(IDbConnectionFactory connections, IMemoryCache cache)
    : ISettingsProvider
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        return await cache.GetOrCreateAsync($"setting:{key}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = CacheFor;
            await using var conn = await connections.OpenAsync(ct);
            return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT setting_value FROM app_settings WHERE setting_key = @key AND scope = 'Global'",
                new { key }, cancellationToken: ct));
        });
    }

    public async Task<int> GetIntAsync(string key, int fallback, CancellationToken ct) =>
        int.TryParse(await GetAsync(key, ct), out var value) ? value : fallback;
}
