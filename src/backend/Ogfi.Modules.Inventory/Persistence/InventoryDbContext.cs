using Microsoft.EntityFrameworkCore;

namespace Ogfi.Modules.Inventory.Persistence;

public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public DbSet<InventoryProfile> InventoryProfiles => Set<InventoryProfile>();
    public DbSet<StockLocation> StockLocations => Set<StockLocation>();

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
    }
}
