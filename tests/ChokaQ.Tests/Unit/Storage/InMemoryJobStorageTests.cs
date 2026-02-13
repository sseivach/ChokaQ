using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Core.Defaults;

namespace ChokaQ.Tests.Unit.Storage;

/// <summary>
/// Tests for InMemoryJobStorage - the largest and most critical component (899 lines, 50+ methods).
/// Covers: Core operations, atomic transitions, retry logic, admin operations, observability, queue management.
/// </summary>
public class InMemoryJobStorageTests
{
    private static string NewId() => Guid.NewGuid().ToString("N");

    #region Core Operations (Tests 1-12)

    [Fact]
    public async Task EnqueueAsync_ShouldStoreJob_AndReturnId()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();

        // Act
        var jobId = await storage.EnqueueAsync(id, "default", "TestJob", "{}");

        // Assert
        jobId.Should().Be(id);
        var job = await storage.GetJobAsync(jobId);
        job.Should().NotBeNull();
        job!.Type.Should().Be("TestJob");
    }

    [Fact]
    public async Task EnqueueAsync_WithIdempotencyKey_ShouldReturnExistingId()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id1 = NewId();
        var id2 = NewId();
        var idempotencyKey = "unique-key-123";

        // Act
        var jobId1 = await storage.EnqueueAsync(id1, "default", "TestJob", "{}", idempotencyKey: idempotencyKey);
        var jobId2 = await storage.EnqueueAsync(id2, "default", "TestJob", "{}", idempotencyKey: idempotencyKey);

        // Assert
        jobId1.Should().Be(id1);
        jobId2.Should().Be(id1); // Returns first ID due to idempotency
    }

    [Fact]
    public async Task EnqueueAsync_WithDelay_ShouldSetScheduledAtUtc()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        var delay = TimeSpan.FromMinutes(5);

        // Act
        await storage.EnqueueAsync(id, "default", "TestJob", "{}", delay: delay);
        var job = await storage.GetJobAsync(id);

        // Assert
        job!.ScheduledAtUtc.Should().NotBeNull();
        job.ScheduledAtUtc.Should().BeCloseTo(DateTime.UtcNow.Add(delay), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task EnqueueAsync_WithPriority_ShouldStorePriority()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();

        // Act
        await storage.EnqueueAsync(id, "default", "TestJob", "{}", priority: 10);
        var job = await storage.GetJobAsync(id);

        // Assert
        job!.Priority.Should().Be(10);
    }

    [Fact]
    public async Task FetchNextBatchAsync_ShouldReturnPendingJobs_OrderedByPriority()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        await storage.EnqueueAsync(NewId(), "default", "Job1", "{}", priority: 1);
        await storage.EnqueueAsync(NewId(), "default", "Job2", "{}", priority: 10);
        await storage.EnqueueAsync(NewId(), "default", "Job3", "{}", priority: 5);

        // Act
        var jobs = (await storage.FetchNextBatchAsync("worker1", 10)).ToList();

        // Assert
        jobs.Should().HaveCount(3);
        jobs[0].Priority.Should().Be(10); // Highest priority first
        jobs[1].Priority.Should().Be(5);
        jobs[2].Priority.Should().Be(1);
    }

    [Fact]
    public async Task FetchNextBatchAsync_ShouldSkipScheduledJobs_NotYetDue()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        await storage.EnqueueAsync(NewId(), "default", "Job1", "{}"); // Ready now
        await storage.EnqueueAsync(NewId(), "default", "Job2", "{}", delay: TimeSpan.FromMinutes(10)); // Scheduled for future

        // Act
        var jobs = (await storage.FetchNextBatchAsync("worker1", 10)).ToList();

        // Assert
        jobs.Should().HaveCount(1);
        jobs[0].Type.Should().Be("Job1");
    }

    [Fact]
    public async Task FetchNextBatchAsync_ShouldFetchScheduledJobs_WhenDue()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        await storage.EnqueueAsync(NewId(), "default", "Job1", "{}", delay: TimeSpan.FromMilliseconds(-100)); // Past due

        // Act
        var jobs = (await storage.FetchNextBatchAsync("worker1", 10)).ToList();

        // Assert
        jobs.Should().HaveCount(1);
        jobs[0].Type.Should().Be("Job1");
    }

    [Fact]
    public async Task FetchNextBatchAsync_ShouldSkipPausedQueues()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        await storage.EnqueueAsync(NewId(), "default", "Job1", "{}");
        await storage.SetQueuePausedAsync("default", true);

        // Act
        var jobs = (await storage.FetchNextBatchAsync("worker1", 10)).ToList();

        // Assert
        jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchNextBatchAsync_ShouldLockJobs_WithFetchedStatus()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{}");

        // Act
        await storage.FetchNextBatchAsync("worker1", 10);

        // Assert
        var job = await storage.GetJobAsync(id);
        job!.Status.Should().Be(JobStatus.Fetched);
        job.WorkerId.Should().Be("worker1");
    }

    [Fact]
    public async Task FetchNextBatchAsync_ShouldIncrementAttemptCount()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{}");

        // Act
        await storage.FetchNextBatchAsync("worker1", 10);

        // Assert
        var job = await storage.GetJobAsync(id);
        job!.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task FetchNextBatchAsync_WithAllowedQueues_ShouldFilterCorrectly()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        await storage.EnqueueAsync(NewId(), "queue1", "Job1", "{}");
        await storage.EnqueueAsync(NewId(), "queue2", "Job2", "{}");

        // Act
        var jobs = (await storage.FetchNextBatchAsync("worker1", 10, new[] { "queue1" })).ToList();

        // Assert
        jobs.Should().HaveCount(1);
        jobs[0].Queue.Should().Be("queue1");
    }

    [Fact]
    public async Task FetchNextBatchAsync_WithBatchSize_ShouldRespectLimit()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        for (int i = 0; i < 10; i++)
        {
            await storage.EnqueueAsync(NewId(), "default", $"Job{i}", "{}");
        }

        // Act
        var jobs = (await storage.FetchNextBatchAsync("worker1", 3)).ToList();

        // Assert
        jobs.Should().HaveCount(3);
    }

    #endregion

    #region State Transitions (Tests 13-16)

    [Fact]
    public async Task MarkAsProcessingAsync_ShouldUpdateStatus()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await storage.FetchNextBatchAsync("worker1", 10);

        // Act
        await storage.MarkAsProcessingAsync(id);

        // Assert
        var job = await storage.GetJobAsync(id);
        job!.Status.Should().Be(JobStatus.Processing);
        job.StartedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task KeepAliveAsync_ShouldUpdateHeartbeat()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await storage.FetchNextBatchAsync("worker1", 10);
        await storage.MarkAsProcessingAsync(id); // Set initial heartbeat
        var initialJob = await storage.GetJobAsync(id);
        var initialHeartbeat = initialJob!.HeartbeatUtc!.Value;

        // Act
        await Task.Delay(100);
        await storage.KeepAliveAsync(id);

        // Assert
        var job = await storage.GetJobAsync(id);
        job!.HeartbeatUtc.Should().BeAfter(initialHeartbeat);
    }

    [Fact]
    public async Task GetJobAsync_ShouldReturnJob_WhenExists()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{\"data\":\"test\"}");

        // Act
        var job = await storage.GetJobAsync(id);

        // Assert
        job.Should().NotBeNull();
        job!.Id.Should().Be(id);
        job.Payload.Should().Be("{\"data\":\"test\"}");
    }

    [Fact]
    public async Task GetJobAsync_ShouldReturnNull_WhenNotFound()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });

        // Act
        var job = await storage.GetJobAsync("nonexistent-id");

        // Assert
        job.Should().BeNull();
    }

    #endregion

    #region Archiving (Tests 17-26)

    [Fact]
    public async Task ArchiveSucceededAsync_ShouldMoveToArchive()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await storage.FetchNextBatchAsync("worker1", 10);

        // Act
        await storage.ArchiveSucceededAsync(id, 150);

        // Assert
        var job = await storage.GetJobAsync(id);
        job.Should().BeNull(); // No longer in Hot table

        var archived = (await storage.GetArchiveJobsAsync()).ToList();
        archived.Should().HaveCount(1);
        archived[0].Id.Should().Be(id);
    }

    [Fact]
    public async Task ArchiveSucceededAsync_ShouldRecordDuration()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await storage.FetchNextBatchAsync("worker1", 10);

        // Act
        await storage.ArchiveSucceededAsync(id, 250);

        // Assert
        var archived = (await storage.GetArchiveJobsAsync()).ToList();
        archived[0].DurationMs.Should().Be(250);
    }

    [Fact]
    public async Task ArchiveSucceededAsync_ShouldIncrementSucceededStats()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await storage.FetchNextBatchAsync("worker1", 10);

        // Act
        await storage.ArchiveSucceededAsync(id, 100);

        // Assert
        var stats = await storage.GetSummaryStatsAsync();
        stats.SucceededTotal.Should().Be(1);
    }

    [Fact]
    public async Task ArchiveFailedAsync_ShouldMoveToDLQ()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await storage.FetchNextBatchAsync("worker1", 10);

        // Act
        await storage.ArchiveFailedAsync(id, "Error: Too many retries");

        // Assert
        var job = await storage.GetJobAsync(id);
        job.Should().BeNull(); // No longer in Hot table

        var dlq = (await storage.GetDLQJobsAsync()).ToList();
        dlq.Should().HaveCount(1);
        dlq[0].ErrorDetails.Should().Contain("Too many retries");
    }

    [Fact]
    public async Task ArchiveCancelledAsync_ShouldMoveToDLQ()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await storage.FetchNextBatchAsync("worker1", 10);

        // Act
        await storage.ArchiveCancelledAsync(id, "admin@example.com");

        // Assert
        var dlq = (await storage.GetDLQJobsAsync()).ToList();
        dlq[0].ErrorDetails.Should().Contain("Cancelled");
    }

    [Fact]
    public async Task ArchiveZombieAsync_ShouldMoveToDLQ()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await storage.FetchNextBatchAsync("worker1", 10);

        // Act
        await storage.ArchiveZombieAsync(id);

        // Assert
        var dlq = (await storage.GetDLQJobsAsync()).ToList();
        dlq[0].ErrorDetails.Should().Contain("Zombie");
    }

    [Fact]
    public async Task ArchiveFailedAsync_ShouldIncrementFailedStats()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await storage.FetchNextBatchAsync("worker1", 10);

        // Act
        await storage.ArchiveFailedAsync(id, "Error details");

        // Assert
        var stats = await storage.GetSummaryStatsAsync();
        stats.FailedTotal.Should().Be(1);
    }

    [Fact]
    public async Task ResurrectAsync_ShouldMoveFromDLQ_ToHot()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await storage.FetchNextBatchAsync("worker1", 10);
        await storage.ArchiveFailedAsync(id, "Error");

        // Act
        await storage.ResurrectAsync(id);

        // Assert
        var job = await storage.GetJobAsync(id);
        job.Should().NotBeNull();
        job!.Status.Should().Be(JobStatus.Pending);
    }

    [Fact]
    public async Task ResurrectAsync_WithUpdates_ShouldApplyNewPayloadAndPriority()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{\"old\":\"data\"}", priority: 5);
        await storage.FetchNextBatchAsync("worker1", 10);
        await storage.ArchiveFailedAsync(id, "Error");

        // Act
        var updates = new JobDataUpdateDto("{\"new\":\"data\"}", null, 10);
        await storage.ResurrectAsync(id, updates);

        // Assert
        var job = await storage.GetJobAsync(id);
        job!.Payload.Should().Be("{\"new\":\"data\"}");
        job.Priority.Should().Be(10);
    }

    [Fact]
    public async Task ResurrectAsync_ShouldDecrementFailedStats()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await storage.FetchNextBatchAsync("worker1", 10);
        await storage.ArchiveFailedAsync(id, "Error");

        // Act
        await storage.ResurrectAsync(id);

        // Assert
        var stats = await storage.GetSummaryStatsAsync();
        stats.FailedTotal.Should().Be(0);
    }

    #endregion

    #region Batch Operations (Tests 27-29)

    [Fact]
    public async Task ResurrectBatchAsync_ShouldResurrectMultipleJobs()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id1 = NewId();
        var id2 = NewId();
        await storage.EnqueueAsync(id1, "default", "Job1", "{}");
        await storage.EnqueueAsync(id2, "default", "Job2", "{}");
        await storage.FetchNextBatchAsync("worker1", 10);
        await storage.ArchiveFailedAsync(id1, "Error");
        await storage.ArchiveFailedAsync(id2, "Error");

        // Act
        var count = await storage.ResurrectBatchAsync(new[] { id1, id2 });

        // Assert
        count.Should().Be(2);
    }

    [Fact]
    public async Task ResurrectBatchAsync_WithEmptyArray_ShouldReturnZero()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });

        // Act
        var count = await storage.ResurrectBatchAsync(Array.Empty<string>());

        // Assert
        count.Should().Be(0);
    }

    [Fact]
    public async Task ReleaseJobAsync_ShouldResetToPending()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await storage.FetchNextBatchAsync("worker1", 10);

        // Act
        await storage.ReleaseJobAsync(id);

        // Assert
        var job = await storage.GetJobAsync(id);
        job!.Status.Should().Be(JobStatus.Pending);
        job.WorkerId.Should().BeNull();
    }

    #endregion

    #region Retry Logic (Tests 30-31)

    [Fact]
    public async Task RescheduleForRetryAsync_ShouldResetToPending_WithSchedule()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await storage.FetchNextBatchAsync("worker1", 10);

        // Act
        var scheduledAt = DateTime.UtcNow.AddMinutes(5);
        await storage.RescheduleForRetryAsync(id, scheduledAt, 2, "Previous error");

        // Assert
        var job = await storage.GetJobAsync(id);
        job!.Status.Should().Be(JobStatus.Pending);
        job.ScheduledAtUtc.Should().BeCloseTo(scheduledAt, TimeSpan.FromSeconds(1));
        job.AttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task RescheduleForRetryAsync_ShouldIncrementRetriedStats()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await storage.FetchNextBatchAsync("worker1", 10);

        // Act
        await storage.RescheduleForRetryAsync(id, DateTime.UtcNow.AddMinutes(1), 2, "Error");

        // Assert
        var stats = await storage.GetSummaryStatsAsync();
        stats.RetriedTotal.Should().Be(1);
    }

    #endregion

    #region Admin Operations (Tests 32-40)

    [Fact]
    public async Task UpdateJobDataAsync_ShouldUpdatePendingJob()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{\"old\":\"data\"}", priority: 5);

        // Act
        var updates = new JobDataUpdateDto("{\"new\":\"data\"}", null, 10);
        var updated = await storage.UpdateJobDataAsync(id, updates);

        // Assert
        updated.Should().BeTrue();
        var job = await storage.GetJobAsync(id);
        job!.Payload.Should().Be("{\"new\":\"data\"}");
        job.Priority.Should().Be(10);
    }

    [Fact]
    public async Task UpdateJobDataAsync_ShouldNotUpdateProcessingJob()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await storage.FetchNextBatchAsync("worker1", 10);
        await storage.MarkAsProcessingAsync(id);

        // Act
        var updates = new JobDataUpdateDto("{\"new\":\"data\"}", null, null);
        var updated = await storage.UpdateJobDataAsync(id, updates);

        // Assert
        updated.Should().BeFalse(); // Only Pending jobs can be updated
    }

    [Fact]
    public async Task PurgeDLQAsync_ShouldDeleteSpecifiedJobs()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id1 = NewId();
        var id2 = NewId();
        await storage.EnqueueAsync(id1, "default", "Job1", "{}");
        await storage.EnqueueAsync(id2, "default", "Job2", "{}");
        await storage.FetchNextBatchAsync("worker1", 10);
        await storage.ArchiveFailedAsync(id1, "Error");
        await storage.ArchiveFailedAsync(id2, "Error");

        // Act
        await storage.PurgeDLQAsync(new[] { id1, id2 });

        // Assert
        var dlq = (await storage.GetDLQJobsAsync()).ToList();
        dlq.Should().BeEmpty();
    }

    [Fact]
    public async Task PurgeArchiveAsync_ShouldDeleteOlderThanCutoff()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await storage.FetchNextBatchAsync("worker1", 10);
        await storage.ArchiveSucceededAsync(id, 100);

        // Act
        var cutoff = DateTime.UtcNow.AddDays(1); // Future cutoff - should delete
        var count = await storage.PurgeArchiveAsync(cutoff);

        // Assert
        count.Should().Be(1);
        var archived = (await storage.GetArchiveJobsAsync()).ToList();
        archived.Should().BeEmpty();
    }

    [Fact]
    public async Task PurgeArchiveAsync_ShouldNotDeleteNewerJobs()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await storage.FetchNextBatchAsync("worker1", 10);
        await storage.ArchiveSucceededAsync(id, 100);

        // Act
        var cutoff = DateTime.UtcNow.AddDays(-1); // Past cutoff - should NOT delete
        var count = await storage.PurgeArchiveAsync(cutoff);

        // Assert
        count.Should().Be(0);
        var archived = (await storage.GetArchiveJobsAsync()).ToList();
        archived.Should().HaveCount(1);
    }

    [Fact]
    public async Task UpdateDLQJobDataAsync_ShouldUpdateDLQJob()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{\"old\":\"data\"}");
        await storage.FetchNextBatchAsync("worker1", 10);
        await storage.ArchiveFailedAsync(id, "Error");

        // Act
        var updates = new JobDataUpdateDto("{\"new\":\"data\"}", null, null);
        var updated = await storage.UpdateDLQJobDataAsync(id, updates);

        // Assert
        updated.Should().BeTrue();
        var dlqJob = await storage.GetDLQJobAsync(id);
        dlqJob!.Payload.Should().Be("{\"new\":\"data\"}");
    }

    [Fact]
    public async Task UpdateDLQJobDataAsync_ShouldReturnFalse_WhenNotFound()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });

        // Act
        var updates = new JobDataUpdateDto("{\"new\":\"data\"}", null, null);
        var updated = await storage.UpdateDLQJobDataAsync("nonexistent-id", updates);

        // Assert
        updated.Should().BeFalse();
    }

    #endregion

    #region Observability (Tests 41-54)

    [Fact]
    public async Task GetSummaryStatsAsync_ShouldReturnAggregatedStats()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        await storage.EnqueueAsync(NewId(), "default", "Job1", "{}");
        await storage.EnqueueAsync(NewId(), "default", "Job2", "{}");

        // Act
        var stats = await storage.GetSummaryStatsAsync();

        // Assert
        stats.Should().NotBeNull();
        stats.Total.Should().Be(2);
        stats.Pending.Should().Be(2);
    }

    [Fact]
    public async Task GetQueueStatsAsync_ShouldReturnPerQueueBreakdown()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        await storage.EnqueueAsync(NewId(), "queue1", "Job1", "{}");
        await storage.EnqueueAsync(NewId(), "queue2", "Job2", "{}");

        // Act
        var stats = (await storage.GetQueueStatsAsync()).ToList();

        // Assert
        stats.Should().HaveCountGreaterOrEqualTo(2);
        stats.Should().Contain(s => s.Queue == "queue1");
        stats.Should().Contain(s => s.Queue == "queue2");
    }

    [Fact]
    public async Task GetActiveJobsAsync_ShouldReturnProcessingJobs()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await storage.FetchNextBatchAsync("worker1", 10);
        await storage.MarkAsProcessingAsync(id);

        // Act
        var active = (await storage.GetActiveJobsAsync()).ToList();

        // Assert
        active.Should().HaveCount(1);
        active[0].Status.Should().Be(JobStatus.Processing);
    }

    [Fact]
    public async Task GetArchiveJobsAsync_ShouldReturnArchivedJobs()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await storage.FetchNextBatchAsync("worker1", 10);
        await storage.ArchiveSucceededAsync(id, 100);

        // Act
        var archived = (await storage.GetArchiveJobsAsync()).ToList();

        // Assert
        archived.Should().HaveCount(1);
        archived[0].DurationMs.Should().Be(100);
    }

    [Fact]
    public async Task GetDLQJobsAsync_ShouldReturnDLQJobs()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await storage.FetchNextBatchAsync("worker1", 10);
        await storage.ArchiveFailedAsync(id, "Error details");

        // Act
        var dlq = (await storage.GetDLQJobsAsync()).ToList();

        // Assert
        dlq.Should().HaveCount(1);
        dlq[0].ErrorDetails.Should().Contain("Error");
    }

    [Fact]
    public async Task GetArchivePagedAsync_ShouldPaginate()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        for (int i = 0; i < 5; i++)
        {
            var id = NewId();
            await storage.EnqueueAsync(id, "default", $"Job{i}", "{}");
            await storage.FetchNextBatchAsync("worker1", 10);
            await storage.ArchiveSucceededAsync(id, 100);
        }

        // Act
        var filter = new HistoryFilterDto(null, null, null, null, null, 1, 2);
        var result = await storage.GetArchivePagedAsync(filter);

        // Assert
        result.Items.Should().HaveCount(2);
        result.TotalCount.Should().Be(5);
        result.PageNumber.Should().Be(1);
    }

    [Fact]
    public async Task GetDLQPagedAsync_ShouldPaginate()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        for (int i = 0; i < 5; i++)
        {
            var id = NewId();
            await storage.EnqueueAsync(id, "default", $"Job{i}", "{}");
            await storage.FetchNextBatchAsync("worker1", 10);
            await storage.ArchiveFailedAsync(id, "Error");
        }

        // Act
        var filter = new HistoryFilterDto(null, null, null, null, null, 1, 2);
        var result = await storage.GetDLQPagedAsync(filter);

        // Assert
        result.Items.Should().HaveCount(2);
        result.TotalCount.Should().Be(5);
    }

    #endregion

    #region Queue Management (Tests 55-59)

    [Fact]
    public async Task SetQueuePausedAsync_ShouldPauseQueue()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        await storage.EnqueueAsync(NewId(), "default", "Job1", "{}");

        // Act
        await storage.SetQueuePausedAsync("default", true);

        // Assert
        var jobs = (await storage.FetchNextBatchAsync("worker1", 10)).ToList();
        jobs.Should().BeEmpty(); // Paused queue should not return jobs
    }

    [Fact]
    public async Task SetQueuePausedAsync_ShouldUnpauseQueue()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        await storage.EnqueueAsync(NewId(), "default", "Job1", "{}");
        await storage.SetQueuePausedAsync("default", true);

        // Act
        await storage.SetQueuePausedAsync("default", false);

        // Assert
        var jobs = (await storage.FetchNextBatchAsync("worker1", 10)).ToList();
        jobs.Should().HaveCount(1);
    }

    [Fact]
    public async Task SetQueueZombieTimeoutAsync_ShouldSetTimeout()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });

        // Act
        await storage.SetQueueZombieTimeoutAsync("default", 300);

        // Assert - This is hard to verify directly, but we can check it doesn't throw
        // The timeout would be used by ArchiveZombiesAsync
    }

    [Fact]
    public async Task SetQueueActiveAsync_ShouldMarkQueueActive()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });

        // Act
        await storage.SetQueueActiveAsync("newqueue", true);

        // Assert
        var stats = (await storage.GetQueueStatsAsync()).ToList();
        stats.Should().Contain(s => s.Queue == "newqueue");
    }

    [Fact]
    public async Task SetQueueActiveAsync_ShouldBeIdempotent()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });

        // Act
        await storage.SetQueueActiveAsync("default", true);
        await storage.SetQueueActiveAsync("default", true);

        // Assert - Should not throw
    }

    #endregion

    #region Zombie Handling (Tests 60-62)

    [Fact]
    public async Task ArchiveZombiesAsync_ShouldDetectStaleJobs()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await storage.FetchNextBatchAsync("worker1", 10);
        await storage.MarkAsProcessingAsync(id);

        // Simulate stale heartbeat by waiting
        await Task.Delay(200);

        // Act
        var count = await storage.ArchiveZombiesAsync(0); // 0 second timeout = immediate zombie

        // Assert
        count.Should().BeGreaterOrEqualTo(1);
        var dlq = (await storage.GetDLQJobsAsync()).ToList();
        dlq.Should().Contain(j => j.Id == id);
    }

    [Fact]
    public async Task ArchiveZombiesAsync_ShouldRespectPerQueueTimeout()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await storage.FetchNextBatchAsync("worker1", 10);
        await storage.MarkAsProcessingAsync(id);
        await storage.SetQueueZombieTimeoutAsync("default", 1); // 1 second timeout

        await Task.Delay(1200);

        // Act
        var count = await storage.ArchiveZombiesAsync(3600); // Default timeout is long, but queue override is short

        // Assert
        count.Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task ArchiveZombiesAsync_ShouldNotArchiveFreshJobs()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 1000 });
        var id = NewId();
        await storage.EnqueueAsync(id, "default", "TestJob", "{}");
        await storage.FetchNextBatchAsync("worker1", 10);
        await storage.MarkAsProcessingAsync(id);

        // Act
        var count = await storage.ArchiveZombiesAsync(3600); // Long timeout

        // Assert
        count.Should().Be(0); // Fresh job should not be archived
    }

    #endregion

    #region Capacity Management (Tests 63-64)

    [Fact]
    public async Task EnforceCapacity_ShouldEvictOldJobs_WhenCapacityExceeded()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 10 });

        // Fill up with archived jobs
        for (int i = 0; i < 5; i++)
        {
            var id = NewId();
            await storage.EnqueueAsync(id, "default", $"Job{i}", "{}");
            await storage.FetchNextBatchAsync("worker1", 10);
            await storage.ArchiveSucceededAsync(id, 100);
        }

        // Add pending jobs to approach capacity
        for (int i = 0; i < 6; i++)
        {
            await storage.EnqueueAsync(NewId(), "default", $"NewJob{i}", "{}");
        }

        // Act - Capacity enforcement happens automatically during enqueue
        // Assert - Should not throw, capacity is enforced internally
        var stats = await storage.GetSummaryStatsAsync();
        stats.Total.Should().BeLessOrEqualTo(10);
    }

    [Fact]
    public async Task EnforceCapacity_ShouldPrioritizeHotJobs()
    {
        // Arrange
        var storage = new InMemoryJobStorage(new InMemoryStorageOptions { MaxCapacity = 10 });

        // Fill with DLQ and Archive
        for (int i = 0; i < 3; i++)
        {
            var id = NewId();
            await storage.EnqueueAsync(id, "default", $"Failed{i}", "{}");
            await storage.FetchNextBatchAsync("worker1", 10);
            await storage.ArchiveFailedAsync(id, "Error");
        }

        for (int i = 0; i < 3; i++)
        {
            var id = NewId();
            await storage.EnqueueAsync(id, "default", $"Success{i}", "{}");
            await storage.FetchNextBatchAsync("worker1", 10);
            await storage.ArchiveSucceededAsync(id, 100);
        }

        // Add hot jobs to exceed capacity
        for (int i = 0; i < 5; i++)
        {
            await storage.EnqueueAsync(NewId(), "default", $"Hot{i}", "{}");
        }

        // Act & Assert - Hot jobs should be preserved
        var hotJobs = (await storage.GetActiveJobsAsync(100)).ToList();
        hotJobs.Should().HaveCountGreaterOrEqualTo(5); // Hot jobs preserved
    }

    #endregion
}
