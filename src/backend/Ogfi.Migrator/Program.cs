using Microsoft.EntityFrameworkCore;
using Ogfi.Modules.Foundation.Persistence;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
    ?? "Host=localhost;Port=5432;Database=ogfi;Username=ogfi;Password=ogfi_dev";

var options = new DbContextOptionsBuilder<FoundationDbContext>()
    .UseNpgsql(connectionString)
    .Options;

await using var db = new FoundationDbContext(options);
await db.Database.MigrateAsync();
Console.WriteLine("OGFI migration complete.");
