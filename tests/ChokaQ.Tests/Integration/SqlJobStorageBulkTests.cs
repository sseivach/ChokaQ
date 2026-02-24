using ChokaQ.Abstractions.Enums;
using ChokaQ.Storage.SqlServer;
using ChokaQ.Tests.Fixtures;
using Microsoft.Extensions.Options;

namespace ChokaQ.Tests.Integration;

/// <summary>
/// Integration tests for Bulk operations via OPENJSON.
/// Requires a real SQL Server connection (Testcontainers).
/// </summary>
[Collection("SqlServer")]
public class SqlJobStorageBulkTests
{
    private readonly SqlServerFixture _fixture;
    private readonly SqlJobStorage _storage;
    private readonly string _testQueue = "bulk_test_queue";

    public SqlJobStorageBulkTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        var options = Options.Create(new SqlJobStorageOptions
        {
            ConnectionString = _fixture.ConnectionString,
            SchemaName = _fixture.Schema
        });
        _storage = new SqlJobStorage(options);
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task ArchiveCancelledBatchAsync_MovesJobsToDlq_AndUpdatesStats()
    {
        // Arrange: seed 50 jobs into the Hot table
        await _fixture.CleanTablesAsync();
        var jobIds = new List<string>();
        for (int i = 0; i < 50; i++)
        {
            var id = NewId();
            jobIds.Add(id);
            await _storage.EnqueueAsync(id, _testQueue, "TestJob", "{}", 10);
        }

        // Act: cancel all 50 in a single batch
        var cancelledCount = await _storage.ArchiveCancelledBatchAsync(jobIds.ToArray(), "AdminTest");

        // Assert
        cancelledCount.Should().Be(50);

        // Hot table should be empty for this queue
        var activeJobs = await _storage.GetActiveJobsAsync(100, queueFilter: _testQueue);
        activeJobs.Should().BeEmpty();

        // DLQ should contain 50 records with Cancelled reason
        var dlqJobs = (await _storage.GetDLQJobsAsync(100, queueFilter: _testQueue)).ToList();
        dlqJobs.Should().HaveCount(50);
        dlqJobs.Should().AllSatisfy(j => j.FailureReason.Should().Be(FailureReason.Cancelled));

        // StatsSummary.FailedTotal should reflect the 50 cancelled jobs
        var stats = (await _storage.GetQueueStatsAsync()).First(q => q.Queue == _testQueue);
        stats.FailedTotal.Should().Be(50);
    }

    [Fact]
    public async Task ResurrectBatchAsync_MovesJobsToHot_AndUpdatesStats()
    {
        // Arrange: enqueue 20 jobs, then cancel them so they land in DLQ
        await _fixture.CleanTablesAsync();
        var jobIds = new List<string>();
        for (int i = 0; i < 20; i++)
        {
            var id = NewId();
            jobIds.Add(id);
            await _storage.EnqueueAsync(id, _testQueue, "TestJob", "{}", 10);
        }
        await _storage.ArchiveCancelledBatchAsync(jobIds.ToArray());

        // Act: resurrect the entire batch
        var resurrectedCount = await _storage.ResurrectBatchAsync(jobIds.ToArray(), "Necromancer");

        // Assert
        resurrectedCount.Should().Be(20);

        // DLQ should now be empty
        var dlqJobs = await _storage.GetDLQJobsAsync(100, queueFilter: _testQueue);
        dlqJobs.Should().BeEmpty();

        // Hot table should contain 20 jobs back in Pending status
        var activeJobs = (await _storage.GetActiveJobsAsync(100, queueFilter: _testQueue)).ToList();
        activeJobs.Should().HaveCount(20);
        activeJobs.Should().AllSatisfy(j =>
        {
            j.Status.Should().Be(JobStatus.Pending);
            j.AttemptCount.Should().Be(0); // Attempt count must be reset
        });

        // FailedTotal should have rolled back to 0
        var stats = (await _storage.GetQueueStatsAsync()).First(q => q.Queue == _testQueue);
        stats.FailedTotal.Should().Be(0);
    }

    [Fact]
    public async Task PurgeDLQAsync_DeletesJobs_AndUpdatesStats()
    {
        // Arrange: push 30 jobs through to DLQ
        await _fixture.CleanTablesAsync();
        var jobIds = new List<string>();
        for (int i = 0; i < 30; i++)
        {
            var id = NewId();
            jobIds.Add(id);
            await _storage.EnqueueAsync(id, _testQueue, "TestJob", "{}", 10);
        }
        await _storage.ArchiveCancelledBatchAsync(jobIds.ToArray());

        // Sanity check: FailedTotal should be 30
        var preStats = (await _storage.GetQueueStatsAsync()).First(q => q.Queue == _testQueue);
        preStats.FailedTotal.Should().Be(30);

        // Act: purge half (15 jobs)
        var idsToPurge = jobIds.Take(15).ToArray();
        await _storage.PurgeDLQAsync(idsToPurge);

        // Assert: 15 should remain in DLQ
        var dlqJobs = await _storage.GetDLQJobsAsync(100, queueFilter: _testQueue);
        dlqJobs.Count().Should().Be(15);

        // Nothing should have moved to Hot — purged jobs are gone forever
        var activeJobs = await _storage.GetActiveJobsAsync(100, queueFilter: _testQueue);
        activeJobs.Should().BeEmpty();

        // Stats should recalculate via COUNT on DLQ
        var postStats = (await _storage.GetQueueStatsAsync()).First(q => q.Queue == _testQueue);
        postStats.FailedTotal.Should().Be(15);
    }
}
