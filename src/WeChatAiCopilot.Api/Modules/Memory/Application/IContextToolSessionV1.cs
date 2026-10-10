using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Modules.Memory.Application;

/// <summary>
/// Creates a tool session bound to a server-authorized scope. Only trusted orchestration
/// code may call Bind; tool request DTOs deliberately contain no account/conversation IDs.
/// </summary>
public interface IContextToolSessionFactoryV1
{
    IContextToolSessionV1 Bind(AuthorizedContextScopeV1 authorizedScope);
}

public interface IContextToolSessionV1
{
    /// <summary>Returns the current context projection for this session's fixed scope.</summary>
    ValueTask<ContextToolContextResultV1> GetContextAsync(
        ContextToolRequestV1 request,
        CancellationToken cancellationToken = default);

    /// <summary>Searches bounded history pages without accepting account or conversation IDs.</summary>
    ValueTask<HistorySearchResultV1> SearchHistoryAsync(
        HistorySearchRequestV1 request,
        CancellationToken cancellationToken = default);

    /// <summary>Returns only requested message IDs that M2 resolves inside the fixed scope.</summary>
    ValueTask<MessageEvidenceResultV1> GetMessageEvidenceAsync(
        MessageEvidenceRequestV1 request,
        CancellationToken cancellationToken = default);
}
