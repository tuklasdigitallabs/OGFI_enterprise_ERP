using Microsoft.EntityFrameworkCore;

namespace Ogfi.Modules.Catalog.Persistence;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Uom> Uoms => Set<Uom>();
    public DbSet<CatalogItem> CatalogItems => Set<CatalogItem>();
    public DbSet<ItemPackagingConversion> PackagingConversions => Set<ItemPackagingConversion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("catalog");

        modelBuilder.Entity<Uom>(entity =>
        {
            entity.ToTable("uoms", table =>
            {
                table.HasCheckConstraint("CK_uoms_numerator", "\"StandardNumerator\" > 0");
                table.HasCheckConstraint("CK_uoms_denominator", "\"StandardDenominator\" > 0");
                table.HasCheckConstraint("CK_uoms_precision", "\"PrecisionScale\" >= 0 AND \"PrecisionScale\" <= 9");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(30);
            entity.Property(x => x.Name).HasMaxLength(100);
            entity.Property(x => x.DimensionCode).HasMaxLength(40);
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.HasIndex(x => x.Code).IsUnique();
            entity.HasData(
                new Uom { Id = UomIds.Each, Code = "EA", Name = "Each", DimensionCode = "COUNT", StandardNumerator = 1, StandardDenominator = 1, PrecisionScale = 3, Status = CatalogStatuses.Active },
                new Uom { Id = UomIds.Kilogram, Code = "KG", Name = "Kilogram", DimensionCode = "MASS", StandardNumerator = 1, StandardDenominator = 1, PrecisionScale = 3, Status = CatalogStatuses.Active },
                new Uom { Id = UomIds.Gram, Code = "G", Name = "Gram", DimensionCode = "MASS", StandardNumerator = 1, StandardDenominator = 1000, PrecisionScale = 3, Status = CatalogStatuses.Active },
                new Uom { Id = UomIds.Case, Code = "CASE", Name = "Case", DimensionCode = "PACKAGE", StandardNumerator = 1, StandardDenominator = 1, PrecisionScale = 3, Status = CatalogStatuses.Active });
        });

        modelBuilder.Entity<CatalogItem>(entity =>
        {
            entity.ToTable("items");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Code).HasMaxLength(60);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            entity.HasOne<Uom>().WithMany().HasForeignKey(x => x.BaseUomId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ItemPackagingConversion>(entity =>
        {
            entity.ToTable("item_packaging_conversions", table =>
            {
                table.HasCheckConstraint("CK_item_pack_conversion_numerator", "\"Numerator\" > 0");
                table.HasCheckConstraint("CK_item_pack_conversion_denominator", "\"Denominator\" > 0");
                table.HasCheckConstraint("CK_item_pack_conversion_dates", "\"EffectiveToBusinessDate\" IS NULL OR \"EffectiveToBusinessDate\" >= \"EffectiveFromBusinessDate\"");
            });
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.CatalogItemId, x.PurchaseUomId, x.EffectiveFromBusinessDate }).IsUnique();
            entity.HasOne<CatalogItem>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CatalogItemId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Uom>().WithMany().HasForeignKey(x => x.PurchaseUomId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Uom>().WithMany().HasForeignKey(x => x.BaseUomId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
