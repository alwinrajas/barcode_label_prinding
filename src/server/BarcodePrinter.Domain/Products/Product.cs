namespace BarcodePrinter.Domain.Products;

/// <summary>
/// Product master row. The `Default*` members are master defaults that the
/// operator may override at print time (A-9); what was actually printed is
/// snapshotted onto the print job (A-10), never read back from here.
/// </summary>
public class Product
{
    public long Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Defaults to Code (A-33); modelled separately so a future
    /// symbology decision (C-6/R-8) cannot force a schema change.</summary>
    public string? BarcodeValue { get; set; }

    public long? UomId { get; set; }
    public string? Size { get; set; }
    public string? Color { get; set; }
    public long? CategoryId { get; set; }

    public string? DefaultBatch { get; set; }
    public DateOnly? DefaultProductionDate { get; set; }
    public DateOnly? DefaultExpiryDate { get; set; }
    public decimal? DefaultQuantity { get; set; }
    public string? DefaultQuantityText { get; set; }   // e.g. '750[D]' (C-12)

    public decimal? CartonQuantity { get; set; }
    public int? CartonsPerPallet { get; set; }

    public long? PrimaryImageId { get; set; }
    public long? DefaultTemplateId { get; set; }

    public bool IsActive { get; set; } = true;
    public string ConcurrencyStamp { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public long? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public long? UpdatedBy { get; set; }

    public Uom? Uom { get; set; }
    public ProductCategory? Category { get; set; }
    public ICollection<ProductImage> Images { get; set; } = [];

    public string EffectiveBarcodeValue => string.IsNullOrWhiteSpace(BarcodeValue) ? Code : BarcodeValue;
}

public class ProductImage
{
    public long Id { get; set; }
    public long ProductId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;   // SHA-256, content-addressed
    public string Mime { get; set; } = string.Empty;
    public int WidthPx { get; set; }
    public int HeightPx { get; set; }
    public int ByteSize { get; set; }
    public string? StorageKey { get; set; }
    public bool IsPrimary { get; set; }
    public DateTime CreatedAt { get; set; }
    public long? CreatedBy { get; set; }
}

public class Uom
{
    public long Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public class ProductCategory
{
    public long Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long? ParentId { get; set; }
    public bool IsActive { get; set; } = true;
}
