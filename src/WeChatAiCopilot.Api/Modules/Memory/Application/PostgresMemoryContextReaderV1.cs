using Npgsql;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Modules.Memory.Application;

/// <summary>
/// Loads persisted memory projections for the exact account/conversation request.
/// Participant profiles, MessageExtracted memories, and consolidation progress are
/// constrained to the confirmed identity namespace and calibration version supplied by M2.
/// </summary>
public sealed class PostgresMemoryContextReaderV1(NpgsqlDataSource dataSource) : IMemoryContextReaderV1
{
    public const int MaximumRowsRead = 200;
    public const int MaximumEvidenceIdsPerRowRead = 200;
    public const int MaximumProfileEvidenceIdsPerRead = 100;

    public async ValueTask<MemoryContextSnapshotV1> ReadAsync(
        MemoryContextReadRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AccountId == Guid.Empty || request.ConversationId == Guid.Empty)
        {
            throw new ArgumentException("A memory context read requires an explicit authorized scope.", nameof(request));
        }

        var mayReadPerspectiveBoundData = request.MayReadPerspectiveBoundData;
        var profileStatus = mayReadPerspectiveBoundData
            ? ProfileStatusV1.FirstConsolidation
            : ProfileStatusV1.WaitingForConfirmation;
        var unprofiledTextCharacters = 0;
        string? consolidationSourceMessageCursor = null;
        string messageSetRevision = string.Empty;
        IReadOnlyList<ParticipantProfileSnapshotV1> participantProfiles =
            Array.Empty<ParticipantProfileSnapshotV1>();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await PostgresMemoryScopePolicy.SetTransactionScopeAsync(
            connection,
            transaction,
            request.AccountId,
            request.ConversationId,
            cancellationToken);

        const string sql = """
            WITH selected_items AS (
                SELECT m.id,
                       m.account_id,
                       m.subject_type,
                       m.subject_id,
                       m.conversation_id,
                       m.identity_namespace,
                       m.text_content,
                       m.category,
                       m.origin,
                       m.status,
                       m.created_at,
                       m.updated_at,
                       m.observed_at,
                       m.calibration_version,
                       m.source_message_cursor,
                       m.version
                FROM copilot.memory_items m
                JOIN copilot.conversations authorized_conversation
                  ON authorized_conversation.id = @conversation_id
                 AND authorized_conversation.account_id = @account_id
                WHERE m.account_id = @account_id
                  AND m.status = 'Active'
                  AND (
                      m.conversation_id = @conversation_id
                      OR (
                          m.conversation_id IS NULL
                          AND m.subject_type = 'WechatAccount'
                          AND m.origin = 'UserEntered'
                      )
                  )
                  AND (
                      m.origin = 'UserEntered'
                      OR (
                          @may_read_perspective_bound_data
                          AND m.origin = 'MessageExtracted'
                          AND m.conversation_id = @conversation_id
                          AND m.identity_namespace = @identity_namespace
                          AND m.calibration_version = @calibration_version
                      )
                  )
                  AND (
                      (m.subject_type = 'WechatAccount' AND m.subject_id = @account_id)
                      OR (m.subject_type = 'Contact' AND m.subject_id = authorized_conversation.contact_id)
                  )
                ORDER BY m.updated_at DESC, m.id
                LIMIT @maximum_rows
            )
            SELECT m.id,
                   m.account_id,
                   m.subject_type,
                   m.subject_id,
                   m.conversation_id,
                   m.identity_namespace,
                   m.text_content,
                   m.category,
                   m.origin,
                   m.status,
                   m.created_at,
                   m.updated_at,
                   m.observed_at,
                   m.calibration_version,
                   m.source_message_cursor,
                   m.version,
                   COALESCE(e.source_message_ids, ARRAY[]::uuid[])
            FROM selected_items m
            LEFT JOIN LATERAL (
                SELECT array_agg(selected_evidence.message_id ORDER BY selected_evidence.ordinal)
                    AS source_message_ids
                FROM (
                    SELECT e.message_id, e.ordinal
                    FROM copilot.memory_evidence e
                    WHERE e.memory_id = m.id
                      AND e.account_id = @account_id
                      AND e.conversation_id = @conversation_id
                    ORDER BY e.ordinal
                    LIMIT @maximum_evidence_ids
                ) selected_evidence
            ) e ON true
            ORDER BY m.updated_at DESC, m.id;
            """;

        var items = new List<MemoryItemV1>();
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("account_id", request.AccountId);
            command.Parameters.AddWithValue("conversation_id", request.ConversationId);
            command.Parameters.AddWithValue("identity_namespace", request.IdentityNamespace);
            command.Parameters.AddWithValue("may_read_perspective_bound_data", mayReadPerspectiveBoundData);
            command.Parameters.AddWithValue("calibration_version", request.CalibrationVersion);
            command.Parameters.AddWithValue("maximum_rows", MaximumRowsRead);
            command.Parameters.AddWithValue("maximum_evidence_ids", MaximumEvidenceIdsPerRowRead);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadMemoryItem(reader));
            }
        }

        if (mayReadPerspectiveBoundData && !string.IsNullOrWhiteSpace(request.IdentityNamespace))
        {
            var state = await ReadConsolidationStateAsync(connection, transaction, request, cancellationToken);
            if (state is not null)
            {
                profileStatus = state.Status;
                unprofiledTextCharacters = state.UnprofiledTextCharacters;
                consolidationSourceMessageCursor = state.ConsolidationSourceMessageCursor;
                messageSetRevision = state.MessageSetRevision;
            }

            participantProfiles = await ReadParticipantProfilesAsync(
                connection,
                transaction,
                request,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new MemoryContextSnapshotV1
        {
            AccountId = request.AccountId,
            ConversationId = request.ConversationId,
            Revision = MemoryContextRevisionV1.Compute(
                request,
                profileStatus,
                items,
                participantProfiles,
                unprofiledTextCharacters,
                consolidationSourceMessageCursor,
                messageSetRevision),
            ProfileStatus = profileStatus,
            UnprofiledTextCharacters = unprofiledTextCharacters,
            ConsolidationSourceMessageCursor = consolidationSourceMessageCursor,
            ParticipantProfiles = participantProfiles,
            MemoryItems = items
        };
    }

    private static async Task<MemoryConsolidationStateV1?> ReadConsolidationStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MemoryContextReadRequestV1 request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT message_set_revision,
                   consolidation_source_message_cursor,
                   unprofiled_text_characters,
                   initial_consolidation_completed,
                   reprocess_required,
                   reprocess_required_revision,
                   profile_status,
                   state_version
            FROM copilot.memory_consolidation_state
            WHERE account_id = @account_id
              AND conversation_id = @conversation_id
              AND identity_namespace = @identity_namespace
              AND calibration_version = @calibration_version;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("account_id", request.AccountId);
        command.Parameters.AddWithValue("conversation_id", request.ConversationId);
        command.Parameters.AddWithValue("identity_namespace", request.IdentityNamespace);
        command.Parameters.AddWithValue("calibration_version", request.CalibrationVersion);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MemoryConsolidationStateV1
        {
            AccountId = request.AccountId,
            ConversationId = request.ConversationId,
            IdentityNamespace = request.IdentityNamespace,
            CalibrationVersion = request.CalibrationVersion,
            MessageSetRevision = reader.GetString(0),
            ConsolidationSourceMessageCursor = reader.IsDBNull(1) ? null : reader.GetString(1),
            UnprofiledTextCharacters = reader.GetInt32(2),
            InitialConsolidationCompleted = reader.GetBoolean(3),
            ReprocessRequired = reader.GetBoolean(4),
            ReprocessRequiredRevision = reader.IsDBNull(5) ? null : reader.GetString(5),
            Status = Enum.Parse<ProfileStatusV1>(reader.GetString(6), ignoreCase: false),
            StateVersion = reader.GetInt64(7)
        };
    }

    private static async Task<IReadOnlyList<ParticipantProfileSnapshotV1>> ReadParticipantProfilesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MemoryContextReadRequestV1 request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH latest_profiles AS (
                SELECT DISTINCT ON (profile.subject_type, profile.subject_id)
                       profile.id,
                       profile.account_id,
                       profile.conversation_id,
                       profile.identity_namespace,
                       profile.subject_type,
                       profile.subject_id,
                       profile.profile_revision,
                       profile.calibration_version,
                       profile.status,
                       profile.summary,
                       profile.source_message_cursor,
                       profile.generated_at
                FROM copilot.participant_profile_snapshots profile
                JOIN copilot.conversations authorized_conversation
                  ON authorized_conversation.id = @conversation_id
                 AND authorized_conversation.account_id = @account_id
                WHERE profile.account_id = @account_id
                  AND profile.conversation_id = @conversation_id
                  AND profile.identity_namespace = @identity_namespace
                  AND profile.calibration_version = @calibration_version
                  AND profile.status = 'Current'
                  AND (
                      (profile.subject_type = 'WechatAccount' AND profile.subject_id = @account_id)
                      OR (profile.subject_type = 'Contact' AND profile.subject_id = authorized_conversation.contact_id)
                  )
                ORDER BY profile.subject_type, profile.subject_id, profile.profile_revision DESC
            )
            SELECT profile.account_id,
                   profile.conversation_id,
                   profile.identity_namespace,
                   profile.subject_type,
                   profile.subject_id,
                   profile.profile_revision,
                   profile.calibration_version,
                   profile.status,
                   profile.summary,
                   profile.source_message_cursor,
                   profile.generated_at,
                   COALESCE(e.evidence_message_ids, ARRAY[]::uuid[])
            FROM latest_profiles profile
            LEFT JOIN LATERAL (
                SELECT array_agg(selected_evidence.message_id ORDER BY selected_evidence.ordinal)
                    AS evidence_message_ids
                FROM (
                    SELECT evidence.message_id, evidence.ordinal
                    FROM copilot.participant_profile_evidence evidence
                    WHERE evidence.profile_id = profile.id
                      AND evidence.account_id = @account_id
                      AND evidence.conversation_id = @conversation_id
                    ORDER BY evidence.ordinal
                    LIMIT @maximum_evidence_ids
                ) selected_evidence
            ) e ON true
            ORDER BY profile.subject_type, profile.subject_id;
            """;
        var profiles = new List<ParticipantProfileSnapshotV1>(2);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("account_id", request.AccountId);
        command.Parameters.AddWithValue("conversation_id", request.ConversationId);
        command.Parameters.AddWithValue("identity_namespace", request.IdentityNamespace);
        command.Parameters.AddWithValue("calibration_version", request.CalibrationVersion);
        command.Parameters.AddWithValue("maximum_evidence_ids", MaximumProfileEvidenceIdsPerRead);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            profiles.Add(new ParticipantProfileSnapshotV1
            {
                AccountId = reader.GetGuid(0),
                ConversationId = reader.GetGuid(1),
                IdentityNamespace = reader.GetString(2),
                SubjectType = Enum.Parse<ParticipantSubjectTypeV1>(reader.GetString(3), ignoreCase: false),
                SubjectId = reader.GetGuid(4),
                ProfileRevision = reader.GetInt64(5),
                CalibrationVersion = reader.GetInt64(6),
                Status = Enum.Parse<ProfileStatusV1>(reader.GetString(7), ignoreCase: false),
                Summary = reader.GetString(8),
                SourceMessageCursor = reader.GetString(9),
                GeneratedAt = AsUtcOffset(reader.GetDateTime(10)),
                EvidenceMessageIds = reader.GetFieldValue<Guid[]>(11)
            });
        }

        return profiles;
    }

    private static MemoryItemV1 ReadMemoryItem(NpgsqlDataReader reader) => new()
    {
        MemoryId = reader.GetGuid(0),
        AccountId = reader.GetGuid(1),
        SubjectType = Enum.Parse<ParticipantSubjectTypeV1>(reader.GetString(2), ignoreCase: false),
        SubjectId = reader.GetGuid(3),
        ConversationId = reader.IsDBNull(4) ? null : reader.GetGuid(4),
        IdentityNamespace = reader.IsDBNull(5) ? null : reader.GetString(5),
        Text = reader.GetString(6),
        Category = reader.IsDBNull(7) ? null : reader.GetString(7),
        Origin = Enum.Parse<MemoryOriginV1>(reader.GetString(8), ignoreCase: false),
        Status = Enum.Parse<MemoryItemStatusV1>(reader.GetString(9), ignoreCase: false),
        CreatedAt = AsUtcOffset(reader.GetDateTime(10)),
        UpdatedAt = AsUtcOffset(reader.GetDateTime(11)),
        ObservedAt = reader.IsDBNull(12) ? null : AsUtcOffset(reader.GetDateTime(12)),
        CalibrationVersion = reader.IsDBNull(13) ? null : reader.GetInt64(13),
        SourceMessageCursor = reader.IsDBNull(14) ? null : reader.GetString(14),
        Version = reader.GetInt64(15),
        SourceMessageIds = reader.GetFieldValue<Guid[]>(16)
    };

    private static DateTimeOffset AsUtcOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
