namespace ChokaQ.TheDeck;

public class ChokaQTheDeckOptions
{
    public string RoutePrefix { get; set; } = "/chokaq";

    /// <summary>
    /// The name of the ASP.NET Core Authorization Policy to apply to the Dashboard and Hub.
    /// If null (default), the dashboard is unsecured.
    /// </summary>
    public string? AuthorizationPolicy { get; set; }
}