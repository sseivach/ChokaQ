using ChokaQ.Abstractions.DTOs;

namespace ChokaQ.TheDeck.UI.Pages;

internal static class DashboardHistoryPaging
{
    /// <summary>
    /// Normalizes operator-provided paging before the dashboard sends it to storage.
    /// The UI should be tolerant of malformed browser state or future query-string binding:
    /// page numbers and sizes below one do not have a useful SQL meaning, so we clamp them
    /// at the dashboard boundary instead of letting each storage provider defend separately.
    /// </summary>
    public static HistoryFilterDto Normalize(HistoryFilterDto filter)
    {
        return filter with
        {
            PageNumber = Math.Max(1, filter.PageNumber),
            PageSize = Math.Max(1, filter.PageSize)
        };
    }

    /// <summary>
    /// Keeps the current history page valid after destructive operations.
    /// Bulk purge/requeue can remove the last visible rows from Archive or DLQ. When that happens,
    /// an enterprise dashboard should move the operator to the new last page instead of showing an
    /// empty table while counters say data still exists.
    /// </summary>
    public static HistoryFilterDto ClampToAvailablePage(HistoryFilterDto filter, int totalItems)
    {
        var normalized = Normalize(filter);
        var totalPages = CalculateTotalPages(totalItems, normalized.PageSize);

        return normalized.PageNumber > totalPages
            ? normalized with { PageNumber = totalPages }
            : normalized;
    }

    public static int CalculateTotalPages(int totalItems, int pageSize)
    {
        return Math.Max(1, (int)Math.Ceiling(totalItems / (double)Math.Max(1, pageSize)));
    }
}
