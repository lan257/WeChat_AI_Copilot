namespace WeChatAiCopilot.Api.Modules.Memory.Application;

public enum ContextSnapshotChangeReasonV1
{
    MessageSetRevisionChanged,
    PerspectiveChanged,
    PageSnapshotChanged
}

/// <summary>
/// Raised when the M2 message/perspective snapshot changes while M3 builds one context.
/// The exception contains only opaque revisions and a reason code, never message content
/// or account/conversation identifiers.
/// </summary>
public sealed class ContextRevisionChangedException : Exception
{
    public const string SafeErrorCode = "CONTEXT_REVISION_CHANGED";

    public ContextRevisionChangedException(
        string? beforeMessageSetRevision,
        string? afterMessageSetRevision,
        ContextSnapshotChangeReasonV1 reason)
        : base("The message context changed while the context bundle was being built.")
    {
        BeforeMessageSetRevision = beforeMessageSetRevision;
        AfterMessageSetRevision = afterMessageSetRevision;
        Reason = reason;
    }

    public string ErrorCode => SafeErrorCode;
    public string? BeforeMessageSetRevision { get; }
    public string? AfterMessageSetRevision { get; }
    public ContextSnapshotChangeReasonV1 Reason { get; }
}
