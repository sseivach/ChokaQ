using ChokaQ.Abstractions.Contexts;
using ChokaQ.Abstractions.Notifications;
using ChokaQ.Core.Contexts;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace ChokaQ.Tests.Unit.Contexts;

public class JobContextTests
{
    [Fact]
    public async Task ReportProgressAsync_ShouldDelegateToNotifier()
    {
        // Arrange
        var notifier = Substitute.For<IChokaQNotifier>();
        var context = new JobContext(notifier);
        context.JobId = "job1";

        // Act
        await context.ReportProgressAsync(50);

        // Assert
        await notifier.Received(1).NotifyJobProgressAsync("job1", 50);
    }

    [Fact]
    public void JobId_ShouldBeSettable()
    {
        var notifier = Substitute.For<IChokaQNotifier>();
        var context = new JobContext(notifier);
        context.JobId = "job1";

        context.JobId.Should().Be("job1");
    }
}
