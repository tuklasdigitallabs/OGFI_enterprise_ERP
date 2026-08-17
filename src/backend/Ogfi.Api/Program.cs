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
using Ogfi.Modules.Inventory.Persistence;
using Ogfi.Modules.Procurement;
using Ogfi.Modules.Procurement.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "__Host-ogfi-session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddScoped<ITenantExecutionContextAccessor, TenantExecutionContextAccessor>();
builder.Services.AddScoped<TenantSessionConnectionInterceptor>();
builder.Services.AddScoped<MembershipResolver>();
builder.Services.AddScoped<FoundationAuthorizationEvaluator>();
builder.Services.AddScoped<BusinessTimeResolver>();
builder.Services.AddScoped<FoundationOrganizationReferenceService>();
builder.Services.AddScoped<CatalogReferenceService>();
builder.Services.AddScoped<StandardUomConversionService>();
builder.Services.AddScoped<PurchaseOrderService>();
builder.Services.AddSingleton(TimeProvider.System);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Database=ogfi;Username=ogfi;Password=ogfi_dev";

builder.Services.AddDbContext<FoundationDbContext>((serviceProvider, options) =>
    options.UseNpgsql(connectionString).AddInterceptors(serviceProvider.GetRequiredService<TenantSessionConnectionInterceptor>()));
builder.Services.AddDbContext<CatalogDbContext>((serviceProvider, options) =>
    options.UseNpgsql(connectionString).AddInterceptors(serviceProvider.GetRequiredService<TenantSessionConnectionInterceptor>()));
builder.Services.AddDbContext<InventoryDbContext>((serviceProvider, options) =>
    options.UseNpgsql(connectionString).AddInterceptors(serviceProvider.GetRequiredService<TenantSessionConnectionInterceptor>()));
builder.Services.AddDbContext<ProcurementDbContext>((serviceProvider, options) =>
    options.UseNpgsql(connectionString).AddInterceptors(serviceProvider.GetRequiredService<TenantSessionConnectionInterceptor>()));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<FoundationDbContext>("foundation-db")
    .AddDbContextCheck<CatalogDbContext>("catalog-db")
    .AddDbContextCheck<InventoryDbContext>("inventory-db")
    .AddDbContextCheck<ProcurementDbContext>("procurement-db");

var app = builder.Build();

app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    const string header = "X-Correlation-Id";
    var correlationId = context.Request.Headers.TryGetValue(header, out var supplied) && !string.IsNullOrWhiteSpace(supplied)
        ? supplied.ToString()
        : Guid.NewGuid().ToString("N");

    context.Items["CorrelationId"] = correlationId;
    context.Response.Headers[header] = correlationId;
    var stopwatch = Stopwatch.StartNew();

    using (app.Logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = correlationId }))
    {
        try
        {
            await next();
        }
        finally
        {
            stopwatch.Stop();
            var tags = new TagList
            {
                { "http.method", context.Request.Method },
                { "http.status_code", context.Response.StatusCode }
            };
            OgfiMetrics.ApiRequests.Add(1, tags);
            OgfiMetrics.ApiDurationMs.Record(stopwatch.Elapsed.TotalMilliseconds, tags);
        }
    }
});

app.UseAuthentication();
app.UseMiddleware<TenantExecutionContextMiddleware>();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.MapGet("/api/system/info", () => Results.Ok(new
{
    referenceImplementation = "RI-01",
    acceptedBaseline = "RI01-BL02",
    candidateBaseline = "RI01-BL03-CANDIDATE",
    activeBatch = "C",
    status = "IN_IMPLEMENTATION",
    metricsMeter = OgfiMetrics.MeterName
}));
app.MapFoundationContextEndpoints();
app.MapCatalogEndpoints();
app.MapInventorySetupEndpoints();
app.MapProcurementEndpoints();

app.Run();

public partial class Program;
