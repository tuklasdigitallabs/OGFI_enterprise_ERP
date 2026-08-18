using Microsoft.EntityFrameworkCore;
using Ogfi.Modules.Catalog.Persistence;
using Ogfi.Modules.Finance.Persistence;
using Ogfi.Modules.Audit.Persistence;
using Ogfi.Modules.Foundation.Persistence;
using Ogfi.Modules.Inventory.Persistence;
using Ogfi.Modules.Procurement.Persistence;
using Ogfi.Modules.Workflow.Persistence;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
    ?? "Host=localhost;Port=5432;Database=ogfi;Username=ogfi;Password=ogfi_dev";

await MigrateAsync(new FoundationDbContext(new DbContextOptionsBuilder<FoundationDbContext>().UseNpgsql(connectionString).Options));
await MigrateAsync(new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(connectionString).Options));
await MigrateAsync(new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(connectionString).Options));
await MigrateAsync(new ProcurementDbContext(new DbContextOptionsBuilder<ProcurementDbContext>().UseNpgsql(connectionString).Options));
await MigrateAsync(new WorkflowDbContext(new DbContextOptionsBuilder<WorkflowDbContext>().UseNpgsql(connectionString).Options));
await MigrateAsync(new FinanceDbContext(new DbContextOptionsBuilder<FinanceDbContext>().UseNpgsql(connectionString).Options));
await MigrateAsync(new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>().UseNpgsql(connectionString).Options));
Console.WriteLine("OGFI migrations complete for Foundation, Catalog, Inventory, Procurement, Workflow, Finance and Audit.");

static async Task MigrateAsync(DbContext dbContext)
{
    await using (dbContext)
    {
        await dbContext.Database.MigrateAsync();
    }
}
