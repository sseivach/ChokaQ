using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Jobs;
using ChokaQ.Abstractions.Notifications;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Core.Defaults;
using ChokaQ.Core.Execution;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ChokaQ.Tests.Unit.Defaults;

/// <summary>
/// Unit tests for InMemoryQueue - producer/consumer buffer.
/// </summary>
public class InMemoryQueueTests
{
    private readonly IJobStorage _storage;
    private readonly IChokaQNotifier _notifier;
    private readonly JobTypeRegistry _registry;
    private readonly InMemoryQueue _queue;

    public InMemoryQueueTests()
    {
        _storage = Substitute.For<IJobStorage>();
        _notifier = Substitute.For<IChokaQNotifier>();
        _registry = new JobTypeRegistry();
        _queue = new InMemoryQueue(_storage, _notifier, _registry, NullLogger<InMemoryQueue>.Instance);
    }

    [Fact]
    public async Task EnqueueAsync_ShouldPersistToStorage()
    {
        // Arrange
        _registry.Register("test_job", typeof(TestJob));
        var job = new TestJob { Id = "job1", Message = "Hello" };

        // Act
        await _queue.EnqueueAsync(job, 5, "critical", "user1", "tag1", CancellationToken.None);

        // Assert
        await _storage.Received(1).EnqueueAsync(
            "job1",
            "critical",
            "test_job",
            Arg.Is<string>(s => s.Contains("Hello")),
            5,
            "user1",
            "tag1",
            Arg.Any<TimeSpan?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task EnqueueAsync_ShouldNotifyDashboard()
    {
        // Arrange
        _registry.Register("test_job", typeof(TestJob));
        var job = new TestJob { Id = "job1", Message = "Hello" };

        // Act
        await _queue.EnqueueAsync(job, 5, "critical", "user1", null, CancellationToken.None);

        // Assert
        await _notifier.Received(1).NotifyJobUpdatedAsync(Arg.Is<JobUpdateDto>(dto =>
            dto.JobId == "job1" &&
            dto.Type == "test_job" &&
            dto.Queue == "critical" &&
            dto.Status == JobStatus.Pending &&
            dto.Priority == 5 &&
            dto.CreatedBy == "user1"
        ));
    }

    [Fact]
    public async Task EnqueueAsync_ShouldWriteToChannel()
    {
        // Arrange
        var job = new TestJob { Id = "job1", Message = "Hello" };

        // Act
        await _queue.EnqueueAsync(job);

        // Assert
        var item = await _queue.Reader.ReadAsync();
        item.Should().Be(job);
    }

    [Fact]
    public async Task EnqueueAsync_ShouldFallbackToTypeName_WhenNotInRegistry()
    {
        // Arrange - Don't register
        var job = new TestJob { Id = "job1" };

        // Act
        await _queue.EnqueueAsync(job);

        // Assert
        await _storage.Received(1).EnqueueAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            nameof(TestJob), // Fallback
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task EnqueueAsync_ShouldHandleNotificationFailure_Gracefully()
    {
        // Arrange
        _notifier.NotifyJobUpdatedAsync(Arg.Any<JobUpdateDto>())
            .Returns(Task.FromException(new Exception("SignalR error")));
        var job = new TestJob { Id = "job1" };

        // Act - Should not throw
        await _queue.EnqueueAsync(job);

        // Assert - Queue write should still happen
        var item = await _queue.Reader.ReadAsync();
        item.Should().Be(job);
    }

    [Fact]
    public async Task RequeueAsync_ShouldWriteToChannel_WithoutPersistence()
    {
        // Arrange
        var job = new TestJob { Id = "job1" };

        // Act
        await _queue.RequeueAsync(job);

        // Assert
        var item = await _queue.Reader.ReadAsync();
        item.Should().Be(job);
        
        // No storage calls
        await _storage.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, default!, default!, default!, default, default, default, default);
    }

    public class TestJob : IChokaQJob
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Message { get; set; } = "";
    }
}
