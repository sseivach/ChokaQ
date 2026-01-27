using ChokaQ.Abstractions;
using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using Microsoft.AspNetCore.Components;

namespace ChokaQ.Dashboard.Components.Features;

public partial class CircuitMonitor : IDisposable
{
    [Inject] public ICircuitBreaker CircuitBreaker { get; set; } = default!;

    private List<CircuitStatsDto> _circuits = new();
    private System.Threading.Timer? _localTimer;

    // Called by Parent (DashboardPage) on SignalR updates
    public void Refresh()
    {
        FetchData();
        InvokeAsync(StateHasChanged);
    }

    protected override void OnInitialized()
    {
        FetchData();
        // Run a local timer every second to update the countdown text smoothly
        // independent of backend events.
        _localTimer = new System.Threading.Timer(_ =>
        {
            if (_circuits.Any(c => c.Status == CircuitStatus.Open))
            {
                InvokeAsync(StateHasChanged);
            }
        }, null, 1000, 1000);
    }

    private void FetchData()
    {
        _circuits = CircuitBreaker.GetCircuitStats().ToList();
    }

    private string GetTimeRemaining(DateTime resetAtUtc)
    {
        var remaining = resetAtUtc - DateTime.UtcNow;
        if (remaining.TotalSeconds <= 0) return "READY";
        return $"{remaining.TotalSeconds:F1}s";
    }

    private string FormatName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return "Unknown";
        var parts = fullName.Split('.');
        return parts.Last();
    }

    private string GetBgColor(CircuitStatus status) => status switch
    {
        CircuitStatus.Open => "rgba(220, 53, 69, 0.1)",
        CircuitStatus.HalfOpen => "rgba(255, 193, 7, 0.1)",
        _ => "transparent"
    };

    private string GetIcon(CircuitStatus status) => status switch
    {
        CircuitStatus.Open => "[OPEN]",
        CircuitStatus.HalfOpen => "[HALF]",
        _ => "[OK]"
    };

    public void Dispose()
    {
        _localTimer?.Dispose();
        GC.SuppressFinalize(this);
    }
}