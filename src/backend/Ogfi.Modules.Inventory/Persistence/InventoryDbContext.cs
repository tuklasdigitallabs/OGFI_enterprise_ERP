using Microsoft.EntityFrameworkCore;

namespace Ogfi.Modules.Inventory.Persistence;

public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public DbSet<InventoryProfile> InventoryProfiles => Set<InventoryProfile>();
    public DbSet<StockLocation> StockLocations => Set<StockLocation>();
    public DbSet<InventorySourceEffect> SourceEffects => Set<InventorySourceEffect>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<StockPosition> StockPositions => Set<StockPosition>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("inventory");

        modelBuilder.Entity<InventoryProfile>(entity =>
        {
            entity.ToTable("inventory_profiles");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.HasIndex(x => new { x.TenantId, x.CatalogItemId }).IsUnique();
        });

        modelBuilder.Entity<StockLocation>(entity =>
        {
            entity.ToTable("stock_locations");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Code).HasMaxLength(50);
            entity.Property(x => x.Name).HasMaxLength(160);
            entity.Property(x => x.LocationType).HasMaxLength(30);
            entity.HasIndex(x => new { x.TenantId, x.OutletId, x.Code }).IsUnique();
        });

        modelBuilder.Entity<InventorySourceEffect>(entity =>
        {
            entity.ToTable("inventory_source_effects");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SourceType).HasMaxLength(160);
            entity.Property(x => x.CorrelationId).HasMaxLength(64);
            entity.HasIndex(x => new { x.TenantId, x.SourceEventId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.SourceType, x.SourceDocumentId });
        });

        modelBuilder.Entity<InventoryMovement>(entity =>
        {
            entity.ToTable("inventory_movements", table =>
            {
                table.HasCheckConstraint("CK_inventory_movement_quantity", "\"QuantityBaseUom\" <> 0");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MovementType).HasMaxLength(40);
            entity.Property(x => x.CatalogItemCodeSnapshot).HasMaxLength(60);
            entity.Property(x => x.CatalogItemNameSnapshot).HasMaxLength(200);
            entity.Property(x => x.StockLocationCodeSnapshot).HasMaxLength(50);
            entity.Property(x => x.BaseUomCodeSnapshot).HasMaxLength(30);
            entity.Property(x => x.QuantityBaseUom).HasPrecision(19, 6);
            entity.Property(x => x.CorrelationId).HasMaxLength(64);
            entity.HasIndex(x => new { x.TenantId, x.SourceEventId, x.SourceLineId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CatalogItemId, x.StockLocationId, x.OccurredAtUtc });
        });

        modelBuilder.Entity<StockPosition>(entity =>
        {
            entity.ToTable("stock_positions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CatalogItemCodeSnapshot).HasMaxLength(60);
            entity.Property(x => x.CatalogItemNameSnapshot).HasMaxLength(200);
            entity.Property(x => x.StockLocationCodeSnapshot).HasMaxLength(50);
            entity.Property(x => x.BaseUomCodeSnapshot).HasMaxLength(30);
            entity.Property(x => x.QuantityOnHand).HasPrecision(19, 6);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.CatalogItemId, x.StockLocationId, x.BaseUomId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.OutletId, x.CatalogItemId });
        });
    }
}
