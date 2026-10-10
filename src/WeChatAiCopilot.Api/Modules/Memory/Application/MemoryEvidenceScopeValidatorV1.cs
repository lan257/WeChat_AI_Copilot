namespace WeChatAiCopilot.Api.Modules.Memory.Application;

/// <summary>
/// Pure half of evidence ownership validation. The database query supplies only message IDs
/// found under the authorized account+conversation; every requested ID must be present there.
/// </summary>
public static class MemoryEvidenceScopeValidatorV1
{
    public static bool AllRequestedEvidenceIsAuthorized(
        IReadOnlyList<Guid>? requestedMessageIds,
        IReadOnlySet<Guid>? authorizedMessageIds)
    {
        if (requestedMessageIds is null || authorizedMessageIds is null)
        {
            return false;
        }

        if (requestedMessageIds.Count == 0)
        {
            return true;
        }

        return requestedMessageIds.All(id => id != Guid.Empty && authorizedMessageIds.Contains(id))
            && requestedMessageIds.Distinct().Count() == requestedMessageIds.Count
            && authorizedMessageIds.Count == requestedMessageIds.Count;
    }
}
