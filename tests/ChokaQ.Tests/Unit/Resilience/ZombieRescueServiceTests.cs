using ChokaQ.Abstractions.Notifications;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Core;
using ChokaQ.Core.Resilience;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChokaQ.Tests.Unit.Resilience;

[Trait(TestCategories.Category, TestCategories.Unit)]
public class ZombieRescueServiceTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldUseSeparateFetchedAndProcessingTimeouts()
    {
        // Arrange
        var storage = Substitute.For<IJobStorage>();
        var notifier = Substitute.For<IChokaQNotifier>();
        var cycleObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var fetchedTimeout = -1;
        var processingTimeout = -1;

        storage.RecoverAbandonedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                fetchedTimeout = call.ArgAt<int>(0);
                return new ValueTask<int>(0);
            });

        storage.ArchiveZombiesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                processingTimeout = call.ArgAt<int>(0);
                cycleObserved.TrySetResult();
                return new ValueTask<int>(0);
            });

        var service = new ZombieRescueService(
            storage,
            notifier,
            NullLogger<ZombieRescueService>.Instance,
            new ChokaQOptions
            {
                FetchedJobTimeoutSeconds = 120,
                ZombieTimeoutSeconds = 30
            });

        // Act
        await service.StartAsync(CancellationToken.None);

        try
        {
            await cycleObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Assert
        // Fetched recovery and Processing zombie archival answer different safety questions.
        // A short heartbeat timeout should not reclaim healthy jobs that are merely waiting in
        // a worker prefetch buffer for an execution slot.
        fetchedTimeout.Should().Be(120);
        processingTimeout.Should().Be(30);
    }
}
