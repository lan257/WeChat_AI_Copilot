using System.Globalization;
using System.Numerics;
using WeChatAiCopilot.Api.Contracts.V1;

namespace WeChatAiCopilot.Api.Modules.Conversations.Domain;

public sealed record SenderCalibrationScopeV1(
    Guid AccountId,
    Guid ConversationId,
    string? IdentityNamespace,
    string? MessageTable,
    string? ShardFingerprint)
{
    public bool IsComparable => AccountId != Guid.Empty
        && ConversationId != Guid.Empty
        && !string.IsNullOrWhiteSpace(IdentityNamespace)
        && !string.IsNullOrWhiteSpace(MessageTable)
        && !string.IsNullOrWhiteSpace(ShardFingerprint);
}

public sealed record ScopedSenderIdentityV1(
    SenderCalibrationScopeV1 Scope,
    string? RawSenderId);

public sealed record SenderPerspectiveV1(
    SenderCalibrationScopeV1 Scope,
    PerspectiveStateV1 State,
    PerspectiveEvidenceV1? Evidence,
    string? AccountSelfRawSenderId,
    string? ContactRawSenderId,
    long CalibrationVersion);

public sealed record DirectionProjectionV1(
    MessageDirectionV1 Direction,
    DirectionEvidenceV1 Evidence);

public static class SenderPerspectiveResolver
{
    public static SenderPerspectiveV1 ResolveDefault(
        SenderCalibrationScopeV1 scope,
        IEnumerable<ScopedSenderIdentityV1> observedSenderIds)
    {
        var candidates = ReadComparableCandidates(scope, observedSenderIds);
        if (candidates is null || candidates.Count != 2 || candidates[0].NumericValue == candidates[1].NumericValue)
        {
            return Unresolved(scope);
        }

        var self = candidates[0].NumericValue < candidates[1].NumericValue
            ? candidates[0]
            : candidates[1];
        var contact = ReferenceEquals(self, candidates[0]) ? candidates[1] : candidates[0];

        return new(
            scope,
            PerspectiveStateV1.PendingConfirmation,
            PerspectiveEvidenceV1.DefaultSmallSenderId,
            self.RawValue,
            contact.RawValue,
            0);
    }

    public static SenderPerspectiveV1 ConfirmSelection(
        SenderCalibrationScopeV1 scope,
        IEnumerable<ScopedSenderIdentityV1> observedSenderIds,
        string accountSelfRawSenderId)
    {
        var candidates = ReadComparableCandidates(scope, observedSenderIds);
        if (candidates is null || candidates.Count != 2 || candidates[0].NumericValue == candidates[1].NumericValue)
        {
            return Unresolved(scope);
        }

        var self = candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.RawValue, accountSelfRawSenderId, StringComparison.Ordinal));
        if (self is null)
        {
            return Unresolved(scope);
        }

        var contact = candidates.Single(candidate => !ReferenceEquals(candidate, self));
        return new(
            scope,
            PerspectiveStateV1.Confirmed,
            PerspectiveEvidenceV1.UserConfirmed,
            self.RawValue,
            contact.RawValue,
            0);
    }

    public static DirectionProjectionV1 Project(
        SenderCalibrationScopeV1 messageScope,
        SenderPerspectiveV1 perspective,
        string? messageRawSenderId)
    {
        ArgumentNullException.ThrowIfNull(messageScope);
        ArgumentNullException.ThrowIfNull(perspective);

        if (!messageScope.IsComparable
            || perspective.Scope != messageScope
            || perspective.State is PerspectiveStateV1.Unresolved
            || string.IsNullOrEmpty(messageRawSenderId)
            || perspective.AccountSelfRawSenderId is null
            || perspective.ContactRawSenderId is null)
        {
            return new(MessageDirectionV1.Unknown, DirectionEvidenceV1.Unresolved);
        }

        var evidence = perspective.State switch
        {
            PerspectiveStateV1.PendingConfirmation => DirectionEvidenceV1.DefaultSmallSenderId,
            PerspectiveStateV1.Confirmed => DirectionEvidenceV1.UserConfirmed,
            _ => DirectionEvidenceV1.Unresolved
        };

        if (string.Equals(messageRawSenderId, perspective.AccountSelfRawSenderId, StringComparison.Ordinal))
        {
            return new(MessageDirectionV1.Outgoing, evidence);
        }

        if (string.Equals(messageRawSenderId, perspective.ContactRawSenderId, StringComparison.Ordinal))
        {
            return new(MessageDirectionV1.Incoming, evidence);
        }

        return new(MessageDirectionV1.Unknown, DirectionEvidenceV1.Unresolved);
    }

    private static List<SenderCandidate>? ReadComparableCandidates(
        SenderCalibrationScopeV1 scope,
        IEnumerable<ScopedSenderIdentityV1> observedSenderIds)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(observedSenderIds);

        if (!scope.IsComparable)
        {
            return null;
        }

        var uniqueRawValues = new HashSet<string>(StringComparer.Ordinal);
        foreach (var observed in observedSenderIds)
        {
            if (observed is null || observed.Scope != scope)
            {
                return null;
            }

            var rawValue = observed.RawSenderId;
            if (!IsNonNegativeDecimal(rawValue))
            {
                return null;
            }

            uniqueRawValues.Add(rawValue!);
            if (uniqueRawValues.Count > 2)
            {
                return null;
            }
        }

        if (uniqueRawValues.Count != 2)
        {
            return null;
        }

        var candidates = new List<SenderCandidate>(capacity: 2);
        foreach (var rawValue in uniqueRawValues)
        {
            if (!BigInteger.TryParse(rawValue, NumberStyles.None, CultureInfo.InvariantCulture, out var numericValue))
            {
                return null;
            }

            candidates.Add(new SenderCandidate(rawValue, numericValue));
        }

        return candidates;
    }

    private static SenderPerspectiveV1 Unresolved(SenderCalibrationScopeV1 scope) => new(
        scope,
        PerspectiveStateV1.Unresolved,
        null,
        null,
        null,
        0);

    private static bool IsNonNegativeDecimal(string? rawValue)
    {
        if (string.IsNullOrEmpty(rawValue))
        {
            return false;
        }

        foreach (var character in rawValue)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private sealed record SenderCandidate(string RawValue, BigInteger NumericValue);
}
