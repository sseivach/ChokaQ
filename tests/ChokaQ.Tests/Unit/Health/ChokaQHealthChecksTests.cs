using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Abstractions.Workers;
using ChokaQ.Core.Extensions;
using ChokaQ.Core.Health;
using ChokaQ.Storage.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace ChokaQ.Tests.Unit.Health;

public class ChokaQHealthChecksTests
{
    [Fact]
    public async Task WorkerHealthCheck_ShouldBeHealthy_WhenWorkerIsRunningAndHeartbeatIsFresh()
    {
        var worker = Substitute.For<IWorkerManager>();
        worker.IsRunning.Returns(true);
        worker.TotalWorkers.Returns(4);
        worker.ActiveWorkers.Returns(1);
        worker.LastHeartbeatUtc.Returns(DateTimeOffset.UtcNow);

        var check = new ChokaQWorkerHealthCheck(
            worker,
            Options.Create(new ChokaQHealthCheckOptions()));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data.Should().ContainKey("lastHeartbeatUtc");
    }

    [Fact]
    public async Task WorkerHealthCheck_ShouldBeUnhealthy_WhenHeartbeatIsStale()
    {
        var worker = Substitute.For<IWorkerManager>();
        worker.IsRunning.Returns(true);
        worker.TotalWorkers.Returns(4);
        worker.LastHeartbeatUtc.Returns(DateTimeOffset.UtcNow.AddMinutes(-5));

        var check = new ChokaQWorkerHealthCheck(
            worker,
            Options.Create(new ChokaQHealthCheckOptions
            {
                WorkerHeartbeatTimeout = TimeSpan.FromSeconds(10)
            }));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("stale");
    }

    [Theory]
    [InlineData(2, HealthStatus.Healthy)]
    [InlineData(7, HealthStatus.Degraded)]
    [InlineData(15, HealthStatus.Unhealthy)]
    public async Task QueueSaturationHealthCheck_ShouldMapLagToHealthStatus(
        double maxLagSeconds,
        HealthStatus expectedStatus)
    {
        var storage = Substitute.For<IJobStorage>();
        storage.GetSystemHealthAsync(Arg.Any<CancellationToken>())
            .Returns(new SystemHealthDto(
                GeneratedAtUtc: DateTime.UtcNow,
                Queues: new[]
                {
                    new QueueHealthDto("critical-path", Pending: 10, AverageLagSeconds: maxLagSeconds / 2, MaxLagSeconds: maxLagSeconds)
                },
                JobsPerSecondLastMinute: 12,
                JobsPerSecondLastFiveMinutes: 10,
                FailureRateLastMinutePercent: 1,
                FailureRateLastFiveMinutesPercent: 2,
                TopErrors: Array.Empty<DlqErrorGroupDto>()));

        var check = new ChokaQQueueSaturationHealthCheck(
            storage,
            Options.Create(new ChokaQHealthCheckOptions
            {
                QueueLagDegradedThreshold = TimeSpan.FromSeconds(5),
                QueueLagUnhealthyThreshold = TimeSpan.FromSeconds(10)
            }));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(expectedStatus);
        result.Data["worstQueue"].Should().Be("critical-path");
    }

    [Fact]
    public void AddChokaQHealthChecks_ShouldRegisterWorkerAndQueueChecks()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChokaQ();

        services.AddHealthChecks()
            .AddChokaQHealthChecks(options =>
            {
                options.WorkerHeartbeatTimeout = TimeSpan.FromSeconds(20);
            });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

        // Registration names are the public contract operators see in /health payloads.
        options.Registrations.Select(registration => registration.Name)
            .Should().Contain(new[] { "chokaq_worker", "chokaq_queue_saturation" });
    }

    [Fact]
    public void AddChokaQHealthChecks_WithConfiguration_ShouldBindThresholds()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ChokaQ:Health:WorkerHeartbeatTimeout"] = "00:00:45",
                ["ChokaQ:Health:QueueLagDegradedThreshold"] = "00:00:07",
                ["ChokaQ:Health:QueueLagUnhealthyThreshold"] = "00:00:15"
            })
            .Build();

        services.AddHealthChecks()
            .AddChokaQHealthChecks(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ChokaQHealthCheckOptions>>().Value;

        // Health thresholds are runtime operations policy. Binding them from IConfiguration lets
        // admins tune readiness sensitivity per environment without recompiling the application.
        options.WorkerHeartbeatTimeout.Should().Be(TimeSpan.FromSeconds(45));
        options.QueueLagDegradedThreshold.Should().Be(TimeSpan.FromSeconds(7));
        options.QueueLagUnhealthyThreshold.Should().Be(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void AddChokaQHealthChecks_ShouldFailFastOnInvalidThresholds()
    {
        var services = new ServiceCollection();

        var act = () => services.AddHealthChecks()
            .AddChokaQHealthChecks(options =>
            {
                options.QueueLagDegradedThreshold = TimeSpan.FromSeconds(30);
                options.QueueLagUnhealthyThreshold = TimeSpan.FromSeconds(10);
            });

        // Readiness thresholds are production policy, so contradictory values should fail during
        // service registration instead of turning into a misleading /health result at runtime.
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*QueueLagUnhealthyThreshold*");
    }

    [Fact]
    public void AddChokaQSqlServerHealthChecks_ShouldRegisterSqlAndGenericChecks()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChokaQ();
        services.UseSqlServer(options =>
        {
            options.ConnectionString = "Server=localhost;Database=ChokaQ;Trusted_Connection=True;TrustServerCertificate=True;";
        });

        services.AddHealthChecks()
            .AddChokaQSqlServerHealthChecks();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

        options.Registrations.Select(registration => registration.Name)
            .Should().Contain(new[] { "chokaq_sql", "chokaq_worker", "chokaq_queue_saturation" });
    }
}
