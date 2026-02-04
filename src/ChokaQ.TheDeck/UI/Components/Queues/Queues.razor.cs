using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChokaQ.TheDeck.UI.Components.Queues;

public partial class Queues : IDisposable
{
    [Parameter] public HubConnection? HubConnection { get; set; }
    [Inject] private IJobStorage Storage { get; set; } = default!;

    private List<QueueEntity> _queues = new();
    private Dictionary<string, StatsSummaryEntity> _queueStats = new();

    // UI Toggle state
    private bool _showInactive = false;

    // Filters queues based on the toggle state
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

        if (HubConnection is not null)
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

        if (HubConnection is not null)
        {
            await HubConnection.InvokeAsync("UpdateQueueTimeout", name, parsedValue);
        }
    }

    // Soft Delete (Deactivate)
    private async Task DeactivateQueue(string name)
    {
        if (HubConnection is not null)
        {
            await HubConnection.InvokeAsync("SetQueueActive", name, false);
            await Refresh();
        }
    }

    // Restore (Activate)
    private async Task ActivateQueue(string name)
    {
        if (HubConnection is not null)
        {
            await HubConnection.InvokeAsync("SetQueueActive", name, true);
            await Refresh();
        }
    }

    private void DeleteQueue(string name)
    {
        Console.WriteLine($"DELETE {name} (Not Implemented)");
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

    public void Dispose() => _timer?.Dispose();
}