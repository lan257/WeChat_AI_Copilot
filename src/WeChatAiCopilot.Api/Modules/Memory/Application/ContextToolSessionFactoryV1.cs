using System.Text;
using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;
using WeChatAiCopilot.Api.Modules.Messages.Contracts;

namespace WeChatAiCopilot.Api.Modules.Memory.Application;

public sealed class ContextToolSessionFactoryV1 : IContextToolSessionFactoryV1
{
    private readonly IMessageQueryV1 _messageQuery;
    private readonly IContextServiceV1 _contextService;
    private readonly ContextToolOptionsV1 _options;

    public ContextToolSessionFactoryV1(
        IMessageQueryV1 messageQuery,
        IContextServiceV1 contextService,
        ContextToolOptionsV1? options = null)
    {
        _messageQuery = messageQuery ?? throw new ArgumentNullException(nameof(messageQuery));
        _contextService = contextService ?? throw new ArgumentNullException(nameof(contextService));
        _options = options ?? new ContextToolOptionsV1();
        ValidateOptions(_options);
    }

    public IContextToolSessionV1 Bind(AuthorizedContextScopeV1 authorizedScope)
    {
        ArgumentNullException.ThrowIfNull(authorizedScope);
        if (authorizedScope.AccountId == Guid.Empty || authorizedScope.ConversationId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("A valid authorized context scope is required.");
        }

        return new ContextToolSessionV1(_messageQuery, _contextService, authorizedScope, _options);
    }

    private static void ValidateOptions(ContextToolOptionsV1 options)
    {
        if (options.MaximumPages is < 1 or > ContextToolOptionsV1.HardMaximumPages
            || options.PageSize is < 2 or > ContextToolOptionsV1.HardMaximumPageSize
            || options.MaximumSearchResults is < 1 or > ContextToolOptionsV1.HardMaximumSearchResults
            || options.MaximumEvidenceIds is < 1 or > ContextToolOptionsV1.HardMaximumEvidenceIds
            || options.MaximumTextCodePointsPerItem is < 1 or > ContextToolOptionsV1.HardMaximumTextCodePointsPerItem
            || options.MaximumSearchTextCodePointsPerMessage is < 1 or > ContextToolOptionsV1.HardMaximumSearchTextCodePointsPerMessage
            || options.MaximumQueryCodePoints is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Context tool limits exceed the supported hard bounds.");
        }
    }

    private sealed class ContextToolSessionV1 : IContextToolSessionV1
    {
        private readonly IMessageQueryV1 _messageQuery;
        private readonly IContextServiceV1 _contextService;
        private readonly AuthorizedContextScopeV1 _scope;
        private readonly ContextToolOptionsV1 _options;

        public ContextToolSessionV1(
            IMessageQueryV1 messageQuery,
            IContextServiceV1 contextService,
            AuthorizedContextScopeV1 scope,
            ContextToolOptionsV1 options)
        {
            _messageQuery = messageQuery;
            _contextService = contextService;
            _scope = scope;
            _options = options;
        }

        public async ValueTask<ContextToolContextResultV1> GetContextAsync(
            ContextToolRequestV1 request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            var messageRequest = new MessageQueryRequestV1
            {
                AccountId = _scope.AccountId,
                ConversationId = _scope.ConversationId,
                Limit = NormalizeContextMessageLimit(request.RecentMessageLimit),
                AnchorMessageId = request.AnchorMessageId
            };
            var before = await ReadContextSnapshotAsync(messageRequest, cancellationToken).ConfigureAwait(false);

            var context = await _contextService.BuildAsync(
                new ContextRequestV1
                {
                    AccountId = _scope.AccountId,
                    ConversationId = _scope.ConversationId,
                    RequestPurpose = "ContextTool",
                    RecentMessageLimit = request.RecentMessageLimit,
                    AnchorMessageId = request.AnchorMessageId,
                    IncludeMemory = request.IncludeMemory
                },
                _scope,
                cancellationToken).ConfigureAwait(false);

            if (context.AccountId != _scope.AccountId || context.ConversationId != _scope.ConversationId)
            {
                throw new InvalidOperationException("M3 context builder returned data outside its bound scope.");
            }

            var after = await ReadContextSnapshotAsync(messageRequest, cancellationToken).ConfigureAwait(false);
            var beforeVersion = GetVersion(before);
            var afterVersion = GetVersion(after);
            if (beforeVersion != afterVersion
                || before.HasMoreMessages != after.HasMoreMessages
                || before.AnchorMessageFound != after.AnchorMessageFound)
            {
                throw new ContextRevisionChangedException(
                    before.MessageSetRevision,
                    after.MessageSetRevision,
                    GetContextChangeReason(before, after));
            }

            return ToToolContext(context, before.MessageSetRevision);
        }

        public async ValueTask<HistorySearchResultV1> SearchHistoryAsync(
            HistorySearchRequestV1 request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ValidateSearchRequest(request);
            cancellationToken.ThrowIfCancellationRequested();

            var effectiveLimit = Math.Clamp(request.Limit, 1, _options.MaximumSearchResults);
            var anchorId = request.AnchorMessageId;
            var excludeAnchor = request.ExcludeAnchor;
            var expectedVersion = request.ExpectedMessageSetRevision;
            SearchVersion? firstVersion = null;
            MessageHistoryCoverageV1 historyCoverage = MessageHistoryCoverageV1.Unknown;
            var hits = new List<HistorySearchHitV1>(effectiveLimit);
            var scannedMessageIds = new HashSet<Guid>();
            Guid? oldestReturnedHitId = null;
            var pagesRead = 0;
            var messagesScanned = 0;
            var hasMoreMessages = false;
            Guid? nextAnchorId = null;
            var textTruncated = false;
            var searchTextTruncated = false;
            var paginationStalled = false;

            while (pagesRead < _options.MaximumPages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = await GetPageAsync(anchorId, cancellationToken).ConfigureAwait(false);
                pagesRead++;

                var validation = ValidatePage(snapshot, firstVersion, expectedVersion, anchorId);
                if (validation.Status == PageStatus.RevisionChanged)
                {
                    return RevisionChangedSearchResult(firstVersion, validation, historyCoverage, pagesRead, messagesScanned);
                }

                if (validation.Status == PageStatus.AnchorNotFound)
                {
                    return new HistorySearchResultV1
                    {
                        Status = HistorySearchStatusV1.AnchorNotFound,
                        MessageSetRevision = firstVersion?.MessageSetRevision ?? snapshot.MessageSetRevision,
                        PerspectiveState = firstVersion?.PerspectiveState ?? snapshot.PerspectiveState,
                        CalibrationVersion = firstVersion?.CalibrationVersion ?? snapshot.CalibrationVersion,
                        HistoryCoverage = snapshot.HistoryCoverage,
                        PagesRead = pagesRead,
                        MessagesScanned = messagesScanned
                    };
                }

                firstVersion ??= GetVersion(snapshot);
                expectedVersion ??= firstVersion.MessageSetRevision;
                historyCoverage = snapshot.HistoryCoverage;
                var page = snapshot.Messages ?? Array.Empty<MessageQueryItemV1>();
                var pageExceedsConfiguredSize = page.Count > _options.PageSize;
                if (pageExceedsConfiguredSize)
                {
                    page = page.TakeLast(_options.PageSize).ToArray();
                }

                var pageIds = page.Select(message => message.MessageId).ToHashSet();
                if (anchorId is { } requestedAnchor && !pageIds.Contains(requestedAnchor))
                {
                    return new HistorySearchResultV1
                    {
                        Status = HistorySearchStatusV1.AnchorNotFound,
                        MessageSetRevision = firstVersion.MessageSetRevision,
                        PerspectiveState = firstVersion.PerspectiveState,
                        CalibrationVersion = firstVersion.CalibrationVersion,
                        HistoryCoverage = historyCoverage,
                        PagesRead = pagesRead,
                        MessagesScanned = messagesScanned
                    };
                }

                var foundExtraHit = false;
                var scannedBeforePage = messagesScanned;
                for (var index = page.Count - 1; index >= 0; index--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var message = page[index];
                    if (excludeAnchor && anchorId == message.MessageId)
                    {
                        continue;
                    }

                    if (!scannedMessageIds.Add(message.MessageId))
                    {
                        continue;
                    }

                    messagesScanned++;
                    var match = Matches(message, request);
                    searchTextTruncated |= match.TextTruncated;
                    if (!match.Matches)
                    {
                        continue;
                    }

                    if (hits.Count >= effectiveLimit)
                    {
                        foundExtraHit = true;
                        break;
                    }

                    var hit = ToSearchHit(message);
                    hits.Add(hit.Hit);
                    textTruncated |= hit.IsTextTruncated;
                    oldestReturnedHitId = message.MessageId;
                }

                if (foundExtraHit)
                {
                    return new HistorySearchResultV1
                    {
                        Status = HistorySearchStatusV1.Truncated,
                        MessageSetRevision = firstVersion.MessageSetRevision,
                        PerspectiveState = firstVersion.PerspectiveState,
                        CalibrationVersion = firstVersion.CalibrationVersion,
                        HistoryCoverage = historyCoverage,
                        Hits = hits,
                        PagesRead = pagesRead,
                        MessagesScanned = messagesScanned,
                        SearchTextTruncated = searchTextTruncated,
                        HasMoreMessages = true,
                        NextAnchorMessageId = oldestReturnedHitId,
                        NextPageExcludesAnchor = oldestReturnedHitId is not null
                    };
                }

                var pageHasMoreMessages = snapshot.HasMoreMessages || pageExceedsConfiguredSize;
                if (!pageHasMoreMessages)
                {
                    hasMoreMessages = false;
                    nextAnchorId = null;
                    break;
                }

                if (messagesScanned == scannedBeforePage)
                {
                    paginationStalled = true;
                    nextAnchorId = null;
                    break;
                }

                hasMoreMessages = true;
                var oldestMessageId = page.FirstOrDefault()?.MessageId;
                if (oldestMessageId is null || oldestMessageId == Guid.Empty || page.Count == 0)
                {
                    nextAnchorId = null;
                    paginationStalled = true;
                    break;
                }

                nextAnchorId = oldestMessageId;
                if (pagesRead >= _options.MaximumPages)
                {
                    break;
                }

                anchorId = oldestMessageId;
                excludeAnchor = true;
            }

            var pageLimitReached = hasMoreMessages && pagesRead >= _options.MaximumPages;
            var status = pageLimitReached || paginationStalled || textTruncated || searchTextTruncated
                ? HistorySearchStatusV1.Truncated
                : HistorySearchStatusV1.Complete;
            return new HistorySearchResultV1
            {
                Status = status,
                MessageSetRevision = firstVersion?.MessageSetRevision,
                PerspectiveState = firstVersion?.PerspectiveState,
                CalibrationVersion = firstVersion?.CalibrationVersion,
                HistoryCoverage = historyCoverage,
                Hits = hits,
                PagesRead = pagesRead,
                MessagesScanned = messagesScanned,
                SearchTextTruncated = searchTextTruncated,
                HasMoreMessages = pageLimitReached || paginationStalled,
                NextAnchorMessageId = pageLimitReached ? nextAnchorId : null,
                NextPageExcludesAnchor = pageLimitReached && nextAnchorId is not null
            };
        }

        public async ValueTask<MessageEvidenceResultV1> GetMessageEvidenceAsync(
            MessageEvidenceRequestV1 request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            var idsToRead = new List<Guid>(_options.MaximumEvidenceIds);
            var seenIds = new HashSet<Guid>();
            var sourceIds = request.MessageIds ?? Array.Empty<Guid>();
            var inputEntriesToProcess = Math.Min(sourceIds.Count, _options.MaximumEvidenceIds);
            var truncated = sourceIds.Count > inputEntriesToProcess;
            var omittedCount = sourceIds.Count - inputEntriesToProcess;
            for (var index = 0; index < inputEntriesToProcess; index++)
            {
                var messageId = sourceIds[index];
                if (messageId == Guid.Empty)
                {
                    omittedCount++;
                    continue;
                }

                if (!seenIds.Add(messageId))
                {
                    continue;
                }

                idsToRead.Add(messageId);
            }
            var evidenceById = new Dictionary<Guid, MessageEvidenceItemV1>();
            SearchVersion? firstVersion = null;
            string? observedRevision = null;

            foreach (var messageId in idsToRead)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = await GetPageAsync(messageId, cancellationToken, limit: 1).ConfigureAwait(false);
                var validation = ValidatePage(snapshot, firstVersion, request.ExpectedMessageSetRevision, messageId);
                if (validation.Status == PageStatus.RevisionChanged)
                {
                    return new MessageEvidenceResultV1
                    {
                        Status = MessageEvidenceStatusV1.RevisionChanged,
                        MessageSetRevision = firstVersion?.MessageSetRevision,
                        ObservedMessageSetRevision = validation.ObservedRevision,
                        PerspectiveState = firstVersion?.PerspectiveState,
                        ObservedPerspectiveState = validation.ObservedPerspectiveState,
                        CalibrationVersion = firstVersion?.CalibrationVersion,
                        ObservedCalibrationVersion = validation.ObservedCalibrationVersion,
                        Evidence = Array.Empty<MessageEvidenceItemV1>(),
                        OmittedCount = 0
                    };
                }

                firstVersion ??= GetVersion(snapshot);
                observedRevision ??= snapshot.MessageSetRevision;
                if (validation.Status == PageStatus.AnchorNotFound)
                {
                    omittedCount++;
                    continue;
                }

                var matchingMessage = snapshot.Messages?.FirstOrDefault(message => message.MessageId == messageId);
                if (matchingMessage is null)
                {
                    omittedCount++;
                    continue;
                }

                var converted = ToEvidence(matchingMessage);
                evidenceById.Add(messageId, converted.Item);
                truncated |= converted.IsTextTruncated;
            }

            var status = truncated
                ? MessageEvidenceStatusV1.Truncated
                : omittedCount > 0
                    ? MessageEvidenceStatusV1.Partial
                    : MessageEvidenceStatusV1.Complete;
            return new MessageEvidenceResultV1
            {
                Status = status,
                MessageSetRevision = firstVersion?.MessageSetRevision ?? observedRevision,
                PerspectiveState = firstVersion?.PerspectiveState,
                CalibrationVersion = firstVersion?.CalibrationVersion,
                Evidence = idsToRead.Where(evidenceById.ContainsKey).Select(messageId => evidenceById[messageId]).ToArray(),
                OmittedCount = omittedCount
            };
        }

        private async ValueTask<MessageQuerySnapshotV1> GetPageAsync(
            Guid? anchorMessageId,
            CancellationToken cancellationToken,
            int? limit = null) =>
            await _messageQuery.GetRecentMessagesAsync(
                new MessageQueryRequestV1
                {
                    AccountId = _scope.AccountId,
                    ConversationId = _scope.ConversationId,
                    Limit = limit ?? _options.PageSize,
                    AnchorMessageId = anchorMessageId
                },
                cancellationToken).ConfigureAwait(false);

        private async ValueTask<MessageQuerySnapshotV1> ReadContextSnapshotAsync(
            MessageQueryRequestV1 request,
            CancellationToken cancellationToken)
        {
            var snapshot = await _messageQuery
                .GetRecentMessagesAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (snapshot is null
                || snapshot.AccountId != _scope.AccountId
                || snapshot.ConversationId != _scope.ConversationId)
            {
                throw new InvalidOperationException("M2 returned a context revision outside the bound scope.");
            }

            if (string.IsNullOrWhiteSpace(snapshot.MessageSetRevision))
            {
                throw new InvalidOperationException("M2 did not provide a context message set revision.");
            }

            return snapshot;
        }

        private static int NormalizeContextMessageLimit(int requestedLimit) => requestedLimit <= 0
            ? ContextBuilderV1.DefaultRecentMessageLimit
            : Math.Min(requestedLimit, ContextBuilderV1.MaximumRecentMessageLimit);

        private static ContextSnapshotChangeReasonV1 GetContextChangeReason(
            MessageQuerySnapshotV1 before,
            MessageQuerySnapshotV1 after)
        {
            if (!string.Equals(before.MessageSetRevision, after.MessageSetRevision, StringComparison.Ordinal))
            {
                return ContextSnapshotChangeReasonV1.MessageSetRevisionChanged;
            }

            if (before.PerspectiveState != after.PerspectiveState
                || before.CalibrationVersion != after.CalibrationVersion
                || !string.Equals(before.IdentityNamespace, after.IdentityNamespace, StringComparison.Ordinal))
            {
                return ContextSnapshotChangeReasonV1.PerspectiveChanged;
            }

            return ContextSnapshotChangeReasonV1.PageSnapshotChanged;
        }

        private void ValidateSearchRequest(HistorySearchRequestV1 request)
        {
            var query = request.Query?.Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                throw new ArgumentException("A non-empty history query is required.", nameof(request));
            }

            if (query.Length > _options.MaximumQueryCodePoints * 2
                || query.EnumerateRunes().Count() > _options.MaximumQueryCodePoints)
            {
                throw new ArgumentException("The history query exceeds its maximum length.", nameof(request));
            }

            if (request.AnchorMessageId is null && request.ExcludeAnchor)
            {
                throw new ArgumentException("ExcludeAnchor requires an anchor message ID.", nameof(request));
            }

            if (request.FromUtc is { } fromUtc && request.ToUtc is { } toUtc && fromUtc > toUtc)
            {
                throw new ArgumentException("The history search range is invalid.", nameof(request));
            }

            if (request.ExpectedMessageSetRevision is { Length: 0 })
            {
                throw new ArgumentException("An expected message set revision cannot be empty.", nameof(request));
            }
        }

        private SearchMatch Matches(MessageQueryItemV1 message, HistorySearchRequestV1 request)
        {
            if (message.MessageId == Guid.Empty
                || string.IsNullOrEmpty(message.Text))
            {
                return new SearchMatch(false, false);
            }

            DateTimeOffset? occurredAtUtc = message.OccurredAt?.ToUniversalTime();
            if ((request.FromUtc is not null || request.ToUtc is not null) && occurredAtUtc is null)
            {
                return new SearchMatch(false, false);
            }

            if (occurredAtUtc is { } occurredAt
                && ((request.FromUtc is { } fromUtc && occurredAt < fromUtc.ToUniversalTime())
                    || (request.ToUtc is { } toUtc && occurredAt > toUtc.ToUniversalTime())))
            {
                return new SearchMatch(false, false);
            }

            var boundedText = ClipText(message.Text, _options.MaximumSearchTextCodePointsPerMessage);
            var matches = boundedText.Text.Contains(request.Query.Trim(), StringComparison.OrdinalIgnoreCase);
            return new SearchMatch(matches, boundedText.Truncated);
        }

        private (HistorySearchHitV1 Hit, bool IsTextTruncated) ToSearchHit(MessageQueryItemV1 source)
        {
            var message = ToContextMessage(source, _options.MaximumTextCodePointsPerItem, out var truncated);
            return (new HistorySearchHitV1 { Message = message, IsTextTruncated = truncated }, truncated);
        }

        private (MessageEvidenceItemV1 Item, bool IsTextTruncated) ToEvidence(MessageQueryItemV1 source)
        {
            var message = ToContextMessage(source, _options.MaximumTextCodePointsPerItem, out var truncated);
            return (new MessageEvidenceItemV1 { Message = message, IsTextTruncated = truncated }, truncated);
        }

        private static ContextMessageV1 ToContextMessage(
            MessageQueryItemV1 source,
            int maximumTextCodePoints,
            out bool textTruncated)
        {
            if (source.MessageId == Guid.Empty)
            {
                throw new InvalidOperationException("M2 returned a message without an internal message ID.");
            }

            var text = source.Text;
            textTruncated = false;
            if (text is not null)
            {
                var clipped = ClipText(text, maximumTextCodePoints);
                text = clipped.Text;
                textTruncated = clipped.Truncated;
            }

            return new ContextMessageV1
            {
                MessageId = source.MessageId,
                Text = text,
                MessageType = source.MessageType,
                OccurredAt = source.OccurredAt?.ToUniversalTime(),
                Direction = source.Direction,
                DirectionEvidence = source.DirectionEvidence
            };
        }

        private static (string Text, bool Truncated) ClipText(string text, int maximumCodePoints)
        {
            var builder = new StringBuilder(Math.Min(text.Length, maximumCodePoints));
            var codePoints = 0;
            foreach (var rune in text.EnumerateRunes())
            {
                if (codePoints >= maximumCodePoints)
                {
                    return (builder.ToString(), true);
                }

                builder.Append(rune.ToString());
                codePoints++;
            }

            return (builder.ToString(), false);
        }

        private static ContextToolContextResultV1 ToToolContext(ContextBundleV1 source, string messageSetRevision) => new()
        {
            ContextRevision = source.ContextRevision,
            MessageSetRevision = messageSetRevision,
            PerspectiveState = source.PerspectiveState,
            CalibrationVersion = source.CalibrationVersion,
            ProfileStatus = source.ProfileStatus,
            UnprofiledTextCharacters = source.UnprofiledTextCharacters,
            ConsolidationThresholdCharacters = source.ConsolidationThresholdCharacters,
            RecentMessages = source.RecentMessages,
            ParticipantProfiles = source.ParticipantProfiles.Select(profile => new ContextToolParticipantProfileV1
            {
                SubjectType = profile.SubjectType,
                ProfileRevision = profile.ProfileRevision,
                CalibrationVersion = profile.CalibrationVersion,
                Status = profile.Status,
                Summary = profile.Summary,
                EvidenceMessageIds = profile.EvidenceMessageIds,
                GeneratedAt = profile.GeneratedAt
            }).ToArray(),
            MemoryItems = source.MemoryItems.Select(memory => new ContextToolMemoryItemV1
            {
                MemoryId = memory.MemoryId,
                SubjectType = memory.SubjectType,
                Text = memory.Text,
                Category = memory.Category,
                Origin = memory.Origin,
                Status = memory.Status,
                SourceMessageIds = memory.SourceMessageIds,
                CalibrationVersion = memory.CalibrationVersion,
                Version = memory.Version
            }).ToArray(),
            Omissions = source.Omissions,
            GeneratedAt = source.GeneratedAt
        };

        private static HistorySearchResultV1 RevisionChangedSearchResult(
            SearchVersion? firstVersion,
            PageValidation observed,
            MessageHistoryCoverageV1 historyCoverage,
            int pagesRead,
            int messagesScanned) => new()
        {
            Status = HistorySearchStatusV1.RevisionChanged,
            MessageSetRevision = firstVersion?.MessageSetRevision,
            ObservedMessageSetRevision = observed.ObservedRevision,
            PerspectiveState = firstVersion?.PerspectiveState,
            ObservedPerspectiveState = observed.ObservedPerspectiveState,
            CalibrationVersion = firstVersion?.CalibrationVersion,
            ObservedCalibrationVersion = observed.ObservedCalibrationVersion,
            HistoryCoverage = historyCoverage,
            Hits = Array.Empty<HistorySearchHitV1>(),
            PagesRead = pagesRead,
            MessagesScanned = messagesScanned,
            HasMoreMessages = false
        };

        private PageValidation ValidatePage(
            MessageQuerySnapshotV1 snapshot,
            SearchVersion? firstVersion,
            string? expectedMessageSetRevision,
            Guid? anchorMessageId)
        {
            if (snapshot is null
                || snapshot.AccountId != _scope.AccountId
                || snapshot.ConversationId != _scope.ConversationId)
            {
                throw new InvalidOperationException("M2 returned a query result outside the bound context scope.");
            }

            if (string.IsNullOrWhiteSpace(snapshot.MessageSetRevision))
            {
                throw new InvalidOperationException("M2 did not provide a message set revision.");
            }

            var observedVersion = GetVersion(snapshot);
            if (expectedMessageSetRevision is not null
                && !string.Equals(expectedMessageSetRevision, snapshot.MessageSetRevision, StringComparison.Ordinal))
            {
                return new PageValidation(
                    PageStatus.RevisionChanged,
                    snapshot.MessageSetRevision,
                    snapshot.PerspectiveState,
                    snapshot.CalibrationVersion);
            }

            if (firstVersion is not null && firstVersion != observedVersion)
            {
                return new PageValidation(
                    PageStatus.RevisionChanged,
                    snapshot.MessageSetRevision,
                    snapshot.PerspectiveState,
                    snapshot.CalibrationVersion);
            }

            if (anchorMessageId is not null && !snapshot.AnchorMessageFound)
            {
                return new PageValidation(PageStatus.AnchorNotFound, null, null, null);
            }

            return new PageValidation(PageStatus.Valid, null, null, null);
        }

        private static SearchVersion GetVersion(MessageQuerySnapshotV1 snapshot) => new(
            snapshot.MessageSetRevision,
            snapshot.PerspectiveState,
            snapshot.CalibrationVersion,
            snapshot.IdentityNamespace,
            snapshot.ConversationType,
            snapshot.HistoryCoverage);

        private enum PageStatus
        {
            Valid,
            RevisionChanged,
            AnchorNotFound
        }

        private sealed record PageValidation(
            PageStatus Status,
            string? ObservedRevision,
            PerspectiveStateV1? ObservedPerspectiveState,
            long? ObservedCalibrationVersion);

        private sealed record SearchMatch(bool Matches, bool TextTruncated);

        private sealed record SearchVersion(
            string MessageSetRevision,
            PerspectiveStateV1 PerspectiveState,
            long CalibrationVersion,
            string? IdentityNamespace,
            string? ConversationType,
            MessageHistoryCoverageV1 HistoryCoverage);
    }
}
