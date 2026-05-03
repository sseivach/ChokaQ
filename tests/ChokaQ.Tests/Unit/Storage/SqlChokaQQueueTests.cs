using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Idempotency;
using ChokaQ.Abstractions.Jobs;
using ChokaQ.Abstractions.Notifications;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Core.Execution;
using ChokaQ.Storage.SqlServer;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChokaQ.Tests.Unit.Storage;

public class SqlChokaQQueueTests
{
    private readonly IJobStorage _storage;
    private readonly IChokaQNotifier _notifier;
    private readonly JobTypeRegistry _registry;
    private readonly SqlChokaQQueue _queue;

    public SqlChokaQQueueTests()
    {
        _storage = Substitute.For<IJobStorage>();
        _notifier = Substitute.For<IChokaQNotifier>();
        _registry = new JobTypeRegistry();
        _queue = new SqlChokaQQueue(_storage, _notifier, _registry, NullLogger<SqlChokaQQueue>.Instance);

        _storage.EnqueueAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<string>((string)call[0]!));
    }

    [Fact]
    public async Task EnqueueAsync_ShouldPersistToSqlStorage()
    {
        _registry.Register("test_job", typeof(TestJob));
        var job = new TestJob { Id = "job1", Message = "Hello SQL" };

        await _queue.EnqueueAsync(job, 5, "critical", "user1", "tag1", CancellationToken.None);

        await _storage.Received(1).EnqueueAsync(
            "job1",
            "critical",
            "test_job",
            Arg.Is<string>(s => s.Contains("Hello SQL")),
            5,
            "user1",
            "tag1",
            Arg.Any<TimeSpan?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnqueueAsync_ShouldPassIIdempotentJobKeyToStorage()
    {
        var job = new IdempotentTestJob
        {
            Id = "job1",
            Message = "Hello SQL",
            BusinessKey = "invoice:42"
        };

        await _queue.EnqueueAsync(job);

        await _storage.Received(1).EnqueueAsync(
            "job1",
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<TimeSpan?>(),
            "invoice:42",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnqueueAsync_ExplicitIdempotencyKey_ShouldOverrideJobKey()
    {
        var job = new IdempotentTestJob
        {
            Id = "job1",
            BusinessKey = "invoice:from-job"
        };

        await _queue.EnqueueAsync(job, idempotencyKey: "invoice:from-call");

        await _storage.Received(1).EnqueueAsync(
            "job1",
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<TimeSpan?>(),
            "invoice:from-call",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnqueueAsync_ShouldPassDelayToStorage()
    {
        var job = new TestJob { Id = "job1" };
        var delay = TimeSpan.FromMinutes(5);

        await _queue.EnqueueAsync(job, delay: delay);

        await _storage.Received(1).EnqueueAsync(
            "job1",
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            delay,
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnqueueAsync_WhenIdempotencyReturnsExistingJob_ShouldSkipNewJobNotification()
    {
        _storage.EnqueueAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string>("existing-job"));

        var job = new IdempotentTestJob { Id = "new-job", BusinessKey = "invoice:42" };

        await _queue.EnqueueAsync(job);

        await _notifier.DidNotReceive().NotifyJobUpdatedAsync(Arg.Any<JobUpdateDto>());
    }

    [Fact]
    public async Task EnqueueAsync_ShouldNotifyDashboardAfterDurableWrite()
    {
        _registry.Register("test_job", typeof(TestJob));
        var job = new TestJob { Id = "job1", Message = "Hello SQL" };

        await _queue.EnqueueAsync(job, 5, "critical", "user1", null, CancellationToken.None);

        await _notifier.Received(1).NotifyJobUpdatedAsync(Arg.Is<JobUpdateDto>(dto =>
            dto.JobId == "job1" &&
            dto.Type == "test_job" &&
            dto.Queue == "critical" &&
            dto.Status == JobStatus.Pending &&
            dto.Priority == 5 &&
            dto.CreatedBy == "user1"));
    }

    [Fact]
    public async Task EnqueueAsync_ShouldNotFailCommittedJob_WhenNotificationFails()
    {
        _notifier.NotifyJobUpdatedAsync(Arg.Any<JobUpdateDto>())
            .Returns(Task.FromException(new Exception("SignalR unavailable")));
        var job = new TestJob { Id = "job1", Message = "Still durable" };

        await _queue.EnqueueAsync(job);

        await _storage.Received(1).EnqueueAsync(
            "job1",
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    public class TestJob : IChokaQJob
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Message { get; set; } = "";
    }

    public class IdempotentTestJob : IChokaQJob, IIdempotentJob
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Message { get; set; } = "";
        public string BusinessKey { get; set; } = "";
        public string IdempotencyKey => BusinessKey;
        public TimeSpan? ResultTtl => TimeSpan.FromHours(1);
    }
}
