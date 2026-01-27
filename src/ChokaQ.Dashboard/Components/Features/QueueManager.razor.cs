using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChokaQ.Dashboard.Components.Features;

/// <summary>
/// Queue management component for Three Pillars architecture.
/// Displays queue configuration and allows pause/resume operations.
/// </summary>
public partial class QueueManager : IDisposable
{
    [Inject] public IJobStorage Storage { get; set; } = default!;
    [Parameter] public HubConnection? HubConnection { get; set; }

    private List<QueueEntity> _queues = new();
    private HashSet<string> _hiddenQueues = new();
    private IEnumerable<QueueEntity> _visibleQueues => _queues.Where(q => !_hiddenQueues.Contains(q.Name));
    private System.Threading.Timer? _timer;
    private bool _isLoading = true;
    private bool _isFirstLoad = true;

    protected override void OnInitialized()
    {
        _timer = new System.Threading.Timer(async _ => await Refresh(), null, 0, 2000);
    }

    private async Task Refresh()
    {
        try
        {
            // Fetch queue configurations
            _queues = (await Storage.GetQueuesAsync()).ToList();
            _isLoading = false;

            // STARTUP LOGIC: Hide inactive queues initially
            if (_isFirstLoad)
            {
                foreach (var q in _queues)
                {
                    // If queue is not active, hide it
                    if (!q.IsActive)
                    {
                        _hiddenQueues.Add(q.Name);
                    }
                }
                _isFirstLoad = false;
            }

            // Auto-unhide if queue becomes active
            foreach (var q in _queues)
            {
                if (q.IsActive && _hiddenQueues.Contains(q.Name))
                {
                    _hiddenQueues.Remove(q.Name);
                }
            }

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

        // Optimistic UI update
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

    /// <summary>
    /// Handles changes to the Zombie Timeout input field.
    /// Values less than 60 seconds are clamped to 60.
    /// </summary>
    private async Task UpdateTimeout(string name, object? value)
    {
        int? parsedValue = null;

        if (value is string strVal && int.TryParse(strVal, out int iVal))
        {
            parsedValue = Math.Max(60, iVal);
        }
        else if (value is int intVal)
        {
            parsedValue = Math.Max(60, intVal);
        }

        // Optimistic local update
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

    private void HideQueue(string name) => _hiddenQueues.Add(name);
    private void ShowAllQueues() => _hiddenQueues.Clear();

    private string GetQueueStatus(QueueEntity q)
    {
        if (q.IsPaused) return "PAUSED";
        if (!q.IsActive) return "INACTIVE";
        return "ACTIVE";
    }

    private string GetStatusColor(QueueEntity q)
    {
        if (q.IsPaused) return "var(--cq-warning)";
        if (!q.IsActive) return "var(--cq-text-muted)";
        return "var(--cq-success)";
    }

    public void Dispose() => _timer?.Dispose();
}
