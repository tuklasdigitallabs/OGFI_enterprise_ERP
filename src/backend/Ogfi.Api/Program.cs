using System.Diagnostics;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Ogfi.Api.Endpoints;
using Ogfi.Api.Security;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.BuildingBlocks.Observability;
using Ogfi.Modules.Catalog;
using Ogfi.Modules.Catalog.Persistence;
using Ogfi.Modules.Foundation.Persistence;
using Ogfi.Modules.Foundation.Security;
using Ogfi.Modules.Inventory;
using Ogfi.Modules.Inventory.Persistence;
using Ogfi.Modules.Procurement;
using Ogfi.Modules.Procurement.Persistence;
using Ogfi.Modules.Workflow;
using Ogfi.Modules.Workflow.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "__Host-ogfi-session"; options.Cookie.HttpOnly = true; options.Cookie.SecurePolicy = CookieSecurePolicy.Always; options.Cookie.SameSite = SameSiteMode.Lax;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddScoped<ITenantExecutionContextAccessor, TenantExecutionContextAccessor>();
builder.Services.AddScoped<TenantSessionConnectionInterceptor>();
builder.Services.AddScoped<MembershipResolver>();
builder.Services.AddScoped<FoundationAuthorizationEvaluator>();
builder.Services.AddScoped<BusinessTimeResolver>();
builder.Services.AddScoped<FoundationOrganizationReferenceService>();
builder.Services.AddScoped<FoundationApproverResolver>();
builder.Services.AddScoped<CatalogReferenceService>();
builder.Services.AddScoped<StandardUomConversionService>();
builder.Services.AddScoped<PurchaseOrderService>();
builder.Services.AddScoped<GoodsReceiptService>();
builder.Services.AddScoped<GoodsReceiptPostedConsumer>();
builder.Services.AddScoped<StockPositionRebuildService>();
builder.Services.AddScoped<WorkflowApprovalService>();
builder.Services.AddSingleton(TimeProvider.System);
var connectionString = builder.Configuration.GetConnectionString("Postgres") ?? "Host=localhost;Port=5432;Database=ogfi;Username=ogfi;Password=ogfi_dev";
builder.Services.AddDbContext<FoundationDbContext>((sp, options) => options.UseNpgsql(connectionString).AddInterceptors(sp.GetRequiredService<TenantSessionConnectionInterceptor>()));
builder.Services.AddDbContext<CatalogDbContext>((sp, options) => options.UseNpgsql(connectionString).AddInterceptors(sp.GetRequiredService<TenantSessionConnectionInterceptor>()));
builder.Services.AddDbContext<InventoryDbContext>((sp, options) => options.UseNpgsql(connectionString).AddInterceptors(sp.GetRequiredService<TenantSessionConnectionInterceptor>()));
builder.Services.AddDbContext<ProcurementDbContext>((sp, options) => options.UseNpgsql(connectionString).AddInterceptors(sp.GetRequiredService<TenantSessionConnectionInterceptor>()));
builder.Services.AddDbContext<WorkflowDbContext>((sp, options) => options.UseNpgsql(connectionString).AddInterceptors(sp.GetRequiredService<TenantSessionConnectionInterceptor>()));
builder.Services.AddHealthChecks().AddDbContextCheck<FoundationDbContext>("foundation-db").AddDbContextCheck<CatalogDbContext>("catalog-db").AddDbContextCheck<InventoryDbContext>("inventory-db").AddDbContextCheck<ProcurementDbContext>("procurement-db").AddDbContextCheck<WorkflowDbContext>("workflow-db");
var app = builder.Build();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    const string header = "X-Correlation-Id";
    var correlationId = context.Request.Headers.TryGetValue(header, out var supplied) && !string.IsNullOrWhiteSpace(supplied) ? supplied.ToString() : Guid.NewGuid().ToString("N");
    context.Items["CorrelationId"] = correlationId; context.Response.Headers[header] = correlationId; var stopwatch = Stopwatch.StartNew();
    using (app.Logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = correlationId }))
    {
        try { await next(); }
        finally { stopwatch.Stop(); var tags = new TagList { { "http.method", context.Request.Method }, { "http.status_code", context.Response.StatusCode } }; OgfiMetrics.ApiRequests.Add(1, tags); OgfiMetrics.ApiDurationMs.Record(stopwatch.Elapsed.TotalMilliseconds, tags); }
    }
});
app.UseAuthentication(); app.UseMiddleware<TenantExecutionContextMiddleware>(); app.UseAuthorization();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapHealthChecks("/health/live"); app.MapHealthChecks("/health/ready");
app.MapGet("/api/system/info", () => Results.Ok(new { referenceImplementation = "RI-01", acceptedBaseline = "RI01-BL04", candidateBaseline = "RI01-BL05-CANDIDATE", architectureBaseline = "OGFI Master Approved Baseline v4.6 / G9.6 Implementation Architecture", activeBatch = "E", status = "IN_IMPLEMENTATION", metricsMeter = OgfiMetrics.MeterName }));
app.MapFoundationContextEndpoints(); app.MapCatalogEndpoints(); app.MapInventorySetupEndpoints(); app.MapInventoryOperationsEndpoints(); app.MapProcurementEndpoints(); app.MapGoodsReceiptEndpoints(); app.MapWorkflowEndpoints();
app.Run();
public partial class Program;
