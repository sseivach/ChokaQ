using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ChokaQ.TheDeck.UI.Components.HeaderRight;

public partial class HeaderRight
{
    [Inject] private IJSRuntime JSRuntime { get; set; } = default!;

    private async Task OnThemeChanged(ChangeEventArgs e)
    {
        var theme = e.Value?.ToString();
        await JSRuntime.InvokeVoidAsync("document.documentElement.setAttribute", "data-theme", theme);
    }
}
