namespace ChokaQ.Abstractions.DTOs;

/// <summary>
/// Dry-run result for a filtered DLQ bulk operation.
/// </summary>
/// <remarks>
/// The preview separates "matched" from "will affect" because enterprise tools should make blast
/// radius explicit. If 50,000 jobs match but the execution cap is 1,000, the UI must show both facts
/// before the operator confirms the action.
/// </remarks>
public sealed record DlqBulkOperationPreviewDto(
    long MatchedCount,
    int MaxJobs,
    IReadOnlyList<string> SampleJobIds)
{
    public int WillAffectCount => (int)Math.Min(MatchedCount, MaxJobs);
    public bool IsTruncated => MatchedCount > MaxJobs;
}
