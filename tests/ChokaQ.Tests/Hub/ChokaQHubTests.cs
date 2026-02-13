using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Abstractions.Workers;
using ChokaQ.TheDeck.Hubs;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace ChokaQ.Tests.Hub;

public class ChokaQHubTests
{
    private readonly IWorkerManager _workerManager;
    private readonly IJobStorage _storage;
    private readonly ChokaQHub _hub;

    public ChokaQHubTests()
    {
        _workerManager = Substitute.For<IWorkerManager>();
        _storage = Substitute.For<IJobStorage>();
        var logger = NullLogger<ChokaQHub>.Instance;
        _hub = new ChokaQHub(_workerManager, _storage, logger);
    }

    [Fact]
    public async Task CancelJob_ShouldDelegateToWorkerManager()
    {
        // Act
        await _hub.CancelJob("job1");

        // Assert
        await _workerManager.Received(1).CancelJobAsync("job1");
    }

    [Fact]
    public async Task RestartJob_ShouldDelegateToWorkerManager()
    {
        // Act
        await _hub.RestartJob("job1");

        // Assert
        await _workerManager.Received(1).RestartJobAsync("job1");
    }

    [Fact]
    public async Task ResurrectJob_ShouldCallStorage_WithUpdates()
    {
        // Act
        await _hub.ResurrectJob("job1", "new_payload", 99);

        // Assert
        await _storage.Received(1).ResurrectAsync(
            "job1", 
            Arg.Is<JobDataUpdateDto>(d => d.Payload == "new_payload" && d.Priority == 99), 
            Arg.Any<string>());
    }

    [Fact]
    public async Task ResurrectJob_NullUpdates_ShouldCallStorage_WithoutUpdates()
    {
        // Act
        await _hub.ResurrectJob("job1");

        // Assert
        await _storage.Received(1).ResurrectAsync("job1", null, Arg.Any<string>());
    }

    [Fact]
    public async Task ToggleQueue_ShouldCallSetQueuePausedAsync()
    {
        // Act
        await _hub.ToggleQueue("default", true);

        // Assert
        await _storage.Received(1).SetQueuePausedAsync("default", true);
    }

    [Fact]
    public async Task SetPriority_ShouldDelegateToWorkerManager()
    {
        // Act
        await _hub.SetPriority("job1", 5);

        // Assert
        await _workerManager.Received(1).SetJobPriorityAsync("job1", 5);
    }

    [Fact]
    public async Task UpdateQueueTimeout_ShouldCallStorage()
    {
        // Act
        await _hub.UpdateQueueTimeout("default", 60);

        // Assert
        await _storage.Received(1).SetQueueZombieTimeoutAsync("default", 60);
    }

    [Fact]
    public async Task PurgeDLQ_ShouldCallStorage()
    {
        // Arrange
        var jobIds = new[] { "job1", "job2" };

        // Act
        await _hub.PurgeDLQ(jobIds);

        // Assert
        await _storage.Received(1).PurgeDLQAsync(jobIds);
    }

    [Fact]
    public async Task EditJob_ShouldTryHotFirst_ThenDLQ()
    {
        // Arrange
        _storage.UpdateJobDataAsync(Arg.Any<string>(), Arg.Any<JobDataUpdateDto>(), Arg.Any<string>())
            .Returns(false); // Not found in hot
        _storage.UpdateDLQJobDataAsync(Arg.Any<string>(), Arg.Any<JobDataUpdateDto>(), Arg.Any<string>())
            .Returns(true); // Found in DLQ

        // Act
        var result = await _hub.EditJob("job1", "payload", "tags", 10);

        // Assert
        result.Should().BeTrue();
        await _storage.Received(1).UpdateJobDataAsync("job1", Arg.Any<JobDataUpdateDto>(), Arg.Any<string>());
        await _storage.Received(1).UpdateDLQJobDataAsync("job1", Arg.Any<JobDataUpdateDto>(), Arg.Any<string>());
    }

    [Fact]
    public async Task EditJob_NotFound_ShouldReturnFalse()
    {
        // Arrange
        _storage.UpdateJobDataAsync(Arg.Any<string>(), Arg.Any<JobDataUpdateDto>(), Arg.Any<string>())
            .Returns(false);
        _storage.UpdateDLQJobDataAsync(Arg.Any<string>(), Arg.Any<JobDataUpdateDto>(), Arg.Any<string>())
            .Returns(false);

        // Act
        var result = await _hub.EditJob("job1", "payload", "tags", 10);

        // Assert
        result.Should().BeFalse();
    }
}
