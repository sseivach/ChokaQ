using ChokaQ.Abstractions;
using ChokaQ.Abstractions.DTOs;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChokaQ.Dashboard.Components.Features;

public partial class QueueManager : IDisposable
{
    [Inject] public IJobStorage Storage { get; set; } = default!;
    [Parameter] public HubConnection? HubConnection { get; set; }

    private List<QueueDto> _queues = new();
    private HashSet<string> _hiddenQueues = new();
    private IEnumerable<QueueDto> _visibleQueues => _queues.Where(q => !_hiddenQueues.Contains(q.Name));
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
            // 1. Fetch data (already sorted by DB)
            _queues = (await Storage.GetQueuesAsync()).ToList();
            _isLoading = false;

            // 2. STARTUP LOGIC: Hide inactive queues initially
            if (_isFirstLoad)
            {
                foreach (var q in _queues)
                {
                    // If queue is empty and idle -> hide it
                    bool isActive = q.PendingCount > 0 || q.FetchedCount > 0 || q.ProcessingCount > 0;
                    if (!isActive)
                    {
                        _hiddenQueues.Add(q.Name);
                    }
                }
                _isFirstLoad = false; // Disable flag after first run
            }

            // 3. Auto-unhide (if an inactive queue suddenly wakes up)
            foreach (var q in _queues)
            {
                bool isActive = q.PendingCount > 0 || q.FetchedCount > 0 || q.ProcessingCount > 0;

                if (isActive && _hiddenQueues.Contains(q.Name))
                {
                    _hiddenQueues.Remove(q.Name);
                }
            }

            await InvokeAsync(StateHasChanged);
        }
        catch
        {
            _isLoading = false;
            // Silent catch for polling
        }
    }

    private async Task ToggleQueue(string name, bool isRunning)
    {
        var pause = !isRunning;

        // Optimistic UI update to prevent flickering
        var q = _queues.FirstOrDefault(x => x.Name == name);
        if (q != null) q = q with { IsPaused = pause };

        if (HubConnection is not null)
        {
            await HubConnection.InvokeAsync("ToggleQueue", name, pause);
            await Refresh();
        }
    }

    /// <summary>
    /// Handles changes to the Zombie Timeout input field.
    /// Values less than 60 seconds are clamped to 60 to prevent accidental immediate termination.
    /// </summary>
    private async Task UpdateTimeout(string name, object? value)
    {
        int? parsedValue = null;

        // Attempt to parse the input
        if (value is string strVal && int.TryParse(strVal, out int iVal))
        {
            // Enforce a minimum safety limit of 60 seconds
            parsedValue = Math.Max(60, iVal);
        }
        else if (value is int intVal)
        {
            parsedValue = Math.Max(60, intVal);
        }

        // Optimistic local update
        var q = _queues.FirstOrDefault(x => x.Name == name);
        if (q != null) q = q with { ZombieTimeoutSeconds = parsedValue };

        // Send to server
        if (HubConnection is not null)
        {
            await HubConnection.InvokeAsync("UpdateQueueTimeout", name, parsedValue);
            // We rely on the background polling timer to eventually refresh the state from the DB.
        }
    }

    private void HideQueue(string name) => _hiddenQueues.Add(name);
    private void ShowAllQueues() => _hiddenQueues.Clear();

    private string GetDuration(QueueDto q)
    {
        if (!q.FirstJobAtUtc.HasValue) return "-";
        var end = q.LastJobAtUtc ?? DateTime.UtcNow;
        if (q.PendingCount > 0 || q.ProcessingCount > 0) end = DateTime.UtcNow;
        var span = end - q.FirstJobAtUtc.Value;

        if (span.TotalHours >= 1) return span.ToString(@"hh\:mm\:ss");
        return span.ToString(@"mm\:ss");
    }

    public void Dispose() => _timer?.Dispose();
}