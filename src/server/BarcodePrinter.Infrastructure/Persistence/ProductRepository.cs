using BarcodePrinter.Application.Products;
using BarcodePrinter.Domain.Products;
using Microsoft.EntityFrameworkCore;

namespace BarcodePrinter.Infrastructure.Persistence;

public sealed class ProductRepository(AppDbContext db) : IProductRepository
{
    public Task<Product?> FindByIdAsync(long id, CancellationToken ct) =>
        db.Products.Include(p => p.Images).FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<Product?> FindByCodeAsync(string code, CancellationToken ct) =>
        db.Products.FirstOrDefaultAsync(p => p.Code == code, ct);

    public async Task AddAsync(Product product, CancellationToken ct) =>
        await db.Products.AddAsync(product, ct);

    public async Task AddImageAsync(ProductImage image, CancellationToken ct) =>
        await db.ProductImages.AddAsync(image, ct);

    /// <summary>True on success, false when the concurrency token no longer
    /// matched (someone saved between our read and write).</summary>
    public async Task<bool> SaveChangesDetectingConflictAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    public async Task RunInTransactionAsync(Func<Task> action, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await action();
        await tx.CommitAsync(ct);
    }

    public async Task<long> GetOrCreateUomAsync(string code, CancellationToken ct)
    {
        // uq_uoms_code is ai_ci, so the EF lookup and the unique key agree on
        // case-insensitivity. A concurrent create races to the unique key; the
        // loser re-reads and finds the winner's row.
        var existing = await db.Uoms.FirstOrDefaultAsync(u => u.Code == code, ct);
        if (existing is not null)
        {
            return existing.Id;
        }

        var uom = new Uom { Code = code, Name = code, IsActive = true };
        await db.Uoms.AddAsync(uom, ct);
        try
        {
            await db.SaveChangesAsync(ct);
            return uom.Id;
        }
        catch (DbUpdateException)
        {
            db.Entry(uom).State = EntityState.Detached;
            return (await db.Uoms.FirstAsync(u => u.Code == code, ct)).Id;
        }
    }
}
