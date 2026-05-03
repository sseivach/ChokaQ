using ChokaQ.Abstractions.DTOs;
using ChokaQ.TheDeck.UI.Pages;

namespace ChokaQ.Tests.Unit.TheDeck;

public class DashboardHistoryPagingTests
{
    [Fact]
    public void Normalize_ShouldClampInvalidPageInputs()
    {
        var filter = NewFilter(pageNumber: 0, pageSize: 0);

        var normalized = DashboardHistoryPaging.Normalize(filter);

        normalized.PageNumber.Should().Be(1);
        normalized.PageSize.Should().Be(1);
    }

    [Fact]
    public void ClampToAvailablePage_ShouldMoveBeyondLastPageToNewLastPage()
    {
        var filter = NewFilter(pageNumber: 5, pageSize: 10);

        var clamped = DashboardHistoryPaging.ClampToAvailablePage(filter, totalItems: 21);

        clamped.PageNumber.Should().Be(3);
        clamped.PageSize.Should().Be(10);
    }

    [Fact]
    public void ClampToAvailablePage_ShouldKeepFirstPageWhenNoItemsRemain()
    {
        var filter = NewFilter(pageNumber: 3, pageSize: 10);

        var clamped = DashboardHistoryPaging.ClampToAvailablePage(filter, totalItems: 0);

        clamped.PageNumber.Should().Be(1);
    }

    [Theory]
    [InlineData(0, 10, 1)]
    [InlineData(1, 10, 1)]
    [InlineData(10, 10, 1)]
    [InlineData(11, 10, 2)]
    public void CalculateTotalPages_ShouldAlwaysReturnAtLeastOnePage(int totalItems, int pageSize, int expected)
    {
        DashboardHistoryPaging.CalculateTotalPages(totalItems, pageSize).Should().Be(expected);
    }

    private static HistoryFilterDto NewFilter(int pageNumber, int pageSize)
    {
        return new HistoryFilterDto(
            FromUtc: null,
            ToUtc: null,
            SearchTerm: null,
            Queue: null,
            Status: null,
            PageNumber: pageNumber,
            PageSize: pageSize,
            SortBy: "Date",
            SortDescending: true);
    }
}
