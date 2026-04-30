using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Resilience;
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
        // Arrange - Default policy: FailureThreshold = 5
        var breaker = new InMemoryCircuitBreaker(TimeProvider.System);

        // Act
        for (int i = 0; i < 5; i++)
            breaker.ReportFailure("TestJob");

        // Assert
        breaker.IsExecutionPermitted("TestJob").Should().BeFalse();
    }

    [Fact]
    public void IsExecutionPermitted_ShouldReturnFalse_WhenOpen()
    {
        // Arrange
        var breaker = new InMemoryCircuitBreaker(TimeProvider.System);
        for (int i = 0; i < 5; i++)
            breaker.ReportFailure("TestJob");

        // Act
        var permitted = breaker.IsExecutionPermitted("TestJob");

        // Assert
        permitted.Should().BeFalse();
    }

    [Fact]
    public void FatalFailure_ShouldOpenCircuit_Immediately_WithoutThreshold()
    {
        // Arrange - Fatal errors bypass the failure threshold
        var breaker = new InMemoryCircuitBreaker(TimeProvider.System);

        // Act - only ONE fatal failure
        breaker.ReportFailure("TestJob", CircuitFailureSeverity.Fatal);

        // Assert - circuit is immediately OPEN
        breaker.IsExecutionPermitted("TestJob").Should().BeFalse();
        breaker.GetStatus("TestJob").Should().Be(CircuitStatus.Open);
    }

    [Fact]
    public void GetStatus_ShouldReturnCorrectState_PerCircuitKey()
    {
        // Arrange - Per-dependency isolation (Bulkhead pattern)
        var breaker = new InMemoryCircuitBreaker(TimeProvider.System);

        // Act - Open "JobA" circuit only
        for (int i = 0; i < 5; i++)
            breaker.ReportFailure("JobA");

        // Assert - "JobB" is completely unaffected
        breaker.GetStatus("JobA").Should().Be(CircuitStatus.Open);
        breaker.GetStatus("JobB").Should().Be(CircuitStatus.Closed);
    }

    [Fact]
    public void GetCircuitStats_ShouldReturnAllTrackedCircuits()
    {
        // Arrange
        var breaker = new InMemoryCircuitBreaker(TimeProvider.System);

        // Act
        breaker.ReportFailure("JobA");
        for (int i = 0; i < 5; i++)
            breaker.ReportFailure("JobB");

        var stats = breaker.GetCircuitStats();

        // Assert - renamed JobType → CircuitKey
        stats.Should().HaveCount(2);
        stats.Should().Contain(s => s.CircuitKey == "JobA" && s.FailureCount == 1);
        stats.Should().Contain(s => s.CircuitKey == "JobB" && s.FailureCount == 5 && s.Status == CircuitStatus.Open);
    }

    [Fact]
    public void RegisterPolicy_ShouldApplyCustomThreshold()
    {
        // [Phase 3 - Per-Dependency Policy]
        // Different external APIs should have different thresholds.
        // A payment gateway might trip after 2 failures, an email service after 10.
        var breaker = new InMemoryCircuitBreaker(TimeProvider.System);
        breaker.RegisterPolicy("PaymentApi", new CircuitPolicy(FailureThreshold: 2));

        // Act - only 2 failures should open this circuit
        breaker.ReportFailure("PaymentApi");
        breaker.ReportFailure("PaymentApi");

        // Assert
        breaker.GetStatus("PaymentApi").Should().Be(CircuitStatus.Open);
    }

    [Fact]
    public void RegisterPolicy_ShouldNotAffect_OtherCircuits()
    {
        // [Bulkhead Isolation] Custom policy on one key must not affect others
        var breaker = new InMemoryCircuitBreaker(TimeProvider.System);
        breaker.RegisterPolicy("PaymentApi", new CircuitPolicy(FailureThreshold: 2));

        // 2 failures opens PaymentApi, but shouldn't affect EmailApi (default threshold = 5)
        breaker.ReportFailure("PaymentApi");
        breaker.ReportFailure("PaymentApi");
        breaker.ReportFailure("EmailApi");
        breaker.ReportFailure("EmailApi");

        // Assert
        breaker.GetStatus("PaymentApi").Should().Be(CircuitStatus.Open);
        breaker.GetStatus("EmailApi").Should().Be(CircuitStatus.Closed);
    }

    [Fact]
    public void HalfOpenMaxCalls_ShouldDeny_ConcurrentProbes()
    {
        // [Phase 3 - Half-Open Limit]
        // With HalfOpenMaxCalls=1, a second probe attempt while the first
        // is still running must be denied. This prevents Thundering Herd
        // on a newly recovered service.
        var fakeTime = new FakeTimeProvider();
        var breaker = new InMemoryCircuitBreaker(fakeTime);
        breaker.RegisterPolicy("TestJob", new CircuitPolicy(
            FailureThreshold: 5,
            BreakDurationSeconds: 30,
            HalfOpenMaxCalls: 1));

        // Open circuit
        for (int i = 0; i < 5; i++)
            breaker.ReportFailure("TestJob");

        // Advance time past break duration
        fakeTime.Advance(TimeSpan.FromSeconds(31));

        // Act - first probe is allowed
        var firstProbe = breaker.IsExecutionPermitted("TestJob");
        // Second probe must be denied while first hasn't reported back
        var secondProbe = breaker.IsExecutionPermitted("TestJob");

        // Assert
        firstProbe.Should().BeTrue();
        secondProbe.Should().BeFalse();
    }

    [Fact]
    public void ReportSuccess_WhenHalfOpen_ShouldClose_AndResetCounters()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider();
        var breaker = new InMemoryCircuitBreaker(fakeTime);
        breaker.RegisterPolicy("TestJob", new CircuitPolicy(FailureThreshold: 5, BreakDurationSeconds: 30));

        for (int i = 0; i < 5; i++)
            breaker.ReportFailure("TestJob");

        fakeTime.Advance(TimeSpan.FromSeconds(31));
        breaker.IsExecutionPermitted("TestJob"); // Transition to Half-Open

        // Act
        breaker.ReportSuccess("TestJob");

        // Assert
        breaker.GetStatus("TestJob").Should().Be(CircuitStatus.Closed);
        breaker.IsExecutionPermitted("TestJob").Should().BeTrue();
    }

    [Fact]
    public void ReportFailure_WhenHalfOpen_ShouldReopen_Immediately()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider();
        var breaker = new InMemoryCircuitBreaker(fakeTime);
        breaker.RegisterPolicy("TestJob", new CircuitPolicy(FailureThreshold: 5, BreakDurationSeconds: 30));

        for (int i = 0; i < 5; i++)
            breaker.ReportFailure("TestJob");

        fakeTime.Advance(TimeSpan.FromSeconds(31));
        breaker.IsExecutionPermitted("TestJob"); // Transition to Half-Open

        // Act - probe failed
        breaker.ReportFailure("TestJob");

        // Assert - immediately back to OPEN, no grace period
        breaker.GetStatus("TestJob").Should().Be(CircuitStatus.Open);
        breaker.IsExecutionPermitted("TestJob").Should().BeFalse();
    }
}

/// <summary>
/// Controllable TimeProvider for deterministic circuit breaker timing tests.
/// Eliminates the need for Thread.Sleep/Task.Delay in tests.
/// </summary>
internal class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration) => _utcNow += duration;
}
