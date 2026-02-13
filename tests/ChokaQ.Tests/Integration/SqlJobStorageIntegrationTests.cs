using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Storage.SqlServer;
using ChokaQ.Tests.Fixtures;
using Microsoft.Extensions.Options;

namespace ChokaQ.Tests.Integration;

/// <summary>
/// Integration tests for SqlJobStorage using real SQL Server via Testcontainers.
/// Tests the Three Pillars architecture (Hot, Archive, DLQ) with actual database operations.
/// </summary>
[Collection("SqlServer")]
public class SqlJobStorageIntegrationTests
{
    private readonly SqlServerFixture _fixture;
    private readonly SqlJobStorage _storage;

    public SqlJobStorageIntegrationTests(SqlServerFixture fixture)
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

    #region Core Operations (Tests 1-8)

    [Fact]
    public async Task EnqueueAsync_ShouldInsertIntoHotTable()
    {
        // Arrange
        var id = NewId();

        // Act
        var jobId = await _storage.EnqueueAsync(id, "default", "TestJob", "{\"test\":\"data\"}");

        // Assert
        jobId.Should().Be(id);
        var job = await _storage.GetJobAsync(id);
        job.Should().NotBeNull();
        job!.Type.Should().Be("TestJob");
        job.Status.Should().Be(JobStatus.Pending);
    }

    [Fact]
    public async Task EnqueueAsync_WithIdempotencyKey_ShouldReturnExistingId()
    {
        // Arrange
        var id1 = NewId();
        var id2 = NewId();
        var key = "idempotency-test-key";

        // Act
        var jobId1 = await _storage.EnqueueAsync(id1, "default", "TestJob", "{}", idempotencyKey: key);
        var jobId2 = await _storage.EnqueueAsync(id2, "default", "TestJob", "{}", idempotencyKey: key);

        // Assert
        jobId1.Should().Be(id1);
        jobId2.Should().Be(id1); // Returns first ID
    }

    [Fact]
    public async Task FetchNextBatchAsync_ShouldAtomicallyLockJobs()
    {
        // Arrange
        var id1 = NewId();
        var id2 = NewId();
        await _storage.EnqueueAsync(id1, "default", "Job1", "{}", priority: 10);
        await _storage.EnqueueAsync(id2, "default", "Job2", "{}", priority: 5);

        // Act
        var jobs = (await _storage.FetchNextBatchAsync("worker1", 10)).ToList();

        // Assert
        jobs.Should().HaveCount(2);
        jobs[0].Priority.Should().Be(10); // Higher priority first
        jobs[0].Status.Should().Be(JobStatus.Fetched);
        jobs[0].WorkerId.Should().Be("worker1");
    }

    [Fact]
    public async Task FetchNextBatchAsync_ShouldSkipPausedQueues()
    {
        // Arrange
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await _storage.SetQueuePausedAsync("default", true);

        // Act
        var jobs = (await _storage.FetchNextBatchAsync("worker1", 10)).ToList();

        // Assert
        jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchNextBatchAsync_ShouldRespectScheduledAtUtc()
    {
        // Arrange
        await _fixture.CleanTablesAsync();
        var id1 = NewId();
        var id2 = NewId();
        await _storage.EnqueueAsync(id1, "default", "Job1", "{}"); // Ready now
        await _storage.EnqueueAsync(id2, "default", "Job2", "{}", delay: TimeSpan.FromHours(1)); // Future

        // Act
        var jobs = (await _storage.FetchNextBatchAsync("worker1", 10)).ToList();

        // Assert
        jobs.Should().HaveCount(1);
        jobs[0].Id.Should().Be(id1);
    }

    [Fact]
    public async Task MarkAsProcessingAsync_ShouldUpdateStatusAndTimestamps()
    {
        // Arrange
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await _storage.FetchNextBatchAsync("worker1", 10);

        // Act
        await _storage.MarkAsProcessingAsync(id);

        // Assert
        var job = await _storage.GetJobAsync(id);
        job!.Status.Should().Be(JobStatus.Processing);
        job.StartedAtUtc.Should().NotBeNull();
        job.HeartbeatUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task KeepAliveAsync_ShouldUpdateHeartbeat()
    {
        // Arrange
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await _storage.FetchNextBatchAsync("worker1", 10);
        await _storage.MarkAsProcessingAsync(id);
        var job1 = await _storage.GetJobAsync(id);
        var initialHeartbeat = job1!.HeartbeatUtc!.Value;

        // Act
        await Task.Delay(100);
        await _storage.KeepAliveAsync(id);

        // Assert
        var job2 = await _storage.GetJobAsync(id);
        job2!.HeartbeatUtc.Should().BeAfter(initialHeartbeat);
    }

    [Fact]
    public async Task GetJobAsync_ShouldReturnNull_WhenNotFound()
    {
        // Act
        var job = await _storage.GetJobAsync("nonexistent-id");

        // Assert
        job.Should().BeNull();
    }

    #endregion

    #region Atomic Transitions (Tests 9-15)

    [Fact]
    public async Task ArchiveSucceededAsync_ShouldMoveToArchiveTable()
    {
        // Arrange
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await _storage.FetchNextBatchAsync("worker1", 10);

        // Act
        await _storage.ArchiveSucceededAsync(id, 250.5);

        // Assert
        var hotJob = await _storage.GetJobAsync(id);
        hotJob.Should().BeNull(); // Removed from Hot

        var archiveJob = await _storage.GetArchiveJobAsync(id);
        archiveJob.Should().NotBeNull();
        archiveJob!.DurationMs.Should().Be(250.5);
    }

    [Fact]
    public async Task ArchiveSucceededAsync_ShouldIncrementStats()
    {
        // Arrange
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await _storage.FetchNextBatchAsync("worker1", 10);

        // Act
        await _storage.ArchiveSucceededAsync(id, 100);

        // Assert
        var stats = await _storage.GetSummaryStatsAsync();
        stats.SucceededTotal.Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task ArchiveFailedAsync_ShouldMoveToDLQTable()
    {
        // Arrange
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await _storage.FetchNextBatchAsync("worker1", 10);

        // Act
        await _storage.ArchiveFailedAsync(id, "Error: Something went wrong");

        // Assert
        var hotJob = await _storage.GetJobAsync(id);
        hotJob.Should().BeNull(); // Removed from Hot

        var dlqJob = await _storage.GetDLQJobAsync(id);
        dlqJob.Should().NotBeNull();
        dlqJob!.ErrorDetails.Should().Contain("Something went wrong");
    }

    [Fact]
    public async Task ArchiveCancelledAsync_ShouldMoveToDLQ()
    {
        // Arrange
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await _storage.FetchNextBatchAsync("worker1", 10);

        // Act
        await _storage.ArchiveCancelledAsync(id, "admin@test.com");

        // Assert
        var dlqJob = await _storage.GetDLQJobAsync(id);
        dlqJob.Should().NotBeNull();
        dlqJob!.ErrorDetails.Should().Contain("Cancelled");
    }

    [Fact]
    public async Task ArchiveZombieAsync_ShouldMoveToDLQ()
    {
        // Arrange
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await _storage.FetchNextBatchAsync("worker1", 10);
        await _storage.MarkAsProcessingAsync(id);

        // Act
        await _storage.ArchiveZombieAsync(id);

        // Assert
        var dlqJob = await _storage.GetDLQJobAsync(id);
        dlqJob.Should().NotBeNull();
        dlqJob!.ErrorDetails.Should().Contain("Zombie");
    }

    [Fact]
    public async Task ResurrectAsync_ShouldMoveFromDLQToHot()
    {
        // Arrange
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await _storage.FetchNextBatchAsync("worker1", 10);
        await _storage.ArchiveFailedAsync(id, "Error");

        // Act
        await _storage.ResurrectAsync(id);

        // Assert
        var dlqJob = await _storage.GetDLQJobAsync(id);
        dlqJob.Should().BeNull(); // Removed from DLQ

        var hotJob = await _storage.GetJobAsync(id);
        hotJob.Should().NotBeNull();
        hotJob!.Status.Should().Be(JobStatus.Pending);
        hotJob.AttemptCount.Should().Be(0); // Reset
    }

    [Fact]
    public async Task ResurrectAsync_WithUpdates_ShouldApplyChanges()
    {
        // Arrange
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{\"old\":\"data\"}", priority: 5);
        await _storage.FetchNextBatchAsync("worker1", 10);
        await _storage.ArchiveFailedAsync(id, "Error");

        // Act
        var updates = new JobDataUpdateDto("{\"new\":\"data\"}", "tag1,tag2", 15);
        await _storage.ResurrectAsync(id, updates, "admin@test.com");

        // Assert
        var job = await _storage.GetJobAsync(id);
        job!.Payload.Should().Be("{\"new\":\"data\"}");
        job.Tags.Should().Be("tag1,tag2");
        job.Priority.Should().Be(15);
    }

    #endregion

    #region Batch Operations (Tests 16-18)

    [Fact]
    public async Task ResurrectBatchAsync_ShouldResurrectMultipleJobs()
    {
        // Arrange
        var id1 = NewId();
        var id2 = NewId();
        await _storage.EnqueueAsync(id1, "default", "Job1", "{}");
        await _storage.EnqueueAsync(id2, "default", "Job2", "{}");
        await _storage.FetchNextBatchAsync("worker1", 10);
        await _storage.ArchiveFailedAsync(id1, "Error");
        await _storage.ArchiveFailedAsync(id2, "Error");

        // Act
        var count = await _storage.ResurrectBatchAsync(new[] { id1, id2 }, "admin@test.com");

        // Assert
        count.Should().Be(2);
        var job1 = await _storage.GetJobAsync(id1);
        var job2 = await _storage.GetJobAsync(id2);
        job1.Should().NotBeNull();
        job2.Should().NotBeNull();
    }

    [Fact]
    public async Task ReleaseJobAsync_ShouldResetToPending()
    {
        // Arrange
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await _storage.FetchNextBatchAsync("worker1", 10);

        // Act
        await _storage.ReleaseJobAsync(id);

        // Assert
        var job = await _storage.GetJobAsync(id);
        job!.Status.Should().Be(JobStatus.Pending);
        job.WorkerId.Should().BeNull();
    }

    [Fact]
    public async Task RescheduleForRetryAsync_ShouldUpdateScheduleAndAttempt()
    {
        // Arrange
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await _storage.FetchNextBatchAsync("worker1", 10);

        // Act
        var scheduledAt = DateTime.UtcNow.AddMinutes(5);
        await _storage.RescheduleForRetryAsync(id, scheduledAt, 2, "Previous error");

        // Assert
        var job = await _storage.GetJobAsync(id);
        job!.Status.Should().Be(JobStatus.Pending);
        job.AttemptCount.Should().Be(2);
        job.ScheduledAtUtc.Should().BeCloseTo(scheduledAt, TimeSpan.FromSeconds(1));
    }

    #endregion

    #region Admin Operations (Tests 19-24)

    [Fact]
    public async Task UpdateJobDataAsync_ShouldUpdatePendingJob()
    {
        // Arrange
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{\"old\":\"data\"}", priority: 5);

        // Act
        var updates = new JobDataUpdateDto("{\"new\":\"data\"}", null, 10);
        var success = await _storage.UpdateJobDataAsync(id, updates, "admin@test.com");

        // Assert
        success.Should().BeTrue();
        var job = await _storage.GetJobAsync(id);
        job!.Payload.Should().Be("{\"new\":\"data\"}");
        job.Priority.Should().Be(10);
    }

    [Fact]
    public async Task UpdateJobDataAsync_ShouldFailForNonPendingJob()
    {
        // Arrange
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await _storage.FetchNextBatchAsync("worker1", 10);
        await _storage.MarkAsProcessingAsync(id);

        // Act
        var updates = new JobDataUpdateDto("{\"new\":\"data\"}", null, null);
        var success = await _storage.UpdateJobDataAsync(id, updates);

        // Assert
        success.Should().BeFalse(); // Can't update Processing jobs
    }

    [Fact]
    public async Task PurgeDLQAsync_ShouldDeleteSpecifiedJobs()
    {
        // Arrange
        var id1 = NewId();
        var id2 = NewId();
        await _storage.EnqueueAsync(id1, "default", "Job1", "{}");
        await _storage.EnqueueAsync(id2, "default", "Job2", "{}");
        await _storage.FetchNextBatchAsync("worker1", 10);
        await _storage.ArchiveFailedAsync(id1, "Error");
        await _storage.ArchiveFailedAsync(id2, "Error");

        // Act
        await _storage.PurgeDLQAsync(new[] { id1, id2 });

        // Assert
        var dlq1 = await _storage.GetDLQJobAsync(id1);
        var dlq2 = await _storage.GetDLQJobAsync(id2);
        dlq1.Should().BeNull();
        dlq2.Should().BeNull();
    }

    [Fact]
    public async Task PurgeArchiveAsync_ShouldDeleteOldJobs()
    {
        // Arrange
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await _storage.FetchNextBatchAsync("worker1", 10);
        await _storage.ArchiveSucceededAsync(id, 100);

        // Act
        var cutoff = DateTime.UtcNow.AddDays(1); // Future cutoff
        var count = await _storage.PurgeArchiveAsync(cutoff);

        // Assert
        count.Should().BeGreaterOrEqualTo(1);
        var archived = await _storage.GetArchiveJobAsync(id);
        archived.Should().BeNull();
    }

    [Fact]
    public async Task UpdateDLQJobDataAsync_ShouldUpdateDLQJob()
    {
        // Arrange
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{\"old\":\"data\"}");
        await _storage.FetchNextBatchAsync("worker1", 10);
        await _storage.ArchiveFailedAsync(id, "Error");

        // Act
        var updates = new JobDataUpdateDto("{\"new\":\"data\"}", "updated-tags", null);
        var success = await _storage.UpdateDLQJobDataAsync(id, updates, "admin@test.com");

        // Assert
        success.Should().BeTrue();
        var dlqJob = await _storage.GetDLQJobAsync(id);
        dlqJob!.Payload.Should().Be("{\"new\":\"data\"}");
        dlqJob.Tags.Should().Be("updated-tags");
    }

    #endregion

    #region Observability (Tests 25-30)

    [Fact]
    public async Task GetSummaryStatsAsync_ShouldReturnAggregatedStats()
    {
        // Arrange
        var id1 = NewId();
        var id2 = NewId();
        await _storage.EnqueueAsync(id1, "default", "Job1", "{}");
        await _storage.EnqueueAsync(id2, "default", "Job2", "{}");

        // Act
        var stats = await _storage.GetSummaryStatsAsync();

        // Assert
        stats.Should().NotBeNull();
        stats.Total.Should().BeGreaterOrEqualTo(2);
        stats.Pending.Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task GetQueueStatsAsync_ShouldReturnPerQueueBreakdown()
    {
        // Arrange
        await _storage.EnqueueAsync(NewId(), "queue1", "Job1", "{}");
        await _storage.EnqueueAsync(NewId(), "queue2", "Job2", "{}");

        // Act
        var stats = (await _storage.GetQueueStatsAsync()).ToList();

        // Assert
        stats.Should().Contain(s => s.Queue == "queue1");
        stats.Should().Contain(s => s.Queue == "queue2");
    }

    [Fact]
    public async Task GetActiveJobsAsync_ShouldReturnProcessingJobs()
    {
        // Arrange
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await _storage.FetchNextBatchAsync("worker1", 10);
        await _storage.MarkAsProcessingAsync(id);

        // Act
        var active = (await _storage.GetActiveJobsAsync(100, JobStatus.Processing)).ToList();

        // Assert
        active.Should().Contain(j => j.Id == id);
    }

    [Fact]
    public async Task GetArchivePagedAsync_ShouldPaginate()
    {
        // Arrange
        for (int i = 0; i < 5; i++)
        {
            var id = NewId();
            await _storage.EnqueueAsync(id, "default", $"Job{i}", "{}");
            await _storage.FetchNextBatchAsync("worker1", 10);
            await _storage.ArchiveSucceededAsync(id, 100);
        }

        // Act
        var filter = new HistoryFilterDto(null, null, null, null, null, 1, 2);
        var result = await _storage.GetArchivePagedAsync(filter);

        // Assert
        result.Items.Should().HaveCount(2);
        result.TotalCount.Should().BeGreaterOrEqualTo(5);
    }

    [Fact]
    public async Task GetDLQPagedAsync_ShouldPaginate()
    {
        // Arrange
        for (int i = 0; i < 5; i++)
        {
            var id = NewId();
            await _storage.EnqueueAsync(id, "default", $"Job{i}", "{}");
            await _storage.FetchNextBatchAsync("worker1", 10);
            await _storage.ArchiveFailedAsync(id, "Error");
        }

        // Act
        var filter = new HistoryFilterDto(null, null, null, null, null, 1, 2);
        var result = await _storage.GetDLQPagedAsync(filter);

        // Assert
        result.Items.Should().HaveCount(2);
        result.TotalCount.Should().BeGreaterOrEqualTo(5);
    }

    #endregion

    #region Queue Management (Tests 31-33)

    [Fact]
    public async Task SetQueuePausedAsync_ShouldPauseQueue()
    {
        // Arrange
        await _storage.EnqueueAsync(NewId(), "testqueue", "Job1", "{}");

        // Act
        await _storage.SetQueuePausedAsync("testqueue", true);

        // Assert
        var jobs = (await _storage.FetchNextBatchAsync("worker1", 10)).ToList();
        jobs.Should().NotContain(j => j.Queue == "testqueue");
    }

    [Fact]
    public async Task SetQueueZombieTimeoutAsync_ShouldUpdateTimeout()
    {
        // Act
        await _storage.SetQueueZombieTimeoutAsync("default", 600);

        // Assert - Verify via queue retrieval
        var queues = (await _storage.GetQueuesAsync()).ToList();
        var defaultQueue = queues.FirstOrDefault(q => q.Name == "default");
        defaultQueue.Should().NotBeNull();
        defaultQueue!.ZombieTimeoutSeconds.Should().Be(600);
    }

    [Fact]
    public async Task SetQueueActiveAsync_ShouldMarkQueueActive()
    {
        // Arrange
        await _fixture.CleanTablesAsync();
        // Create the queue first by enqueueing a job
        await _storage.EnqueueAsync(NewId(), "newqueue", "TestJob", "{}");

        // Act
        await _storage.SetQueueActiveAsync("newqueue", true);

        // Assert
        var queues = (await _storage.GetQueuesAsync()).ToList();
        queues.Should().Contain(q => q.Name == "newqueue" && q.IsActive);
    }

    #endregion

    #region Zombie Detection (Tests 34-35)

    [Fact]
    public async Task ArchiveZombiesAsync_ShouldDetectStaleJobs()
    {
        // Arrange
        await _fixture.CleanTablesAsync();
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await _storage.FetchNextBatchAsync("worker1", 10);
        await _storage.MarkAsProcessingAsync(id);
        
        // Wait longer for heartbeat to become stale (SQL Server has second precision)
        await Task.Delay(2000);

        // Act
        var count = await _storage.ArchiveZombiesAsync(1); // 1 second timeout

        // Assert
        count.Should().BeGreaterOrEqualTo(1);
        var dlqJob = await _storage.GetDLQJobAsync(id);
        dlqJob.Should().NotBeNull();
    }

    [Fact]
    public async Task ArchiveZombiesAsync_ShouldNotArchiveFreshJobs()
    {
        // Arrange
        var id = NewId();
        await _storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await _storage.FetchNextBatchAsync("worker1", 10);
        await _storage.MarkAsProcessingAsync(id);

        // Act
        var count = await _storage.ArchiveZombiesAsync(3600); // 1 hour timeout

        // Assert - This specific job should not be archived
        var hotJob = await _storage.GetJobAsync(id);
        hotJob.Should().NotBeNull(); // Still in Hot table
    }

    #endregion
}
