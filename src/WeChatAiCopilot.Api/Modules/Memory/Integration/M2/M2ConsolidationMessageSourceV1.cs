using WeChatAiCopilot.Api.Contracts.V1;
using WeChatAiCopilot.Api.Modules.Memory.Application;
using WeChatAiCopilot.Api.Modules.Messages.Contracts;

namespace WeChatAiCopilot.Api.Modules.Memory.Integration.M2;

/// <summary>
/// Adapts M2's stable scoped page contract to the M3 consolidation collector contract.
/// The request is forwarded unchanged so M2 remains responsible for scope, anchor, and limit semantics.
/// </summary>
public sealed class M2ConsolidationMessageSourceAdapterV1(IMessageQueryV1 messageQuery) : IConsolidationMessageSourceV1
{
    public async ValueTask<ConsolidationMessagePageV1> GetPageAsync(
        MessageQueryRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = await messageQuery.GetRecentMessagesAsync(request, cancellationToken);
        if (snapshot is null)
        {
            throw new InvalidOperationException("M2 returned no message query snapshot.");
        }

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
            Messages = snapshot.Messages ?? null!
        };
    }
}
