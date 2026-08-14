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
}
