using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using ChokaQ.TheDeck.Enums;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChokaQ.TheDeck.UI.Components.OpsPanel.HistoryFilter;

public partial class HistoryFilter
{
    private const string BulkOperationPurge = "PURGE";
    private const string BulkOperationRequeue = "REQUEUE";

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

    /// <summary>
    /// Live SignalR connection used for operator bulk commands.
    /// </summary>
    [Parameter] public HubConnection? HubConnection { get; set; }

    // --- Local State ---

    private DateTime _dateFrom = DateTime.Today; // Default to Today
    private DateTime _dateTo = DateTime.Today;
    private string _searchTerm = "";
    private string _queue = "";
    private string _failureReason = "";
    private string _jobType = "";
    private string _sortBy = "Date";
    private bool _sortDesc = true;
    private int _selectedPageSize = 100;
    private int _bulkMaxJobs = DlqBulkOperationFilterDto.DefaultMaxJobs;
    private bool _isBulkBusy;
    private string? _bulkOperation;
    private string _bulkConfirmation = "";
    private string? _bulkResultMessage;
    private DlqBulkOperationPreviewDto? _bulkPreview;

    // --- Computed Properties ---

    private int TotalPages => (int)Math.Ceiling((double)TotalItems / PageSize);
    private bool CanGoPrev => CurrentPage > 1;
    private bool CanGoNext => CurrentPage < TotalPages;
    private string BulkConfirmationToken => _bulkOperation == BulkOperationPurge
        ? BulkOperationPurge
        : BulkOperationRequeue;
    private bool CanExecuteBulk =>
        !_isBulkBusy
        && _bulkPreview is { WillAffectCount: > 0 }
        && string.Equals(_bulkConfirmation, BulkConfirmationToken, StringComparison.Ordinal);

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
            SortDescending: _sortDesc,
            FailureReason: ParseFailureReason()
        );

        await OnLoadRequest.InvokeAsync(filter);
    }

    private async Task PreviewBulkRequeue() => await PreviewBulkOperation(BulkOperationRequeue);

    private async Task PreviewBulkPurge() => await PreviewBulkOperation(BulkOperationPurge);

    private async Task PreviewBulkOperation(string operation)
    {
        if (Context != JobSource.DLQ || HubConnection is null)
            return;

        _isBulkBusy = true;
        _bulkOperation = operation;
        _bulkConfirmation = "";
        _bulkResultMessage = null;

        try
        {
            _bulkPreview = await HubConnection.InvokeAsync<DlqBulkOperationPreviewDto?>(
                "PreviewDLQBulkOperation",
                BuildDlqBulkFilter());
        }
        finally
        {
            _isBulkBusy = false;
        }
    }

    private async Task ExecuteBulkOperation()
    {
        if (!CanExecuteBulk || HubConnection is null || _bulkOperation is null)
            return;

        _isBulkBusy = true;

        try
        {
            var method = _bulkOperation == BulkOperationPurge
                ? "PurgeDLQByFilter"
                : "RequeueDLQByFilter";

            var affected = await HubConnection.InvokeAsync<int>(method, BuildDlqBulkFilter());
            _bulkResultMessage = $"{affected:N0} jobs affected";
            _bulkPreview = null;
            _bulkOperation = null;
            _bulkConfirmation = "";

            // Refresh the current DLQ view after the mutation so the operator sees the new truth
            // immediately. Enterprise admin tools should not leave stale rows on screen after a
            // destructive action succeeds.
            await TriggerLoad(1);
        }
        finally
        {
            _isBulkBusy = false;
        }
    }

    private DlqBulkOperationFilterDto BuildDlqBulkFilter()
    {
        var maxJobs = Math.Clamp(
            _bulkMaxJobs <= 0 ? DlqBulkOperationFilterDto.DefaultMaxJobs : _bulkMaxJobs,
            1,
            DlqBulkOperationFilterDto.AbsoluteMaxJobs);

        return new DlqBulkOperationFilterDto(
            Queue: string.IsNullOrWhiteSpace(_queue) ? null : _queue,
            FailureReason: ParseFailureReason(),
            Type: string.IsNullOrWhiteSpace(_jobType) ? null : _jobType,
            FromUtc: _dateFrom.ToUniversalTime(),
            ToUtc: _dateTo.AddDays(1).AddTicks(-1).ToUniversalTime(),
            SearchTerm: string.IsNullOrWhiteSpace(_searchTerm) ? null : _searchTerm,
            MaxJobs: maxJobs);
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

    private FailureReason? ParseFailureReason()
    {
        if (Context != JobSource.DLQ || string.IsNullOrWhiteSpace(_failureReason))
            return null;

        return Enum.TryParse<FailureReason>(_failureReason, ignoreCase: true, out var reason)
            ? reason
            : null;
    }
}
