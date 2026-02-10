using ChokaQ.TheDeck.Enums;

namespace ChokaQ.TheDeck.Models;

/// <summary>
/// Shared model for passing job data between Inspector, Editor and OpsPanel.
/// </summary>
public record JobEditorModel
{
    public required string Id { get; init; }
    public required string Queue { get; init; }
    public required string Type { get; init; }
    public string? Payload { get; init; }
    public int Priority { get; init; }
    public required JobSource Source { get; init; }
}