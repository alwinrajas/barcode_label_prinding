using System.Text.Json;
using BarcodePrinter.Application.Abstractions;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Products;
using BarcodePrinter.Domain;
using BarcodePrinter.Domain.Products;

namespace BarcodePrinter.Application.Products;

/// <summary>Write-side product use cases. Reads for the grid/search go
/// through the Dapper ProductsQuery, not through here (§5.3).</summary>
public interface IProductRepository
{
    Task<Product?> FindByIdAsync(long id, CancellationToken ct);
    Task<Product?> FindByCodeAsync(string code, CancellationToken ct);
    Task AddAsync(Product product, CancellationToken ct);
    Task AddImageAsync(ProductImage image, CancellationToken ct);
    Task<bool> SaveChangesDetectingConflictAsync(CancellationToken ct);

    /// <summary>Runs <paramref name="action"/> inside one database transaction
    /// so multi-save use cases (image row + primary pointer) commit or roll
    /// back together instead of half-applying on a crash.</summary>
    Task RunInTransactionAsync(Func<Task> action, CancellationToken ct);

    /// <summary>Case-insensitive find-or-create of a UOM by code (§10 free-text
    /// UOM entry) — the FK survives, so import validation keeps working.</summary>
    Task<long> GetOrCreateUomAsync(string code, CancellationToken ct);
}

public sealed class ProductService(
    IProductRepository products,
    IProductImageStore images,
    IAuditWriter audit,
    TimeProvider clock)
{
    public async Task<long> CreateAsync(SaveProductRequest request, ActorInfo actor, CancellationToken ct)
    {
        Validate(request);
        request = await WithResolvedUomAsync(request, ct);

        if (await products.FindByCodeAsync(request.Code.Trim(), ct) is not null)
        {
            throw new DomainException(ErrorCodes.ProductCodeDuplicate,
                "A product with this code already exists.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var product = new Product
        {
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            CreatedAt = now,
            CreatedBy = actor.UserId,
        };
        Apply(product, request);

        await products.AddAsync(product, ct);
        await products.SaveChangesDetectingConflictAsync(ct);

        await audit.WriteAsync(new AuditEntry("ProductCreated",
            UserId: actor.UserId, UsernameSnapshot: actor.Username,
            EntityType: "Product", EntityId: product.Code,
            AfterJson: Snapshot(product), Workstation: actor.Workstation,
            CorrelationId: actor.CorrelationId), ct);

        return product.Id;
    }

    public async Task UpdateAsync(long id, SaveProductRequest request, ActorInfo actor, CancellationToken ct)
    {
        Validate(request);
        request = await WithResolvedUomAsync(request, ct);

        var product = await products.FindByIdAsync(id, ct)
            ?? throw new NotFoundException("Product", id);

        // Optimistic concurrency (§11.1): the client must present the stamp it
        // loaded; a mismatch means someone saved first.
        if (string.IsNullOrEmpty(request.ConcurrencyStamp) ||
            !string.Equals(product.ConcurrencyStamp, request.ConcurrencyStamp, StringComparison.Ordinal))
        {
            throw new ConcurrencyException("product");
        }

        if (!string.Equals(product.Code, request.Code.Trim(), StringComparison.Ordinal) &&
            await products.FindByCodeAsync(request.Code.Trim(), ct) is not null)
        {
            throw new DomainException(ErrorCodes.ProductCodeDuplicate,
                "A product with this code already exists.");
        }

        var before = Snapshot(product);
        Apply(product, request);
        product.ConcurrencyStamp = Guid.NewGuid().ToString();
        product.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        product.UpdatedBy = actor.UserId;

        if (!await products.SaveChangesDetectingConflictAsync(ct))
        {
            throw new ConcurrencyException("product");   // raced between read and save
        }

        await audit.WriteAsync(new AuditEntry("ProductUpdated",
            UserId: actor.UserId, UsernameSnapshot: actor.Username,
            EntityType: "Product", EntityId: product.Code,
            BeforeJson: before, AfterJson: Snapshot(product),
            Workstation: actor.Workstation, CorrelationId: actor.CorrelationId), ct);
    }

    /// <summary>Deactivate, not delete: print history references products
    /// forever (A-10), so master rows are never physically removed.</summary>
    public async Task SetActiveAsync(long id, bool active, ActorInfo actor, CancellationToken ct)
    {
        var product = await products.FindByIdAsync(id, ct)
            ?? throw new NotFoundException("Product", id);
        if (product.IsActive == active)
        {
            return;
        }

        product.IsActive = active;
        product.ConcurrencyStamp = Guid.NewGuid().ToString();
        product.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        product.UpdatedBy = actor.UserId;
        await products.SaveChangesDetectingConflictAsync(ct);

        await audit.WriteAsync(new AuditEntry(active ? "ProductActivated" : "ProductDeactivated",
            UserId: actor.UserId, UsernameSnapshot: actor.Username,
            EntityType: "Product", EntityId: product.Code,
            Workstation: actor.Workstation, CorrelationId: actor.CorrelationId), ct);
    }

    public async Task<string> SetImageAsync(long id, Stream content, string fileName,
        ActorInfo actor, CancellationToken ct)
    {
        var product = await products.FindByIdAsync(id, ct)
            ?? throw new NotFoundException("Product", id);

        var stored = await images.SaveAsync(content, ct);

        var image = new ProductImage
        {
            ProductId = product.Id,
            FileName = Path.GetFileName(fileName),
            ContentHash = stored.ContentHash,
            Mime = stored.Mime,
            WidthPx = stored.WidthPx,
            HeightPx = stored.HeightPx,
            ByteSize = stored.ByteSize,
            StorageKey = stored.StorageKey,
            IsPrimary = true,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
            CreatedBy = actor.UserId,
        };

        // Same content re-uploaded → reuse the existing row (uq_img_hash).
        // Both saves run in one transaction: a crash between materialising the
        // image row and pointing primary_image_id at it must not orphan the row.
        var existing = product.Images.FirstOrDefault(i => i.ContentHash == stored.ContentHash);
        await products.RunInTransactionAsync(async () =>
        {
            if (existing is null)
            {
                await products.AddImageAsync(image, ct);
                await products.SaveChangesDetectingConflictAsync(ct);   // materialise image.Id
                product.PrimaryImageId = image.Id;
            }
            else
            {
                product.PrimaryImageId = existing.Id;
            }
            foreach (var other in product.Images.Where(i => i.Id != product.PrimaryImageId))
            {
                other.IsPrimary = false;
            }

            product.UpdatedAt = clock.GetUtcNow().UtcDateTime;
            product.UpdatedBy = actor.UserId;
            await products.SaveChangesDetectingConflictAsync(ct);
        }, ct);

        await audit.WriteAsync(new AuditEntry("ProductImageChanged",
            UserId: actor.UserId, UsernameSnapshot: actor.Username,
            EntityType: "Product", EntityId: product.Code,
            AfterJson: JsonSerializer.Serialize(new { stored.ContentHash, fileName }),
            Workstation: actor.Workstation, CorrelationId: actor.CorrelationId), ct);

        return stored.ContentHash;
    }

    private static void Validate(SaveProductRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Code) || r.Code.Trim().Length > 64)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Product code is required (max 64 characters).");
        }
        if (string.IsNullOrWhiteSpace(r.Description) || r.Description.Trim().Length > 255)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Description is required (max 255 characters).");
        }
        // No production/expiry ordering rule here any more: those dates belong
        // to a print run, and PrintJobService still enforces the same rule on
        // the values the operator actually enters.
        if (r.DefaultQuantity is < 0 || r.CartonQuantity is < 0 || r.CartonsPerPallet is < 0)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Quantities cannot be negative.");
        }
    }

    /// <summary>
    /// Copies the editable master fields onto the entity. BarcodeValue,
    /// CategoryId, DefaultProductionDate and DefaultExpiryDate are pointedly
    /// NOT assigned: they left the contract, and writing null over them here
    /// would erase values that existing installations already hold. The label
    /// still falls back to the code for the barcode, and the print screen
    /// supplies its own dates.
    /// </summary>
    private static void Apply(Product p, SaveProductRequest r)
    {
        p.Code = r.Code.Trim();
        p.Description = r.Description.Trim();
        p.UomId = r.UomId;
        p.Size = Trimmed(r.Size);
        p.Color = Trimmed(r.Color);
        p.DefaultBatch = Trimmed(r.DefaultBatch);
        p.DefaultQuantity = r.DefaultQuantity;
        p.DefaultQuantityText = Trimmed(r.DefaultQuantityText);
        p.CartonQuantity = r.CartonQuantity;
        p.CartonsPerPallet = r.CartonsPerPallet;
    }

    /// <summary>Free-text UOM (§10): a typed code that matches nothing becomes
    /// a new UOM row. An explicit UomId always wins.</summary>
    private async Task<SaveProductRequest> WithResolvedUomAsync(
        SaveProductRequest r, CancellationToken ct)
    {
        if (r.UomId is not null || string.IsNullOrWhiteSpace(r.UomCode))
        {
            return r;
        }
        var code = r.UomCode.Trim().ToUpperInvariant();
        if (code.Length > 16)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "UOM must be 16 characters or fewer.");
        }
        return r with { UomId = await products.GetOrCreateUomAsync(code, ct) };
    }

    private static string? Trimmed(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Audit snapshot: label-relevant fields only, never the stamps.</summary>
    private static string Snapshot(Product p) => JsonSerializer.Serialize(new
    {
        p.Code, p.Description, p.BarcodeValue, p.UomId, p.Size, p.Color, p.CategoryId,
        p.DefaultBatch, p.DefaultProductionDate, p.DefaultExpiryDate,
        p.DefaultQuantity, p.DefaultQuantityText, p.CartonQuantity, p.CartonsPerPallet,
        p.IsActive,
    });
}

/// <summary>Who is performing the action — flowed from the JWT by the endpoint.</summary>
public sealed record ActorContext(long UserId, string Username, string? Workstation, string? CorrelationId);
