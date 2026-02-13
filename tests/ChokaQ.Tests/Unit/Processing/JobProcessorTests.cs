using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Notifications;
using ChokaQ.Abstractions.Resilience;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Core.Execution;
using ChokaQ.Core.Processing;
using ChokaQ.Core.State;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace ChokaQ.Tests.Unit.Processing;

/// <summary>
/// Unit tests for JobProcessor - the core job execution engine.
/// Tests success, failure, retry logic, cancellation, and circuit breaker integration.
/// </summary>
public class JobProcessorTests
{
    private readonly IJobStorage _storage;
    private readonly ICircuitBreaker _breaker;
    private readonly IJobDispatcher _dispatcher;
    private readonly IJobStateManager _stateManager;
    private readonly JobProcessor _processor;

    public JobProcessorTests()
    {
        _storage = Substitute.For<IJobStorage>();
        _breaker = Substitute.For<ICircuitBreaker>();
        _dispatcher = Substitute.For<IJobDispatcher>();
        _stateManager = Substitute.For<IJobStateManager>();

        _processor = new JobProcessor(
            _storage,
            NullLogger<JobProcessor>.Instance,
            _breaker,
            _dispatcher,
            _stateManager);
    }

    [Fact]
    public async Task ProcessJobAsync_Success_ShouldArchiveToArchive()
    {
        // Arrange
        _breaker.IsExecutionPermitted("TestJob").Returns(true);
        _dispatcher.ParseMetadata(Arg.Any<string>()).Returns(new JobMetadata("default", 10));
        _dispatcher.DispatchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Act
        await _processor.ProcessJobAsync("job1", "TestJob", "{}", "worker1", 1, null, CancellationToken.None);

        // Assert
        await _stateManager.Received(1).ArchiveSucceededAsync(
            "job1", "TestJob", "default", Arg.Any<double>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessJobAsync_Success_ShouldReportToCircuitBreaker()
    {
        // Arrange
        _breaker.IsExecutionPermitted("TestJob").Returns(true);
        _dispatcher.ParseMetadata(Arg.Any<string>()).Returns(new JobMetadata("default", 10));
        _dispatcher.DispatchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Act
        await _processor.ProcessJobAsync("job1", "TestJob", "{}", "worker1", 1, null, CancellationToken.None);

        // Assert
        _breaker.Received(1).ReportSuccess("TestJob");
    }

    [Fact]
    public async Task ProcessJobAsync_Failure_UnderMaxRetries_ShouldReschedule()
    {
        // Arrange
        _processor.MaxRetries = 3;
        _breaker.IsExecutionPermitted("TestJob").Returns(true);
        _dispatcher.ParseMetadata(Arg.Any<string>()).Returns(new JobMetadata("default", 10));
        _dispatcher.DispatchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Test error"));

        // Act
        await _processor.ProcessJobAsync("job1", "TestJob", "{}", "worker1", 1, null, CancellationToken.None);

        // Assert
        await _stateManager.Received(1).RescheduleForRetryAsync(
            "job1", "TestJob", "default", 10,
            Arg.Any<DateTime>(), 2, "Test error", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessJobAsync_Failure_AtMaxRetries_ShouldArchiveToDLQ()
    {
        // Arrange
        _processor.MaxRetries = 3;
        _breaker.IsExecutionPermitted("TestJob").Returns(true);
        _dispatcher.ParseMetadata(Arg.Any<string>()).Returns(new JobMetadata("default", 10));
        _dispatcher.DispatchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Test error"));

        // Act - Attempt 3 (last retry)
        await _processor.ProcessJobAsync("job1", "TestJob", "{}", "worker1", 3, null, CancellationToken.None);

        // Assert
        await _stateManager.Received(1).ArchiveFailedAsync(
            "job1", "TestJob", "default", Arg.Is<string>(s => s.Contains("Test error")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessJobAsync_Failure_ShouldReportToCircuitBreaker()
    {
        // Arrange
        _breaker.IsExecutionPermitted("TestJob").Returns(true);
        _dispatcher.ParseMetadata(Arg.Any<string>()).Returns(new JobMetadata("default", 10));
        _dispatcher.DispatchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Test error"));

        // Act
        await _processor.ProcessJobAsync("job1", "TestJob", "{}", "worker1", 1, null, CancellationToken.None);

        // Assert
        _breaker.Received(1).ReportFailure("TestJob");
    }

    [Fact]
    public async Task ProcessJobAsync_Cancellation_ShouldArchiveToDLQ()
    {
        // Arrange
        _breaker.IsExecutionPermitted("TestJob").Returns(true);
        _dispatcher.ParseMetadata(Arg.Any<string>()).Returns(new JobMetadata("default", 10));
        _dispatcher.DispatchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException());

        // Act
        await _processor.ProcessJobAsync("job1", "TestJob", "{}", "worker1", 1, null, CancellationToken.None);

        // Assert
        await _stateManager.Received(1).ArchiveCancelledAsync(
            "job1", "TestJob", "default", "Worker/Admin cancellation", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessJobAsync_CircuitOpen_ShouldRescheduleWithDelay()
    {
        // Arrange
        _processor.CircuitBreakerDelaySeconds = 5;
        _breaker.IsExecutionPermitted("TestJob").Returns(false);
        _dispatcher.ParseMetadata(Arg.Any<string>()).Returns(new JobMetadata("default", 10));

        // Act
        await _processor.ProcessJobAsync("job1", "TestJob", "{}", "worker1", 1, null, CancellationToken.None);

        // Assert
        await _stateManager.Received(1).RescheduleForRetryAsync(
            "job1", "TestJob", "default", 10,
            Arg.Is<DateTime>(dt => dt > DateTime.UtcNow.AddSeconds(4)),
            1, "Circuit Breaker Open", Arg.Any<CancellationToken>());
        
        // Should NOT execute the job
        await _dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessJobAsync_ShouldMarkAsProcessing_BeforeExecution()
    {
        // Arrange
        _breaker.IsExecutionPermitted("TestJob").Returns(true);
        _dispatcher.ParseMetadata(Arg.Any<string>()).Returns(new JobMetadata("default", 10));
        
        var executionStarted = false;
        _dispatcher.DispatchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                executionStarted = true;
                await Task.CompletedTask;
            });

        // Act
        await _processor.ProcessJobAsync("job1", "TestJob", "{}", "worker1", 1, "user1", CancellationToken.None);

        // Assert
        await _stateManager.Received(1).MarkAsProcessingAsync(
            "job1", "TestJob", "default", 10, 1, "user1", Arg.Any<CancellationToken>());
        executionStarted.Should().BeTrue();
    }

    [Fact]
    public void CancelJob_ShouldTriggerCancellationToken()
    {
        // Arrange
        _breaker.IsExecutionPermitted("TestJob").Returns(true);
        _dispatcher.ParseMetadata(Arg.Any<string>()).Returns(new JobMetadata("default", 10));
        
        var cancellationDetected = false;
        _dispatcher.DispatchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var ct = callInfo.Arg<CancellationToken>();
                await Task.Delay(100, ct); // Will throw if cancelled
            });

        // Act
        var processTask = _processor.ProcessJobAsync("job1", "TestJob", "{}", "worker1", 1, null, CancellationToken.None);
        Task.Delay(10).Wait(); // Give it time to start
        _processor.CancelJob("job1");

        // Assert - Should complete without hanging
        var completed = processTask.Wait(TimeSpan.FromSeconds(2));
        completed.Should().BeTrue("Job should be cancelled quickly");
    }

    [Fact]
    public void CalculateBackoff_ShouldUseExponentialFormula()
    {
        // Arrange
        _processor.RetryDelaySeconds = 3;

        // Act - Use reflection to call private method
        var method = typeof(JobProcessor).GetMethod("CalculateBackoff", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        var backoff1 = (int)method!.Invoke(_processor, new object[] { 1 })!;
        var backoff2 = (int)method!.Invoke(_processor, new object[] { 2 })!;
        var backoff3 = (int)method!.Invoke(_processor, new object[] { 3 })!;

        // Assert - Exponential growth (with jitter tolerance)
        // Attempt 1: 3 * 2^0 = 3 seconds = 3000ms
        // Attempt 2: 3 * 2^1 = 6 seconds = 6000ms
        // Attempt 3: 3 * 2^2 = 12 seconds = 12000ms
        backoff1.Should().BeInRange(3000, 4000); // 3s + jitter
        backoff2.Should().BeInRange(6000, 7000); // 6s + jitter
        backoff3.Should().BeInRange(12000, 13000); // 12s + jitter
    }

    [Fact]
    public void CalculateBackoff_ShouldCapAtOneHour()
    {
        // Arrange
        _processor.RetryDelaySeconds = 3;

        // Act - Use reflection to call private method for a very high attempt
        var method = typeof(JobProcessor).GetMethod("CalculateBackoff", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        var backoff = (int)method!.Invoke(_processor, new object[] { 20 })!;

        // Assert - Should cap at 1 hour (3600 seconds = 3600000ms)
        backoff.Should().BeLessOrEqualTo(3601000); // 1 hour + jitter
    }
}
