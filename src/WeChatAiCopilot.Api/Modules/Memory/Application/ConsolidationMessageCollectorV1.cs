using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using WeChatAiCopilot.Api.Modules.Messages.Contracts;

namespace WeChatAiCopilot.Api.Modules.Memory.Application;

public interface IConsolidationMessageSourceV1
{
    ValueTask<ConsolidationMessagePageV1> GetPageAsync(
        MessageQueryRequestV1 request,
        CancellationToken cancellationToken = default);
}

public sealed class M2ConsolidationMessageSourceV1(IMessageQueryV1 messageQuery) : IConsolidationMessageSourceV1
{
    public async ValueTask<ConsolidationMessagePageV1> GetPageAsync(
        MessageQueryRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await messageQuery.GetRecentMessagesAsync(request, cancellationToken);
        return new ConsolidationMessagePageV1
        {
            AccountId = snapshot.AccountId,
            ConversationId = snapshot.ConversationId,
            ConversationType = snapshot.ConversationType ?? string.Empty,
            IdentityNamespace = snapshot.IdentityNamespace ?? string.Empty,
            MessageSetRevision = snapshot.MessageSetRevision,
            PerspectiveState = snapshot.PerspectiveState,
            CalibrationVersion = snapshot.CalibrationVersion,
            HasMoreMessages = snapshot.HasMoreMessages,
            AnchorMessageFound = snapshot.AnchorMessageFound,
            Messages = snapshot.Messages
        };
    }
}

/// <summary>
/// Adapter-neutral page used by M3. The M2 adapter supplies these fields from its query
/// snapshot, leaving the M2 interface and implementation owned by M2.
/// </summary>
public sealed record ConsolidationMessagePageV1
{
    public Guid AccountId { get; init; }
    public Guid ConversationId { get; init; }
    public string ConversationType { get; init; } = string.Empty;
    public string IdentityNamespace { get; init; } = string.Empty;
    public string MessageSetRevision { get; init; } = string.Empty;
    public PerspectiveStateV1 PerspectiveState { get; init; }
    public long CalibrationVersion { get; init; }
    public bool HasMoreMessages { get; init; }
    public bool AnchorMessageFound { get; init; } = true;
    public IReadOnlyList<MessageQueryItemV1> Messages { get; init; } = Array.Empty<MessageQueryItemV1>();
}

public enum ConsolidationCaptureFailureCodeV1
{
    None,
    InvalidScope,
    ScopeMismatch,
    PerspectiveNotConfirmed,
    CalibrationVersionMismatch,
    UnsupportedConversationType,
    IdentityNamespaceUnavailable,
    MessageSetRevisionUnavailable,
    MessageSetChangedDuringPagination,
    AnchorUnavailable,
    PaginationMadeNoProgress,
    PageLimitExceeded,
    InvalidMessage,
    InvalidMessageCursor,
    CursorNotInNewerRange
}

public sealed record ConsolidationCaptureV1
{
    public Guid AccountId { get; init; }
    public Guid ConversationId { get; init; }
    public string ConversationType { get; init; } = "Direct";
    public string IdentityNamespace { get; init; } = string.Empty;
    public long CalibrationVersion { get; init; }
    public string MessageSetRevision { get; init; } = string.Empty;
    public string? RangeStartMessageCursor { get; init; }
    public string? HighWaterMessageCursor { get; init; }
    public IReadOnlyList<MessageQueryItemV1> AllMessages { get; init; } = Array.Empty<MessageQueryItemV1>();
    public IReadOnlyList<MemoryConsolidationInputMessageV1> EligibleMessages { get; init; } = Array.Empty<MemoryConsolidationInputMessageV1>();
    public int EligibleTextCodePoints { get; init; }
}

public sealed record ConsolidationCaptureResultV1(
    ConsolidationCaptureV1? Capture,
    ConsolidationCaptureFailureCodeV1 FailureCode)
{
    public bool Succeeded => Capture is not null && FailureCode == ConsolidationCaptureFailureCodeV1.None;

    public static ConsolidationCaptureResultV1 Success(ConsolidationCaptureV1 capture) =>
        new(capture, ConsolidationCaptureFailureCodeV1.None);

    public static ConsolidationCaptureResultV1 Failure(ConsolidationCaptureFailureCodeV1 code) =>
        new(null, code);
}

public sealed record ConsolidationHeadReadResultV1(
    ConsolidationMessagePageV1? Page,
    ConsolidationCaptureFailureCodeV1 FailureCode)
{
    public bool Succeeded => Page is not null && FailureCode == ConsolidationCaptureFailureCodeV1.None;
}

/// <summary>
/// Reads bounded stable pages using M2's older-anchor contract. The page set must retain
/// one scope, one confirmed calibration, one identity namespace, and one message-set revision.
/// </summary>
public sealed class ConsolidationMessageCollectorV1
{
    public const int PageSize = 100;
    public const int MaximumPages = 100;

    private readonly IConsolidationMessageSourceV1 _source;

    public ConsolidationMessageCollectorV1(IConsolidationMessageSourceV1 source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public async ValueTask<ConsolidationHeadReadResultV1> ReadHeadAsync(
        Guid accountId,
        Guid conversationId,
        long? expectedCalibrationVersion = null,
        CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty || conversationId == Guid.Empty)
        {
            return new ConsolidationHeadReadResultV1(null, ConsolidationCaptureFailureCodeV1.InvalidScope);
        }

        var page = await _source.GetPageAsync(new MessageQueryRequestV1
        {
            AccountId = accountId,
            ConversationId = conversationId,
            Limit = 1
        }, cancellationToken);
        var failure = ValidatePage(
            page,
            firstPage: null,
            accountId,
            conversationId,
            expectedCalibrationVersion,
            expectedIdentityNamespace: null,
            requestedAnchor: null);
        if (failure != ConsolidationCaptureFailureCodeV1.None)
        {
            return new ConsolidationHeadReadResultV1(null, failure);
        }

        if (page.Messages.Count > 1
            || page.Messages.Any(message => message.MessageId == Guid.Empty || (message.MessageType == "Text" && message.Text is null)))
        {
            return new ConsolidationHeadReadResultV1(null, ConsolidationCaptureFailureCodeV1.InvalidMessage);
        }

        return new ConsolidationHeadReadResultV1(page, ConsolidationCaptureFailureCodeV1.None);
    }

    public async ValueTask<ConsolidationCaptureResultV1> CaptureAllAsync(
        Guid accountId,
        Guid conversationId,
        long? expectedCalibrationVersion = null,
        string? expectedIdentityNamespace = null,
        CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty || conversationId == Guid.Empty)
        {
            return ConsolidationCaptureResultV1.Failure(ConsolidationCaptureFailureCodeV1.InvalidScope);
        }

        var pagesResult = await ReadPagesAsync(
            accountId,
            conversationId,
            stopAtMessageId: null,
            expectedCalibrationVersion,
            expectedIdentityNamespace,
            cancellationToken);
        if (pagesResult.FailureCode != ConsolidationCaptureFailureCodeV1.None)
        {
            return ConsolidationCaptureResultV1.Failure(pagesResult.FailureCode);
        }

        var pagesNewestFirst = pagesResult.Pages;
        var messages = FlattenChronologically(pagesNewestFirst);
        var highWaterCursor = pagesNewestFirst.Count == 0 || pagesNewestFirst[0].Messages.Count == 0
            ? null
            : ToCursor(pagesNewestFirst[0].Messages[^1].MessageId);
        return ConsolidationCaptureResultV1.Success(CreateCapture(
            pagesResult.Metadata!,
            messages,
            rangeStartCursor: null,
            highWaterCursor));
    }

    public async ValueTask<ConsolidationCaptureResultV1> CaptureAfterCursorThroughEventAsync(
        Guid accountId,
        Guid conversationId,
        string? checkpointCursor,
        string eventMessageCursor,
        long expectedCalibrationVersion,
        string expectedIdentityNamespace,
        CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty || conversationId == Guid.Empty)
        {
            return ConsolidationCaptureResultV1.Failure(ConsolidationCaptureFailureCodeV1.InvalidScope);
        }

        if (!Guid.TryParse(eventMessageCursor, out var eventMessageId))
        {
            return ConsolidationCaptureResultV1.Failure(ConsolidationCaptureFailureCodeV1.InvalidMessageCursor);
        }

        Guid? checkpointMessageId = null;
        if (!string.IsNullOrWhiteSpace(checkpointCursor))
        {
            if (!Guid.TryParse(checkpointCursor, out var parsedCheckpoint))
            {
                return ConsolidationCaptureResultV1.Failure(ConsolidationCaptureFailureCodeV1.InvalidMessageCursor);
            }

            checkpointMessageId = parsedCheckpoint;
        }

        var pagesResult = await ReadPagesAsync(
            accountId,
            conversationId,
            checkpointMessageId,
            expectedCalibrationVersion,
            expectedIdentityNamespace,
            cancellationToken);
        if (pagesResult.FailureCode != ConsolidationCaptureFailureCodeV1.None)
        {
            return ConsolidationCaptureResultV1.Failure(pagesResult.FailureCode);
        }

        var scannedMessages = FlattenChronologically(pagesResult.Pages);
        var checkpointIndex = checkpointMessageId is null
            ? -1
            : scannedMessages.ToList().FindIndex(message => message.MessageId == checkpointMessageId.Value);
        if (checkpointMessageId is not null && checkpointIndex < 0)
        {
            return ConsolidationCaptureResultV1.Failure(ConsolidationCaptureFailureCodeV1.AnchorUnavailable);
        }

        var eventIndex = scannedMessages.ToList().FindIndex(message => message.MessageId == eventMessageId);
        if (eventIndex <= checkpointIndex)
        {
            return ConsolidationCaptureResultV1.Failure(ConsolidationCaptureFailureCodeV1.CursorNotInNewerRange);
        }

        var throughEvent = scannedMessages
            .Skip(checkpointIndex + 1)
            .Take(eventIndex - checkpointIndex)
            .ToArray();
        var capture = CreateCapture(
            pagesResult.Metadata!,
            throughEvent,
            checkpointCursor,
            eventMessageCursor);
        return ConsolidationCaptureResultV1.Success(capture);
    }

    private async ValueTask<PagesReadResult> ReadPagesAsync(
        Guid accountId,
        Guid conversationId,
        Guid? stopAtMessageId,
        long? expectedCalibrationVersion,
        string? expectedIdentityNamespace,
        CancellationToken cancellationToken)
    {
        var pages = new List<ConsolidationMessagePageV1>();
        var seenMessageIds = new HashSet<Guid>();
        Guid? anchorMessageId = null;
        ConsolidationMessagePageV1? metadata = null;

        for (var pageIndex = 0; pageIndex < MaximumPages; pageIndex++)
        {
            var page = await _source.GetPageAsync(new MessageQueryRequestV1
            {
                AccountId = accountId,
                ConversationId = conversationId,
                Limit = PageSize,
                AnchorMessageId = anchorMessageId
            }, cancellationToken);

            var pageFailure = ValidatePage(
                page,
                metadata,
                accountId,
                conversationId,
                expectedCalibrationVersion,
                expectedIdentityNamespace,
                anchorMessageId);
            if (pageFailure != ConsolidationCaptureFailureCodeV1.None)
            {
                return PagesReadResult.Failure(pageFailure);
            }

            metadata ??= page;
            var pageMessages = page.Messages;
            if (pageMessages.Select(message => message.MessageId).Distinct().Count() != pageMessages.Count)
            {
                return PagesReadResult.Failure(ConsolidationCaptureFailureCodeV1.InvalidMessage);
            }

            var foundAnchorInPage = anchorMessageId is not null
                && pageMessages.Any(message => message.MessageId == anchorMessageId.Value);
            if (anchorMessageId is not null && !foundAnchorInPage)
            {
                return PagesReadResult.Failure(ConsolidationCaptureFailureCodeV1.AnchorUnavailable);
            }

            foreach (var message in pageMessages)
            {
                if (message.MessageId == Guid.Empty || (message.MessageType == "Text" && message.Text is null))
                {
                    return PagesReadResult.Failure(ConsolidationCaptureFailureCodeV1.InvalidMessage);
                }

                if (!seenMessageIds.Add(message.MessageId)
                    && message.MessageId != anchorMessageId)
                {
                    return PagesReadResult.Failure(ConsolidationCaptureFailureCodeV1.InvalidMessage);
                }
            }

            pages.Add(page);
            var foundStopCursor = stopAtMessageId is not null
                && pageMessages.Any(message => message.MessageId == stopAtMessageId.Value);
            if (foundStopCursor)
            {
                return PagesReadResult.Success(metadata, pages);
            }

            if (!page.HasMoreMessages)
            {
                return stopAtMessageId is null
                    ? PagesReadResult.Success(metadata, pages)
                    : PagesReadResult.Failure(ConsolidationCaptureFailureCodeV1.AnchorUnavailable);
            }

            if (pageMessages.Count == 0)
            {
                return PagesReadResult.Failure(ConsolidationCaptureFailureCodeV1.PaginationMadeNoProgress);
            }

            var nextAnchor = pageMessages[0].MessageId;
            if (nextAnchor == anchorMessageId)
            {
                return PagesReadResult.Failure(ConsolidationCaptureFailureCodeV1.PaginationMadeNoProgress);
            }

            anchorMessageId = nextAnchor;
        }

        return PagesReadResult.Failure(ConsolidationCaptureFailureCodeV1.PageLimitExceeded);
    }

    private static ConsolidationCaptureFailureCodeV1 ValidatePage(
        ConsolidationMessagePageV1 page,
        ConsolidationMessagePageV1? firstPage,
        Guid accountId,
        Guid conversationId,
        long? expectedCalibrationVersion,
        string? expectedIdentityNamespace,
        Guid? requestedAnchor)
    {
        if (page.AccountId != accountId || page.ConversationId != conversationId)
        {
            return ConsolidationCaptureFailureCodeV1.ScopeMismatch;
        }

        if (requestedAnchor is not null && !page.AnchorMessageFound)
        {
            return ConsolidationCaptureFailureCodeV1.AnchorUnavailable;
        }

        if (page.PerspectiveState != PerspectiveStateV1.Confirmed)
        {
            return ConsolidationCaptureFailureCodeV1.PerspectiveNotConfirmed;
        }

        if (page.CalibrationVersion <= 0
            || (expectedCalibrationVersion is not null && page.CalibrationVersion != expectedCalibrationVersion))
        {
            return ConsolidationCaptureFailureCodeV1.CalibrationVersionMismatch;
        }

        if (!string.Equals(page.ConversationType, "Direct", StringComparison.Ordinal))
        {
            return ConsolidationCaptureFailureCodeV1.UnsupportedConversationType;
        }

        if (string.IsNullOrWhiteSpace(page.IdentityNamespace)
            || (expectedIdentityNamespace is not null
                && !string.Equals(page.IdentityNamespace, expectedIdentityNamespace, StringComparison.Ordinal)))
        {
            return ConsolidationCaptureFailureCodeV1.IdentityNamespaceUnavailable;
        }

        if (string.IsNullOrWhiteSpace(page.MessageSetRevision))
        {
            return ConsolidationCaptureFailureCodeV1.MessageSetRevisionUnavailable;
        }

        if (page.Messages is null || page.Messages.Count > PageSize)
        {
            return ConsolidationCaptureFailureCodeV1.InvalidMessage;
        }

        if (firstPage is not null
            && (page.CalibrationVersion != firstPage.CalibrationVersion
                || page.PerspectiveState != firstPage.PerspectiveState
                || !string.Equals(page.ConversationType, firstPage.ConversationType, StringComparison.Ordinal)
                || !string.Equals(page.IdentityNamespace, firstPage.IdentityNamespace, StringComparison.Ordinal)))
        {
            return ConsolidationCaptureFailureCodeV1.ScopeMismatch;
        }

        if (firstPage is not null
            && !string.Equals(page.MessageSetRevision, firstPage.MessageSetRevision, StringComparison.Ordinal))
        {
            return ConsolidationCaptureFailureCodeV1.MessageSetChangedDuringPagination;
        }

        return ConsolidationCaptureFailureCodeV1.None;
    }

    private static IReadOnlyList<MessageQueryItemV1> FlattenChronologically(
        IReadOnlyList<ConsolidationMessagePageV1> pagesNewestFirst)
    {
        var messages = new List<MessageQueryItemV1>();
        var seen = new HashSet<Guid>();
        for (var pageIndex = pagesNewestFirst.Count - 1; pageIndex >= 0; pageIndex--)
        {
            foreach (var message in pagesNewestFirst[pageIndex].Messages)
            {
                if (seen.Add(message.MessageId))
                {
                    messages.Add(message);
                }
            }
        }

        return messages;
    }

    private static ConsolidationCaptureV1 CreateCapture(
        ConsolidationMessagePageV1 page,
        IReadOnlyList<MessageQueryItemV1> messages,
        string? rangeStartCursor,
        string? highWaterCursor)
    {
        var eligible = messages
            .Where(message => message.MessageType == "Text"
                && (message.Direction is MessageDirectionV1.Incoming or MessageDirectionV1.Outgoing)
                && message.DirectionEvidence == DirectionEvidenceV1.SourceConfirmed)
            .Select(message => new MemoryConsolidationInputMessageV1(
                message.MessageId,
                MemoryConsolidationWindowV1.NormalizeText(message.Text ?? string.Empty),
                message.OccurredAt?.ToUniversalTime(),
                message.Direction))
            .ToArray();
        var codePoints = eligible.Sum(message => MemoryConsolidationWindowV1.CountCodePoints(message.Text));
        return new ConsolidationCaptureV1
        {
            AccountId = page.AccountId,
            ConversationId = page.ConversationId,
            ConversationType = page.ConversationType,
            IdentityNamespace = page.IdentityNamespace,
            CalibrationVersion = page.CalibrationVersion,
            MessageSetRevision = page.MessageSetRevision,
            RangeStartMessageCursor = rangeStartCursor,
            HighWaterMessageCursor = highWaterCursor,
            AllMessages = messages,
            EligibleMessages = eligible,
            EligibleTextCodePoints = codePoints
        };
    }

    private static string ToCursor(Guid messageId) => messageId.ToString("N");

    private sealed record PagesReadResult(
        ConsolidationMessagePageV1? Metadata,
        IReadOnlyList<ConsolidationMessagePageV1> Pages,
        ConsolidationCaptureFailureCodeV1 FailureCode)
    {
        public static PagesReadResult Success(
            ConsolidationMessagePageV1? metadata,
            IReadOnlyList<ConsolidationMessagePageV1> pages) =>
            new(metadata, pages, ConsolidationCaptureFailureCodeV1.None);

        public static PagesReadResult Failure(ConsolidationCaptureFailureCodeV1 code) =>
            new(null, Array.Empty<ConsolidationMessagePageV1>(), code);
    }
}
