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
    public async Task ArchiveCancelledAsync_ShouldCallStorage_AndNotifyFailure()
    {
        // Act
        await _manager.ArchiveCancelledAsync("job1", "TestJob", "default", "admin");

        // Assert
        await _storage.Received(1).ArchiveCancelledAsync("job1", "admin", Arg.Any<CancellationToken>());
        await _notifier.Received(1).NotifyJobFailedAsync("job1", "default", "Cancelled");
        await _notifier.Received(1).NotifyStatsUpdatedAsync();
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
}
