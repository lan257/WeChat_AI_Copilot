using WeChatAiCopilot.Api.Contracts.V1;

namespace WeChatAiCopilot.DomainTests;

public sealed class ContractValidatorV1Tests
{
    [Fact]
    public void Validate_RawMessageEventAcceptsValidD01Message()
    {
        var violations = ContractValidatorV1.Validate(ValidMessage());

        Assert.Empty(violations);
    }

    [Fact]
    public void Validate_NullRawMessageEventReportsRequiredViolation()
    {
        var violations = ContractValidatorV1.Validate((RawMessageEventV1?)null);

        AssertViolation(violations, "MESSAGE_REQUIRED", "$");
    }

    [Fact]
    public void Validate_RawMessageEventReportsD01FieldAndTimeViolations()
    {
        var invalid = new RawMessageEventV1
        {
            SchemaVersion = 2,
            Provider = " ",
            AccountExternalId = null,
            ConversationExternalId = "",
            IdentityNamespace = "\t",
            ConversationType = "Group",
            MessageType = "Text",
            Text = null,
            SourceOccurredAt = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.FromHours(8)),
            SourceIdentity = null
        };

        var violations = ContractValidatorV1.Validate(invalid);

        AssertViolation(violations, "UNSUPPORTED_SCHEMA_VERSION", "schemaVersion");
        AssertViolation(violations, "FIELD_REQUIRED", "provider");
        AssertViolation(violations, "FIELD_REQUIRED", "accountExternalId");
        AssertViolation(violations, "FIELD_REQUIRED", "conversationExternalId");
        AssertViolation(violations, "FIELD_REQUIRED", "identityNamespace");
        AssertViolation(violations, "UNSUPPORTED_CONVERSATION_TYPE", "conversationType");
        AssertViolation(violations, "TEXT_REQUIRED", "text");
        AssertViolation(violations, "SOURCE_TIME_MUST_BE_UTC", "sourceOccurredAt");
        AssertViolation(violations, "SOURCE_IDENTITY_REQUIRED", "sourceIdentity");
    }

    [Fact]
    public void Validate_HistoryBatchAcceptsValidD02HistoryItem()
    {
        var item = ValidMessage() with { SourceKind = SourceKindV1.HistoryBackfill };
        var batch = new HistoryBatchV1
        {
            JobId = "job-1",
            AccountExternalId = "account-1",
            ConversationExternalId = "conversation-1",
            RequestedRange = new RequestedRangeV1
            {
                FromUtc = new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero),
                ToUtc = new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero)
            },
            Items = [item]
        };

        var violations = ContractValidatorV1.Validate(batch);

        Assert.Empty(violations);
    }

    [Fact]
    public void Validate_NullHistoryBatchReportsRequiredViolation()
    {
        var violations = ContractValidatorV1.Validate((HistoryBatchV1?)null);

        AssertViolation(violations, "HISTORY_BATCH_REQUIRED", "$");
    }

    [Fact]
    public void Validate_HistoryBatchReportsD02ScopeSourceAndItemPathViolations()
    {
        var invalidItem = ValidMessage() with
        {
            SourceKind = SourceKindV1.Realtime,
            AccountExternalId = "other-account",
            ConversationExternalId = "other-conversation",
            MessageType = "Text",
            Text = null
        };
        var batch = new HistoryBatchV1
        {
            SchemaVersion = 3,
            JobId = " ",
            AccountExternalId = "account-1",
            ConversationExternalId = "conversation-1",
            RequestedRange = new RequestedRangeV1
            {
                FromUtc = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.FromHours(8)),
                ToUtc = new DateTimeOffset(2026, 10, 9, 3, 0, 0, TimeSpan.Zero)
            },
            Items = [invalidItem]
        };

        var violations = ContractValidatorV1.Validate(batch);

        AssertViolation(violations, "UNSUPPORTED_SCHEMA_VERSION", "schemaVersion");
        AssertViolation(violations, "FIELD_REQUIRED", "jobId");
        AssertViolation(violations, "TIME_MUST_BE_UTC", "requestedRange.fromUtc");
        AssertViolation(violations, "INVALID_REQUESTED_RANGE", "requestedRange");
        AssertViolation(violations, "TEXT_REQUIRED", "items[0].text");
        AssertViolation(violations, "HISTORY_ITEM_SOURCE_KIND_REQUIRED", "items[0].sourceKind");
        AssertViolation(violations, "HISTORY_ITEM_SCOPE_MISMATCH", "items[0]");
    }

    [Fact]
    public void Validate_HistoryBatchRequiresRangeAndItems()
    {
        var violations = ContractValidatorV1.Validate(new HistoryBatchV1
        {
            JobId = "job-1",
            AccountExternalId = "account-1",
            ConversationExternalId = "conversation-1",
            RequestedRange = null,
            Items = null
        });

        AssertViolation(violations, "REQUESTED_RANGE_REQUIRED", "requestedRange");
        AssertViolation(violations, "ITEMS_REQUIRED", "items");
    }

    [Fact]
    public void Validate_PerspectiveRequestAcceptsValidD04Selection()
    {
        var violations = ContractValidatorV1.Validate(new SetWeChatAccountPerspectiveRequestV1
        {
            AccountId = Guid.NewGuid(),
            ConversationId = Guid.NewGuid(),
            IdentityNamespace = "wx4:account-a",
            AccountSelfRawSenderId = "123456",
            ExpectedCalibrationVersion = 0
        });

        Assert.Empty(violations);
    }

    [Fact]
    public void Validate_NullPerspectiveRequestReportsRequiredViolation()
    {
        var violations = ContractValidatorV1.Validate((SetWeChatAccountPerspectiveRequestV1?)null);

        AssertViolation(violations, "PERSPECTIVE_REQUEST_REQUIRED", "$");
    }

    [Fact]
    public void Validate_PerspectiveRequestReportsD04IdentityAndVersionViolations()
    {
        var violations = ContractValidatorV1.Validate(new SetWeChatAccountPerspectiveRequestV1
        {
            AccountId = Guid.Empty,
            ConversationId = Guid.Empty,
            IdentityNamespace = " ",
            AccountSelfRawSenderId = "-1",
            ExpectedCalibrationVersion = -1
        });

        AssertViolation(violations, "ACCOUNT_ID_REQUIRED", "accountId");
        AssertViolation(violations, "CONVERSATION_ID_REQUIRED", "conversationId");
        AssertViolation(violations, "FIELD_REQUIRED", "identityNamespace");
        AssertViolation(violations, "INVALID_SENDER_ID", "accountSelfRawSenderId");
        AssertViolation(violations, "INVALID_CALIBRATION_VERSION", "expectedCalibrationVersion");
    }

    private static RawMessageEventV1 ValidMessage() => new()
    {
        SchemaVersion = 1,
        Provider = "WeChat",
        SourceKind = SourceKindV1.Realtime,
        AccountExternalId = "account-1",
        ConversationExternalId = "conversation-1",
        ConversationType = "Direct",
        MessageType = "Text",
        Text = "hello",
        SourceOccurredAt = new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero),
        RawSenderId = "123456",
        IdentityNamespace = "wx4:account-1",
        SourceIdentity = new SourceIdentityV1
        {
            LocalId = "local-1",
            SortSeq = "sort-1",
            ServerId = "server-1",
            Shard = "message_0.db",
            ShardFingerprint = "shard-a",
            MessageTable = "MSG_0"
        },
        ExternalMessageId = "external-1",
        Direction = MessageDirectionV1.Unknown,
        DirectionEvidence = DirectionEvidenceV1.Unresolved
    };

    private static void AssertViolation(
        IReadOnlyList<ContractViolationV1> violations,
        string expectedCode,
        string expectedPath)
    {
        Assert.Contains(violations, violation =>
            violation.Code == expectedCode && violation.Path == expectedPath);
    }
}
