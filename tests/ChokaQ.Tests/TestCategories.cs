namespace ChokaQ.Tests;

/// <summary>
/// Stable xUnit trait names used by local commands and CI filters.
/// Keep these constants boring and explicit: a release pipeline should not need
/// to know folder names or namespace conventions to decide which test tier it is running.
/// </summary>
internal static class TestCategories
{
    /// <summary>
    /// The trait key understood by `dotnet test --filter "Category=..."`.
    /// </summary>
    public const string Category = "Category";

    /// <summary>
    /// Fast tests that must not require Docker, SQL Server, networking, or external services.
    /// </summary>
    public const string Unit = "Unit";

    /// <summary>
    /// Tests that validate behavior against real infrastructure, such as SQL Server Testcontainers.
    /// </summary>
    public const string Integration = "Integration";
}
