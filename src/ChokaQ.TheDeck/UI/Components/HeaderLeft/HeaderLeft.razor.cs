using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ChokaQ.TheDeck.UI.Components.HeaderLeft;

public partial class HeaderLeft
{
    [Inject] private IJSRuntime JSRuntime { get; set; } = default!;

    [Parameter] public bool IsConnected { get; set; }

    private async Task OnThemeChanged(ChangeEventArgs e)
    {
        var theme = e.Value?.ToString();
        await JSRuntime.InvokeVoidAsync("document.documentElement.setAttribute", "data-theme", theme);
    }
}
