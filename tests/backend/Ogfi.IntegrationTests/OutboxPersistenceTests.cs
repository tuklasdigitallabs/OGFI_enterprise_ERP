using Microsoft.EntityFrameworkCore;
using Ogfi.BuildingBlocks.Messaging;
using Ogfi.Modules.Foundation.Persistence;
using Xunit;

namespace Ogfi.IntegrationTests;

public sealed class OutboxPersistenceTests
{
    [Fact]
    public async Task Outbox_message_round_trips_through_postgresql()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var options = new DbContextOptionsBuilder<FoundationDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var db = new FoundationDbContext(options);
        Assert.True(await db.Database.CanConnectAsync());
        await db.Database.MigrateAsync();

        var message = new OutboxMessage
        {
            TenantId = Guid.NewGuid(),
            Type = "BatchA.IntegrationTest",
            OccurredAtUtc = DateTimeOffset.UtcNow,
            CorrelationId = Guid.NewGuid().ToString("N"),
            Payload = "{}"
        };

        db.OutboxMessages.Add(message);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var persisted = await db.OutboxMessages.SingleAsync(x => x.Id == message.Id);
        Assert.Equal(message.TenantId, persisted.TenantId);
        Assert.Equal(message.Type, persisted.Type);
        Assert.Equal(message.CorrelationId, persisted.CorrelationId);
        Assert.Equal("{}", persisted.Payload);
    }
}
