using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Modules.Memory.Application;

public interface IContextServiceV1
{
    ValueTask<ContextBundleV1> BuildAsync(
        ContextRequestV1 request,
        AuthorizedContextScopeV1 authorizedScope,
        CancellationToken cancellationToken = default);
}
