namespace ChokaQ.TheDeck.Enums;

/// <summary>
/// Defines the available tabs within the Operations Panel (OpsPanel).
/// Acts as the state machine for the sidebar navigation.
/// </summary>
public enum OpsTab
{
    /// <summary>
    /// No tab selected (Panel is empty or closed).
    /// </summary>
    None,

    /// <summary>
    /// Search and Filtering controls. 
    /// Only available when the application is in History Mode.
    /// </summary>
    HistoryFilter,

    /// <summary>
    /// Detailed view of a specific Job (Inspector).
    /// Automatically selected when a job row is clicked.
    /// </summary>
    JobInspector,

    /// <summary>
    /// JSON Payload editor for modifying jobs.
    /// Accessible from the JobInspector.
    /// </summary>
    JobEditor
}