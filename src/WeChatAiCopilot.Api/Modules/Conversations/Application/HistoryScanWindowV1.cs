namespace WeChatAiCopilot.Api.Modules.Conversations.Application;

/// <summary>
/// A scan cursor only carries over into the identical requested range, so the range is part of the
/// cursor key. A range-less scan is the whole readable local store (ADR-2026-10-14-02).
/// </summary>
public static class HistoryScanWindowV1
{
    public const string FullLocalStore = "full";

    public static string Key(DateTimeOffset? fromUtc, DateTimeOffset? toUtc) =>
        fromUtc is null || toUtc is null
            ? FullLocalStore
            : $"{fromUtc.Value.UtcTicks:00000000000000000}~{toUtc.Value.UtcTicks:00000000000000000}";
}
