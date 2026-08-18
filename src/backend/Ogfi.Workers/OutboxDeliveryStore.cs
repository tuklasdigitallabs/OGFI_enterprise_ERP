using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ogfi.Modules.Procurement.Persistence;

namespace Ogfi.Workers;

public static class OutboxConsumerCodes
{
    public const string InventoryStockConsequence = "inventory.stock-consequence";
    public const string FinanceFinancialConsequence = "finance.financial-consequence";
    public const string AuditMaterialAction = "audit.material-action-ingestion";
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
                SELECT count(*) FILTER (WHERE "ConsumerCode" IN ('inventory.stock-consequence','finance.financial-consequence')) AS total,
                       count(*) FILTER (WHERE "ConsumerCode" IN ('inventory.stock-consequence','finance.financial-consequence')
                                          AND "Status" IN ('COMPLETED','TERMINAL_REJECTED')) AS terminal,
                       max("LastError") FILTER (WHERE "ConsumerCode" IN ('inventory.stock-consequence','finance.financial-consequence')
                                                  AND "Status" = 'TERMINAL_REJECTED') AS terminal_error
                  FROM procurement.outbox_deliveries
                 WHERE "TenantId" = @tenant
                   AND "OutboxMessageId" = @message
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

    public async Task<Guid[]> GetPendingMessageIdsAsync(
        Guid tenantId,
        string consumerCode,
        string[] messageTypes,
        int limit,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT m."Id"
              FROM procurement.outbox_messages m
              LEFT JOIN procurement.outbox_deliveries d
                ON d."TenantId" = m."TenantId" AND d."OutboxMessageId" = m."Id"
               AND d."ConsumerCode" = @consumer
             WHERE m."TenantId" = @tenant
               AND m."Type" = ANY(@types)
               AND (d."Status" IS NULL OR d."Status" NOT IN ('COMPLETED','TERMINAL_REJECTED'))
             ORDER BY m."OccurredAtUtc", m."Id"
             LIMIT @limit;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("consumer", consumerCode);
        command.Parameters.AddWithValue("types", messageTypes);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 100));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetGuid(0));
        return ids.ToArray();
    }

    public async Task<ProcessorIterationResult> GetConsumerStateAsync(
        Guid tenantId, string consumerCode, CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FILTER (WHERE d."Status" = 'PENDING'),
                   count(*) FILTER (WHERE d."Status" = 'RETRY_PENDING'),
                   count(*) FILTER (WHERE d."Status" = 'TERMINAL_REJECTED'),
                   min(m."OccurredAtUtc") FILTER (WHERE d."Status" IN ('PENDING','RETRY_PENDING')),
                   max(d."LastError") FILTER (WHERE d."Status" IN ('RETRY_PENDING','TERMINAL_REJECTED'))
              FROM procurement.outbox_deliveries d
              JOIN procurement.outbox_messages m
                ON m."TenantId" = d."TenantId" AND m."Id" = d."OutboxMessageId"
             WHERE d."TenantId" = @tenant AND d."ConsumerCode" = @consumer;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("consumer", consumerCode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new ProcessorIterationResult(
            null, Convert.ToInt32(reader.GetInt64(0)), Convert.ToInt32(reader.GetInt64(1)),
            Convert.ToInt32(reader.GetInt64(2)),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
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
