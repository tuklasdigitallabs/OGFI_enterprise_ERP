using Microsoft.EntityFrameworkCore;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.Modules.Foundation.Persistence;
using Ogfi.Modules.Foundation.Security;
using Ogfi.Modules.Procurement;
using Ogfi.Modules.Procurement.Persistence;
using Ogfi.Modules.Workflow;
using Ogfi.Modules.Workflow.Persistence;
using Ogfi.Workers;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Database=ogfi;Username=ogfi;Password=ogfi_dev";

builder.Services.AddScoped<ITenantExecutionContextAccessor, TenantExecutionContextAccessor>();
builder.Services.AddScoped<TenantSessionConnectionInterceptor>();
builder.Services.AddScoped<FoundationApproverResolver>();
builder.Services.AddScoped<WorkflowApprovalService>();
builder.Services.AddScoped<PurchaseOrderApprovalOutcomeService>();
builder.Services.AddScoped<ApprovalSpineProcessor>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddDbContext<FoundationDbContext>((serviceProvider, options) =>
    options.UseNpgsql(connectionString).AddInterceptors(serviceProvider.GetRequiredService<TenantSessionConnectionInterceptor>()));
builder.Services.AddDbContext<ProcurementDbContext>((serviceProvider, options) =>
    options.UseNpgsql(connectionString).AddInterceptors(serviceProvider.GetRequiredService<TenantSessionConnectionInterceptor>()));
builder.Services.AddDbContext<WorkflowDbContext>((serviceProvider, options) =>
    options.UseNpgsql(connectionString).AddInterceptors(serviceProvider.GetRequiredService<TenantSessionConnectionInterceptor>()));

builder.Services.AddHostedService<OutboxDispatcherWorker>();
builder.Services.AddHostedService<ApprovalSpineWorker>();

await builder.Build().RunAsync();
