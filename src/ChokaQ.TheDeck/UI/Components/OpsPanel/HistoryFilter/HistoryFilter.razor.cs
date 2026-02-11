using ChokaQ.Abstractions.DTOs;
using ChokaQ.TheDeck.Enums;
using Microsoft.AspNetCore.Components;

namespace ChokaQ.TheDeck.UI.Components.OpsPanel.HistoryFilter;

public partial class HistoryFilter
{
    // --- Parameters ---

    /// <summary>
    /// The source table we are querying (Archive or DLQ).
    /// Passed down from the main page to display context.
    /// </summary>
    [Parameter] public JobSource Context { get; set; } = JobSource.Archive;

    /// <summary>
    /// Total records found by the last query (used for pagination calculation).
    /// </summary>
    [Parameter] public int TotalItems { get; set; }

    /// <summary>
    /// Current active page number.
    /// </summary>
    [Parameter] public int CurrentPage { get; set; } = 1;

    /// <summary>
    /// Current page size limit.
    /// </summary>
    [Parameter] public int PageSize { get; set; } = 100;

    /// <summary>
    /// Event fired when the user clicks "Load", changes page, or changes sort.
    /// </summary>
    [Parameter] public EventCallback<HistoryFilterDto> OnLoadRequest { get; set; }

    // --- Local State ---

    private DateTime _dateFrom = DateTime.Today; // Default to Today
    private DateTime _dateTo = DateTime.Today;
    private string _searchTerm = "";
    private string _queue = "";
    private string _sortBy = "Date";
    private bool _sortDesc = true;
    private int _selectedPageSize = 100;

    // --- Computed Properties ---

    private int TotalPages => (int)Math.Ceiling((double)TotalItems / PageSize);
    private bool CanGoPrev => CurrentPage > 1;
    private bool CanGoNext => CurrentPage < TotalPages;

    protected override void OnParametersSet()
    {
        if (new[] { 100, 500, 1000 }.Contains(PageSize))
        {
            _selectedPageSize = PageSize;
        }
    }

    /// <summary>
    /// Constructs the DTO and fires the load event.
    /// </summary>
    private async Task TriggerLoad(int pageNumber = 1)
    {
        var filter = new HistoryFilterDto(
            FromUtc: _dateFrom.ToUniversalTime(),
            // End of the selected day (23:59:59)
            ToUtc: _dateTo.AddDays(1).AddTicks(-1).ToUniversalTime(),
            SearchTerm: _searchTerm,
            Queue: string.IsNullOrWhiteSpace(_queue) ? null : _queue,
            Status: null, // Status is inferred from the Context (Archive/DLQ)
            PageNumber: pageNumber,
            PageSize: _selectedPageSize,
            SortBy: _sortBy,
            SortDescending: _sortDesc
        );

        await OnLoadRequest.InvokeAsync(filter);
    }

    // --- Event Handlers ---

    private async Task HandleSearchClick()
    {
        // Reset to page 1 on new search
        await TriggerLoad(1);
    }

    private async Task HandlePageSizeChange(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out int size))
        {
            _selectedPageSize = size;
            await TriggerLoad(1); // Reset page to 1 to avoid offset issues
        }
    }

    private async Task HandlePrev()
    {
        if (CanGoPrev) await TriggerLoad(CurrentPage - 1);
    }

    private async Task HandleNext()
    {
        if (CanGoNext) await TriggerLoad(CurrentPage + 1);
    }

    private async Task HandleSortChange(ChangeEventArgs e)
    {
        _sortBy = e.Value?.ToString() ?? "Date";
        await TriggerLoad(1);
    }

    private async Task ToggleSortDirection()
    {
        _sortDesc = !_sortDesc;
        await TriggerLoad(1);
    }
}