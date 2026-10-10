using System.Data;
using Npgsql;
using NpgsqlTypes;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using WeChatAiCopilot.Api.Modules.Messages.Contracts;

namespace WeChatAiCopilot.Api.Modules.Conversations.Application;

public sealed class PostgresMessageQueryV1(NpgsqlDataSource dataSource) : IMessageQueryV1
{
    private const int MaximumPageSize = 100;

    internal const string ReadAnchorSql = """
        SELECT source_occurred_at, ingested_at, id
        FROM copilot.chat_messages
        WHERE account_id = @account_id
          AND conversation_id = @conversation_id
          AND id = @message_id;
        """;

    internal const string ReadPageSql = """
        SELECT id,
               text_content,
               message_type,
               source_occurred_at,
               direction,
               direction_evidence,
               ingested_at
        FROM copilot.chat_messages AS message
        WHERE message.account_id = @account_id
          AND message.conversation_id = @conversation_id
          AND (
            @has_anchor = false
            OR (
                (@anchor_source_occurred_at IS NULL
                    AND message.source_occurred_at IS NOT NULL)
                OR (@anchor_source_occurred_at IS NOT NULL
                    AND message.source_occurred_at < @anchor_source_occurred_at)
                OR (
                    message.source_occurred_at IS NOT DISTINCT FROM @anchor_source_occurred_at
                    AND (
                        message.ingested_at < @anchor_ingested_at
                        OR (message.ingested_at = @anchor_ingested_at AND message.id <= @anchor_id)
                    )
                )
            )
          )
        ORDER BY message.source_occurred_at DESC NULLS FIRST,
                 message.ingested_at DESC,
                 message.id DESC
        LIMIT @limit_plus_one;
        """;

    public async ValueTask<MessageQuerySnapshotV1> GetRecentMessagesAsync(
        MessageQueryRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AccountId == Guid.Empty || request.ConversationId == Guid.Empty)
        {
            throw new ArgumentException("A scoped account and conversation are required.", nameof(request));
        }

        var limit = Math.Clamp(request.Limit, 1, MaximumPageSize);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);

        var conversation = await ReadConversationSnapshotAsync(
            connection,
            transaction,
            request.AccountId,
            request.ConversationId,
            cancellationToken);
        if (conversation is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return EmptySnapshot(request, anchorMessageFound: request.AnchorMessageId is null);
        }

        var perspective = await ReadPerspectiveSnapshotAsync(
            connection,
            transaction,
            request.AccountId,
            request.ConversationId,
            cancellationToken);

        AnchorPosition? scopedAnchor = null;
        if (request.AnchorMessageId is { } anchorId)
        {
            scopedAnchor = await ReadAnchorAsync(
                connection,
                transaction,
                request.AccountId,
                request.ConversationId,
                anchorId,
                cancellationToken);
        }

        var anchorPolicy = ResolveAnchorQuery(request.AnchorMessageId, scopedAnchor);
        var messages = anchorPolicy.ShouldReadPage
            ? await ReadPageAsync(
                connection,
                transaction,
                request.AccountId,
                request.ConversationId,
                anchorPolicy.Position,
                limit,
                cancellationToken)
            : [];
        var hasMore = messages.Count > limit;
        if (hasMore)
        {
            messages.RemoveAt(messages.Count - 1);
        }
        messages.Reverse();

        await transaction.CommitAsync(cancellationToken);
        return new MessageQuerySnapshotV1
        {
            AccountId = request.AccountId,
            ConversationId = request.ConversationId,
            ConversationType = conversation.ConversationType,
            IdentityNamespace = perspective.IdentityNamespace,
            MessageSetRevision = conversation.MessageSetRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PerspectiveState = perspective.State,
            CalibrationVersion = perspective.Version,
            HistoryCoverage = MessageHistoryCoverageV1.Unknown,
            HasMoreMessages = hasMore,
            AnchorMessageFound = anchorPolicy.AnchorMessageFound,
            Messages = messages
        };
    }

    private static async Task<ConversationSnapshot?> ReadConversationSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT message_set_revision, conversation_type
            FROM copilot.conversations
            WHERE account_id = @account_id AND id = @conversation_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("conversation_id", conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ConversationSnapshot(reader.GetInt64(0), reader.GetString(1))
            : null;
    }

    private static async Task<PerspectiveSnapshot> ReadPerspectiveSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT identity_namespace, perspective_state, calibration_version
            FROM copilot.sender_calibrations
            WHERE account_id = @account_id AND conversation_id = @conversation_id
            LIMIT 2;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("conversation_id", conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var calibrations = new List<PerspectiveCalibrationRow>(capacity: 2);
        while (await reader.ReadAsync(cancellationToken))
        {
            calibrations.Add(new PerspectiveCalibrationRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2)));
        }

        return ResolvePerspectiveSnapshot(calibrations);
    }

    internal static PerspectiveSnapshot ResolvePerspectiveSnapshot(
        IReadOnlyList<PerspectiveCalibrationRow> calibrations)
    {
        ArgumentNullException.ThrowIfNull(calibrations);
        if (calibrations.Count != 1)
        {
            return new PerspectiveSnapshot(PerspectiveStateV1.Unresolved, 0, null);
        }

        var calibration = calibrations[0];
        return new PerspectiveSnapshot(
            Enum.Parse<PerspectiveStateV1>(calibration.State, ignoreCase: false),
            calibration.Version,
            calibration.IdentityNamespace);
    }

    private static async Task<AnchorPosition?> ReadAnchorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid conversationId,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ReadAnchorSql, connection, transaction);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("conversation_id", conversationId);
        command.Parameters.AddWithValue("message_id", messageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AnchorPosition(
            reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0),
            reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetGuid(2));
    }

    private static async Task<List<MessageQueryItemV1>> ReadPageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid conversationId,
        AnchorPosition? anchor,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ReadPageSql, connection, transaction);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("conversation_id", conversationId);
        command.Parameters.AddWithValue("has_anchor", anchor is not null);
        AddNullableTimestamp(command, "anchor_source_occurred_at", anchor?.SourceOccurredAt);
        AddNullableTimestamp(command, "anchor_ingested_at", anchor?.IngestedAt);
        AddNullableUuid(command, "anchor_id", anchor?.MessageId);
        command.Parameters.AddWithValue("limit_plus_one", limit + 1);

        var messages = new List<MessageQueryItemV1>(limit + 1);
        MessageSortPosition? previousPosition = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var messageId = reader.GetGuid(0);
            DateTimeOffset? sourceOccurredAt = reader.IsDBNull(3)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(3);
            var ingestedAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("ingested_at"));
            var position = new MessageSortPosition(sourceOccurredAt, ingestedAt, messageId);
            if ((previousPosition is not null
                    && CompareChronologically(previousPosition, position) < 0)
                || (anchor is not null && !IsAtOrBeforeAnchor(position, ToSortPosition(anchor))))
            {
                throw new InvalidOperationException("The message page violated its ordering boundary.");
            }

            messages.Add(new MessageQueryItemV1
            {
                MessageId = messageId,
                Text = reader.IsDBNull(1) ? null : reader.GetString(1),
                MessageType = reader.GetString(2),
                OccurredAt = sourceOccurredAt,
                Direction = Enum.Parse<MessageDirectionV1>(reader.GetString(4), ignoreCase: false),
                DirectionEvidence = Enum.Parse<DirectionEvidenceV1>(reader.GetString(5), ignoreCase: false)
            });
            previousPosition = position;
        }

        return messages;
    }

    private static MessageQuerySnapshotV1 EmptySnapshot(
        MessageQueryRequestV1 request,
        bool anchorMessageFound) => new()
    {
        AccountId = request.AccountId,
        ConversationId = request.ConversationId,
        MessageSetRevision = "missing",
        PerspectiveState = PerspectiveStateV1.Unresolved,
        CalibrationVersion = 0,
        HistoryCoverage = MessageHistoryCoverageV1.Unknown,
        HasMoreMessages = false,
        AnchorMessageFound = anchorMessageFound,
        Messages = Array.Empty<MessageQueryItemV1>()
    };

    private static void AddNullableTimestamp(
        NpgsqlCommand command,
        string name,
        DateTimeOffset? value) => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz)
    {
        Value = value is null ? DBNull.Value : value.Value
    });

    private static void AddNullableUuid(
        NpgsqlCommand command,
        string name,
        Guid? value) => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Uuid)
    {
        Value = value is null ? DBNull.Value : value.Value
    });

    internal sealed record AnchorPosition(
        DateTimeOffset? SourceOccurredAt,
        DateTimeOffset IngestedAt,
        Guid MessageId);

    internal sealed record AnchorQueryPolicy(
        AnchorPosition? Position,
        bool AnchorMessageFound,
        bool ShouldReadPage);

    internal sealed record MessageSortPosition(
        DateTimeOffset? SourceOccurredAt,
        DateTimeOffset IngestedAt,
        Guid MessageId);

    internal static AnchorQueryPolicy ResolveAnchorQuery(
        Guid? requestedAnchorId,
        AnchorPosition? scopedAnchor)
    {
        if (requestedAnchorId is null)
        {
            return new AnchorQueryPolicy(null, AnchorMessageFound: true, ShouldReadPage: true);
        }

        return scopedAnchor is not null && scopedAnchor.MessageId == requestedAnchorId
            ? new AnchorQueryPolicy(scopedAnchor, AnchorMessageFound: true, ShouldReadPage: true)
            : new AnchorQueryPolicy(null, AnchorMessageFound: false, ShouldReadPage: false);
    }

    internal static int CompareChronologically(MessageSortPosition left, MessageSortPosition right)
    {
        if (left.SourceOccurredAt is { } leftOccurredAt && right.SourceOccurredAt is null)
        {
            return -1;
        }

        if (left.SourceOccurredAt is null && right.SourceOccurredAt is { })
        {
            return 1;
        }

        if (left.SourceOccurredAt is { } leftTime && right.SourceOccurredAt is { } rightTime)
        {
            var sourceTimeOrder = leftTime.CompareTo(rightTime);
            if (sourceTimeOrder != 0)
            {
                return sourceTimeOrder;
            }
        }

        var ingestedAtOrder = left.IngestedAt.CompareTo(right.IngestedAt);
        if (ingestedAtOrder != 0)
        {
            return ingestedAtOrder;
        }

        return StringComparer.Ordinal.Compare(
            left.MessageId.ToString("N"),
            right.MessageId.ToString("N"));
    }

    internal static bool IsAtOrBeforeAnchor(MessageSortPosition position, MessageSortPosition anchor) =>
        CompareChronologically(position, anchor) <= 0;

    private static MessageSortPosition ToSortPosition(AnchorPosition anchor) =>
        new(anchor.SourceOccurredAt, anchor.IngestedAt, anchor.MessageId);

    internal sealed record PerspectiveCalibrationRow(string IdentityNamespace, string State, long Version);

    internal sealed record PerspectiveSnapshot(
        PerspectiveStateV1 State,
        long Version,
        string? IdentityNamespace);

    private sealed record ConversationSnapshot(long MessageSetRevision, string ConversationType);
}
