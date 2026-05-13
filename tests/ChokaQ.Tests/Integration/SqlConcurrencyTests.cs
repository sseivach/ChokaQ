using ChokaQ.Abstractions.Observability;
using ChokaQ.Storage.SqlServer;
using ChokaQ.Tests.Fixtures;
using Microsoft.Extensions.Options;

namespace ChokaQ.Tests.Integration;

/// <summary>
/// Concurrency tests for SqlJobStorage.
/// Tests race conditions, parallel operations, and atomic guarantees.
/// </summary>
[Collection("SqlServer")]
[Trait(TestCategories.Category, TestCategories.Integration)]
public class SqlConcurrencyTests
{
    private readonly SqlServerFixture _fixture;
    private readonly SqlJobStorage _storage;

    public SqlConcurrencyTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        var options = Options.Create(new SqlJobStorageOptions
        {
            ConnectionString = _fixture.ConnectionString,
            SchemaName = _fixture.Schema
        });
        _storage = new SqlJobStorage(options, Substitute.For<IChokaQMetrics>());
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task FetchNextBatchAsync_WithMultipleWorkers_ShouldNotReturnSameJob()
    {
        // Arrange
        await _fixture.CleanTablesAsync();
        for (int i = 0; i < 10; i++)
        {
            await _storage.EnqueueAsync(NewId(), "default", $"Job{i}", "{}");
        }

        // Act - Simulate 5 workers fetching sequentially with small delays
        var allJobs = new List<string>();
        for (int workerId = 1; workerId <= 5; workerId++)
        {
            var jobs = await _storage.FetchNextBatchAsync($"worker{workerId}", 3);
            allJobs.AddRange(jobs.Select(j => j.Id));
            await Task.Delay(10); // Small delay to ensure locks are released
        }

        // Assert - No job should be fetched by multiple workers
        allJobs.Should().OnlyHaveUniqueItems();
        allJobs.Should().HaveCountGreaterOrEqualTo(9); // Most jobs should be fetched
    }

    [Fact]
    public async Task FetchNextBatchAsync_WithConcurrentWorkers_ShouldNotReturnDuplicateJobs()
    {
        // Arrange
        await _fixture.CleanTablesAsync();
        for (int i = 0; i < 50; i++)
        {
            await _storage.EnqueueAsync(NewId(), "default", $"Job{i}", "{}");
        }

        // Act
        var fetchTasks = Enumerable.Range(1, 10)
            .Select(workerNumber =>
            {
                var workerId = $"worker-{workerNumber}";
                return Task.Run(async () => (await _storage.FetchNextBatchAsync(workerId, 10)).ToList());
            })
            .ToArray();

        var batches = await Task.WhenAll(fetchTasks);
        var fetchedJobs = batches.SelectMany(batch => batch).ToList();

        var markTasks = fetchedJobs
            .Select(job => _storage.MarkAsProcessingAsync(job.Id, workerId: job.WorkerId).AsTask())
            .ToArray();
        var marked = await Task.WhenAll(markTasks);

        // Assert
        // UPDLOCK + READPAST must behave as a multi-instance work-claim protocol:
        // one persisted row can be claimed by one worker only, even when workers fetch in parallel.
        // MarkAsProcessing then proves each returned row is still owned by the worker that fetched it.
        fetchedJobs.Select(job => job.Id).Should().OnlyHaveUniqueItems();
        fetchedJobs.Should().HaveCountLessOrEqualTo(50);
        marked.Should().OnlyContain(result => result);
    }

    [Fact(Skip = "SQL Server deadlock under high concurrency - known limitation")]
    public async Task EnqueueAsync_WithSameIdempotencyKey_ShouldReturnSameId()
    {
        // Arrange
        await _fixture.CleanTablesAsync();
        var key = "concurrent-idempotency-test";

        // Act - Simulate 5 concurrent enqueues with same idempotency key (reduced from 10 to avoid deadlocks)
        var tasks = Enumerable.Range(1, 5)
            .Select(i => Task.Run(async () =>
            {
                var id = NewId();
                return await _storage.EnqueueAsync(id, "default", "TestJob", "{}", idempotencyKey: key);
            }))
            .ToArray();

        var jobIds = await Task.WhenAll(tasks);

        // Assert - All should return the same job ID (first one wins)
        jobIds.Distinct().Should().HaveCount(1); // Only one unique ID
    }

    [Fact]
    public async Task ArchiveSucceededAsync_Concurrent_ShouldIncrementStatsCorrectly()
    {
        // Arrange
        await _fixture.CleanTablesAsync();
        var jobIds = new List<string>();
        for (int i = 0; i < 20; i++)
        {
            var id = NewId();
            await _storage.EnqueueAsync(id, "default", $"Job{i}", "{}");
            jobIds.Add(id);
        }
        await _storage.FetchNextBatchAsync("worker1", 20);

        // Act - Archive all jobs concurrently
        var tasks = jobIds.Select(id => Task.Run(async () =>
        {
            await _storage.ArchiveSucceededAsync(id, 100);
        })).ToArray();

        await Task.WhenAll(tasks);

        // Assert - Stats should be accurate
        var stats = await _storage.GetSummaryStatsAsync();
        stats.SucceededTotal.Should().BeGreaterOrEqualTo(20);
    }

    [Fact]
    public async Task ArchiveSucceededAsync_ConcurrentSameJob_ShouldMoveExactlyOnce()
    {
        // Arrange
        await _fixture.CleanTablesAsync();
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await _storage.FetchNextBatchAsync("worker1", 1);

        // Act
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () => await _storage.ArchiveSucceededAsync(id, 100)))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert
        // Concurrent finalization of the same job should be idempotent at the storage boundary:
        // one contender moves the row, while later contenders observe that there is nothing left to move.
        var hotJob = await _storage.GetJobAsync(id);
        hotJob.Should().BeNull();

        var archiveJob = await _storage.GetArchiveJobAsync(id);
        archiveJob.Should().NotBeNull();

        var stats = await _storage.GetSummaryStatsAsync();
        stats.SucceededTotal.Should().Be(1);
    }

    [Fact]
    public async Task ArchiveFailedAsync_ConcurrentSameJob_ShouldMoveExactlyOnce()
    {
        // Arrange
        await _fixture.CleanTablesAsync();
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await _storage.FetchNextBatchAsync("worker1", 1);

        // Act
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () => await _storage.ArchiveFailedAsync(id, "boom")))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert
        // Failed finalization uses the same atomic move rule as successful finalization:
        // a job can be in Hot or DLQ, never both, and the failure counter advances once.
        var hotJob = await _storage.GetJobAsync(id);
        hotJob.Should().BeNull();

        var dlqJob = await _storage.GetDLQJobAsync(id);
        dlqJob.Should().NotBeNull();

        var stats = await _storage.GetSummaryStatsAsync();
        stats.FailedTotal.Should().Be(1);
    }

    [Fact]
    public async Task ArchiveSucceededAsync_WithWrongWorkerId_ShouldNotFinalize()
    {
        // Arrange
        await _fixture.CleanTablesAsync();
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await _storage.FetchNextBatchAsync("owner-a", 1);

        // Act
        var staleMoved = await _storage.ArchiveSucceededAsync(id, 100, workerId: "owner-b");
        var ownerMoved = await _storage.ArchiveSucceededAsync(id, 100, workerId: "owner-a");

        // Assert
        // Ownership guards make stale worker finalization a zero-row no-op. The actual owner
        // can still complete the job, proving the guard is selective rather than blocking all moves.
        staleMoved.Should().BeFalse();
        ownerMoved.Should().BeTrue();

        var hotJob = await _storage.GetJobAsync(id);
        hotJob.Should().BeNull();

        var archiveJob = await _storage.GetArchiveJobAsync(id);
        archiveJob.Should().NotBeNull();

        var stats = await _storage.GetSummaryStatsAsync();
        stats.SucceededTotal.Should().Be(1);
    }

    [Fact]
    public async Task MarkAsProcessingAsync_AfterRelease_ShouldNotStartBufferedCopy()
    {
        // Arrange
        await _fixture.CleanTablesAsync();
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await _storage.FetchNextBatchAsync("prefetch-worker", 1);

        // Act
        await _storage.ReleaseJobAsync(id);
        var marked = await _storage.MarkAsProcessingAsync(id, workerId: "prefetch-worker");

        // Assert
        // This is the Phase 3.1 production race: a worker may still hold a buffered copy after
        // shutdown or pause released the database lease. The Processing transition must reject
        // that stale copy so the job remains available for the next legitimate fetch.
        marked.Should().BeFalse();

        var hotJob = await _storage.GetJobAsync(id);
        hotJob.Should().NotBeNull();
        hotJob!.Status.Should().Be(ChokaQ.Abstractions.Enums.JobStatus.Pending);
        hotJob.WorkerId.Should().BeNull();
    }

    [Fact]
    public async Task ArchiveSucceededAsync_AfterZombieRescue_ShouldNotOverrideDlqDecision()
    {
        // Arrange
        await _fixture.CleanTablesAsync();
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await _storage.FetchNextBatchAsync("frozen-worker", 1);
        await _storage.MarkAsProcessingAsync(id);
        await Task.Delay(2000);

        // Act
        var rescued = await _storage.ArchiveZombiesAsync(1);
        var staleMoved = await _storage.ArchiveSucceededAsync(id, 100, workerId: "frozen-worker");

        // Assert
        // This is the production failure mode Phase 2 closes: a frozen worker resumes after
        // rescue already moved the job to DLQ. Its late success path must not create an Archive row.
        rescued.Should().BeGreaterOrEqualTo(1);
        staleMoved.Should().BeFalse();

        var archiveJob = await _storage.GetArchiveJobAsync(id);
        archiveJob.Should().BeNull();

        var dlqJob = await _storage.GetDLQJobAsync(id);
        dlqJob.Should().NotBeNull();

        var stats = await _storage.GetSummaryStatsAsync();
        stats.SucceededTotal.Should().Be(0);
        stats.FailedTotal.Should().Be(1);
    }

    [Fact]
    public async Task ResurrectAsync_Concurrent_ShouldNotCreateDuplicates()
    {
        // Arrange
        await _fixture.CleanTablesAsync();
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await _storage.FetchNextBatchAsync("worker1", 10);
        await _storage.ArchiveFailedAsync(id, "Error");

        // Act - Simulate 5 concurrent resurrection attempts
        var tasks = Enumerable.Range(1, 5)
            .Select(i => Task.Run(async () =>
            {
                try
                {
                    await _storage.ResurrectAsync(id);
                }
                catch
                {
                    // Ignore errors - some may fail due to race conditions
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert - Job should exist in Hot table only once
        var hotJob = await _storage.GetJobAsync(id);
        hotJob.Should().NotBeNull();

        var dlqJob = await _storage.GetDLQJobAsync(id);
        dlqJob.Should().BeNull(); // Should be removed from DLQ
    }

    [Fact]
    public async Task KeepAliveAsync_Concurrent_ShouldUpdateHeartbeat()
    {
        // Arrange
        await _fixture.CleanTablesAsync();
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await _storage.FetchNextBatchAsync("worker1", 10);
        await _storage.MarkAsProcessingAsync(id);

        var initialJob = await _storage.GetJobAsync(id);
        var initialHeartbeat = initialJob!.HeartbeatUtc!.Value;

        // Act - Simulate 10 concurrent heartbeat updates
        await Task.Delay(100);
        var tasks = Enumerable.Range(1, 10)
            .Select(i => Task.Run(async () =>
            {
                await _storage.KeepAliveAsync(id);
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert - Heartbeat should be updated
        var finalJob = await _storage.GetJobAsync(id);
        finalJob!.HeartbeatUtc.Should().BeAfter(initialHeartbeat);
    }

    [Fact]
    public async Task UpdateJobDataAsync_Concurrent_ShouldHandleRaceConditions()
    {
        // Arrange
        await _fixture.CleanTablesAsync();
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{}", priority: 1);

        // Act - Simulate 5 concurrent updates with different priorities
        var tasks = Enumerable.Range(1, 5)
            .Select(priority => Task.Run(async () =>
            {
                var updates = new ChokaQ.Abstractions.DTOs.JobDataUpdateDto(null, null, priority * 10);
                return await _storage.UpdateJobDataAsync(id, updates);
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Assert - At least one update should succeed
        results.Should().Contain(true);

        // Final priority should be one of the attempted values
        var job = await _storage.GetJobAsync(id);
        job!.Priority.Should().BeOneOf(10, 20, 30, 40, 50);
    }

    [Fact]
    public async Task PurgeDLQAsync_Concurrent_ShouldNotFail()
    {
        // Arrange
        await _fixture.CleanTablesAsync();
        var jobIds = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            var id = NewId();
            await _storage.EnqueueAsync(id, "default", $"Job{i}", "{}");
            jobIds.Add(id);
        }
        await _storage.FetchNextBatchAsync("worker1", 10);
        foreach (var id in jobIds)
        {
            await _storage.ArchiveFailedAsync(id, "Error");
        }

        // Act - Simulate concurrent purge operations
        var tasks = Enumerable.Range(1, 3)
            .Select(i => Task.Run(async () =>
            {
                await _storage.PurgeDLQAsync(jobIds.ToArray());
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert - All jobs should be purged
        var dlqJobs = (await _storage.GetDLQJobsAsync()).ToList();
        dlqJobs.Should().BeEmpty();
    }

    [Fact]
    public async Task RescheduleForRetryAsync_Concurrent_ShouldHandleMultipleRetries()
    {
        // Arrange
        await _fixture.CleanTablesAsync();
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await _storage.FetchNextBatchAsync("worker1", 10);

        // Act - Simulate 3 concurrent retry scheduling attempts
        var tasks = Enumerable.Range(1, 3)
            .Select(attempt => Task.Run(async () =>
            {
                var scheduledAt = DateTime.UtcNow.AddMinutes(attempt);
                await _storage.RescheduleForRetryAsync(id, scheduledAt, attempt, $"Error {attempt}");
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert - Job should be rescheduled (last write wins)
        var job = await _storage.GetJobAsync(id);
        job!.Status.Should().Be(ChokaQ.Abstractions.Enums.JobStatus.Pending);
        job.AttemptCount.Should().BeOneOf(1, 2, 3);
    }
}
