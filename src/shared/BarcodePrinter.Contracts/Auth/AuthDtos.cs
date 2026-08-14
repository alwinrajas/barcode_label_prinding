namespace BarcodePrinter.Contracts.Auth;

public sealed record LoginRequest(string Username, string Password, string? Workstation);

public sealed record LoginResponse(
    string AccessToken,
    DateTime AccessTokenExpiresUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresUtc,
    UserInfo User,
    bool MustChangePassword,
    string MinimumClientVersion);

public sealed record RefreshRequest(string RefreshToken, string? Workstation);

public sealed record RefreshResponse(
    string AccessToken,
    DateTime AccessTokenExpiresUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresUtc);

public sealed record LogoutRequest(string RefreshToken);

/// <summary>Workstation is carried so the replacement session is audited like
/// any other login.</summary>
public sealed record ChangePasswordRequest(
    string CurrentPassword, string NewPassword, string? Workstation = null);

public sealed record UserInfo(
    long Id,
    string Username,
    string FullName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);

public sealed record UserSummary(
    long Id,
    string Username,
    string FullName,
    string? Email,
    bool IsActive,
    IReadOnlyList<string> Roles,
    DateTime? LastLoginAtUtc);
