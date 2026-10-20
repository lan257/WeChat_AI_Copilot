namespace WeChatAiCopilot.Api.Contracts.V1;

public sealed record ContractViolationV1(string Code, string Path, string Message);

public static class ContractValidatorV1
{
    public static IReadOnlyList<ContractViolationV1> Validate(RawMessageEventV1? message)
    {
        if (message is null)
        {
            return [new("MESSAGE_REQUIRED", "$", "A message event is required.")];
        }

        var violations = new List<ContractViolationV1>();

        if (message.SchemaVersion != 1)
        {
            violations.Add(new("UNSUPPORTED_SCHEMA_VERSION", "schemaVersion", "Only schema version 1 is supported."));
        }

        Require(message.Provider, "provider", violations);
        Require(message.AccountExternalId, "accountExternalId", violations);
        Require(message.ConversationExternalId, "conversationExternalId", violations);
        Require(message.IdentityNamespace, "identityNamespace", violations);
        ValidateOptionalIdentifier(message.AccountWxid, "accountWxid", 512, violations);
        ValidateOptionalIdentifier(message.SenderWxid, "senderWxid", 512, violations);

        if (!string.Equals(message.ConversationType, "Direct", StringComparison.Ordinal))
        {
            violations.Add(new("UNSUPPORTED_CONVERSATION_TYPE", "conversationType", "Only Direct conversations are supported."));
        }

        Require(message.MessageType, "messageType", violations);

        if (string.Equals(message.MessageType, "Text", StringComparison.Ordinal) && message.Text is null)
        {
            violations.Add(new("TEXT_REQUIRED", "text", "Text messages must include a text value."));
        }

        if (message.SourceOccurredAt is { Offset: var offset } && offset != TimeSpan.Zero)
        {
            violations.Add(new("SOURCE_TIME_MUST_BE_UTC", "sourceOccurredAt", "Source timestamps must use UTC."));
        }

        if (message.SourceIdentity is null)
        {
            violations.Add(new("SOURCE_IDENTITY_REQUIRED", "sourceIdentity", "Source identity details must be provided, even when individual values are unknown."));
        }
        else
        {
            ValidateOptionalIdentifier(message.SourceIdentity.Shard, "sourceIdentity.shard", 128, violations);
        }

        return violations;
    }

    public static IReadOnlyList<ContractViolationV1> Validate(HistoryBatchV1? batch)
    {
        if (batch is null)
        {
            return [new("HISTORY_BATCH_REQUIRED", "$", "A history batch is required.")];
        }

        var violations = new List<ContractViolationV1>();
        if (batch.SchemaVersion != 1)
        {
            violations.Add(new("UNSUPPORTED_SCHEMA_VERSION", "schemaVersion", "Only schema version 1 is supported."));
        }

        Require(batch.JobId, "jobId", violations);
        Require(batch.AccountExternalId, "accountExternalId", violations);
        Require(batch.ConversationExternalId, "conversationExternalId", violations);

        if (batch.RequestedRange is null)
        {
            violations.Add(new("REQUESTED_RANGE_REQUIRED", "requestedRange", "A requested range is required."));
        }
        else
        {
            ValidateUtc(batch.RequestedRange.FromUtc, "requestedRange.fromUtc", violations);
            ValidateUtc(batch.RequestedRange.ToUtc, "requestedRange.toUtc", violations);
            if (batch.RequestedRange.FromUtc is { } fromUtc
                && batch.RequestedRange.ToUtc is { } toUtc
                && fromUtc > toUtc)
            {
                violations.Add(new("INVALID_REQUESTED_RANGE", "requestedRange", "fromUtc must not be later than toUtc."));
            }
        }

        if (batch.Items is null)
        {
            violations.Add(new("ITEMS_REQUIRED", "items", "The items collection is required."));
            return violations;
        }

        for (var index = 0; index < batch.Items.Count; index++)
        {
            var item = batch.Items[index];
            foreach (var violation in Validate(item))
            {
                violations.Add(violation with { Path = $"items[{index}].{violation.Path}" });
            }

            if (item is null)
            {
                continue;
            }

            if (item.SourceKind != SourceKindV1.HistoryBackfill)
            {
                violations.Add(new("HISTORY_ITEM_SOURCE_KIND_REQUIRED", $"items[{index}].sourceKind", "History batch items must use HistoryBackfill."));
            }

            if (!string.Equals(item.AccountExternalId, batch.AccountExternalId, StringComparison.Ordinal)
                || !string.Equals(item.ConversationExternalId, batch.ConversationExternalId, StringComparison.Ordinal))
            {
                violations.Add(new("HISTORY_ITEM_SCOPE_MISMATCH", $"items[{index}]", "Every item must match the batch account and conversation."));
            }
        }

        return violations;
    }

    public static IReadOnlyList<ContractViolationV1> Validate(SetWeChatAccountPerspectiveRequestV1? request)
    {
        if (request is null)
        {
            return [new("PERSPECTIVE_REQUEST_REQUIRED", "$", "A perspective request is required.")];
        }

        var violations = new List<ContractViolationV1>();
        if (request.AccountId == Guid.Empty)
        {
            violations.Add(new("ACCOUNT_ID_REQUIRED", "accountId", "A valid account ID is required."));
        }

        if (request.ConversationId == Guid.Empty)
        {
            violations.Add(new("CONVERSATION_ID_REQUIRED", "conversationId", "A valid conversation ID is required."));
        }

        Require(request.IdentityNamespace, "identityNamespace", violations);
        if (!IsNonNegativeDecimal(request.AccountSelfRawSenderId))
        {
            violations.Add(new("INVALID_SENDER_ID", "accountSelfRawSenderId", "The selected sender ID must be a non-negative decimal string."));
        }

        if (request.ExpectedCalibrationVersion < 0)
        {
            violations.Add(new("INVALID_CALIBRATION_VERSION", "expectedCalibrationVersion", "The expected version must be non-negative."));
        }

        return violations;
    }

    private static void Require(string? value, string path, ICollection<ContractViolationV1> violations)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            violations.Add(new("FIELD_REQUIRED", path, "This field is required."));
        }
    }

    private static void ValidateOptionalIdentifier(
        string? value,
        string path,
        int maximumLength,
        ICollection<ContractViolationV1> violations)
    {
        if (value is not null
            && (value.Length > maximumLength || value.Any(char.IsControl)))
        {
            violations.Add(new("IDENTIFIER_INVALID", path, "The identifier contains unsupported characters or is too long."));
        }
    }

    private static void ValidateUtc(DateTimeOffset? value, string path, ICollection<ContractViolationV1> violations)
    {
        if (value is { Offset: var offset } && offset != TimeSpan.Zero)
        {
            violations.Add(new("TIME_MUST_BE_UTC", path, "Timestamps must use UTC."));
        }
    }

    private static bool IsNonNegativeDecimal(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
