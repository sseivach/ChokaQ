using ChokaQ.TheDeck.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ChokaQ.TheDeck.UI.Components.ConsoleLog;

public partial class ConsoleLog
{
    [Parameter] public List<LogEntry> Logs { get; set; } = new();
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private ElementReference _bottomAnchor;
    private int _lastCount = 0;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (Logs.Count > _lastCount)
        {
            _lastCount = Logs.Count;
            try
            {
                await JS.InvokeVoidAsync("deck.scrollToBottom", _bottomAnchor);
            }
            catch { }
        }
    }

    private string GetMessageModifier(string level) => level.ToLower() switch
    {
        "info" => "console__message--info",
        "warning" => "console__message--warning",
        "error" => "console__message--error",
        "success" => "console__message--success",
        "debug" => "console__message--debug",
        _ => ""
    };
}
