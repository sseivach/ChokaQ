using ChokaQ.Core.Defaults;

namespace ChokaQ.Tests.Unit.Resilience;

public class InMemoryCircuitBreakerTests
{
    [Fact]
    public void IsExecutionPermitted_ShouldReturnTrue_WhenClosed()
    {
        // Arrange
        var breaker = new InMemoryCircuitBreaker(TimeProvider.System);

        // Act
        var permitted = breaker.IsExecutionPermitted("TestJob");

        // Assert
        permitted.Should().BeTrue();
    }

    [Fact]
    public void ReportFailure_ShouldOpenCircuit_AfterThreshold()
    {
        // Arrange
        var breaker = new InMemoryCircuitBreaker(TimeProvider.System);

        // Act - Hardcoded threshold is 5
        for (int i = 0; i < 5; i++)
        {
            breaker.ReportFailure("TestJob");
        }

        // Assert
        breaker.IsExecutionPermitted("TestJob").Should().BeFalse();
    }

    [Fact]
    public void IsExecutionPermitted_ShouldReturnFalse_WhenOpen()
    {
        // Arrange
        var breaker = new InMemoryCircuitBreaker(TimeProvider.System);
        for (int i = 0; i < 5; i++)
        {
            breaker.ReportFailure("TestJob"); // Open circuit
        }

        // Act
        var permitted = breaker.IsExecutionPermitted("TestJob");

        // Assert
        permitted.Should().BeFalse();
    }

    [Fact]
    public async Task IsExecutionPermitted_ShouldAllowOneProbe_AfterBreakDuration()
    {
        // Arrange - Break duration is hardcoded to 30 seconds
        // NOTE: This test takes 31 seconds to run - consider skipping in CI
        var breaker = new InMemoryCircuitBreaker(TimeProvider.System);
        for (int i = 0; i < 5; i++)
        {
            breaker.ReportFailure("TestJob"); // Open circuit
        }

        // Act
        await Task.Delay(31000); // Wait for break duration to expire (30s + buffer)
        var permitted = breaker.IsExecutionPermitted("TestJob");

        // Assert
        permitted.Should().BeTrue(); // Half-Open allows one probe
    }

    [Fact]
    public async Task ReportSuccess_ShouldCloseCircuit_WhenHalfOpen()
    {
        // Arrange - NOTE: This test takes 31 seconds to run
        var breaker = new InMemoryCircuitBreaker(TimeProvider.System);
        for (int i = 0; i < 5; i++)
        {
            breaker.ReportFailure("TestJob"); // Open circuit
        }
        await Task.Delay(31000); // Wait for Half-Open

        // Act
        breaker.IsExecutionPermitted("TestJob"); // Transition to Half-Open
        breaker.ReportSuccess("TestJob"); // Close circuit

        // Assert
        breaker.IsExecutionPermitted("TestJob").Should().BeTrue();
        breaker.GetStatus("TestJob").Should().Be(ChokaQ.Abstractions.Enums.CircuitStatus.Closed);
    }

    [Fact]
    public async Task ReportFailure_ShouldReopenCircuit_WhenHalfOpen()
    {
        // Arrange - NOTE: This test takes 31 seconds to run
        var breaker = new InMemoryCircuitBreaker(TimeProvider.System);
        for (int i = 0; i < 5; i++)
        {
            breaker.ReportFailure("TestJob"); // Open circuit
        }
        await Task.Delay(31000); // Wait for Half-Open

        // Act
        breaker.IsExecutionPermitted("TestJob"); // Transition to Half-Open
        breaker.ReportFailure("TestJob"); // Reopen circuit

        // Assert
        breaker.IsExecutionPermitted("TestJob").Should().BeFalse();
        breaker.GetStatus("TestJob").Should().Be(ChokaQ.Abstractions.Enums.CircuitStatus.Open);
    }

    [Fact]
    public void GetStatus_ShouldReturnCorrectState_PerJobType()
    {
        // Arrange
        var breaker = new InMemoryCircuitBreaker(TimeProvider.System);

        // Act
        for (int i = 0; i < 5; i++)
        {
            breaker.ReportFailure("JobA"); // JobA circuit opens
        }

        // Assert
        breaker.GetStatus("JobA").Should().Be(ChokaQ.Abstractions.Enums.CircuitStatus.Open);
        breaker.GetStatus("JobB").Should().Be(ChokaQ.Abstractions.Enums.CircuitStatus.Closed); // JobB unaffected
    }

    [Fact]
    public void GetCircuitStats_ShouldReturnAllTrackedTypes()
    {
        // Arrange
        var breaker = new InMemoryCircuitBreaker(TimeProvider.System);

        // Act
        breaker.ReportFailure("JobA");
        for (int i = 0; i < 5; i++)
        {
            breaker.ReportFailure("JobB"); // JobB opens
        }

        var stats = breaker.GetCircuitStats();

        // Assert
        stats.Should().HaveCount(2);
        stats.Should().Contain(s => s.JobType == "JobA" && s.FailureCount == 1);
        stats.Should().Contain(s => s.JobType == "JobB" && s.FailureCount == 5 && s.Status == ChokaQ.Abstractions.Enums.CircuitStatus.Open);
    }

    [Fact]
    public void ReportSuccess_WhenClosed_ShouldNotResetFailureCount()
    {
        // Arrange
        var breaker = new InMemoryCircuitBreaker(TimeProvider.System);

        // Act
        breaker.ReportFailure("TestJob");
        breaker.ReportFailure("TestJob"); // 2 failures
        breaker.ReportSuccess("TestJob"); // Does NOT reset when Closed

        var stats = breaker.GetCircuitStats();

        // Assert
        var jobStats = stats.First(s => s.JobType == "TestJob");
        jobStats.FailureCount.Should().Be(2); // Failure count persists
        jobStats.Status.Should().Be(ChokaQ.Abstractions.Enums.CircuitStatus.Closed);
    }
}
