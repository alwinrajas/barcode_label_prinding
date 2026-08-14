namespace BarcodePrinter.Contracts.Admin;

// ---- Users ------------------------------------------------------------------

public sealed record UserDetail(
    long Id, string Username, string FullName, string? Email,
    bool IsActive, bool MustChangePassword, DateTime? LockedUntilUtc,
    DateTime? LastLoginAtUtc, IReadOnlyList<long> RoleIds,
    IReadOnlyList<string> RoleCodes, string ConcurrencyStamp);

public sealed record CreateUserRequest(
    string Username, string FullName, string? Email,
    string InitialPassword, IReadOnlyList<long> RoleIds);

public sealed record UpdateUserRequest(
    string FullName, string? Email, IReadOnlyList<long> RoleIds, string ConcurrencyStamp);

public sealed record ResetPasswordRequest(string NewPassword);

public sealed record UserCreatedResponse(long Id);

// ---- Roles ------------------------------------------------------------------

public sealed record RoleSummary(
    long Id, string Code, string Name, string? Description,
    bool IsSystem, int PermissionCount, int UserCount);

public sealed record RoleDetail(
    long Id, string Code, string Name, string? Description,
    bool IsSystem, IReadOnlyList<long> PermissionIds, int UserCount);

public sealed record SaveRoleRequest(
    string Code, string Name, string? Description, IReadOnlyList<long> PermissionIds);

/// <summary>Permission catalogue for the matrix editor, grouped by module.</summary>
public sealed record PermissionDto(
    long Id, string Code, string Module, string Action, string DisplayName, int SortOrder);

// ---- Settings ----------------------------------------------------------------

public sealed record SettingDto(
    string Key, string? Value, string ValueType, string? Description, bool IsSecret);

public sealed record SaveSettingsRequest(IReadOnlyDictionary<string, string?> Values);

// ---- Audit -------------------------------------------------------------------

public sealed record AuditEntryDto(
    long Id, DateTime OccurredAtUtc, long? UserId, string Username,
    string Action, string? EntityType, string? EntityId,
    string? BeforeJson, string? AfterJson,
    string? Workstation, string? Ip, string? CorrelationId, string Severity);

public sealed record AuditFilter(
    DateTime? FromUtc, DateTime? ToUtc, long? UserId,
    string? Action, string? EntityType, string? Severity,
    string? Cursor, int PageSize);
