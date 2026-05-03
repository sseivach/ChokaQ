using System.Diagnostics;
using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Observability;
using ChokaQ.Storage.SqlServer;
using ChokaQ.Tests.Fixtures;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace ChokaQ.Tests.Integration;

/// <summary>
/// SQL performance guardrails for the production hot paths.
/// </summary>
/// <remarks>
/// These are not microbenchmarks. CI runners, Docker, and SQL Server startup state are too noisy
/// for nanosecond-style assertions. Instead, each test seeds enough rows to make bad query shapes
/// visible and then checks that the operation stays inside a generous wall-clock budget. The goal
/// is to catch accidental full scans, missing-index regressions, or a future query edit that turns
/// the dashboard into a workload.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Category, TestCategories.Integration)]
public class SqlQueryPerformanceTests
{
    private static readonly TimeSpan HotPathBudget = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DashboardBudget = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan HistoryBudget = TimeSpan.FromSeconds(4);

    private readonly SqlServerFixture _fixture;
    private readonly SqlJobStorage _storage;

    public SqlQueryPerformanceTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        var options = Options.Create(new SqlJobStorageOptions
        {
            ConnectionString = _fixture.ConnectionString,
            SchemaName = _fixture.Schema,
            CommandTimeoutSeconds = 30
        });
        _storage = new SqlJobStorage(options, Substitute.For<IChokaQMetrics>());
    }

    [Fact]
    public async Task FetchNextBatchAsync_ShouldStayInsideHotPathBudget_WithLargeHotTable()
    {
        // Arrange: mix pending, fetched, and processing rows so the fetch query must use the
        // filtered pending index plus committed active counts. This guards the engine-room path:
        // fetching jobs must remain fast even when the Hot table contains non-pending work.
        await _fixture.CleanTablesAsync();
        var queue = $"perf-fetch-{Guid.NewGuid():N}";
        await SeedPerformanceDataAsync(queue, pending: 2500, fetched: 400, processing: 400);

        await _storage.FetchNextBatchAsync("warmup-worker", 10, new[] { queue });

        // Act
        var (elapsed, jobs) = await MeasureAsync(async () =>
            (await _storage.FetchNextBatchAsync("measured-worker", 50, new[] { queue })).ToList());

        // Assert
        jobs.Should().HaveCount(50);
        elapsed.Should().BeLessThan(HotPathBudget,
            "FetchNextBatchAsync is the highest-frequency SQL path and should stay index-backed. Actual: {0}", elapsed);
    }

    [Fact]
    public async Task GetSystemHealthAsync_ShouldStayInsideDashboardBudget_WithLargeOperationalSnapshot()
    {
        // Arrange: health combines queue lag, recent throughput, failure rate, and top DLQ errors.
        // The budget is intentionally broad, but the dataset is large enough to expose accidental
        // unbounded dashboard scans before they reach production.
        await _fixture.CleanTablesAsync();
        var queue = $"perf-health-{Guid.NewGuid():N}";
        await SeedPerformanceDataAsync(queue, pending: 2500, archive: 2000, dlq: 2000);

        await _storage.GetSystemHealthAsync();

        // Act
        var (elapsed, health) = await MeasureAsync(() => _storage.GetSystemHealthAsync().AsTask());

        // Assert
        health.Queues.Should().Contain(q => q.Queue == queue);
        health.TopErrors.Should().NotBeEmpty();
        elapsed.Should().BeLessThan(DashboardBudget,
            "GetSystemHealthAsync should remain a bounded dashboard snapshot, not an analytics scan. Actual: {0}", elapsed);
    }

    [Fact]
    public async Task HistoryPaging_ShouldStayInsideHistoryBudget_WithLargeArchiveAndDlq()
    {
        // Arrange: history pages back the operator inspector and follow-up actions. They now use
        // committed reads, so this test documents that correctness did not come at the cost of
        // obviously slow paging on normal dashboard-sized result sets.
        await _fixture.CleanTablesAsync();
        var queue = $"perf-history-{Guid.NewGuid():N}";
        await SeedPerformanceDataAsync(queue, archive: 3000, dlq: 3000);

        var archiveFilter = new HistoryFilterDto(
            FromUtc: DateTime.UtcNow.AddDays(-2),
            ToUtc: DateTime.UtcNow.AddMinutes(1),
            SearchTerm: null,
            Queue: queue,
            Status: null,
            PageNumber: 1,
            PageSize: 100,
            SortBy: "Date",
            SortDescending: true);

        var dlqFilter = archiveFilter with
        {
            FailureReason = FailureReason.FatalError
        };

        await _storage.GetArchivePagedAsync(archiveFilter);
        await _storage.GetDLQPagedAsync(dlqFilter);

        // Act
        var (archiveElapsed, archivePage) = await MeasureAsync(() => _storage.GetArchivePagedAsync(archiveFilter).AsTask());
        var (dlqElapsed, dlqPage) = await MeasureAsync(() => _storage.GetDLQPagedAsync(dlqFilter).AsTask());

        // Assert
        archivePage.Items.Should().HaveCount(100);
        dlqPage.Items.Should().NotBeEmpty();
        archiveElapsed.Should().BeLessThan(HistoryBudget,
            "Archive paging should use date/queue indexes and stay responsive. Actual: {0}", archiveElapsed);
        dlqElapsed.Should().BeLessThan(HistoryBudget,
            "DLQ paging should use reason/date indexes and stay responsive. Actual: {0}", dlqElapsed);
    }

    private async Task SeedPerformanceDataAsync(
        string queue,
        int pending = 0,
        int fetched = 0,
        int processing = 0,
        int archive = 0,
        int dlq = 0)
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 30;
        cmd.CommandText = $@"
            IF NOT EXISTS (SELECT 1 FROM [{_fixture.Schema}].[Queues] WHERE [Name] = @Queue)
            BEGIN
                INSERT INTO [{_fixture.Schema}].[Queues]
                ([Name], [IsPaused], [IsActive], [ZombieTimeoutSeconds], [MaxWorkers], [LastUpdatedUtc])
                VALUES (@Queue, 0, 1, NULL, NULL, SYSUTCDATETIME());
            END;

            IF NOT EXISTS (SELECT 1 FROM [{_fixture.Schema}].[StatsSummary] WHERE [Queue] = @Queue)
            BEGIN
                INSERT INTO [{_fixture.Schema}].[StatsSummary]
                ([Queue], [SucceededTotal], [FailedTotal], [RetriedTotal], [LastActivityUtc])
                VALUES (@Queue, 0, 0, 0, SYSUTCDATETIME());
            END;

            ;WITH n AS (
                SELECT TOP (@PendingCount) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                FROM sys.all_objects a CROSS JOIN sys.all_objects b
            )
            INSERT INTO [{_fixture.Schema}].[JobsHot]
            ([Id], [Queue], [Type], [Payload], [Tags], [IdempotencyKey], [Priority], [Status], [AttemptCount],
             [WorkerId], [HeartbeatUtc], [ScheduledAtUtc], [CreatedAtUtc], [StartedAtUtc], [LastUpdatedUtc],
             [CreatedBy], [LastModifiedBy])
            SELECT
                CONCAT(@RunId, '-pending-', n),
                @Queue,
                CONCAT('PerfJob', n % 10),
                N'{{""kind"":""pending""}}',
                CASE WHEN n % 5 = 0 THEN 'perf,hot' ELSE NULL END,
                NULL,
                10 + (n % 50),
                0,
                0,
                NULL,
                NULL,
                NULL,
                DATEADD(SECOND, -n, SYSUTCDATETIME()),
                NULL,
                SYSUTCDATETIME(),
                'perf-test',
                NULL
            FROM n;

            ;WITH n AS (
                SELECT TOP (@FetchedCount) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                FROM sys.all_objects a CROSS JOIN sys.all_objects b
            )
            INSERT INTO [{_fixture.Schema}].[JobsHot]
            ([Id], [Queue], [Type], [Payload], [Tags], [IdempotencyKey], [Priority], [Status], [AttemptCount],
             [WorkerId], [HeartbeatUtc], [ScheduledAtUtc], [CreatedAtUtc], [StartedAtUtc], [LastUpdatedUtc],
             [CreatedBy], [LastModifiedBy])
            SELECT
                CONCAT(@RunId, '-fetched-', n),
                @Queue,
                CONCAT('PerfJob', n % 10),
                N'{{""kind"":""fetched""}}',
                NULL,
                NULL,
                10,
                1,
                0,
                'perf-worker',
                NULL,
                NULL,
                DATEADD(SECOND, -n, SYSUTCDATETIME()),
                NULL,
                SYSUTCDATETIME(),
                'perf-test',
                NULL
            FROM n;

            ;WITH n AS (
                SELECT TOP (@ProcessingCount) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                FROM sys.all_objects a CROSS JOIN sys.all_objects b
            )
            INSERT INTO [{_fixture.Schema}].[JobsHot]
            ([Id], [Queue], [Type], [Payload], [Tags], [IdempotencyKey], [Priority], [Status], [AttemptCount],
             [WorkerId], [HeartbeatUtc], [ScheduledAtUtc], [CreatedAtUtc], [StartedAtUtc], [LastUpdatedUtc],
             [CreatedBy], [LastModifiedBy])
            SELECT
                CONCAT(@RunId, '-processing-', n),
                @Queue,
                CONCAT('PerfJob', n % 10),
                N'{{""kind"":""processing""}}',
                NULL,
                NULL,
                10,
                2,
                1,
                'perf-worker',
                DATEADD(SECOND, -5, SYSUTCDATETIME()),
                NULL,
                DATEADD(SECOND, -n, SYSUTCDATETIME()),
                DATEADD(SECOND, -10, SYSUTCDATETIME()),
                SYSUTCDATETIME(),
                'perf-test',
                NULL
            FROM n;

            ;WITH n AS (
                SELECT TOP (@ArchiveCount) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                FROM sys.all_objects a CROSS JOIN sys.all_objects b
            )
            INSERT INTO [{_fixture.Schema}].[JobsArchive]
            ([Id], [Queue], [Type], [Payload], [Tags], [AttemptCount], [WorkerId], [CreatedBy], [LastModifiedBy],
             [CreatedAtUtc], [StartedAtUtc], [FinishedAtUtc], [DurationMs])
            SELECT
                CONCAT(@RunId, '-archive-', n),
                @Queue,
                CONCAT('PerfJob', n % 10),
                N'{{""kind"":""archive""}}',
                CASE WHEN n % 7 = 0 THEN 'perf,archive' ELSE NULL END,
                1 + (n % 3),
                'perf-worker',
                'perf-test',
                NULL,
                DATEADD(MINUTE, -n, SYSUTCDATETIME()),
                DATEADD(MINUTE, -n, SYSUTCDATETIME()),
                DATEADD(SECOND, -n, SYSUTCDATETIME()),
                CAST(10 + (n % 250) AS float)
            FROM n;

            ;WITH n AS (
                SELECT TOP (@DlqCount) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                FROM sys.all_objects a CROSS JOIN sys.all_objects b
            )
            INSERT INTO [{_fixture.Schema}].[JobsDLQ]
            ([Id], [Queue], [Type], [Payload], [Tags], [FailureReason], [ErrorDetails], [AttemptCount],
             [WorkerId], [CreatedBy], [LastModifiedBy], [CreatedAtUtc], [FailedAtUtc])
            SELECT
                CONCAT(@RunId, '-dlq-', n),
                @Queue,
                CONCAT('PerfJob', n % 10),
                N'{{""kind"":""dlq""}}',
                CASE WHEN n % 7 = 0 THEN 'perf,dlq' ELSE NULL END,
                CASE WHEN n % 2 = 0 THEN @FatalReason ELSE @TransientReason END,
                CONCAT(N'Performance baseline failure family ', n % 5),
                3,
                'perf-worker',
                'perf-test',
                NULL,
                DATEADD(MINUTE, -n, SYSUTCDATETIME()),
                DATEADD(SECOND, -n, SYSUTCDATETIME())
            FROM n;

            UPDATE [{_fixture.Schema}].[StatsSummary]
            SET [SucceededTotal] = @ArchiveCount,
                [FailedTotal] = @DlqCount,
                [RetriedTotal] = 0,
                [LastActivityUtc] = SYSUTCDATETIME()
            WHERE [Queue] = @Queue;";

        cmd.Parameters.AddWithValue("@Queue", queue);
        cmd.Parameters.AddWithValue("@RunId", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("@PendingCount", pending);
        cmd.Parameters.AddWithValue("@FetchedCount", fetched);
        cmd.Parameters.AddWithValue("@ProcessingCount", processing);
        cmd.Parameters.AddWithValue("@ArchiveCount", archive);
        cmd.Parameters.AddWithValue("@DlqCount", dlq);
        cmd.Parameters.AddWithValue("@FatalReason", (int)FailureReason.FatalError);
        cmd.Parameters.AddWithValue("@TransientReason", (int)FailureReason.Transient);

        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<(TimeSpan Elapsed, T Result)> MeasureAsync<T>(Func<Task<T>> action)
    {
        var sw = Stopwatch.StartNew();
        var result = await action();
        sw.Stop();
        return (sw.Elapsed, result);
    }
}
