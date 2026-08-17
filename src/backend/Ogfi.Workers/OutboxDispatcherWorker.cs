using Microsoft.EntityFrameworkCore;
using Ogfi.BuildingBlocks.Observability;
using Ogfi.Modules.Foundation.Persistence;

namespace Ogfi.Workers;

public sealed class OutboxDispatcherWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxDispatcherWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<FoundationDbContext>();
                var pending = await db.OutboxMessages
                    .Where(x => x.ProcessedAtUtc == null)
                    .OrderBy(x => x.OccurredAtUtc)
                    .Take(50)
                    .ToListAsync(stoppingToken);

                foreach (var message in pending)
                {
                    message.AttemptCount++;
                    OgfiMetrics.OutboxDispatchAttempts.Add(1,
                        new KeyValuePair<string, object?>("message.type", message.Type));
                    logger.LogInformation(
                        "Dispatching outbox message {MessageId} {Type} correlation={CorrelationId}",
                        message.Id, message.Type, message.CorrelationId);
                    message.ProcessedAtUtc = DateTimeOffset.UtcNow;
                }

                if (pending.Count > 0)
                {
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                OgfiMetrics.WorkerFailures.Add(1,
                    new KeyValuePair<string, object?>("worker", nameof(OutboxDispatcherWorker)));
                logger.LogError(ex, "Outbox worker iteration failed; retrying with bounded delay");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
