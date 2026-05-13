using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Observability;
using ChokaQ.Storage.SqlServer;
using ChokaQ.Tests.Fixtures;
using Microsoft.Extensions.Options;

namespace ChokaQ.Tests.Integration;

/// <summary>
/// Integration tests for Bulk operations via OPENJSON.
/// Requires a real SQL Server connection (Testcontainers).
/// </summary>
[Collection("SqlServer")]
[Trait(TestCategories.Category, TestCategories.Integration)]
public class SqlJobStorageBulkTests
{
    private readonly SqlServerFixture _fixture;
    private readonly SqlJobStorage _storage;
    private readonly string _testQueue = "bulk_test_queue";

    public SqlJobStorageBulkTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _storage = CreateStorage();
    }

    private SqlJobStorage CreateStorage(int cleanupBatchSize = 1000)
    {
        var options = Options.Create(new SqlJobStorageOptions
        {
            ConnectionString = _fixture.ConnectionString,
            SchemaName = _fixture.Schema,
            CleanupBatchSize = cleanupBatchSize
        });
        return new SqlJobStorage(options, Substitute.For<IChokaQMetrics>());
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

    [Fact]
    public async Task PurgeDLQAsync_ShouldDeleteSelectedJobsAcrossConfiguredBatches()
    {
        // Arrange: use a tiny cleanup batch to force the production retention loop to run
        // multiple short transactions. The behavior should stay identical to one operator action:
        // selected rows disappear, unselected rows remain, and counters reflect actual deletes.
        await _fixture.CleanTablesAsync();
        var storage = CreateStorage(cleanupBatchSize: 2);
        var jobIds = new List<string>();

        for (int i = 0; i < 7; i++)
        {
            var id = NewId();
            jobIds.Add(id);
            await storage.EnqueueAsync(id, _testQueue, "TestJob", "{}", 10);
        }

        await storage.ArchiveCancelledBatchAsync(jobIds.ToArray());

        // Act: five selected IDs require three SQL delete batches with CleanupBatchSize=2.
        await storage.PurgeDLQAsync(jobIds.Take(5).ToArray());

        // Assert
        var remaining = (await storage.GetDLQJobsAsync(100, queueFilter: _testQueue)).ToList();
        remaining.Should().HaveCount(2);
        remaining.Select(j => j.Id).Should().BeEquivalentTo(jobIds.Skip(5));

        var stats = (await storage.GetQueueStatsAsync()).First(q => q.Queue == _testQueue);
        stats.FailedTotal.Should().Be(2);
    }

    [Fact]
    public async Task PurgeArchiveAsync_ShouldDeleteOldHistoryAcrossConfiguredBatches()
    {
        // Arrange: archive cleanup uses the same batch philosophy as DLQ purge. This test keeps
        // the batch size tiny so we prove the public method loops until the retention window is
        // clean and still returns the total number of rows actually deleted.
        await _fixture.CleanTablesAsync();
        var storage = CreateStorage(cleanupBatchSize: 2);
        var jobIds = new List<string>();

        for (int i = 0; i < 5; i++)
        {
            var id = NewId();
            jobIds.Add(id);
            await storage.EnqueueAsync(id, _testQueue, "TestJob", "{}", 10);
        }

        await storage.FetchNextBatchAsync("archive-cleanup-worker", 10);

        foreach (var id in jobIds)
        {
            await storage.ArchiveSucceededAsync(id, durationMs: 10);
        }

        // Act: all five rows are older than a future cutoff and require three SQL batches.
        var deleted = await storage.PurgeArchiveAsync(DateTime.UtcNow.AddDays(1));

        // Assert
        deleted.Should().Be(5);
        var archived = await storage.GetArchiveJobsAsync(100, queueFilter: _testQueue);
        archived.Should().BeEmpty();

        var stats = (await storage.GetQueueStatsAsync()).First(q => q.Queue == _testQueue);
        stats.SucceededTotal.Should().Be(0);
    }

    [Fact]
    public async Task PreviewDLQBulkOperationAsync_ShouldReturnMatchedCountAndSample()
    {
        // Arrange: create a mixed DLQ so the preview proves the filter is typed, not textual.
        await _fixture.CleanTablesAsync();
        var fatalEmail = NewId();
        var transientEmail = NewId();
        var fatalBilling = NewId();

        await _storage.EnqueueAsync(fatalEmail, _testQueue, "EmailJob", "{}", 10);
        await _storage.EnqueueAsync(transientEmail, _testQueue, "EmailJob", "{}", 10);
        await _storage.EnqueueAsync(fatalBilling, _testQueue, "BillingJob", "{}", 10);
        await _storage.ArchiveFailedAsync(fatalEmail, "bad template", failureReason: FailureReason.FatalError);
        await _storage.ArchiveFailedAsync(transientEmail, "gateway blip", failureReason: FailureReason.Transient);
        await _storage.ArchiveFailedAsync(fatalBilling, "bad invoice", failureReason: FailureReason.FatalError);

        var filter = new DlqBulkOperationFilterDto(
            Queue: _testQueue,
            FailureReason: FailureReason.FatalError,
            Type: "EmailJob",
            MaxJobs: 10);

        // Act
        var preview = await _storage.PreviewDLQBulkOperationAsync(filter);

        // Assert
        preview.MatchedCount.Should().Be(1);
        preview.WillAffectCount.Should().Be(1);
        preview.SampleJobIds.Should().ContainSingle().Which.Should().Be(fatalEmail);
    }

    [Fact]
    public async Task PurgeDLQByFilterAsync_ShouldDeleteOnlyMatchingSubset()
    {
        // Arrange: three matching failures, capped to two affected rows.
        await _fixture.CleanTablesAsync();
        var ids = new[] { NewId(), NewId(), NewId() };

        foreach (var id in ids)
        {
            await _storage.EnqueueAsync(id, _testQueue, "EmailJob", "{}", 10);
            await _storage.ArchiveFailedAsync(id, "fatal", failureReason: FailureReason.FatalError);
        }

        var filter = new DlqBulkOperationFilterDto(
            Queue: _testQueue,
            FailureReason: FailureReason.FatalError,
            Type: "EmailJob",
            MaxJobs: 2);

        // Act
        var purged = await _storage.PurgeDLQByFilterAsync(filter);

        // Assert
        purged.Should().Be(2);
        var remaining = (await _storage.GetDLQJobsAsync(10, queueFilter: _testQueue)).ToList();
        remaining.Should().HaveCount(1);

        var stats = (await _storage.GetQueueStatsAsync()).First(q => q.Queue == _testQueue);
        stats.FailedTotal.Should().Be(1);
    }

    [Fact]
    public async Task ResurrectDLQByFilterAsync_ShouldMoveMatchesBackToHot()
    {
        // Arrange: only throttled webhook failures should be requeued.
        await _fixture.CleanTablesAsync();
        var throttledId = NewId();
        var fatalId = NewId();

        await _storage.EnqueueAsync(throttledId, _testQueue, "WebhookJob", "{}", 10);
        await _storage.EnqueueAsync(fatalId, _testQueue, "WebhookJob", "{}", 10);
        await _storage.ArchiveFailedAsync(throttledId, "429", failureReason: FailureReason.Throttled);
        await _storage.ArchiveFailedAsync(fatalId, "bad payload", failureReason: FailureReason.FatalError);

        var filter = new DlqBulkOperationFilterDto(
            Queue: _testQueue,
            FailureReason: FailureReason.Throttled,
            Type: "WebhookJob",
            MaxJobs: 100);

        // Act
        var resurrected = await _storage.ResurrectDLQByFilterAsync(filter, "ops@example.com");

        // Assert
        resurrected.Should().Be(1);

        var hotJob = await _storage.GetJobAsync(throttledId);
        hotJob.Should().NotBeNull();
        hotJob!.Status.Should().Be(JobStatus.Pending);
        hotJob.LastModifiedBy.Should().Be("ops@example.com");

        var dlq = (await _storage.GetDLQJobsAsync(10, queueFilter: _testQueue)).ToList();
        dlq.Should().ContainSingle(j => j.Id == fatalId);
    }
}
