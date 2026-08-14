using BarcodePrinter.Application.Abstractions;
using BarcodePrinter.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace BarcodePrinter.Infrastructure.Persistence;

public sealed class UserStore(AppDbContext db) : IUserStore
{
    public Task<User?> FindByUsernameAsync(string username, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);

    public Task<User?> FindByIdAsync(long id, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public async Task<IReadOnlyList<string>> GetRoleCodesAsync(long userId, CancellationToken ct) =>
        await db.UserRoles.Where(ur => ur.UserId == userId)
            .Select(ur => ur.Role.Code)
            .OrderBy(c => c)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<string>> GetPermissionCodesAsync(long userId, CancellationToken ct) =>
        await db.UserRoles.Where(ur => ur.UserId == userId)
            .SelectMany(ur => ur.Role.RolePermissions)
            .Select(rp => rp.Permission.Code)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

public sealed class RefreshTokenStore(AppDbContext db) : IRefreshTokenStore
{
    public Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct) =>
        db.RefreshTokens.Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

    public async Task AddAsync(RefreshToken token, CancellationToken ct) =>
        await db.RefreshTokens.AddAsync(token, ct);

    public Task RevokeAllForUserAsync(long userId, DateTime utcNow, CancellationToken ct) =>
        db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > utcNow)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, utcNow), ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
