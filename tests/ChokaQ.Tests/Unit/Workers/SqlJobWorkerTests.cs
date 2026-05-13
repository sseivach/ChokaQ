using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Core.Processing;
using ChokaQ.Core.State;
using ChokaQ.Storage.SqlServer;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChokaQ.Tests.Unit.Workers;

[Trait(TestCategories.Category, TestCategories.Unit)]
public class SqlJobWorkerTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldStayAliveUntilCancellation()
    {
        var storage = Substitute.For<IJobStorage>();
        storage.GetQueuesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<IEnumerable<QueueEntity>>(Array.Empty<QueueEntity>()));

        var worker = CreateWorker(storage);

        using var cts = new CancellationTokenSource();
        var runTask = worker.RunAsync(cts.Token);

        await Task.Delay(75);

        // A hosted BackgroundService must keep ExecuteAsync alive while its loops run.
        // This catches the Task.Factory.StartNew(async) pitfall where ExecuteAsync awaits
        // only the outer Task<Task> and returns while the inner loop is still running.
        runTask.IsCompleted.Should().BeFalse();

        await cts.CancelAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ExecuteAsync_OnShutdown_ShouldWaitForActiveProcessingTasks()
    {
        var storage = Substitute.For<IJobStorage>();
        var processor = Substitute.For<IJobProcessor>();

        var queue = new QueueEntity(
            Name: "default",
            IsPaused: false,
            IsActive: true,
            ZombieTimeoutSeconds: null,
            MaxWorkers: null,
            LastUpdatedUtc: DateTime.UtcNow);
        storage.GetQueuesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<IEnumerable<QueueEntity>>(new[] { queue }));

        var job = new JobHotEntity(
            Id: "job-1",
            Queue: "default",
            Type: "TestJob",
            Payload: "{}",
            Tags: null,
            IdempotencyKey: null,
            Priority: 10,
            Status: JobStatus.Fetched,
            AttemptCount: 1,
            WorkerId: "sql-worker-1",
            HeartbeatUtc: null,
            ScheduledAtUtc: null,
            CreatedAtUtc: DateTime.UtcNow,
            StartedAtUtc: null,
            LastUpdatedUtc: DateTime.UtcNow,
            CreatedBy: null,
            LastModifiedBy: null);

        var fetchCalls = 0;
        storage.FetchNextBatchAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string[]>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var jobs = Interlocked.Increment(ref fetchCalls) == 1
                    ? new[] { job }
                    : Array.Empty<JobHotEntity>();

                return new ValueTask<IEnumerable<JobHotEntity>>(jobs);
            });

        var processingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProcessing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        processor.ProcessJobAsync(
                job.Id,
                job.Type,
                job.Payload!,
                job.WorkerId!,
                job.AttemptCount,
                job.CreatedBy,
                job.ScheduledAtUtc,
                job.CreatedAtUtc,
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                processingStarted.SetResult();
                return releaseProcessing.Task;
            });

        var worker = CreateWorker(storage, processor);

        using var cts = new CancellationTokenSource();
        var runTask = worker.RunAsync(cts.Token);

        await processingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cts.CancelAsync();

        await Task.Delay(75);

        // Shutdown cancellation is only the request to stop. The worker must still observe
        // active job tasks until they finish their final state transition or cancellation path.
        runTask.IsCompleted.Should().BeFalse();

        releaseProcessing.SetResult();
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ExecuteAsync_WhenQueuePausedAfterFetch_ShouldReleaseBufferedJob()
    {
        var storage = Substitute.For<IJobStorage>();
        var processor = Substitute.For<IJobProcessor>();

        var activeQueue = new QueueEntity(
            Name: "default",
            IsPaused: false,
            IsActive: true,
            ZombieTimeoutSeconds: null,
            MaxWorkers: null,
            LastUpdatedUtc: DateTime.UtcNow);
        var pausedQueue = activeQueue with { IsPaused = true };

        var queueReads = 0;
        storage.GetQueuesAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var queue = Interlocked.Increment(ref queueReads) == 1
                    ? activeQueue
                    : pausedQueue;

                return new ValueTask<IEnumerable<QueueEntity>>(new[] { queue });
            });

        var job = new JobHotEntity(
            Id: "job-1",
            Queue: "default",
            Type: "TestJob",
            Payload: "{}",
            Tags: null,
            IdempotencyKey: null,
            Priority: 10,
            Status: JobStatus.Fetched,
            AttemptCount: 0,
            WorkerId: "sql-worker-1",
            HeartbeatUtc: null,
            ScheduledAtUtc: null,
            CreatedAtUtc: DateTime.UtcNow,
            StartedAtUtc: null,
            LastUpdatedUtc: DateTime.UtcNow,
            CreatedBy: null,
            LastModifiedBy: null);

        var fetchCalls = 0;
        storage.FetchNextBatchAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string[]>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var jobs = Interlocked.Increment(ref fetchCalls) == 1
                    ? new[] { job }
                    : Array.Empty<JobHotEntity>();

                return new ValueTask<IEnumerable<JobHotEntity>>(jobs);
            });

        var releaseObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.ReleaseJobAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                releaseObserved.SetResult();
                return ValueTask.CompletedTask;
            });

        var worker = CreateWorker(storage, processor);

        using var cts = new CancellationTokenSource();
        var runTask = worker.RunAsync(cts.Token);

        await releaseObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cts.CancelAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));

        // Operators expect pause to stop new work, including jobs already fetched into a local
        // SQL worker buffer. Releasing the row keeps the queue paused without dispatching stale work.
        await processor.DidNotReceive().ProcessJobAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<string?>(),
            Arg.Any<DateTime?>(),
            Arg.Any<DateTime>(),
            Arg.Any<CancellationToken>());
    }

    private static TestableSqlJobWorker CreateWorker(
        IJobStorage storage,
        IJobProcessor? processor = null)
    {
        processor ??= Substitute.For<IJobProcessor>();

        return new TestableSqlJobWorker(
            storage,
            processor,
            Substitute.For<IJobStateManager>(),
            new NullLogger<SqlJobWorker>(),
            new SqlJobStorageOptions
            {
                PollingInterval = TimeSpan.FromMilliseconds(10),
                NoQueuesSleepInterval = TimeSpan.FromMilliseconds(10)
            });
    }

    private sealed class TestableSqlJobWorker : SqlJobWorker
    {
        public TestableSqlJobWorker(
            IJobStorage storage,
            IJobProcessor processor,
            IJobStateManager stateManager,
            NullLogger<SqlJobWorker> logger,
            SqlJobStorageOptions options)
            : base(storage, processor, stateManager, logger, options)
        {
        }

        public Task RunAsync(CancellationToken ct) => ExecuteAsync(ct);
    }
}
