using ChokaQ.Dashboard.Models;
using Microsoft.AspNetCore.Components;

namespace ChokaQ.Dashboard.Components.Shared;

public partial class JobRow
{
    [Parameter]
    public required JobViewModel Job { get; set; }

    [Parameter]
    public bool IsSelected { get; set; }

    [Parameter] public EventCallback<string> OnCancelRequested { get; set; }
    [Parameter] public EventCallback<string> OnRestartRequested { get; set; }

    // ВАЖНО: Изменили тип с <string> на <JobViewModel>
    // Теперь мы кидаем наверх весь объект, чтобы Инспектор открывался мгновенно
    [Parameter] public EventCallback<JobViewModel> OnSelected { get; set; }

    [Parameter] public EventCallback<bool> OnSelectionChanged { get; set; }

    private async Task CancelJob() => await OnCancelRequested.InvokeAsync(Job.Id);

    private async Task RestartJob() => await OnRestartRequested.InvokeAsync(Job.Id);

    // ВАЖНО: Передаем Job, а не Job.Id
    private async Task HandleRowClick() => await OnSelected.InvokeAsync(Job);

    private async Task HandleCheckboxChange(ChangeEventArgs e)
    {
        var isChecked = (bool)(e.Value ?? false);
        await OnSelectionChanged.InvokeAsync(isChecked);
    }

    // --- DEAD CODE REMOVED ---
    // FormatDuration -> Удалено (логика теперь в Razor или не нужна, так как DurationMs число)
    // GetStatusBadgeClass -> Удалено (теперь Job.StatusColor)
    // GetStatusLabel -> Удалено (теперь Job.Status.ToString())
}