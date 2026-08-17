using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ogfi.Modules.Foundation.Persistence;

public sealed class FoundationDbContextDesignFactory : IDesignTimeDbContextFactory<FoundationDbContext>
{
    public FoundationDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5432;Database=ogfi;Username=ogfi;Password=ogfi_dev";

        var options = new DbContextOptionsBuilder<FoundationDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new FoundationDbContext(options);
    }
}
