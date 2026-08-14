using BarcodePrinter.Domain.Identity;
using BarcodePrinter.Domain.Products;
using Microsoft.EntityFrameworkCore;

namespace BarcodePrinter.Infrastructure.Persistence;

/// <summary>
/// EF Core mapping onto the DbUp-owned schema (blueprint B-8: the ORM maps to
/// the schema, it never defines it — migrations are disabled by construction:
/// this assembly contains none). Write-side only; reads for grids/reports use
/// Dapper (§5.3).
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductImage> ProductImages => Set<ProductImage>();
    public DbSet<Uom> Uoms => Set<Uom>();
    public DbSet<ProductCategory> ProductCategories => Set<ProductCategory>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Username).HasColumnName("username");
            e.Property(x => x.FullName).HasColumnName("full_name");
            e.Property(x => x.Email).HasColumnName("email");
            e.Property(x => x.PasswordHash).HasColumnName("password_hash");
            e.Property(x => x.SecurityStamp).HasColumnName("security_stamp");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.MustChangePassword).HasColumnName("must_change_password");
            e.Property(x => x.FailedLoginCount).HasColumnName("failed_login_count");
            e.Property(x => x.LockedUntil).HasColumnName("locked_until");
            e.Property(x => x.LastLoginAt).HasColumnName("last_login_at");
            e.Property(x => x.ConcurrencyStamp).HasColumnName("concurrency_stamp")
                .IsConcurrencyToken();
            MapAudit(e);
        });

        b.Entity<Role>(e =>
        {
            e.ToTable("roles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Code).HasColumnName("code");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.IsSystem).HasColumnName("is_system");
            MapAudit(e);
        });

        b.Entity<Permission>(e =>
        {
            e.ToTable("permissions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Code).HasColumnName("code");
            e.Property(x => x.Module).HasColumnName("module");
            e.Property(x => x.Action).HasColumnName("action");
            e.Property(x => x.DisplayName).HasColumnName("display_name");
            e.Property(x => x.SortOrder).HasColumnName("sort_order");
        });

        b.Entity<UserRole>(e =>
        {
            e.ToTable("user_roles");
            e.HasKey(x => new { x.UserId, x.RoleId });
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.RoleId).HasColumnName("role_id");
            e.HasOne(x => x.User).WithMany(u => u.UserRoles).HasForeignKey(x => x.UserId);
            e.HasOne(x => x.Role).WithMany(r => r.UserRoles).HasForeignKey(x => x.RoleId);
        });

        b.Entity<RolePermission>(e =>
        {
            e.ToTable("role_permissions");
            e.HasKey(x => new { x.RoleId, x.PermissionId });
            e.Property(x => x.RoleId).HasColumnName("role_id");
            e.Property(x => x.PermissionId).HasColumnName("permission_id");
            e.HasOne(x => x.Role).WithMany(r => r.RolePermissions).HasForeignKey(x => x.RoleId);
            e.HasOne(x => x.Permission).WithMany().HasForeignKey(x => x.PermissionId);
        });

        MapProducts(b);

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.TokenHash).HasColumnName("token_hash");
            e.Property(x => x.IssuedAt).HasColumnName("issued_at");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            e.Property(x => x.ReplacedById).HasColumnName("replaced_by_id");
            e.Property(x => x.Workstation).HasColumnName("workstation");
            e.Property(x => x.Ip).HasColumnName("ip");
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        });
    }

    private void MapProducts(ModelBuilder b)
    {
        b.Entity<Product>(e =>
        {
            e.ToTable("products");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Code).HasColumnName("code");
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.BarcodeValue).HasColumnName("barcode_value");
            e.Property(x => x.UomId).HasColumnName("uom_id");
            e.Property(x => x.Size).HasColumnName("size");
            e.Property(x => x.Color).HasColumnName("color");
            e.Property(x => x.CategoryId).HasColumnName("category_id");
            e.Property(x => x.DefaultBatch).HasColumnName("default_batch");
            e.Property(x => x.DefaultProductionDate).HasColumnName("default_production_date");
            e.Property(x => x.DefaultExpiryDate).HasColumnName("default_expiry_date");
            e.Property(x => x.DefaultQuantity).HasColumnName("default_quantity");
            e.Property(x => x.DefaultQuantityText).HasColumnName("default_quantity_text");
            e.Property(x => x.CartonQuantity).HasColumnName("carton_quantity");
            e.Property(x => x.CartonsPerPallet).HasColumnName("cartons_per_pallet");
            e.Property(x => x.PrimaryImageId).HasColumnName("primary_image_id");
            e.Property(x => x.DefaultTemplateId).HasColumnName("default_template_id");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.ConcurrencyStamp).HasColumnName("concurrency_stamp")
                .IsConcurrencyToken();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by");
            // search_text is a DB-generated column — never written by EF.
            e.HasOne(x => x.Uom).WithMany().HasForeignKey(x => x.UomId);
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId);
            e.HasMany(x => x.Images).WithOne().HasForeignKey(i => i.ProductId);
        });

        b.Entity<ProductImage>(e =>
        {
            e.ToTable("product_images");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ProductId).HasColumnName("product_id");
            e.Property(x => x.FileName).HasColumnName("file_name");
            e.Property(x => x.ContentHash).HasColumnName("content_hash");
            e.Property(x => x.Mime).HasColumnName("mime");
            e.Property(x => x.WidthPx).HasColumnName("width_px");
            e.Property(x => x.HeightPx).HasColumnName("height_px");
            e.Property(x => x.ByteSize).HasColumnName("byte_size");
            e.Property(x => x.StorageKey).HasColumnName("storage_key");
            e.Property(x => x.IsPrimary).HasColumnName("is_primary");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
        });

        b.Entity<Uom>(e =>
        {
            e.ToTable("uoms");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Code).HasColumnName("code");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.IsActive).HasColumnName("is_active");
        });

        b.Entity<ProductCategory>(e =>
        {
            e.ToTable("product_categories");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Code).HasColumnName("code");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.ParentId).HasColumnName("parent_id");
            e.Property(x => x.IsActive).HasColumnName("is_active");
        });
    }

    private static void MapAudit<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> e)
        where T : class
    {
        e.Property<DateTime>("CreatedAt").HasColumnName("created_at");
        e.Property<long?>("CreatedBy").HasColumnName("created_by");
        e.Property<DateTime?>("UpdatedAt").HasColumnName("updated_at");
        e.Property<long?>("UpdatedBy").HasColumnName("updated_by");
    }
}
