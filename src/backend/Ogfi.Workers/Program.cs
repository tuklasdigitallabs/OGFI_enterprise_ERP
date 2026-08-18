using Microsoft.EntityFrameworkCore;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.BuildingBlocks.Messaging.Contracts;
using Ogfi.Modules.Finance;
using Ogfi.Modules.Finance.Persistence;
using Ogfi.Modules.Audit;
using Ogfi.Modules.Audit.Persistence;
using Ogfi.Modules.DurableOperations;
using Ogfi.Modules.DurableOperations.Persistence;
using Ogfi.Modules.Foundation.Persistence;
using Ogfi.Modules.Foundation.Security;
using Ogfi.Modules.Inventory;
using Ogfi.Modules.Inventory.Persistence;
using Ogfi.Modules.Procurement;
using Ogfi.Modules.Procurement.Persistence;
using Ogfi.Modules.Workflow;
using Ogfi.Modules.Workflow.Persistence;
using Ogfi.Workers;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Postgres") ?? "Host=localhost;Port=5432;Database=ogfi;Username=ogfi;Password=ogfi_dev";
builder.Services.AddScoped<ITenantExecutionContextAccessor, TenantExecutionContextAccessor>();
builder.Services.AddScoped<TenantSessionConnectionInterceptor>();
builder.Services.AddScoped<FoundationApproverResolver>();
builder.Services.AddScoped<WorkflowApprovalService>();
builder.Services.AddScoped<PurchaseOrderApprovalOutcomeService>();
builder.Services.AddScoped<ApprovalSpineProcessor>();
builder.Services.AddScoped<GoodsReceiptPostedConsumer>();
builder.Services.AddScoped<StockConsequenceProcessor>();
builder.Services.AddScoped<FinancePostingService>();
builder.Services.AddScoped<FinancialConsequenceProcessor>();
builder.Services.AddScoped<AuditMaterialActionProcessor>();
builder.Services.AddScoped<AuditIngestionService>();
builder.Services.AddScoped<AuditQueryService>();
builder.Services.AddScoped<Rs01TraceProjectionService>();
builder.Services.AddScoped<DurableOperationService>();
builder.Services.AddScoped<ReplayCoordinator>();
builder.Services.AddScoped<OperationalReplayService>();
builder.Services.AddScoped<OperationalAuditEvidenceService>();
builder.Services.AddScoped<OutboxDeliveryStore>();
builder.Services.AddScoped<OperationalHealthService>();
builder.Services.AddSingleton(new OperationalHealthOptions
{
    StaleHeartbeatAge = builder.Configuration.GetValue("Operations:Health:StaleHeartbeatSeconds", 120) is var stale
        ? TimeSpan.FromSeconds(stale) : TimeSpan.FromMinutes(2),
    DegradedPendingAge = TimeSpan.FromSeconds(builder.Configuration.GetValue("Operations:Health:DegradedPendingSeconds", 300)),
    UnhealthyPendingAge = TimeSpan.FromSeconds(builder.Configuration.GetValue("Operations:Health:UnhealthyPendingSeconds", 900)),
    DegradedRetryPendingCount = builder.Configuration.GetValue("Operations:Health:DegradedRetryCount", 1),
    UnhealthyRetryPendingCount = builder.Configuration.GetValue("Operations:Health:UnhealthyRetryCount", 10),
    UnhealthyTerminalFailureCount = builder.Configuration.GetValue("Operations:Health:UnhealthyTerminalCount", 1),
    DegradedDeliveryLag = TimeSpan.FromSeconds(builder.Configuration.GetValue("Operations:Health:DegradedDeliveryLagSeconds", 300)),
    UnhealthyDeliveryLag = TimeSpan.FromSeconds(builder.Configuration.GetValue("Operations:Health:UnhealthyDeliveryLagSeconds", 900))
});
builder.Services.AddSingleton(new ReplayWorkerOptions
{
    LeaseRenewalInterval = TimeSpan.FromSeconds(builder.Configuration.GetValue("Operations:Replay:LeaseRenewalSeconds", 60)),
    LeaseDuration = TimeSpan.FromSeconds(builder.Configuration.GetValue("Operations:Replay:LeaseDurationSeconds", 300)),
    BatchSize = builder.Configuration.GetValue("Operations:Replay:BatchSize", 25)
});
builder.Services.AddSingleton<WorkerHeartbeatReporter>();
builder.Services.AddSingleton<TenantWorkerRunner>();
builder.Services.AddSingleton<ProcessorFailureRecorder>();
builder.Services.AddSingleton<IReplayLeaseRenewalObserver, NoopReplayLeaseRenewalObserver>();
builder.Services.AddSingleton<IReplayLeaseRenewalRunner, ReplayLeaseRenewalRunner>();
builder.Services.AddScoped<IReplayOwnerHandler, ApprovalRequestReplayHandler>();
builder.Services.AddScoped<IReplayOwnerHandler, ApprovalOutcomeReplayHandler>();
builder.Services.AddScoped<IReplayOwnerHandler, InventoryReplayHandler>();
builder.Services.AddScoped<IReplayOwnerHandler, FinanceReplayHandler>();
builder.Services.AddScoped<IReplayOwnerHandler, ProcurementAuditReplayHandler>();
builder.Services.AddScoped<IReplayOwnerHandler, WorkflowAuditReplayHandler>();
builder.Services.AddSingleton<IStockConsequenceAttemptHook, NoopStockConsequenceAttemptHook>();
builder.Services.AddSingleton<IFinancialConsequenceAttemptHook, NoopFinancialConsequenceAttemptHook>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<FoundationDbContext>((sp, options) => options.UseNpgsql(connectionString).AddInterceptors(sp.GetRequiredService<TenantSessionConnectionInterceptor>()));
builder.Services.AddDbContext<InventoryDbContext>((sp, options) => options.UseNpgsql(connectionString).AddInterceptors(sp.GetRequiredService<TenantSessionConnectionInterceptor>()));
builder.Services.AddDbContext<ProcurementDbContext>((sp, options) => options.UseNpgsql(connectionString).AddInterceptors(sp.GetRequiredService<TenantSessionConnectionInterceptor>()));
builder.Services.AddDbContext<WorkflowDbContext>((sp, options) => options.UseNpgsql(connectionString).AddInterceptors(sp.GetRequiredService<TenantSessionConnectionInterceptor>()));
builder.Services.AddDbContext<FinanceDbContext>((sp, options) => options.UseNpgsql(connectionString).AddInterceptors(sp.GetRequiredService<TenantSessionConnectionInterceptor>()));
builder.Services.AddDbContext<AuditDbContext>((sp, options) => options.UseNpgsql(connectionString).AddInterceptors(sp.GetRequiredService<TenantSessionConnectionInterceptor>()));
builder.Services.AddDbContext<DurableOperationsDbContext>((sp, options) => options.UseNpgsql(connectionString).AddInterceptors(sp.GetRequiredService<TenantSessionConnectionInterceptor>()));
builder.Services.AddHostedService<OutboxDispatcherWorker>();
builder.Services.AddHostedService<ApprovalSpineWorker>();
builder.Services.AddHostedService<StockConsequenceWorker>();
builder.Services.AddHostedService<FinancialConsequenceWorker>();
builder.Services.AddHostedService<AuditMaterialActionWorker>();
builder.Services.AddHostedService<ReplayOperationWorker>();
await builder.Build().RunAsync();
