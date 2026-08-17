using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Ogfi.BuildingBlocks.Observability;
using Ogfi.Modules.Foundation.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<FoundationDbContext>();

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Database=ogfi;Username=ogfi;Password=ogfi_dev";

builder.Services.AddDbContext<FoundationDbContext>(options =>
    options.UseNpgsql(connectionString));

var app = builder.Build();

app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    const string header = "X-Correlation-Id";
    var correlationId = context.Request.Headers.TryGetValue(header, out var supplied) && !string.IsNullOrWhiteSpace(supplied)
        ? supplied.ToString()
        : Guid.NewGuid().ToString("N");

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

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.MapGet("/api/system/info", () => Results.Ok(new
{
    referenceImplementation = "RI-01",
    baseline = "RI01-BL01",
    activeBatch = "A",
    status = "IN_IMPLEMENTATION",
    metricsMeter = OgfiMetrics.MeterName
}));

app.Run();
