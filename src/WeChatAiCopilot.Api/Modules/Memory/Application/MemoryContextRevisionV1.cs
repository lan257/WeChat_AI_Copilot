using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Modules.Memory.Application;

/// <summary>
/// Pure fingerprint for the memory portion of a ContextBundle. It includes editable memory
/// values, versions, and evidence IDs so changing a UserEntered note invalidates old bundles.
/// </summary>
public static class MemoryContextRevisionV1
{
    public static string Compute(
        MemoryContextReadRequestV1 request,
        ProfileStatusV1 profileStatus,
        IReadOnlyList<MemoryItemV1> items,
        IReadOnlyList<ParticipantProfileSnapshotV1> profiles,
        int unprofiledTextCharacters,
        string? consolidationSourceMessageCursor,
        string messageSetRevision)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(messageSetRevision);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "memory-context-v1");
        Append(hash, request.AccountId.ToString("N"));
        Append(hash, request.ConversationId.ToString("N"));
        Append(hash, request.IdentityNamespace);
        Append(hash, request.PerspectiveState.ToString());
        Append(hash, request.CalibrationVersion.ToString(CultureInfo.InvariantCulture));
        Append(hash, profileStatus.ToString());
        Append(hash, unprofiledTextCharacters.ToString(CultureInfo.InvariantCulture));
        Append(hash, consolidationSourceMessageCursor);
        Append(hash, messageSetRevision);
        foreach (var item in items)
        {
            Append(hash, item.MemoryId.ToString("N"));
            Append(hash, item.AccountId.ToString("N"));
            Append(hash, item.SubjectType.ToString());
            Append(hash, item.SubjectId.ToString("N"));
            Append(hash, item.ConversationId?.ToString("N"));
            Append(hash, item.IdentityNamespace);
            Append(hash, item.Text);
            Append(hash, item.Category);
            Append(hash, item.Origin.ToString());
            Append(hash, item.Status.ToString());
            Append(hash, item.CreatedAt.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
            Append(hash, item.UpdatedAt.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
            Append(hash, item.ObservedAt?.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
            Append(hash, item.CalibrationVersion?.ToString(CultureInfo.InvariantCulture));
            Append(hash, item.SourceMessageCursor);
            Append(hash, item.Version.ToString(CultureInfo.InvariantCulture));
            foreach (var messageId in item.SourceMessageIds)
            {
                Append(hash, messageId.ToString("N"));
            }

            Append(hash, "end-item");
        }

        foreach (var profile in profiles)
        {
            Append(hash, profile.SubjectType.ToString());
            Append(hash, profile.SubjectId.ToString("N"));
            Append(hash, profile.ProfileRevision.ToString(CultureInfo.InvariantCulture));
            Append(hash, profile.Summary);
            Append(hash, profile.SourceMessageCursor);
            foreach (var evidenceId in profile.EvidenceMessageIds)
            {
                Append(hash, evidenceId.ToString("N"));
            }

            Append(hash, "end-profile");
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
