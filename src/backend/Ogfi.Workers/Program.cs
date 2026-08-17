using Microsoft.EntityFrameworkCore;
using Ogfi.Modules.Foundation.Persistence;
using Ogfi.Workers;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Database=ogfi;Username=ogfi;Password=ogfi_dev";

builder.Services.AddDbContext<FoundationDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddHostedService<OutboxDispatcherWorker>();

await builder.Build().RunAsync();
