using System.Reflection;
using ChokaQ.Storage.SqlServer.DataEngine;

namespace ChokaQ.Tests.Unit.DataEngine;

[Trait(TestCategories.Category, TestCategories.Unit)]
public class QueriesReadConsistencyTests
{
    private static readonly HashSet<string> DashboardTelemetryQueries = new(StringComparer.Ordinal)
    {
        nameof(Queries.GetSummaryStats),
        nameof(Queries.GetQueueStats),
        nameof(Queries.GetQueueHealth),
        nameof(Queries.GetThroughputStats),
        nameof(Queries.GetTopDlqErrors)
    };

    [Fact]
    public void NoLockUsage_ShouldBeLimitedToPassiveDashboardTelemetry()
    {
        var queries = new Queries("chokaq");

        // NOLOCK is a conscious dashboard trade-off, not a default SQL style. This test protects
        // future contributors from accidentally adding dirty reads to fetch, ownership, bulk
        // preview, history inspection, or mutation paths where correctness matters more than
        // observer non-interference.
        var unexpectedNoLockQueries = GetStringQueries(queries)
            .Where(query => query.Sql.Contains("NOLOCK", StringComparison.OrdinalIgnoreCase))
            .Where(query => !DashboardTelemetryQueries.Contains(query.Name))
            .Select(query => query.Name)
            .ToArray();

        unexpectedNoLockQueries.Should().BeEmpty();
    }

    [Theory]
    [InlineData(nameof(Queries.FetchNextBatch))]
    [InlineData(nameof(Queries.GetDLQBulkIds))]
    [InlineData(nameof(Queries.GetActiveJobs))]
    [InlineData(nameof(Queries.GetArchiveJobs))]
    [InlineData(nameof(Queries.GetArchiveJob))]
    [InlineData(nameof(Queries.GetDLQJobs))]
    [InlineData(nameof(Queries.GetDLQJob))]
    [InlineData(nameof(Queries.GetQueues))]
    [InlineData(nameof(Queries.GetArchivePaged))]
    [InlineData(nameof(Queries.GetArchiveCount))]
    [InlineData(nameof(Queries.GetDLQPaged))]
    [InlineData(nameof(Queries.GetDLQCount))]
    public void OperatorDecisionQueries_ShouldUseCommittedReads(string queryName)
    {
        var queries = new Queries("chokaq");
        var sql = GetStringQueries(queries).Single(query => query.Name == queryName).Sql;

        sql.Should().NotContain("NOLOCK", because:
            "operator-facing lists, previews, and capacity decisions should read committed data");
    }

    private static IEnumerable<(string Name, string Sql)> GetStringQueries(Queries queries)
    {
        return typeof(Queries)
            .GetFields(BindingFlags.Instance | BindingFlags.Public)
            .Where(field => field.FieldType == typeof(string))
            .Select(field => (field.Name, Sql: (string)field.GetValue(queries)!));
    }
}
