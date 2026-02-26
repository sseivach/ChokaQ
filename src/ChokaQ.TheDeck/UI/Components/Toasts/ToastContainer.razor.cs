using ChokaQ.TheDeck.Models;
using Microsoft.AspNetCore.Components;

namespace ChokaQ.TheDeck.UI.Components.Toasts;

public partial class ToastContainer
{
    [Parameter] public List<ToastModel> Toasts { get; set; } = new();
    [Parameter] public EventCallback<Guid> OnClose { get; set; }

    private async Task HandleClose(Guid id)
    {
        await OnClose.InvokeAsync(id);
    }

    private string GetIcon(string level) => level.ToLower() switch
    {
        "success" => "✓",
        "error" => "!",
        "warning" => "⚠",
        "info" => "i",
        _ => "•"
    };
}