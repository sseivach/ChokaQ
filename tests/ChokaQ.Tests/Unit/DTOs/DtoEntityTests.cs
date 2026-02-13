using ChokaQ.Abstractions.DTOs;

namespace ChokaQ.Tests.Unit.DTOs;

public class DtoEntityTests
{
    [Fact]
    public void JobDataUpdateDto_HasChanges_True_WhenPayloadSet()
    {
        var dto = new JobDataUpdateDto("payload", null, null);
        dto.HasChanges.Should().BeTrue();
    }

    [Fact]
    public void JobDataUpdateDto_HasChanges_True_WhenPrioritySet()
    {
        var dto = new JobDataUpdateDto(null, null, 1);
        dto.HasChanges.Should().BeTrue();
    }

    [Fact]
    public void JobDataUpdateDto_HasChanges_False_WhenEmpty()
    {
        var dto = new JobDataUpdateDto(null, null, null);
        dto.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void PagedResult_Empty_ShouldHaveZeroTotalCount()
    {
        var result = new PagedResult<string>([], 0, 1, 10);
        result.TotalCount.Should().Be(0);
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public void HistoryFilterDto_Defaults()
    {
        var filter = new HistoryFilterDto(null, null, null, null, null);
        filter.PageNumber.Should().Be(1);
        filter.PageSize.Should().Be(100);
        filter.SortBy.Should().Be("Date");
        filter.SortDescending.Should().BeTrue();
    }
}
