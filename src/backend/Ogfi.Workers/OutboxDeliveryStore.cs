using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ogfi.Modules.Procurement.Persistence;

namespace Ogfi.Workers;

public static class OutboxConsumerCodes
{
    public const string InventoryStockConsequence = "inventory.stock-consequence";
    public const string FinanceFinancialConsequence = "finance.financial-consequence";
}

public static class OutboxDeliveryStatuses
{
    public const string Pending = "PENDING";
    public const string RetryPending = "RETRY_PENDING";
    public const string Completed = "COMPLETED";
    public const string TerminalRejected = "TERMINAL_REJECTED";
}

public sealed record OutboxDeliveryState(
    Guid Id,
    Guid TenantId,
    Guid OutboxMessageId,
    string ConsumerCode,
    string Status,
    int AttemptCount,
    string? LastError,
    DateTimeOffset? CompletedAtUtc);

public sealed class OutboxDeliveryStore(ProcurementDbContext procurementDb, TimeProvider timeProvider)
{
    public async Task<OutboxDeliveryState> EnsureAsync(
        Guid tenantId,
        Guid outboxMessageId,
        string consumerCode,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var connection = await OpenAsync(cancellationToken);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO procurement.outbox_deliveries
                ("Id", "TenantId", "OutboxMessageId", "ConsumerCode", "Status", "AttemptCount", "CreatedAtUtc", "UpdatedAtUtc")
            VALUES (@id, @tenant, @message, @consumer, 'PENDING', 0, @now, @now)
            ON CONFLICT ("TenantId", "OutboxMessageId", "ConsumerCode") DO NOTHING;
            """, connection))
        {
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("tenant", tenantId);
            insert.Parameters.AddWithValue("message", outboxMessageId);
            insert.Parameters.AddWithValue("consumer", consumerCode);
            insert.Parameters.AddWithValue("now", now);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        return await GetAsync(connection, tenantId, outboxMessageId, consumerCode, cancellationToken);
    }

    public Task MarkAttemptAsync(Guid tenantId, Guid messageId, string consumerCode, CancellationToken cancellationToken)
        => UpdateAsync(tenantId, messageId, consumerCode,
            "\"AttemptCount\" = \"AttemptCount\" + 1, \"Status\" = 'PENDING', \"LastError\" = NULL, \"UpdatedAtUtc\" = @now",
            null, cancellationToken);

    public Task MarkCompletedAsync(Guid tenantId, Guid messageId, string consumerCode, CancellationToken cancellationToken)
        => UpdateAsync(tenantId, messageId, consumerCode,
            "\"Status\" = 'COMPLETED', \"LastError\" = NULL, \"UpdatedAtUtc\" = @now, \"CompletedAtUtc\" = @now",
            null, cancellationToken);

    public Task MarkRetryAsync(Guid tenantId, Guid messageId, string consumerCode, string error, CancellationToken cancellationToken)
        => UpdateAsync(tenantId, messageId, consumerCode,
            "\"Status\" = 'RETRY_PENDING', \"LastError\" = @error, \"UpdatedAtUtc\" = @now, \"CompletedAtUtc\" = NULL",
            error, cancellationToken);

    public Task MarkTerminalRejectedAsync(Guid tenantId, Guid messageId, string consumerCode, string error, CancellationToken cancellationToken)
        => UpdateAsync(tenantId, messageId, consumerCode,
            "\"Status\" = 'TERMINAL_REJECTED', \"LastError\" = @error, \"UpdatedAtUtc\" = @now, \"CompletedAtUtc\" = @now",
            error, cancellationToken);

    public async Task TryFinalizeMessageAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH delivery_state AS (
                SELECT count(*) AS total,
                       count(*) FILTER (WHERE "Status" IN ('COMPLETED','TERMINAL_REJECTED')) AS terminal,
                       max("LastError") FILTER (WHERE "Status" = 'TERMINAL_REJECTED') AS terminal_error
                  FROM procurement.outbox_deliveries
                 WHERE "TenantId" = @tenant
                   AND "OutboxMessageId" = @message
                   AND "ConsumerCode" IN ('inventory.stock-consequence','finance.financial-consequence')
            )
            UPDATE procurement.outbox_messages m
               SET "ProcessedAtUtc" = COALESCE(m."ProcessedAtUtc", @now),
                   "LastError" = delivery_state.terminal_error
              FROM delivery_state
             WHERE m."TenantId" = @tenant
               AND m."Id" = @message
               AND delivery_state.total = 2
               AND delivery_state.terminal = 2;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("message", messageId);
        command.Parameters.AddWithValue("now", timeProvider.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateAsync(
        Guid tenantId,
        Guid messageId,
        string consumerCode,
        string assignmentSql,
        string? error,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($$"""
            UPDATE procurement.outbox_deliveries
               SET {{assignmentSql}}
             WHERE "TenantId" = @tenant
               AND "OutboxMessageId" = @message
               AND "ConsumerCode" = @consumer;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("message", messageId);
        command.Parameters.AddWithValue("consumer", consumerCode);
        command.Parameters.AddWithValue("now", timeProvider.GetUtcNow());
        if (error is not null) command.Parameters.AddWithValue("error", error.Length <= 160 ? error : error[..160]);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<OutboxDeliveryState> GetAsync(
        NpgsqlConnection connection,
        Guid tenantId,
        Guid messageId,
        string consumerCode,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT "Id", "TenantId", "OutboxMessageId", "ConsumerCode", "Status", "AttemptCount", "LastError", "CompletedAtUtc"
              FROM procurement.outbox_deliveries
             WHERE "TenantId" = @tenant
               AND "OutboxMessageId" = @message
               AND "ConsumerCode" = @consumer;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("message", messageId);
        command.Parameters.AddWithValue("consumer", consumerCode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Outbox delivery row was not created.");
        }
        return new OutboxDeliveryState(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4),
            reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7));
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await procurementDb.Database.OpenConnectionAsync(cancellationToken);
        return (NpgsqlConnection)procurementDb.Database.GetDbConnection();
    }
}
