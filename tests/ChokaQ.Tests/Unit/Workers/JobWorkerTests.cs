using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Jobs;
using ChokaQ.Abstractions.Notifications;
using ChokaQ.Abstractions.Observability;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Core.Defaults;
using ChokaQ.Core.Execution;
using ChokaQ.Core.Processing;
using ChokaQ.Core.State;
using ChokaQ.Core.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChokaQ.Tests.Unit.Workers;

[Trait(TestCategories.Category, TestCategories.Unit)]
public class JobWorkerTests
{
    [Fact]
    public async Task WorkerLoop_ShouldProcessInMemoryJob_WithRegisteredJobKey()
    {
        var registry = new JobTypeRegistry();
        registry.Register("registered_test_job", typeof(TestJob));

        var storage = Substitute.For<IJobStorage>();
        var processor = Substitute.For<IJobProcessor>();
        var queue = CreateQueue(storage, registry);
        var worker = CreateWorker(queue, storage, processor, registry);

        var job = new TestJob { Id = "job-1", Message = "hello" };
        var processingObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        storage.GetJobAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<JobHotEntity?>(CreateHotJob(job.Id, "registered_test_job")));
        storage.GetQueuesAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IEnumerable<QueueEntity>>(new[] { CreateQueueEntity() }));

        processor.ProcessJobAsync(
                Arg.Is(job.Id),
                Arg.Is("registered_test_job"),
                Arg.Is<string>(payload => payload.Contains("hello")),
                Arg.Any<string>(),
                Arg.Is(1),
                Arg.Any<string?>(),
                Arg.Any<DateTime?>(),
                Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                processingObserved.SetResult();
                return Task.CompletedTask;
            });

        try
        {
            worker.UpdateWorkerCount(1);
            await queue.RequeueAsync(job);

            await processingObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            worker.UpdateWorkerCount(0);
            worker.Dispose();
        }

        // In-memory Bus mode must dispatch with the same registered key that enqueue persisted.
        // Otherwise a job using a public profile key works in SQL mode but fails in memory mode.
        await processor.Received(1).ProcessJobAsync(
            Arg.Is(job.Id),
            Arg.Is("registered_test_job"),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Is(1),
            Arg.Any<string?>(),
            Arg.Any<DateTime?>(),
            Arg.Any<DateTime>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestartJobAsync_ShouldRequeueDlqJob_UsingRegisteredJobKey()
    {
        var registry = new JobTypeRegistry();
        registry.Register("registered_test_job", typeof(TestJob));

        var storage = Substitute.For<IJobStorage>();
        var processor = Substitute.For<IJobProcessor>();
        var queue = CreateQueue(storage, registry);
        var worker = CreateWorker(queue, storage, processor, registry);

        var hotLookupCount = 0;
        storage.GetJobAsync("job-1", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var call = Interlocked.Increment(ref hotLookupCount);
                return new ValueTask<JobHotEntity?>(
                    call == 1
                        ? null
                        : CreateHotJob("job-1", "registered_test_job", """{"Id":"job-1","Message":"from-dlq"}"""));
            });

        storage.GetDLQJobAsync("job-1", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<JobDLQEntity?>(new JobDLQEntity(
                Id: "job-1",
                Queue: "default",
                Type: "registered_test_job",
                Payload: """{"Id":"job-1","Message":"from-dlq"}""",
                Tags: null,
                FailureReason: FailureReason.MaxRetriesExceeded,
                ErrorDetails: "failed",
                AttemptCount: 1,
                WorkerId: null,
                CreatedBy: null,
                LastModifiedBy: null,
                CreatedAtUtc: DateTime.UtcNow,
                FailedAtUtc: DateTime.UtcNow)));

        await worker.RestartJobAsync("job-1");

        var requeued = await queue.Reader.ReadAsync();
        requeued.Should().BeOfType<TestJob>();
        ((TestJob)requeued).Message.Should().Be("from-dlq");

        // DLQ rows store the dispatch key. Registry-first type resolution keeps resurrection
        // independent from CLR naming and prevents custom profile keys from becoming unrequeueable.
        await storage.Received(1).ResurrectAsync("job-1", null, "Admin restart");
    }

    private static InMemoryQueue CreateQueue(IJobStorage storage, JobTypeRegistry registry) =>
        new(
            storage,
            Substitute.For<IChokaQNotifier>(),
            registry,
            Substitute.For<IChokaQMetrics>(),
            NullLogger<InMemoryQueue>.Instance);

    private static JobWorker CreateWorker(
        InMemoryQueue queue,
        IJobStorage storage,
        IJobProcessor processor,
        JobTypeRegistry registry) =>
        new(
            queue,
            storage,
            NullLogger<JobWorker>.Instance,
            Substitute.For<IJobStateManager>(),
            processor,
            registry);

    private static QueueEntity CreateQueueEntity() =>
        new(
            Name: "default",
            IsPaused: false,
            IsActive: true,
            ZombieTimeoutSeconds: null,
            MaxWorkers: null,
            LastUpdatedUtc: DateTime.UtcNow);

    private static JobHotEntity CreateHotJob(
        string id,
        string type,
        string payload = """{"Id":"job-1","Message":"hello"}""") =>
        new(
            Id: id,
            Queue: "default",
            Type: type,
            Payload: payload,
            Tags: null,
            IdempotencyKey: null,
            Priority: 10,
            Status: JobStatus.Fetched,
            AttemptCount: 1,
            WorkerId: "worker-1",
            HeartbeatUtc: null,
            ScheduledAtUtc: null,
            CreatedAtUtc: DateTime.UtcNow,
            StartedAtUtc: null,
            LastUpdatedUtc: DateTime.UtcNow,
            CreatedBy: null,
            LastModifiedBy: null);

    public sealed class TestJob : IChokaQJob
    {
        public string Id { get; set; } = "";
        public string Message { get; set; } = "";
    }
}
