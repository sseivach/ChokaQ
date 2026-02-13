using ChokaQ.Abstractions.DTOs;
using ChokaQ.Core.Defaults;
using FluentAssertions;
using Xunit;

namespace ChokaQ.Tests.Unit.Defaults;

public class NullNotifierTests
{
    [Fact]
    public async Task AllMethods_ShouldReturnCompletedTask_AndNotThrow()
    {
        // Arrange
        var notifier = new NullNotifier();

        // Act & Assert - Execute all methods to ensure no exceptions
        await notifier.NotifyJobUpdatedAsync(new JobUpdateDto("j1", "t1", "q1", Abstractions.Enums.JobStatus.Pending, 0, 10, null, null, null));
        await notifier.NotifyJobProgressAsync("j1", 50);
        await notifier.NotifyJobArchivedAsync("j1", "q1");
        await notifier.NotifyJobFailedAsync("j1", "q1", "reason");
        await notifier.NotifyJobResurrectedAsync("j1", "q1");
        await notifier.NotifyJobsPurgedAsync(["j1"], "dlq");
        await notifier.NotifyQueueStateChangedAsync("q1", true);
        await notifier.NotifyStatsUpdatedAsync();

        // If we reached here, no exception was thrown
        true.Should().BeTrue();
    }
}
