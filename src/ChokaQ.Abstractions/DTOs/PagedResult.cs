namespace ChokaQ.Abstractions.DTOs;

/// <summary>
/// Generic container for paginated data.
/// </summary>
public record PagedResult<T>(
    IEnumerable<T> Items,
    int TotalCount,
    int PageNumber,
    int PageSize
)
{
    public static PagedResult<T> Empty(int pageSize) => new(Enumerable.Empty<T>(), 0, 1, pageSize);
}