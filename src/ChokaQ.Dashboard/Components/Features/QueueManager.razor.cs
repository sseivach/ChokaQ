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

    protected override void OnInitialized()
    {
        _timer = new System.Threading.Timer(async _ => await Refresh(), null, 0, 2000);
    }

    private async Task Refresh()
    {
        try
        {
            _queues = (await Storage.GetQueuesAsync()).ToList();
            _isLoading = false;

            // Auto-unhide active queues
            foreach (var q in _queues)
            {
                if ((q.PendingCount > 0 || q.ProcessingCount > 0) && _hiddenQueues.Contains(q.Name))
                {
                    _hiddenQueues.Remove(q.Name);
                }
            }

            await InvokeAsync(StateHasChanged);
        }
        catch
        {
            _isLoading = false;
            // Silent catch is better for UI polling
        }
    }

    private async Task ToggleQueue(string name, bool isRunning)
    {
        var pause = !isRunning;
        // Optimistic UI update
        var q = _queues.FirstOrDefault(x => x.Name == name);
        if (q != null) q = q with { IsPaused = pause };

        if (HubConnection is not null)
        {
            await HubConnection.InvokeAsync("ToggleQueue", name, pause);
            await Refresh();
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