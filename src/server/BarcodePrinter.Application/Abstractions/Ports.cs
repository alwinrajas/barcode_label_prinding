using BarcodePrinter.Domain.Identity;

namespace BarcodePrinter.Application.Abstractions;

/// <summary>Write-side store for users (EF-backed). Reads for grids/reports
/// go through Dapper query handlers, not through this port (blueprint §5.3).</summary>
public interface IUserStore
{
    Task<User?> FindByUsernameAsync(string username, CancellationToken ct);
    Task<User?> FindByIdAsync(long id, CancellationToken ct);
    Task<IReadOnlyList<string>> GetRoleCodesAsync(long userId, CancellationToken ct);
    Task<IReadOnlyList<string>> GetPermissionCodesAsync(long userId, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public interface IRefreshTokenStore
{
    Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct);
    Task AddAsync(RefreshToken token, CancellationToken ct);
    Task RevokeAllForUserAsync(long userId, DateTime utcNow, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public interface IPasswordService
{
    string Hash(string password);
    PasswordVerdict Verify(string hash, string password);
}

public enum PasswordVerdict { Failed, Success, SuccessRehashNeeded }

/// <summary>Audit log writer (blueprint §21). Volume discipline is the
/// caller's job: one row per business action, never one per record.</summary>
public interface IAuditWriter
{
    Task WriteAsync(AuditEntry entry, CancellationToken ct);
}

/// <summary>Who is performing an action � flowed from the JWT by endpoints.
/// Single canonical type shared by every service (no per-layer duplicates).</summary>
public sealed record ActorInfo(long UserId, string Username, string? CorrelationId = null, string? Workstation = null);

public sealed record AuditEntry(
    string Action,
    string Severity = "Info",
    long? UserId = null,
    string UsernameSnapshot = "",
    string? EntityType = null,
    string? EntityId = null,
    string? BeforeJson = null,
    string? AfterJson = null,
    string? Workstation = null,
    string? Ip = null,
    string? CorrelationId = null);

/// <summary>Product image storage (C-14: file store recommended, BLOB possible —
/// this port is what makes the choice swappable).</summary>
public interface IProductImageStore
{
    Task<StoredImage> SaveAsync(Stream content, CancellationToken ct);
    Task<Stream?> OpenAsync(string hash, ImageVariant variant, CancellationToken ct);
}

public enum ImageVariant { Full, Thumb }

public sealed record StoredImage(
    string ContentHash, string StorageKey, string Mime,
    int WidthPx, int HeightPx, int ByteSize);

/// <summary>DB-backed application settings (app_settings), cached with
/// invalidation on write (blueprint §23.2 "no repeated unnecessary DB calls").</summary>
public interface ISettingsProvider
{
    Task<string?> GetAsync(string key, CancellationToken ct);
    Task<int> GetIntAsync(string key, int fallback, CancellationToken ct);
}
