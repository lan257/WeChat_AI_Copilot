namespace WeChatAiCopilot.Api.Modules.Memory.Application;

/// <summary>
/// M3 service-side limits. Constructor validation prevents configuration from exceeding
/// the hard caps agreed for the read-only tool surface.
/// </summary>
public sealed record ContextToolOptionsV1
{
    public const int HardMaximumPages = 20;
    public const int HardMaximumPageSize = 100;
    public const int HardMaximumSearchResults = 20;
    public const int HardMaximumEvidenceIds = 20;
    public const int HardMaximumTextCodePointsPerItem = 4000;
    public const int HardMaximumSearchTextCodePointsPerMessage = 32000;

    public int MaximumPages { get; init; } = HardMaximumPages;
    public int PageSize { get; init; } = HardMaximumPageSize;
    public int MaximumSearchResults { get; init; } = HardMaximumSearchResults;
    public int MaximumEvidenceIds { get; init; } = HardMaximumEvidenceIds;
    public int MaximumTextCodePointsPerItem { get; init; } = HardMaximumTextCodePointsPerItem;
    public int MaximumSearchTextCodePointsPerMessage { get; init; } = 8000;
    public int MaximumQueryCodePoints { get; init; } = 512;
}
