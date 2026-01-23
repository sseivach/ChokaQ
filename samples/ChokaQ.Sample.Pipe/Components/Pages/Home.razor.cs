using ChokaQ.Abstractions;
using ChokaQ.Sample.Pipe.Jobs;
using Microsoft.AspNetCore.Components;

namespace ChokaQ.Sample.Pipe.Components.Pages;

public partial class Home
{
    [Inject] public IChokaQQueue Queue { get; set; } = default!;

    private string _selectedType = "log";
    private int _count = 1;
    private bool _isBusy;
    private string? _message;

    private async Task Fire()
    {
        _isBusy = true;
        _message = null;

        // Run in background to avoid blocking UI thread on heavy enqueues
        await Task.Run(async () =>
        {
            var tasks = new List<Task>();

            for (int i = 0; i < _count; i++)
            {
                // In Pipe Mode, we send typed objects (DTOs).
                // The library uses the class name ("FastLog", "MetricEvent") as the Job Type Key.
                // The GlobalPipeHandler will receive this key and the JSON payload.

                Task t = _selectedType switch
                {
                    "log" => Queue.EnqueueAsync(new FastLog($"Log entry #{i}", "INFO")),
                    "metric" => Queue.EnqueueAsync(new MetricEvent("Sensor-01", Random.Shared.NextDouble() * 100, DateTime.UtcNow)),
                    "heavy" => Queue.EnqueueAsync(new SlowOperation(2000)),
                    _ => Task.CompletedTask
                };
                tasks.Add(t);
            }

            await Task.WhenAll(tasks);
        });

        _isBusy = false;
        _message = $"Sent {_count} events of type '{_selectedType}'!";
    }
}