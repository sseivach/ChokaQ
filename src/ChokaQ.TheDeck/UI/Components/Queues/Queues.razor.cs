using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChokaQ.TheDeck.UI.Components.Queues;

public partial class Queues : IDisposable
{
    [Parameter] public HubConnection? HubConnection { get; set; }
    [Inject] private IJobStorage Storage { get; set; } = default!;
    [Inject] private ChokaQTheDeckOptions Options { get; set; } = default!;

    private List<QueueEntity> _queues = new();
    private Dictionary<string, StatsSummaryEntity> _queueStats = new();
    private Dictionary<string, QueueHealthDto> _queueHealth = new();


    private bool _showInactive = false;


    private bool IsConnected => HubConnection?.State == HubConnectionState.Connected;


    private IEnumerable<QueueEntity> VisibleQueues =>
        _showInactive ? _queues : _queues.Where(q => q.IsActive);

    private System.Threading.Timer? _timer;
    private bool _isLoading = true;

    protected override void OnInitialized()
    {
        _timer = new System.Threading.Timer(async _ => await Refresh(), null, 0, 1000);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && HubConnection != null)
        {
            if (HubConnection.State == HubConnectionState.Disconnected)
            {
                await HubConnection.StartAsync();
            }
        }
    }

    private async Task Refresh()
    {
        try
        {
            _queues = (await Storage.GetQueuesAsync()).ToList();
            var stats = await Storage.GetQueueStatsAsync();
            _queueStats = stats.ToDictionary(s => s.Queue ?? "", s => s);
            var health = await Storage.GetSystemHealthAsync();
            _queueHealth = health.Queues.ToDictionary(q => q.Queue, q => q);

            _isLoading = false;
            await InvokeAsync(StateHasChanged);
        }
        catch
        {
            _isLoading = false;
        }
    }

    private async Task ToggleQueue(string name, bool isRunning)
    {
        var pause = !isRunning;
        var q = _queues.FirstOrDefault(x => x.Name == name);
        if (q != null)
        {
            var index = _queues.IndexOf(q);
            _queues[index] = q with { IsPaused = pause };
        }

        if (HubConnection is not null && IsConnected)
        {
            await HubConnection.InvokeAsync("ToggleQueue", name, pause);
            await Refresh();
        }
    }

    private async Task UpdateTimeout(string name, object? value)
    {
        int? parsedValue = null;
        if (value is string strVal && int.TryParse(strVal, out int iVal)) parsedValue = Math.Max(60, iVal);
        else if (value is int intVal) parsedValue = Math.Max(60, intVal);

        var q = _queues.FirstOrDefault(x => x.Name == name);
        if (q != null)
        {
            var index = _queues.IndexOf(q);
            _queues[index] = q with { ZombieTimeoutSeconds = parsedValue };
        }

        if (HubConnection is not null && IsConnected)
        {
            await HubConnection.InvokeAsync("UpdateQueueTimeout", name, parsedValue);
        }
    }

    private async Task UpdateMaxWorkers(string name, object? value)
    {
        int? parsedValue = null;
        if (value is string strVal && int.TryParse(strVal, out int iVal)) parsedValue = Math.Max(1, iVal);
        else if (value is int intVal) parsedValue = Math.Max(1, intVal);

        var q = _queues.FirstOrDefault(x => x.Name == name);
        if (q != null)
        {
            var index = _queues.IndexOf(q);
            _queues[index] = q with { MaxWorkers = parsedValue };
        }

        if (HubConnection is not null && IsConnected)
        {
            await HubConnection.InvokeAsync("UpdateQueueMaxWorkers", name, parsedValue);
        }
    }


    private async Task DeactivateQueue(string name)
    {
        if (HubConnection is not null && IsConnected)
        {
            await HubConnection.InvokeAsync("SetQueueActive", name, false);
            await Refresh();
        }
    }


    private async Task ActivateQueue(string name)
    {
        if (HubConnection is not null && IsConnected)
        {
            await HubConnection.InvokeAsync("SetQueueActive", name, true);
            await Refresh();
        }
    }

    private string GetQueueStatus(QueueEntity q)
    {
        if (q.IsPaused) return "PAUSED";
        if (!q.IsActive) return "INACTIVE";
        return "ACTIVE";
    }

    private string GetStatusModifier(QueueEntity q)
    {
        if (q.IsPaused) return "queues__status--paused";
        if (!q.IsActive) return "queues__status--inactive";
        return "queues__status--active";
    }

    private StatsSummaryEntity? GetQueueStats(string queueName) => _queueStats.GetValueOrDefault(queueName);

    private QueueHealthDto? GetQueueHealth(string queueName) => _queueHealth.GetValueOrDefault(queueName);

    private string GetLagModifier(QueueHealthDto? health)
    {
        if (health is null || health.Pending == 0)
            return "queues__lag--healthy";

        if (health.MaxLagSeconds >= Options.QueueLagCriticalThresholdSeconds)
            return "queues__lag--critical";

        if (health.MaxLagSeconds >= Options.QueueLagWarningThresholdSeconds)
            return "queues__lag--warning";

        return "queues__lag--healthy";
    }

    private static string FormatLag(double seconds)
    {
        if (seconds < 1)
            return $"{seconds * 1000:0}ms";

        if (seconds < 60)
            return $"{seconds:0.0}s";

        return $"{TimeSpan.FromSeconds(seconds):mm\\:ss}";
    }

    public void Dispose() => _timer?.Dispose();
}
