using ChokaQ.Abstractions.Jobs;
using ChokaQ.Core;
using ChokaQ.Core.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ChokaQ.Tests.Unit.Config;

public class ChokaQOptionsTests
{
    [Fact]
    public void Defaults_ShouldBeCorrect()
    {
        var options = new ChokaQOptions();
        options.MaxRetries.Should().Be(3);
        options.RetryDelaySeconds.Should().Be(3);
        options.ZombieTimeoutSeconds.Should().Be(600);
        options.FetchedJobTimeoutSeconds.Should().Be(600);
        options.Execution.DefaultTimeout.Should().Be(TimeSpan.FromMinutes(15));
        options.Execution.HeartbeatIntervalMin.Should().Be(TimeSpan.FromSeconds(8));
        options.Execution.HeartbeatIntervalMax.Should().Be(TimeSpan.FromSeconds(12));
        options.Execution.HeartbeatFailureThreshold.Should().Be(3);
        options.Retry.MaxAttempts.Should().Be(3);
        options.Retry.BaseDelay.Should().Be(TimeSpan.FromSeconds(3));
        options.Retry.MaxDelay.Should().Be(TimeSpan.FromHours(1));
        options.Retry.BackoffMultiplier.Should().Be(2.0);
        options.Retry.JitterMaxDelay.Should().Be(TimeSpan.FromSeconds(1));
        options.Retry.CircuitBreakerDelay.Should().Be(TimeSpan.FromSeconds(5));
        options.Recovery.FetchedJobTimeout.Should().Be(TimeSpan.FromMinutes(10));
        options.Recovery.ProcessingZombieTimeout.Should().Be(TimeSpan.FromMinutes(10));
        options.Recovery.ScanInterval.Should().Be(TimeSpan.FromMinutes(1));
        options.Worker.PausedQueuePollingDelay.Should().Be(TimeSpan.FromSeconds(1));
        options.InMemory.MaxCapacity.Should().Be(100_000);
        options.InMemoryOptions.MaxCapacity.Should().Be(100_000);
        options.Metrics.MaxQueueTagValues.Should().Be(100);
        options.Metrics.MaxJobTypeTagValues.Should().Be(500);
        options.Metrics.MaxErrorTagValues.Should().Be(100);
        options.Metrics.MaxFailureReasonTagValues.Should().Be(50);
        options.Metrics.MaxTagValueLength.Should().Be(128);
        options.Metrics.UnknownTagValue.Should().Be("unknown");
        options.Metrics.OverflowTagValue.Should().Be("other");
        options.IsPipeMode.Should().BeFalse();
        options.ProfileTypes.Should().BeEmpty();
    }

    [Fact]
    public void LegacyProperties_ShouldMapToNestedRuntimeOptions()
    {
        var options = new ChokaQOptions
        {
            MaxRetries = 5,
            RetryDelaySeconds = 7,
            FetchedJobTimeoutSeconds = 90,
            ZombieTimeoutSeconds = 120
        };

        // These compatibility aliases let existing hosts keep compiling while new docs teach
        // the clearer nested configuration model used by appsettings binding.
        options.Retry.MaxAttempts.Should().Be(5);
        options.Retry.BaseDelay.Should().Be(TimeSpan.FromSeconds(7));
        options.Recovery.FetchedJobTimeout.Should().Be(TimeSpan.FromSeconds(90));
        options.Recovery.ProcessingZombieTimeout.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void BindFromConfiguration_ShouldPopulateNestedRuntimeOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Execution:DefaultTimeout"] = "00:02:00",
                ["Execution:HeartbeatIntervalMin"] = "00:00:04",
                ["Execution:HeartbeatIntervalMax"] = "00:00:06",
                ["Execution:HeartbeatFailureThreshold"] = "4",
                ["Retry:MaxAttempts"] = "6",
                ["Retry:BaseDelay"] = "00:00:02",
                ["Retry:MaxDelay"] = "00:10:00",
                ["Retry:BackoffMultiplier"] = "3",
                ["Retry:JitterMaxDelay"] = "00:00:00.250",
                ["Retry:CircuitBreakerDelay"] = "00:00:08",
                ["Recovery:FetchedJobTimeout"] = "00:03:00",
                ["Recovery:ProcessingZombieTimeout"] = "00:04:00",
                ["Recovery:ScanInterval"] = "00:00:20",
                ["Worker:PausedQueuePollingDelay"] = "00:00:00.500",
                ["InMemory:MaxCapacity"] = "250",
                ["Metrics:MaxQueueTagValues"] = "12",
                ["Metrics:MaxJobTypeTagValues"] = "34",
                ["Metrics:MaxErrorTagValues"] = "8",
                ["Metrics:MaxFailureReasonTagValues"] = "5",
                ["Metrics:MaxTagValueLength"] = "64",
                ["Metrics:UnknownTagValue"] = "missing",
                ["Metrics:OverflowTagValue"] = "overflow",
                ["Queues:reports:ExecutionTimeout"] = "01:00:00"
            })
            .Build();

        var options = new ChokaQOptions();
        configuration.Bind(options);

        options.Execution.DefaultTimeout.Should().Be(TimeSpan.FromMinutes(2));
        options.Execution.HeartbeatIntervalMin.Should().Be(TimeSpan.FromSeconds(4));
        options.Execution.HeartbeatIntervalMax.Should().Be(TimeSpan.FromSeconds(6));
        options.Execution.HeartbeatFailureThreshold.Should().Be(4);
        options.Retry.MaxAttempts.Should().Be(6);
        options.Retry.BaseDelay.Should().Be(TimeSpan.FromSeconds(2));
        options.Retry.MaxDelay.Should().Be(TimeSpan.FromMinutes(10));
        options.Retry.BackoffMultiplier.Should().Be(3);
        options.Retry.JitterMaxDelay.Should().Be(TimeSpan.FromMilliseconds(250));
        options.Retry.CircuitBreakerDelay.Should().Be(TimeSpan.FromSeconds(8));
        options.Recovery.FetchedJobTimeout.Should().Be(TimeSpan.FromMinutes(3));
        options.Recovery.ProcessingZombieTimeout.Should().Be(TimeSpan.FromMinutes(4));
        options.Recovery.ScanInterval.Should().Be(TimeSpan.FromSeconds(20));
        options.Worker.PausedQueuePollingDelay.Should().Be(TimeSpan.FromMilliseconds(500));
        options.InMemory.MaxCapacity.Should().Be(250);
        options.InMemoryOptions.MaxCapacity.Should().Be(250);
        options.Metrics.MaxQueueTagValues.Should().Be(12);
        options.Metrics.MaxJobTypeTagValues.Should().Be(34);
        options.Metrics.MaxErrorTagValues.Should().Be(8);
        options.Metrics.MaxFailureReasonTagValues.Should().Be(5);
        options.Metrics.MaxTagValueLength.Should().Be(64);
        options.Metrics.UnknownTagValue.Should().Be("missing");
        options.Metrics.OverflowTagValue.Should().Be("overflow");
        options.GetExecutionTimeoutForQueue("reports").Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void AddChokaQ_WithConfiguration_ShouldBindChokaQSectionAndAllowCodeOverrides()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ChokaQ:Retry:MaxAttempts"] = "8",
                ["ChokaQ:Execution:DefaultTimeout"] = "00:05:00",
                ["ChokaQ:InMemory:MaxCapacity"] = "2048"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChokaQ(configuration, options =>
        {
            options.Retry.MaxAttempts = 4;
            options.AddProfile<TestProfile>();
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<ChokaQOptions>();

        // appsettings supplies the operational default; code can still own compile-time
        // registration such as profiles and rare test/deployment overrides.
        options.Execution.DefaultTimeout.Should().Be(TimeSpan.FromMinutes(5));
        options.Retry.MaxAttempts.Should().Be(4);
        options.InMemory.MaxCapacity.Should().Be(2048);
        options.ProfileTypes.Should().Contain(typeof(TestProfile));
    }

    [Fact]
    public void ValidateOrThrow_ShouldRejectUnsafeRuntimeConfiguration()
    {
        var options = new ChokaQOptions();
        options.Execution.DefaultTimeout = TimeSpan.Zero;
        options.Retry.MaxAttempts = 0;
        options.Retry.BackoffMultiplier = 0.5;
        options.InMemory.MaxCapacity = 0;
        options.Metrics.MaxQueueTagValues = 0;

        Action act = options.ValidateOrThrow;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Execution.DefaultTimeout*Retry.MaxAttempts*Retry.BackoffMultiplier*InMemory.MaxCapacity*Metrics.MaxQueueTagValues*");
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
