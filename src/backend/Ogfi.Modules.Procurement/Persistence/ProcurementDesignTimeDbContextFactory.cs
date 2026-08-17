using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ogfi.Modules.Procurement.Persistence;

public sealed class ProcurementDesignTimeDbContextFactory : IDesignTimeDbContextFactory<ProcurementDbContext>
{
    public ProcurementDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5432;Database=ogfi;Username=ogfi;Password=ogfi_dev";
        var options = new DbContextOptionsBuilder<ProcurementDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new ProcurementDbContext(options);
    }
}
