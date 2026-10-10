using WeChatAiCopilot.Api.Modules.Memory.Contracts;

namespace WeChatAiCopilot.Api.Modules.Memory.Application;

public interface IMemoryServiceV1
{
    Task<IReadOnlyList<MemoryItemV1>> ListAsync(
        MemoryConversationScopeV1 scope,
        int limit,
        CancellationToken cancellationToken = default);

    Task<MemoryItemV1?> GetAsync(
        MemoryConversationScopeV1 scope,
        Guid memoryId,
        CancellationToken cancellationToken = default);

    Task<MemoryItemV1> CreateUserEnteredAsync(
        MemoryConversationScopeV1 scope,
        UserEnteredMemoryCreateRequestV1 request,
        CancellationToken cancellationToken = default);

    Task<MemoryItemV1> UpdateUserEnteredAsync(
        MemoryConversationScopeV1 scope,
        Guid memoryId,
        UserEnteredMemoryUpdateRequestV1 request,
        CancellationToken cancellationToken = default);

    Task DeleteUserEnteredAsync(
        MemoryConversationScopeV1 scope,
        Guid memoryId,
        long expectedVersion,
        CancellationToken cancellationToken = default);
}

public enum MemoryServiceFailureCodeV1
{
    InvalidRequest,
    EvidenceUnavailable,
    MemoryNotFound,
    VersionConflict
}

public sealed class MemoryServiceException(
    MemoryServiceFailureCodeV1 failureCode,
    string? detailCode = null) : Exception(detailCode ?? failureCode.ToString())
{
    public MemoryServiceFailureCodeV1 FailureCode { get; } = failureCode;
}
