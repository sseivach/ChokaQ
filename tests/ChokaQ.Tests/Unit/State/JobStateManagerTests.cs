using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Notifications;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Core.State;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;

namespace ChokaQ.Tests.Unit.State;

/// <summary>
/// Unit tests for JobStateManager - orchestrates state transitions and notifications.
/// </summary>
public class JobStateManagerTests
{
    private readonly IJobStorage _storage;
    private readonly IChokaQNotifier _notifier;
    private readonly JobStateManager _manager;

    public JobStateManagerTests()
    {
        _storage = Substitute.For<IJobStorage>();
        _notifier = Substitute.For<IChokaQNotifier>();
        _storage.ArchiveSucceededAsync(Arg.Any<string>(), Arg.Any<double?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(new ValueTask<bool>(true));
        _storage.ArchiveFailedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<FailureReason>())
            .Returns(new ValueTask<bool>(true));
        _storage.ArchiveCancelledAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(new ValueTask<bool>(true));
        _storage.RescheduleForRetryAsync(Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(new ValueTask<bool>(true));
        _storage.MarkAsProcessingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(new ValueTask<bool>(true));
        _manager = new JobStateManager(_storage, _notifier, NullLogger<JobStateManager>.Instance);
    }

    [Fact]
    public async Task ArchiveSucceededAsync_ShouldCallStorage_AndNotify()
    {
        // Act
        await _manager.ArchiveSucceededAsync("job1", "TestJob", "default", 1500.5);

        // Assert
        await _storage.Received(1).ArchiveSucceededAsync("job1", 1500.5, Arg.Any<CancellationToken>());
        await _notifier.Received(1).NotifyJobArchivedAsync("job1", "default");
        await _notifier.Received(1).NotifyStatsUpdatedAsync();
    }

    [Fact]
    public async Task ArchiveFailedAsync_ShouldCallStorage_AndNotifyFailure()
    {
        // Act
        await _manager.ArchiveFailedAsync("job1", "TestJob", "default", "Error details");

        // Assert
        await _storage.Received(1).ArchiveFailedAsync("job1", "Error details", Arg.Any<CancellationToken>());
        await _notifier.Received(1).NotifyJobFailedAsync("job1", "default", "MaxRetriesExceeded");
        await _notifier.Received(1).NotifyStatsUpdatedAsync();
    }

    [Fact]
    public async Task ArchiveFailedAsync_WithExplicitReason_ShouldPersistAndNotifyReason()
    {
        // Act
        await _manager.ArchiveFailedAsync(
            "job1",
            "TestJob",
            "default",
            "poison payload",
            failureReason: FailureReason.FatalError);

        // Assert
        // The state manager is the seam where execution taxonomy becomes operator-facing
        // dashboard data. Notification and storage must agree on the same reason.
        await _storage.Received(1).ArchiveFailedAsync(
            "job1",
            "poison payload",
            Arg.Any<CancellationToken>(),
            null,
            FailureReason.FatalError);
        await _notifier.Received(1).NotifyJobFailedAsync("job1", "default", "FatalError");
    }

    [Fact]
    public async Task ArchiveCancelledAsync_ShouldCallStorage_AndNotifyFailure()
    {
        // Act
        await _manager.ArchiveCancelledAsync("job1", "TestJob", "default", JobCancellationReason.Admin, "admin");

        // Assert
        await _storage.Received(1).ArchiveCancelledAsync("job1", "Admin: admin", Arg.Any<CancellationToken>());
        await _notifier.Received(1).NotifyJobFailedAsync("job1", "default", "Cancelled");
        await _notifier.Received(1).NotifyStatsUpdatedAsync();
    }

    [Fact]
    public async Task ArchiveCancelledAsync_WithTimeout_ShouldPersistTimeoutReason()
    {
        // Act
        await _manager.ArchiveCancelledAsync("job1", "TestJob", "default", JobCancellationReason.Timeout, "execution budget exceeded");

        // Assert
        // Timeout is caused by runtime policy, not by a human cancel button. Persisting it as
        // Timeout keeps remediation focused on execution budgets, heartbeats, and downstream slowness.
        await _storage.Received(1).ArchiveFailedAsync(
            "job1",
            "Timeout: execution budget exceeded",
            Arg.Any<CancellationToken>(),
            null,
            FailureReason.Timeout);
        await _storage.DidNotReceive().ArchiveCancelledAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>());
        await _notifier.Received(1).NotifyJobFailedAsync("job1", "default", "Timeout");
    }

    [Fact]
    public async Task RescheduleForRetryAsync_ShouldCallStorage_AndNotifyUpdate()
    {
        // Arrange
        var scheduledAt = DateTime.UtcNow.AddMinutes(5);

        // Act
        await _manager.RescheduleForRetryAsync("job1", "TestJob", "default", 10, scheduledAt, 2, "Error");

        // Assert
        await _storage.Received(1).RescheduleForRetryAsync("job1", scheduledAt, 2, "Error", Arg.Any<CancellationToken>());
        await _notifier.Received(1).NotifyJobUpdatedAsync(Arg.Is<JobUpdateDto>(dto =>
            dto.JobId == "job1" &&
            dto.Type == "TestJob" &&
            dto.Queue == "default" &&
            dto.Status == JobStatus.Pending &&
            dto.AttemptCount == 2 &&
            dto.Priority == 10));
        await _notifier.Received(1).NotifyStatsUpdatedAsync();
    }

    [Fact]
    public async Task MarkAsProcessingAsync_ShouldCallStorage_AndNotifyUpdate()
    {
        // Act
        await _manager.MarkAsProcessingAsync("job1", "TestJob", "default", 10, 1, "user1");

        // Assert
        await _storage.Received(1).MarkAsProcessingAsync("job1", Arg.Any<CancellationToken>());
        await _notifier.Received(1).NotifyJobUpdatedAsync(Arg.Is<JobUpdateDto>(dto =>
            dto.JobId == "job1" &&
            dto.Type == "TestJob" &&
            dto.Queue == "default" &&
            dto.Status == JobStatus.Processing &&
            dto.AttemptCount == 1 &&
            dto.Priority == 10 &&
            dto.CreatedBy == "user1"));
    }

    [Fact]
    public async Task SafeNotifyAsync_ShouldSwallowExceptions()
    {
        // Arrange
        _notifier.NotifyJobArchivedAsync(Arg.Any<string>(), Arg.Any<string>())
            .Throws(new InvalidOperationException("SignalR error"));

        // Act - Should not throw
        await _manager.ArchiveSucceededAsync("job1", "TestJob", "default", 1000);

        // Assert - Storage should still be called
        await _storage.Received(1).ArchiveSucceededAsync("job1", 1000, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ArchiveSucceededAsync_WhenStorageMovesNoRows_ShouldNotNotify()
    {
        // Arrange
        _storage.ArchiveSucceededAsync("job1", 1000, Arg.Any<CancellationToken>(), "stale-worker")
            .Returns(new ValueTask<bool>(false));

        // Act
        await _manager.ArchiveSucceededAsync("job1", "TestJob", "default", 1000, CancellationToken.None, "stale-worker");

        // Assert
        // A stale worker finalization must be visible in logs, but invisible to operators as a
        // fake state transition. The real owner or rescue path will emit the authoritative event.
        await _notifier.DidNotReceive().NotifyJobArchivedAsync(Arg.Any<string>(), Arg.Any<string>());
        await _notifier.DidNotReceive().NotifyStatsUpdatedAsync();
    }

    [Fact]
    public async Task MarkAsProcessingAsync_WhenStorageMovesNoRows_ShouldNotNotify()
    {
        // Arrange
        _storage.MarkAsProcessingAsync("job1", Arg.Any<CancellationToken>(), "stale-worker")
            .Returns(new ValueTask<bool>(false));

        // Act
        var marked = await _manager.MarkAsProcessingAsync(
            "job1", "TestJob", "default", 10, 1, "user1", CancellationToken.None, "stale-worker");

        // Assert
        // Losing the processing lease is not a user-visible state change. The dashboard should
        // continue to reflect the persisted row, not a stale worker's intention to execute it.
        marked.Should().BeFalse();
        await _notifier.DidNotReceive().NotifyJobUpdatedAsync(Arg.Any<JobUpdateDto>());
    }
}
