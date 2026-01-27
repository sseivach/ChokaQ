using ChokaQ.Abstractions;
using Microsoft.AspNetCore.Components;

namespace ChokaQ.Dashboard.Components.Pages;

public partial class DashboardPage : ComponentBase
{
    // Inject Options mainly for Title/Version display
    [Inject] public ChokaQDashboardOptions Options { get; set; } = default!;

    private string _currentTheme = "office";

    // CSS class mapping
    private string CurrentThemeClass => _currentTheme switch
    {
        "nightshift" => "cq-theme-nightshift",
        "caviar" => "cq-theme-caviar",
        "flashbang" => "cq-theme-flashbang",
        "kiddie" => "cq-theme-kiddie",
        "bravosix" => "cq-theme-bravosix",
        "bsod" => "cq-theme-bsod",
        _ => "cq-theme-office"
    };

    private void HandleThemeChanged(string newTheme)
    {
        _currentTheme = newTheme;
        StateHasChanged();
    }
}