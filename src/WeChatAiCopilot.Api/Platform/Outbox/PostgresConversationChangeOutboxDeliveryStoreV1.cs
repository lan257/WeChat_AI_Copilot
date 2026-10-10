using Npgsql;
using NpgsqlTypes;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Platform.Outbox;

internal sealed class PostgresConversationChangeOutboxDeliveryStoreV1(NpgsqlDataSource dataSource)
    : IConversationChangeOutboxDeliveryStoreV1
{
    internal const string ConsumerName = "m3-memory-consolidation-v1";

    private const string ClaimNextSql = """
        WITH candidate AS (
            SELECT event.id
            FROM copilot.conversation_change_outbox AS event
            LEFT JOIN copilot.conversation_change_outbox_deliveries AS delivery
              ON delivery.consumer_name = @consumer_name
             AND delivery.event_id = event.id
            WHERE delivery.completed_at IS NULL
              AND (delivery.next_retry_at IS NULL OR delivery.next_retry_at <= now())
              AND (delivery.lease_until IS NULL OR delivery.lease_until <= now())
              AND NOT EXISTS (
                  SELECT 1
                  FROM copilot.conversation_change_outbox AS earlier
                  LEFT JOIN copilot.conversation_change_outbox_deliveries AS earlier_delivery
                    ON earlier_delivery.consumer_name = @consumer_name
                   AND earlier_delivery.event_id = earlier.id
                  WHERE earlier.account_id = event.account_id
                    AND earlier.conversation_id = event.conversation_id
                    AND earlier.event_sequence < event.event_sequence
                    AND earlier_delivery.completed_at IS NULL
              )
            ORDER BY event.event_sequence
            LIMIT 1
            FOR UPDATE OF event SKIP LOCKED
        ), claimed AS (
            INSERT INTO copilot.conversation_change_outbox_deliveries AS existing_delivery(
                consumer_name,
                event_id,
                attempts,
                next_retry_at,
                lease_id,
                lease_until,
                updated_at)
            SELECT
                @consumer_name,
                candidate.id,
                1,
                NULL,
                @lease_id,
                now() + @lease_duration,
                now()
            FROM candidate
            ON CONFLICT (consumer_name, event_id)
            DO UPDATE SET
                attempts = existing_delivery.attempts + 1,
                next_retry_at = NULL,
                lease_id = EXCLUDED.lease_id,
                lease_until = EXCLUDED.lease_until,
                updated_at = now()
            WHERE existing_delivery.completed_at IS NULL
              AND (existing_delivery.next_retry_at IS NULL OR existing_delivery.next_retry_at <= now())
              AND (existing_delivery.lease_until IS NULL OR existing_delivery.lease_until <= now())
            RETURNING event_id, lease_id, attempts
        )
        SELECT event.id,
               event.account_id,
               event.conversation_id,
               event.event_type,
               event.message_cursor,
               event.calibration_version,
               claimed.lease_id,
               claimed.attempts
        FROM claimed
        JOIN copilot.conversation_change_outbox AS event ON event.id = claimed.event_id;
        """;

    public async ValueTask<OutboxDeliveryClaimV1?> TryClaimNextAsync(
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        var leaseId = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(ClaimNextSql, connection, transaction);
        command.Parameters.AddWithValue("consumer_name", ConsumerName);
        command.Parameters.AddWithValue("lease_id", leaseId);
        command.Parameters.Add(new NpgsqlParameter("lease_duration", NpgsqlDbType.Interval)
        {
            Value = leaseDuration
        });

        OutboxDeliveryClaimV1? claim = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                var eventType = reader.GetString(3);
                claim = new OutboxDeliveryClaimV1(
                    new MemoryConsolidationOutboxEventV1
                    {
                        EventId = reader.GetGuid(0),
                        AccountId = reader.GetGuid(1),
                        ConversationId = reader.GetGuid(2),
                        Kind = ParseKind(eventType),
                        MessageCursor = reader.IsDBNull(4) ? null : reader.GetString(4),
                        CalibrationVersion = reader.IsDBNull(5) ? null : reader.GetInt64(5)
                    },
                    eventType,
                    reader.GetGuid(6),
                    reader.GetInt32(7));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return claim;
    }

    public ValueTask<bool> CompleteAsync(
        OutboxDeliveryClaimV1 claim,
        CancellationToken cancellationToken = default) => ExecuteLeaseUpdateAsync(
            """
            UPDATE copilot.conversation_change_outbox_deliveries
            SET completed_at = now(),
                next_retry_at = NULL,
                lease_id = NULL,
                lease_until = NULL,
                last_error_code = NULL,
                updated_at = now()
            WHERE consumer_name = @consumer_name
              AND event_id = @event_id
              AND lease_id = @lease_id
              AND completed_at IS NULL;
            """,
            claim,
            cancellationToken);

    public ValueTask<bool> ScheduleRetryAsync(
        OutboxDeliveryClaimV1 claim,
        string errorCode,
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        ValidateErrorCode(errorCode);
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }

        return ExecuteLeaseUpdateAsync(
            """
            UPDATE copilot.conversation_change_outbox_deliveries
            SET next_retry_at = now() + @retry_delay,
                lease_id = NULL,
                lease_until = NULL,
                last_error_code = @last_error_code,
                updated_at = now()
            WHERE consumer_name = @consumer_name
              AND event_id = @event_id
              AND lease_id = @lease_id
              AND completed_at IS NULL;
            """,
            claim,
            cancellationToken,
            errorCode,
            delay);
    }

    public ValueTask<bool> ReleaseLeaseAsync(
        OutboxDeliveryClaimV1 claim,
        CancellationToken cancellationToken = default) => ExecuteLeaseUpdateAsync(
            """
            UPDATE copilot.conversation_change_outbox_deliveries
            SET next_retry_at = NULL,
                lease_id = NULL,
                lease_until = NULL,
                updated_at = now()
            WHERE consumer_name = @consumer_name
              AND event_id = @event_id
              AND lease_id = @lease_id
              AND completed_at IS NULL;
            """,
            claim,
            cancellationToken);

    private async ValueTask<bool> ExecuteLeaseUpdateAsync(
        string sql,
        OutboxDeliveryClaimV1 claim,
        CancellationToken cancellationToken,
        string? errorCode = null,
        TimeSpan? delay = null)
    {
        ArgumentNullException.ThrowIfNull(claim);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("consumer_name", ConsumerName);
        command.Parameters.AddWithValue("event_id", claim.Event.EventId);
        command.Parameters.AddWithValue("lease_id", claim.LeaseId);
        if (errorCode is not null)
        {
            command.Parameters.AddWithValue("last_error_code", errorCode);
        }

        if (delay is { } retryDelay)
        {
            command.Parameters.Add(new NpgsqlParameter("retry_delay", NpgsqlDbType.Interval)
            {
                Value = retryDelay
            });
        }

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static MemoryConsolidationTriggerKindV1 ParseKind(string eventType) => eventType switch
    {
        "message.created" => MemoryConsolidationTriggerKindV1.MessageCreated,
        "conversation.perspective-confirmed" => MemoryConsolidationTriggerKindV1.PerspectiveConfirmed,
        "conversation.perspective-changed" => MemoryConsolidationTriggerKindV1.PerspectiveChanged,
        _ => throw new InvalidOperationException("The outbox event type is not supported by the M3 consumer.")
    };

    private static void ValidateErrorCode(string errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode)
            || errorCode.Length > 96
            || errorCode.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= 'A' and <= 'Z')
                    and not (>= '0' and <= '9')
                    and not '_' and not '-'))
        {
            throw new ArgumentException("A stable error code is required.", nameof(errorCode));
        }
    }
}
