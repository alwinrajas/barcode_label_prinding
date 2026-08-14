namespace BarcodePrinter.Domain.Identity;

public class User
{
    public long Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Bumped on password reset / role change / deactivation; JWTs
    /// carrying a stale stamp are rejected within the cache window (§19.3).</summary>
    public string SecurityStamp { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public short FailedLoginCount { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public string ConcurrencyStamp { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public long? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public long? UpdatedBy { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = [];

    public bool IsLockedOut(DateTime utcNow) => LockedUntil.HasValue && LockedUntil.Value > utcNow;

    public void RegisterFailedLogin(int lockoutThreshold, TimeSpan lockoutDuration, DateTime utcNow)
    {
        FailedLoginCount++;
        if (FailedLoginCount >= lockoutThreshold)
        {
            LockedUntil = utcNow.Add(lockoutDuration);
            FailedLoginCount = 0;
        }
    }

    public void RegisterSuccessfulLogin(DateTime utcNow)
    {
        FailedLoginCount = 0;
        LockedUntil = null;
        LastLoginAt = utcNow;
    }
}

public class Role
{
    public long Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public DateTime CreatedAt { get; set; }
    public long? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public long? UpdatedBy { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = [];
    public ICollection<RolePermission> RolePermissions { get; set; } = [];
}

public class Permission
{
    public long Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public short SortOrder { get; set; }
}

public class UserRole
{
    public long UserId { get; set; }
    public long RoleId { get; set; }
    public User User { get; set; } = null!;
    public Role Role { get; set; } = null!;
}

public class RolePermission
{
    public long RoleId { get; set; }
    public long PermissionId { get; set; }
    public Role Role { get; set; } = null!;
    public Permission Permission { get; set; } = null!;
}

public class RefreshToken
{
    public long Id { get; set; }
    public long UserId { get; set; }

    /// <summary>SHA-256 of the token — a DB read must never yield a usable token.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime IssuedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public long? ReplacedById { get; set; }
    public string? Workstation { get; set; }
    public string? Ip { get; set; }

    public User User { get; set; } = null!;

    public bool IsActive(DateTime utcNow) => RevokedAt is null && ExpiresAt > utcNow;
}
