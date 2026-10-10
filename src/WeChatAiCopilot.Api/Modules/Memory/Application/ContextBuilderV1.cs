using System.Security.Cryptography;
using System.Text;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using WeChatAiCopilot.Api.Modules.Messages.Contracts;

namespace WeChatAiCopilot.Api.Modules.Memory.Application;

/// <summary>
/// Builds the first D05/D06 context projection from M2's scoped message-query port and
/// M3's storage-agnostic memory reader. Persistence and consolidation are intentionally
/// outside this builder.
/// </summary>
public sealed class ContextBuilderV1 : IContextServiceV1
{
    public const int DefaultRecentMessageLimit = 30;
    public const int MaximumRecentMessageLimit = 100;
    public const int DefaultConsolidationThresholdCharacters = 5000;

    private readonly IMessageQueryV1 _messageQuery;
    private readonly IMemoryContextReaderV1 _memoryReader;
    private readonly TimeProvider _timeProvider;
    private readonly ContextBuilderOptionsV1 _options;

    public ContextBuilderV1(
        IMessageQueryV1 messageQuery,
        IMemoryContextReaderV1 memoryReader,
        TimeProvider? timeProvider = null,
        ContextBuilderOptionsV1? options = null)
    {
        _messageQuery = messageQuery ?? throw new ArgumentNullException(nameof(messageQuery));
        _memoryReader = memoryReader ?? throw new ArgumentNullException(nameof(memoryReader));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new ContextBuilderOptionsV1();
        if (_options.MaximumTotalTextCodePoints <= 0
            || _options.MaximumMemoryItems <= 0
            || _options.MaximumMemoryEvidenceIdsPerItem <= 0
            || _options.MaximumProfileEvidenceIds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Context budget limits must all be positive.");
        }
    }

    public async ValueTask<ContextBundleV1> BuildAsync(
        ContextRequestV1 request,
        AuthorizedContextScopeV1 authorizedScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authorizedScope);

        if (authorizedScope.AccountId == Guid.Empty
            || authorizedScope.ConversationId == Guid.Empty
            || request.AccountId != authorizedScope.AccountId
            || request.ConversationId != authorizedScope.ConversationId)
        {
            throw new UnauthorizedAccessException("The requested conversation is outside the authorized context scope.");
        }

        var limit = NormalizeLimit(request.RecentMessageLimit);
        var messageRequest = new MessageQueryRequestV1
        {
            AccountId = authorizedScope.AccountId,
            ConversationId = authorizedScope.ConversationId,
            Limit = limit,
            AnchorMessageId = request.AnchorMessageId
        };

        var messageSnapshot = await _messageQuery
            .GetRecentMessagesAsync(messageRequest, cancellationToken)
            .ConfigureAwait(false);
        ValidateMessageSnapshotScope(messageSnapshot, authorizedScope);

        var memoryRequest = new MemoryContextReadRequestV1
        {
            AccountId = authorizedScope.AccountId,
            ConversationId = authorizedScope.ConversationId,
            IdentityNamespace = messageSnapshot.IdentityNamespace ?? string.Empty,
            PerspectiveState = messageSnapshot.PerspectiveState,
            CalibrationVersion = messageSnapshot.CalibrationVersion
        };
        var memorySnapshot = await _memoryReader
            .ReadAsync(memoryRequest, cancellationToken)
            .ConfigureAwait(false);
        ValidateMemorySnapshotScope(memorySnapshot, authorizedScope);

        var messages = messageSnapshot.Messages ?? Array.Empty<MessageQueryItemV1>();
        var hasExcessMessages = messages.Count > limit;
        var recentMessages = messages
            .TakeLast(limit)
            .Select(ToContextMessage)
            .ToArray();

        var omissions = new HashSet<ContextOmissionCodeV1>();
        if (recentMessages.Length == 0)
        {
            omissions.Add(ContextOmissionCodeV1.NoRecentMessages);
        }

        if (request.AnchorMessageId is not null && !messageSnapshot.AnchorMessageFound)
        {
            omissions.Add(ContextOmissionCodeV1.AnchorMessageUnavailable);
        }

        if (messageSnapshot.HasMoreMessages || hasExcessMessages || request.RecentMessageLimit > MaximumRecentMessageLimit)
        {
            omissions.Add(ContextOmissionCodeV1.RecentMessagesTruncated);
        }

        switch (messageSnapshot.HistoryCoverage)
        {
            case MessageHistoryCoverageV1.Unknown:
                omissions.Add(ContextOmissionCodeV1.HistoryCoverageUnknown);
                break;
            case MessageHistoryCoverageV1.Partial:
                omissions.Add(ContextOmissionCodeV1.HistoryCoveragePartial);
                break;
        }

        var perspectiveConfirmed = messageSnapshot.PerspectiveState == PerspectiveStateV1.Confirmed;
        var profileStatus = perspectiveConfirmed
            ? memorySnapshot.ProfileStatus
            : ProfileStatusV1.WaitingForConfirmation;
        var mayExposePerspectiveBoundData = perspectiveConfirmed
            && profileStatus is (ProfileStatusV1.Accumulating or ProfileStatusV1.Consolidating or ProfileStatusV1.Current or ProfileStatusV1.Failed);

        IReadOnlyList<ParticipantProfileSnapshotV1> participantProfiles = Array.Empty<ParticipantProfileSnapshotV1>();
        IReadOnlyList<MemoryItemV1> memoryItems = Array.Empty<MemoryItemV1>();

        if (!perspectiveConfirmed)
        {
            omissions.Add(ContextOmissionCodeV1.PerspectiveNotConfirmed);
            if (request.IncludeMemory)
            {
                // User-entered memories are independent of sender calibration. Message-derived
                // memories and participant profiles are deliberately unavailable until confirmed.
                memoryItems = FilterMemoryItems(
                    memorySnapshot.MemoryItems,
                    authorizedScope,
                    perspectiveConfirmed: false,
                    messageSnapshot.CalibrationVersion,
                    messageSnapshot.IdentityNamespace);
            }
        }
        else if (request.IncludeMemory)
        {
            participantProfiles = mayExposePerspectiveBoundData
                ? FilterProfiles(
                    memorySnapshot.ParticipantProfiles,
                    authorizedScope,
                    messageSnapshot.CalibrationVersion,
                    messageSnapshot.IdentityNamespace)
                : Array.Empty<ParticipantProfileSnapshotV1>();
            memoryItems = FilterMemoryItems(
                memorySnapshot.MemoryItems,
                authorizedScope,
                mayExposePerspectiveBoundData,
                messageSnapshot.CalibrationVersion,
                messageSnapshot.IdentityNamespace);

            if (!mayExposePerspectiveBoundData
                || (profileStatus == ProfileStatusV1.Failed
                    && participantProfiles.Count == 0
                    && !memoryItems.Any(item => item.Origin == MemoryOriginV1.MessageExtracted)))
            {
                omissions.Add(ContextOmissionCodeV1.PerspectiveBoundDataUnavailable);
            }
        }

        var budgeted = ApplyBudget(recentMessages, participantProfiles, memoryItems, omissions);
        var revision = ComputeContextRevision(
            authorizedScope,
            messageSnapshot,
            memorySnapshot,
            request,
            limit);

        return new ContextBundleV1
        {
            AccountId = authorizedScope.AccountId,
            ConversationId = authorizedScope.ConversationId,
            ContextRevision = revision,
            PerspectiveState = messageSnapshot.PerspectiveState,
            CalibrationVersion = messageSnapshot.CalibrationVersion,
            ProfileStatus = profileStatus,
            UnprofiledTextCharacters = perspectiveConfirmed ? Math.Max(0, memorySnapshot.UnprofiledTextCharacters) : 0,
            ConsolidationThresholdCharacters = DefaultConsolidationThresholdCharacters,
            ConsolidationSourceMessageCursor = perspectiveConfirmed ? memorySnapshot.ConsolidationSourceMessageCursor : null,
            RecentMessages = budgeted.RecentMessages,
            ParticipantProfiles = budgeted.ParticipantProfiles,
            MemoryItems = budgeted.MemoryItems,
            Omissions = omissions.OrderBy(code => code).Select(code => new ContextOmissionV1(code)).ToArray(),
            GeneratedAt = _timeProvider.GetUtcNow()
        };
    }

    private static int NormalizeLimit(int requestedLimit)
    {
        if (requestedLimit <= 0)
        {
            return DefaultRecentMessageLimit;
        }

        return Math.Min(requestedLimit, MaximumRecentMessageLimit);
    }

    private static ContextMessageV1 ToContextMessage(MessageQueryItemV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.MessageId == Guid.Empty)
        {
            throw new InvalidOperationException("M2 returned a message without an internal message ID.");
        }

        return new ContextMessageV1
        {
            MessageId = message.MessageId,
            Text = message.Text,
            MessageType = message.MessageType,
            OccurredAt = message.OccurredAt?.ToUniversalTime(),
            Direction = message.Direction,
            DirectionEvidence = message.DirectionEvidence
        };
    }

    private static IReadOnlyList<ParticipantProfileSnapshotV1> FilterProfiles(
        IReadOnlyList<ParticipantProfileSnapshotV1> profiles,
        AuthorizedContextScopeV1 scope,
        long calibrationVersion,
        string? identityNamespace)
    {
        if (profiles is null)
        {
            return Array.Empty<ParticipantProfileSnapshotV1>();
        }

        return profiles
            .Where(profile => profile is not null)
            .Where(profile => profile.AccountId == scope.AccountId
                && profile.ConversationId == scope.ConversationId
                && profile.SubjectType is (ParticipantSubjectTypeV1.WechatAccount or ParticipantSubjectTypeV1.Contact)
                && profile.SubjectId != Guid.Empty
                && (profile.SubjectType != ParticipantSubjectTypeV1.WechatAccount || profile.SubjectId == scope.AccountId)
                && profile.CalibrationVersion == calibrationVersion
                && string.Equals(profile.IdentityNamespace, identityNamespace, StringComparison.Ordinal)
                && profile.Status == ProfileStatusV1.Current
                && !string.IsNullOrWhiteSpace(profile.Summary)
                && profile.EvidenceMessageIds is { Count: > 0 })
            .GroupBy(profile => (profile.SubjectType, profile.SubjectId))
            .Select(group => group.OrderByDescending(profile => profile.ProfileRevision).First())
            .OrderBy(profile => profile.SubjectType)
            .ThenBy(profile => profile.SubjectId)
            .ToArray();
    }

    private static IReadOnlyList<MemoryItemV1> FilterMemoryItems(
        IReadOnlyList<MemoryItemV1> memoryItems,
        AuthorizedContextScopeV1 scope,
        bool perspectiveConfirmed,
        long calibrationVersion,
        string? identityNamespace)
    {
        if (memoryItems is null)
        {
            return Array.Empty<MemoryItemV1>();
        }

        return memoryItems
            .Where(item => item is not null)
            .GroupBy(item => item.MemoryId)
            .Select(group => group.OrderByDescending(item => item.Version).First())
            .Where(item => item is not null
                && item.MemoryId != Guid.Empty
                && item.AccountId == scope.AccountId
                && item.SubjectType is (ParticipantSubjectTypeV1.WechatAccount or ParticipantSubjectTypeV1.Contact)
                && item.SubjectId != Guid.Empty
                && (item.SubjectType != ParticipantSubjectTypeV1.WechatAccount || item.SubjectId == scope.AccountId)
                && item.Status == MemoryItemStatusV1.Active
                && !string.IsNullOrWhiteSpace(item.Text)
                && IsMemoryInConversationScope(item, scope.ConversationId)
                && IsMemoryAllowed(item, perspectiveConfirmed, calibrationVersion, identityNamespace))
            .OrderByDescending(item => item.Origin == MemoryOriginV1.UserEntered)
            .ThenByDescending(item => item.UpdatedAt)
            .ToArray();
    }

    private static bool IsMemoryInConversationScope(MemoryItemV1 item, Guid conversationId)
    {
        if (item.ConversationId == conversationId)
        {
            return true;
        }

        // Only account-subject manual notes may be account-wide. Contact memories must be
        // explicitly tied to this conversation so a same-account contact cannot leak across.
        return item.ConversationId is null
            && item.SubjectType == ParticipantSubjectTypeV1.WechatAccount
            && item.Origin == MemoryOriginV1.UserEntered;
    }

    private static bool IsMemoryAllowed(
        MemoryItemV1 item,
        bool perspectiveConfirmed,
        long calibrationVersion,
        string? identityNamespace)
    {
        if (item.Origin == MemoryOriginV1.UserEntered)
        {
            return true;
        }

        return perspectiveConfirmed
            && item.Origin == MemoryOriginV1.MessageExtracted
            && item.ConversationId is not null
            && !string.IsNullOrWhiteSpace(identityNamespace)
            && string.Equals(item.IdentityNamespace, identityNamespace, StringComparison.Ordinal)
            && item.CalibrationVersion == calibrationVersion
            && !string.IsNullOrWhiteSpace(item.SourceMessageCursor)
            && item.SourceMessageIds is { Count: > 0 };
    }

    private BudgetedContext ApplyBudget(
        IReadOnlyList<ContextMessageV1> messages,
        IReadOnlyList<ParticipantProfileSnapshotV1> profiles,
        IReadOnlyList<MemoryItemV1> memoryItems,
        ISet<ContextOmissionCodeV1> omissions)
    {
        var remaining = _options.MaximumTotalTextCodePoints;
        var budgetedMessages = new List<ContextMessageV1>(messages.Count);

        // Keep the newest chat first when budget runs out, then restore chronological order.
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (string.IsNullOrEmpty(message.Text))
            {
                budgetedMessages.Add(message);
                continue;
            }

            if (remaining == 0)
            {
                omissions.Add(ContextOmissionCodeV1.RecentMessagesOmitted);
                continue;
            }

            var clipped = TakeTextPrefix(message.Text, remaining);
            budgetedMessages.Add(message with { Text = clipped.Text });
            remaining -= clipped.CodePoints;
            if (clipped.WasTruncated)
            {
                omissions.Add(ContextOmissionCodeV1.RecentMessageTextTruncated);
            }
        }

        budgetedMessages.Reverse();

        var budgetedMemoryItems = new List<MemoryItemV1>(Math.Min(memoryItems.Count, _options.MaximumMemoryItems));
        for (var index = 0; index < memoryItems.Count; index++)
        {
            if (index >= _options.MaximumMemoryItems)
            {
                omissions.Add(ContextOmissionCodeV1.MemoryItemsTruncated);
                break;
            }

            var item = memoryItems[index];
            if (remaining == 0)
            {
                omissions.Add(ContextOmissionCodeV1.MemoryItemsOmitted);
                break;
            }

            var clipped = TakeTextPrefix(item.Text, remaining);
            var sourceMessageIds = item.SourceMessageIds ?? Array.Empty<Guid>();
            var evidenceIds = sourceMessageIds.Take(_options.MaximumMemoryEvidenceIdsPerItem).ToArray();
            if (clipped.WasTruncated)
            {
                omissions.Add(ContextOmissionCodeV1.MemoryItemTextTruncated);
            }

            if (evidenceIds.Length < sourceMessageIds.Count)
            {
                omissions.Add(ContextOmissionCodeV1.MemoryEvidenceTruncated);
            }

            budgetedMemoryItems.Add(item with
            {
                Text = clipped.Text,
                SourceMessageIds = evidenceIds
            });
            remaining -= clipped.CodePoints;
        }

        var budgetedProfiles = new List<ParticipantProfileSnapshotV1>(profiles.Count);
        foreach (var profile in profiles)
        {
            if (remaining == 0)
            {
                omissions.Add(ContextOmissionCodeV1.ParticipantProfilesOmitted);
                break;
            }

            var clipped = TakeTextPrefix(profile.Summary, remaining);
            var evidenceIds = profile.EvidenceMessageIds.Take(_options.MaximumProfileEvidenceIds).ToArray();
            if (clipped.WasTruncated)
            {
                omissions.Add(ContextOmissionCodeV1.ProfileSummaryTruncated);
            }

            if (evidenceIds.Length < profile.EvidenceMessageIds.Count)
            {
                omissions.Add(ContextOmissionCodeV1.ProfileEvidenceTruncated);
            }

            budgetedProfiles.Add(profile with
            {
                Summary = clipped.Text,
                EvidenceMessageIds = evidenceIds
            });
            remaining -= clipped.CodePoints;
        }

        return new BudgetedContext(
            budgetedMessages,
            budgetedProfiles,
            budgetedMemoryItems);
    }

    private static (string Text, int CodePoints, bool WasTruncated) TakeTextPrefix(string text, int maximumCodePoints)
    {
        var builder = new StringBuilder(Math.Min(text.Length, maximumCodePoints));
        var codePoints = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (codePoints >= maximumCodePoints)
            {
                return (builder.ToString(), codePoints, true);
            }

            builder.Append(rune.ToString());
            codePoints++;
        }

        return (builder.ToString(), codePoints, false);
    }

    private sealed record BudgetedContext(
        IReadOnlyList<ContextMessageV1> RecentMessages,
        IReadOnlyList<ParticipantProfileSnapshotV1> ParticipantProfiles,
        IReadOnlyList<MemoryItemV1> MemoryItems);

    private string ComputeContextRevision(
        AuthorizedContextScopeV1 scope,
        MessageQuerySnapshotV1 messages,
        MemoryContextSnapshotV1 memory,
        ContextRequestV1 request,
        int effectiveLimit)
    {
        if (string.IsNullOrWhiteSpace(messages.MessageSetRevision))
        {
            throw new InvalidOperationException("M2 must provide a scoped message-set revision.");
        }

        if (string.IsNullOrWhiteSpace(memory.Revision))
        {
            throw new InvalidOperationException("M3 memory context reader must provide a scoped revision.");
        }

        var canonical = string.Join("\n",
            "context-v1",
            scope.AccountId.ToString("N"),
            scope.ConversationId.ToString("N"),
            messages.MessageSetRevision,
            messages.IdentityNamespace ?? string.Empty,
            messages.PerspectiveState.ToString(),
            messages.CalibrationVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            messages.HistoryCoverage.ToString(),
            messages.HasMoreMessages.ToString(System.Globalization.CultureInfo.InvariantCulture),
            messages.AnchorMessageFound.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.AnchorMessageId?.ToString("N") ?? string.Empty,
            effectiveLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.IncludeMemory.ToString(System.Globalization.CultureInfo.InvariantCulture),
            memory.Revision,
            _options.MaximumTotalTextCodePoints.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _options.MaximumMemoryItems.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _options.MaximumMemoryEvidenceIdsPerItem.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _options.MaximumProfileEvidenceIds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static void ValidateMessageSnapshotScope(
        MessageQuerySnapshotV1 snapshot,
        AuthorizedContextScopeV1 scope)
    {
        if (snapshot is null
            || snapshot.AccountId != scope.AccountId
            || snapshot.ConversationId != scope.ConversationId)
        {
            throw new InvalidOperationException("M2 returned a message query result outside the requested scope.");
        }
    }

    private static void ValidateMemorySnapshotScope(
        MemoryContextSnapshotV1 snapshot,
        AuthorizedContextScopeV1 scope)
    {
        if (snapshot is null
            || snapshot.AccountId != scope.AccountId
            || snapshot.ConversationId != scope.ConversationId)
        {
            throw new InvalidOperationException("M3 memory reader returned data outside the requested scope.");
        }
    }
}
