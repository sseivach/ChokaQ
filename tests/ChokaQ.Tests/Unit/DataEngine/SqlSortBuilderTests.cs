using ChokaQ.Storage.SqlServer.DataEngine;

namespace ChokaQ.Tests.Unit.DataEngine;

public class SqlSortBuilderTests
{
    [Theory]
    [InlineData("id", false, false, "ORDER BY [Id] ASC")]
    [InlineData("queue", true, false, "ORDER BY [Queue] DESC")]
    [InlineData("priority", true, false, "ORDER BY [Priority] DESC")]
    [InlineData("priority", true, true, "ORDER BY 0 DESC")] // Archive has no priority
    [InlineData("unknown", false, false, "ORDER BY [FailedAtUtc] ASC")] // Default fallback
    [InlineData("unknown", false, true, "ORDER BY [FinishedAtUtc] ASC")] // Archive fallback
    [InlineData(null, false, false, "ORDER BY [FailedAtUtc] ASC")]
    public void BuildOrderBy_ShouldReturnCorrectClause(string? column, bool descending, bool isArchive, string expected)
    {
        var result = SqlSortBuilder.BuildOrderBy(column!, descending, isArchive);
        result.Should().Be(expected);
    }
}
