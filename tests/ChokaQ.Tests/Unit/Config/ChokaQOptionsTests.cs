using ChokaQ.Abstractions.Jobs;
using ChokaQ.Core;

namespace ChokaQ.Tests.Unit.Config;

public class ChokaQOptionsTests
{
    [Fact]
    public void Defaults_ShouldBeCorrect()
    {
        var options = new ChokaQOptions();
        options.MaxRetries.Should().Be(3);
        options.RetryDelaySeconds.Should().Be(3);
        options.IsPipeMode.Should().BeFalse();
        options.ProfileTypes.Should().BeEmpty();
    }

    [Fact]
    public void UsePipe_ShouldSetPipeMode()
    {
        var options = new ChokaQOptions();
        options.UsePipe<TestPipeHandler>();

        options.IsPipeMode.Should().BeTrue();
        options.PipeHandlerType.Should().Be(typeof(TestPipeHandler));
    }

    [Fact]
    public void AddProfile_ShouldAddProfileType()
    {
        var options = new ChokaQOptions();
        options.AddProfile<TestProfile>();

        options.ProfileTypes.Should().Contain(typeof(TestProfile));
    }

    [Fact]
    public void ConfigureInMemory_ShouldUpdateOptions()
    {
        var options = new ChokaQOptions();
        options.ConfigureInMemory(o => o.MaxCapacity = 100);

        options.InMemoryOptions.MaxCapacity.Should().Be(100);
    }

    private class TestPipeHandler : IChokaQPipeHandler
    {
        public Task HandleAsync(string jobType, string payload, CancellationToken ct) => Task.CompletedTask;
    }

    private class TestProfile : ChokaQJobProfile
    {
    }
}
