using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using Microsoft.AspNetCore.Components;

namespace ChokaQ.TheDeck.UI.Components.Circuits;

public partial class Circuits : IDisposable
{
    [Parameter] public IEnumerable<CircuitStatsDto> Breakers { get; set; } = Enumerable.Empty<CircuitStatsDto>();

    private System.Threading.Timer? _timer;

    protected override void OnInitialized()
    {
        _timer = new System.Threading.Timer(_ => 
        {
            if (Breakers != null && Breakers.Any(c => c.Status == CircuitStatus.Open))
            {
                InvokeAsync(StateHasChanged);
            }
        }, null, 1000, 1000);
    }

    private string GetTimeRemaining(DateTime resetAt)
    {
        var remaining = resetAt - DateTime.UtcNow;
        if (remaining.TotalSeconds <= 0) return "READY";
        return $"{remaining.TotalSeconds:F1}s";
    }

    private string FormatName(string fullName) => fullName?.Split('.').Last() ?? "Unknown";

    private string GetStatusModifier(CircuitStatus status) => status switch
    {
        CircuitStatus.Open => "circuits__item--open",
        CircuitStatus.HalfOpen => "circuits__item--half-open",
        _ => ""
    };

    private string GetIcon(CircuitStatus status) => status switch
    {
        CircuitStatus.Open => "⚡ OPEN",
        CircuitStatus.HalfOpen => "⚠️ HALF",
        CircuitStatus.Closed => "✓ OK",
        _ => "?"
    };

    private string GetIconModifier(CircuitStatus status) => status switch
    {
        CircuitStatus.Open => "circuits__icon--open",
        CircuitStatus.HalfOpen => "circuits__icon--half-open",
        CircuitStatus.Closed => "circuits__icon--ok",
        _ => ""
    };

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
