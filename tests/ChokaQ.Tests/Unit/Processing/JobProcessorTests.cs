using ChokaQ.Abstractions.Observability;
using ChokaQ.Abstractions.Resilience;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Exceptions;
using ChokaQ.Core;
using ChokaQ.Core.Execution;
using ChokaQ.Core.Processing;
using ChokaQ.Core.State;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;
using System.Reflection;

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
        _stateManager.MarkAsProcessingAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>())
            .Returns(Task.FromResult(true));

        _processor = new JobProcessor(
            _storage,
            NullLogger<JobProcessor>.Instance,
            _breaker,
            _dispatcher,
            _stateManager,
            Substitute.For<IChokaQMetrics>());
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
        await _processor.ProcessJobAsync("job1", "TestJob", "{}", "worker1", 0, null, DateTime.UtcNow, DateTime.UtcNow, CancellationToken.None);

        // Assert
        await _stateManager.Received(1).ArchiveSucceededAsync(
            "job1", "TestJob", "default", Arg.Any<double>(), Arg.Any<CancellationToken>(), "worker1");
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
        await _processor.ProcessJobAsync("job1", "TestJob", "{}", "worker1", 0, null, DateTime.UtcNow, DateTime.UtcNow, CancellationToken.None);

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
            .Throws(new Exception("Test error"));

        // Act
        await _processor.ProcessJobAsync("job1", "TestJob", "{}", "worker1", 0, null, DateTime.UtcNow, DateTime.UtcNow, CancellationToken.None);

        // Assert
        await _stateManager.Received(1).RescheduleForRetryAsync(
            "job1", "TestJob", "default", 10,
            Arg.Any<DateTime>(), 1, "Test error", Arg.Any<CancellationToken>(), "worker1");
    }

    [Fact]
    public async Task ProcessJobAsync_Failure_AtMaxRetries_ShouldArchiveToDLQ()
    {
        // Arrange
        _processor.MaxRetries = 3;
        _breaker.IsExecutionPermitted("TestJob").Returns(true);
        _dispatcher.ParseMetadata(Arg.Any<string>()).Returns(new JobMetadata("default", 10));
        _dispatcher.DispatchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new Exception("Test error"));

        // Act - Two attempts already started; this execution is attempt 3 (last retry)
        await _processor.ProcessJobAsync("job1", "TestJob", "{}", "worker1", 2, null, DateTime.UtcNow, DateTime.UtcNow, CancellationToken.None);

        // Assert
        await _stateManager.Received(1).ArchiveFailedAsync(
            "job1",
            "TestJob",
            "default",
            Arg.Is<string>(s => s.Contains("Test error")),
            Arg.Any<CancellationToken>(),
            "worker1",
            FailureReason.Transient);
    }

    [Fact]
    public async Task ProcessJobAsync_FatalFailure_ShouldArchiveWithFatalReason()
    {
        // Arrange
        _processor.MaxRetries = 3;
        _breaker.IsExecutionPermitted("TestJob").Returns(true);
        _dispatcher.ParseMetadata(Arg.Any<string>()).Returns(new JobMetadata("default", 10));
        _dispatcher.DispatchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ChokaQFatalException("poison payload"));

        // Act
        await _processor.ProcessJobAsync("job1", "TestJob", "{}", "worker1", 0, null, DateTime.UtcNow, DateTime.UtcNow, CancellationToken.None);

        // Assert
        // Fatal errors are poison pills. Persisting FatalError lets operators fix payload/code
        // instead of wasting time treating it like a retryable infrastructure blip.
        await _stateManager.Received(1).ArchiveFailedAsync(
            "job1",
            "TestJob",
            "default",
            Arg.Is<string>(s => s.Contains("FATAL") && s.Contains("poison payload")),
            Arg.Any<CancellationToken>(),
            "worker1",
            FailureReason.FatalError);
    }

    [Fact]
    public async Task ProcessJobAsync_ThrottledFailure_AtMaxRetries_ShouldArchiveWithThrottledReason()
    {
        // Arrange
        _processor.MaxRetries = 3;
        _breaker.IsExecutionPermitted("TestJob").Returns(true);
        _dispatcher.ParseMetadata(Arg.Any<string>()).Returns(new JobMetadata("default", 10));
        _dispatcher.DispatchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new TestThrottledException());

        // Act
        await _processor.ProcessJobAsync("job1", "TestJob", "{}", "worker1", 2, null, DateTime.UtcNow, DateTime.UtcNow, CancellationToken.None);

        // Assert
        // Throttling is a capacity/contract signal, not a generic exception. Operators need
        // the DLQ reason to point them toward quotas and queue concurrency, not handler bugs.
        await _stateManager.Received(1).ArchiveFailedAsync(
            "job1",
            "TestJob",
            "default",
            Arg.Is<string>(s => s.Contains(nameof(TestThrottledException))),
            Arg.Any<CancellationToken>(),
            "worker1",
            FailureReason.Throttled);
    }

    [Fact]
    public async Task ProcessJobAsync_Failure_ShouldReportToCircuitBreaker()
    {
        // Arrange
        _breaker.IsExecutionPermitted("TestJob").Returns(true);
        _dispatcher.ParseMetadata(Arg.Any<string>()).Returns(new JobMetadata("default", 10));
        _dispatcher.DispatchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new Exception("Test error"));

        // Act
        await _processor.ProcessJobAsync("job1", "TestJob", "{}", "worker1", 0, null, DateTime.UtcNow, DateTime.UtcNow, CancellationToken.None);

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
        await _processor.ProcessJobAsync("job1", "TestJob", "{}", "worker1", 0, null, DateTime.UtcNow, DateTime.UtcNow, CancellationToken.None);

        // Assert
        await _stateManager.Received(1).ArchiveCancelledAsync(
            "job1", "TestJob", "default", ChokaQ.Abstractions.Enums.JobCancellationReason.Timeout, "Execution Timeout or Admin Cancellation", Arg.Any<CancellationToken>(), "worker1");
    }

    [Fact]
    public async Task ProcessJobAsync_CircuitOpen_ShouldRescheduleWithDelay()
    {
        // Arrange
        _processor.CircuitBreakerDelaySeconds = 5;
        _breaker.IsExecutionPermitted("TestJob").Returns(false);
        _dispatcher.ParseMetadata(Arg.Any<string>()).Returns(new JobMetadata("default", 10));

        // Act
        await _processor.ProcessJobAsync("job1", "TestJob", "{}", "worker1", 0, null, DateTime.UtcNow, DateTime.UtcNow, CancellationToken.None);

        // Assert
        await _stateManager.Received(1).RescheduleForRetryAsync(
            "job1", "TestJob", "default", 10,
            Arg.Is<DateTime>(dt => dt > DateTime.UtcNow.AddSeconds(4)),
            0, "Circuit Breaker Open", Arg.Any<CancellationToken>(), "worker1");

        // Should NOT execute the job
        await _dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessJobAsync_ShouldUsePerQueueExecutionTimeout()
    {
        // Arrange
        var options = new ChokaQOptions
        {
            Execution = { DefaultTimeout = TimeSpan.FromSeconds(10) },
            Queues =
            {
                ["slow-reports"] = new ChokaQQueueRuntimeOptions
                {
                    ExecutionTimeout = TimeSpan.FromMilliseconds(50)
                }
            }
        };

        var processor = new JobProcessor(
            _storage,
            NullLogger<JobProcessor>.Instance,
            _breaker,
            _dispatcher,
            _stateManager,
            Substitute.For<IChokaQMetrics>(),
            options);

        _breaker.IsExecutionPermitted("TestJob").Returns(true);
        _dispatcher.ParseMetadata(Arg.Any<string>()).Returns(new JobMetadata("slow-reports", 10));
        _dispatcher.DispatchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var ct = callInfo.Arg<CancellationToken>();
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            });

        // Act
        await processor.ProcessJobAsync("job1", "TestJob", "{}", "worker1", 0, null, DateTime.UtcNow, DateTime.UtcNow, CancellationToken.None);

        // Assert
        // The timeout is resolved from the queue, not from the global default. This lets a
        // production host isolate long-running queues instead of raising the timeout for every job.
        await _stateManager.Received(1).ArchiveCancelledAsync(
            "job1",
            "TestJob",
            "slow-reports",
            JobCancellationReason.Timeout,
            "Execution Timeout or Admin Cancellation",
            Arg.Any<CancellationToken>(),
            "worker1");
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
        await _processor.ProcessJobAsync("job1", "TestJob", "{}", "worker1", 0, "user1", DateTime.UtcNow, DateTime.UtcNow, CancellationToken.None);

        // Assert
        await _stateManager.Received(1).MarkAsProcessingAsync(
            "job1", "TestJob", "default", 10, 1, "user1", Arg.Any<CancellationToken>(), "worker1");
        executionStarted.Should().BeTrue();
    }

    [Fact]
    public async Task ProcessJobAsync_WhenProcessingLeaseIsLost_ShouldNotDispatch()
    {
        // Arrange
        _breaker.IsExecutionPermitted("TestJob").Returns(true);
        _dispatcher.ParseMetadata(Arg.Any<string>()).Returns(new JobMetadata("default", 10));
        _stateManager.MarkAsProcessingAsync(
                "job1",
                "TestJob",
                "default",
                10,
                1,
                null,
                Arg.Any<CancellationToken>(),
                "worker1")
            .Returns(Task.FromResult(false));

        // Act
        await _processor.ProcessJobAsync("job1", "TestJob", "{}", "worker1", 0, null, DateTime.UtcNow, DateTime.UtcNow, CancellationToken.None);

        // Assert
        // This is the buffered-job safety contract: if persisted ownership disappeared after
        // fetch, user code must never run from a stale in-memory copy.
        await _dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _stateManager.DidNotReceive().ArchiveSucceededAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task CancelJob_ShouldTriggerCancellationToken()
    {
        // Arrange
        _breaker.IsExecutionPermitted("TestJob").Returns(true);
        _dispatcher.ParseMetadata(Arg.Any<string>()).Returns(new JobMetadata("default", 10));

        var cancellationDetected = false;
        _dispatcher.DispatchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var ct = callInfo.Arg<CancellationToken>();
                try
                {
                    await Task.Delay(2000, ct); // Will throw if cancelled
                }
                catch (OperationCanceledException)
                {
                    cancellationDetected = true;
                    throw;
                }
            });

        // Act
        var processTask = _processor.ProcessJobAsync("job1", "TestJob", "{}", "worker1", 0, null, DateTime.UtcNow, DateTime.UtcNow, CancellationToken.None);
        await Task.Delay(50); // Give it time to start
        _processor.CancelJob("job1");

        // Assert - Should complete without hanging
        await processTask;
        cancellationDetected.Should().BeTrue();
    }

    [Fact]
    public void CalculateBackoff_ShouldUseExponentialFormula()
    {
        // Arrange
        _processor.RetryDelaySeconds = 3;

        // Act - Use reflection to call private method
        var method = typeof(JobProcessor).GetMethod("CalculateBackoff",
            BindingFlags.NonPublic | BindingFlags.Instance);

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
            BindingFlags.NonPublic | BindingFlags.Instance);

        var backoff = (int)method!.Invoke(_processor, new object[] { 20 })!;

        // Assert - MaxDelay is a true cap. Jitter is clipped instead of being added past it.
        backoff.Should().Be(3600000);
    }

    private sealed class TestThrottledException : Exception, IChokaQThrottledException
    {
        public TimeSpan? RetryAfter => TimeSpan.FromSeconds(30);
    }
}
