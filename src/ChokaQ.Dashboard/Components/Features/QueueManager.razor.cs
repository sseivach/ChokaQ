using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Entities; // <--- ВАЖНО: Используем Entities
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChokaQ.Dashboard.Components.Features;

public partial class QueueManager : IDisposable
{
    [Inject] public IJobStorage Storage { get; set; } = default!;
    [Parameter] public HubConnection? HubConnection { get; set; }

    // FIX: Zero-Copy. Работаем напрямую с Entity.
    private List<QueueEntity> _queues = new();
    private HashSet<string> _hiddenQueues = new();

    // FIX: Тип коллекции тоже меняем
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
            // 1. Fetch data
            // Теперь это работает, так как слева List<QueueEntity> и справа IEnumerable<QueueEntity>
            _queues = (await Storage.GetQueuesAsync()).ToList();
            _isLoading = false;

            // 2. STARTUP LOGIC: Hide inactive queues initially
            if (_isFirstLoad)
            {
                foreach (var q in _queues)
                {
                    // NOTE: Убедись, что в QueueEntity есть эти свойства (PendingCount и т.д.).
                    // Если их нет, придется джойнить статистику отдельно или добавить [NotMapped] свойства в Entity.
                    bool isActive = q.PendingCount > 0 || q.FetchedCount > 0 || q.ProcessingCount > 0;
                    if (!isActive)
                    {
                        _hiddenQueues.Add(q.Name);
                    }
                }
                _isFirstLoad = false;
            }

            // 3. Auto-unhide
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
        }
    }

    private async Task ToggleQueue(string name, bool isRunning)
    {
        var pause = !isRunning;

        // Optimistic UI update
        var q = _queues.FirstOrDefault(x => x.Name == name);
        if (q != null)
        {
            // FIX: Entity - это класс, меняем свойство напрямую. 
            // Синтаксис 'with' работает только для records.
            q.IsPaused = pause;
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
            // FIX: Прямая мутация
            q.ZombieTimeoutSeconds = parsedValue;
        }

        if (HubConnection is not null)
        {
            await HubConnection.InvokeAsync("UpdateQueueTimeout", name, parsedValue);
        }
    }

    private void HideQueue(string name) => _hiddenQueues.Add(name);
    private void ShowAllQueues() => _hiddenQueues.Clear();

    // FIX: Принимаем Entity
    private string GetDuration(QueueEntity q)
    {
        // NOTE: Убедись, что FirstJobAtUtc/LastJobAtUtc есть в Entity.
        // Если нет - придется убрать этот метод или брать данные из другого места.
        if (!q.FirstJobAtUtc.HasValue) return "-";

        var end = q.LastJobAtUtc ?? DateTime.UtcNow;
        if (q.PendingCount > 0 || q.ProcessingCount > 0) end = DateTime.UtcNow;
        var span = end - q.FirstJobAtUtc.Value;

        if (span.TotalHours >= 1) return span.ToString(@"hh\:mm\:ss");
        return span.ToString(@"mm\:ss");
    }

    public void Dispose() => _timer?.Dispose();
}