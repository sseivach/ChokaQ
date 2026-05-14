using ChokaQ.Abstractions.Contexts;
using ChokaQ.Abstractions.Idempotency;
using ChokaQ.Abstractions.Jobs;
using ChokaQ.Abstractions.Middleware;
using ChokaQ.Abstractions.Observability;
using ChokaQ.Core.Idempotency;
using ChokaQ.Core.Serialization;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChokaQ.Tests.Unit.Idempotency;

[Trait(TestCategories.Category, TestCategories.Unit)]
public class IdempotencyMiddlewareTests
{
    private readonly InMemoryIdempotencyStore _store = new();
    private readonly IJobContext _context;

    public IdempotencyMiddlewareTests()
    {
        var ctx = Substitute.For<IJobContext>();
        ctx.JobId.Returns("job-123");
        ctx.CancellationToken.Returns(CancellationToken.None);
        _context = ctx;
    }

    private IdempotencyMiddleware CreateMiddleware(IChokaQMetrics? metrics = null) =>
        new(
            _store,
            new SystemTextJsonChokaQJobSerializer(new ChokaQ.Core.ChokaQSerializationOptions()),
            new ChokaQ.Core.ChokaQOptions(),
            NullLogger<IdempotencyMiddleware>.Instance,
            metrics);

    // ── Helper jobs ──────────────────────────────────────────────────────────────

    private class PaymentJob : IChokaQJob, IIdempotentJob
    {
        public string Id { get; set; } = "";
        public string OrderId { get; set; } = "order-42";
        public string IdempotencyKey => $"payment:{OrderId}";
        public TimeSpan? ResultTtl => TimeSpan.FromHours(24);
    }

    private class RegularJob : IChokaQJob
    {
        public string Id { get; set; } = "";
    }

    // ── Tests ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InvokeAsync_ShouldCallNext_WhenJobIsNotIdempotent()
    {
        // Non-idempotent jobs must pass through unchanged
        var middleware = CreateMiddleware();
        var job = new RegularJob();
        int callCount = 0;

        await middleware.InvokeAsync(_context, job, () => { callCount++; return Task.CompletedTask; });

        callCount.Should().Be(1);
    }

    [Fact]
    public async Task InvokeAsync_OnCacheMiss_ShouldCallNext_AndStoreResult()
    {
        // First execution: no cache → handler runs → result stored
        var middleware = CreateMiddleware();
        var job = new PaymentJob();
        int callCount = 0;

        await middleware.InvokeAsync(_context, job, () => { callCount++; return Task.CompletedTask; });

        callCount.Should().Be(1);

        // Result should now be in the cache
        var stored = await _store.TryGetResultAsync(job.IdempotencyKey);
        stored.Should().NotBeNull();
    }

    [Fact]
    public async Task InvokeAsync_OnCacheHit_ShouldNotCallNext()
    {
        // [THE CRITICAL GUARANTEE]:
        // Second call with same key → handler NEVER runs again.
        // This prevents double-charging, double-sending emails, etc.
        var middleware = CreateMiddleware();
        var job = new PaymentJob();
        int callCount = 0;
        JobDelegate next = () => { callCount++; return Task.CompletedTask; };

        // First call (populates cache)
        await middleware.InvokeAsync(_context, job, next);
        callCount.Should().Be(1);

        // Second call (duplicate — should be short-circuited)
        await middleware.InvokeAsync(_context, job, next);
        callCount.Should().Be(1); // Handler NOT invoked again
    }

    [Fact]
    public async Task InvokeAsync_ConcurrentDuplicates_ShouldExecuteHandlerOnce()
    {
        var middleware = CreateMiddleware();
        var job = new PaymentJob();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;

        JobDelegate next = async () =>
        {
            Interlocked.Increment(ref callCount);
            entered.SetResult();
            await release.Task;
        };

        var first = middleware.InvokeAsync(_context, job, next);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = middleware.InvokeAsync(_context, job, next);
        await second;

        callCount.Should().Be(1);
        release.SetResult();
        await first;
    }

    [Fact]
    public async Task InvokeAsync_WhenHandlerFails_ShouldReleaseClaim()
    {
        var middleware = CreateMiddleware();
        var job = new PaymentJob();
        var attempts = 0;

        await middleware.Invoking(m => m.InvokeAsync(_context, job, () =>
            {
                attempts++;
                throw new InvalidOperationException("boom");
            }))
            .Should().ThrowAsync<InvalidOperationException>();

        await middleware.InvokeAsync(_context, job, () =>
        {
            attempts++;
            return Task.CompletedTask;
        });

        attempts.Should().Be(2);
    }

    [Fact]
    public async Task InvokeAsync_ShouldRecordIdempotencyOutcomes()
    {
        var metrics = Substitute.For<IChokaQMetrics>();
        var middleware = CreateMiddleware(metrics);
        var job = new PaymentJob();

        await middleware.InvokeAsync(_context, job, () => Task.CompletedTask);
        await middleware.InvokeAsync(_context, job, () => Task.CompletedTask);

        var failingJob = new PaymentJob { OrderId = "order-fails" };
        await middleware.Invoking(m => m.InvokeAsync(_context, failingJob, () =>
            {
                throw new InvalidOperationException("boom");
            }))
            .Should().ThrowAsync<InvalidOperationException>();

        metrics.Received().RecordIdempotencyOutcome("claimed");
        metrics.Received().RecordIdempotencyOutcome("completed");
        metrics.Received().RecordIdempotencyOutcome("completed_duplicate");
        metrics.Received().RecordIdempotencyOutcome("released");
    }

    [Fact]
    public async Task InvokeAsync_ShouldPassExecutionCancellationToken_ToClaimStore()
    {
        var ctx = Substitute.For<IJobContext>();
        using var cts = new CancellationTokenSource();
        ctx.JobId.Returns("job-cancel");
        ctx.CancellationToken.Returns(cts.Token);
        cts.Cancel();

        var middleware = CreateMiddleware();
        var job = new PaymentJob();
        var callCount = 0;

        await middleware.Invoking(m => m.InvokeAsync(ctx, job, () =>
            {
                callCount++;
                return Task.CompletedTask;
            }))
            .Should().ThrowAsync<OperationCanceledException>();

        callCount.Should().Be(0);
    }

    [Fact]
    public async Task InMemoryIdempotencyStore_ShouldAllowClaimAfterInProgressLeaseExpires()
    {
        var store = new InMemoryIdempotencyStore();

        var first = await store.TryBeginAsync("payment:42", "job-1", TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        var second = await store.TryBeginAsync("payment:42", "job-2", TimeSpan.FromMinutes(1));

        first.Status.Should().Be(IdempotencyBeginStatus.Claimed);
        second.Status.Should().Be(IdempotencyBeginStatus.Claimed);
    }

    [Fact]
    public async Task InMemoryIdempotencyStore_ShouldCleanupExpiredEntries_WhenDifferentKeysAreUsed()
    {
        var store = new InMemoryIdempotencyStore();
        await store.StoreResultAsync("expired-1", "{}", ttl: TimeSpan.FromMilliseconds(1));
        await store.StoreResultAsync("expired-2", "{}", ttl: TimeSpan.FromMilliseconds(1));

        await Task.Delay(50);

        for (var i = 0; i < 64; i++)
        {
            await store.TryBeginAsync($"active-{i}", $"job-{i}", TimeSpan.FromMinutes(1));
        }

        store.Count.Should().Be(64);
    }

    [Fact]
    public async Task InvokeAsync_DifferentKeys_ShouldExecuteIndependently()
    {
        // Each unique key is independent — different orders don't interfere
        var middleware = CreateMiddleware();
        var job1 = new PaymentJob { OrderId = "order-1" };
        var job2 = new PaymentJob { OrderId = "order-2" };
        int callCount = 0;

        await middleware.InvokeAsync(_context, job1, () => { callCount++; return Task.CompletedTask; });
        await middleware.InvokeAsync(_context, job2, () => { callCount++; return Task.CompletedTask; });

        callCount.Should().Be(2); // Both orders processed independently
    }

    [Fact]
    public async Task InMemoryIdempotencyStore_ShouldExpireEntries_AfterTtl()
    {
        // Entries with TTL should expire and allow re-execution
        var store = new InMemoryIdempotencyStore();
        await store.StoreResultAsync("key-1", "{}", ttl: TimeSpan.FromMilliseconds(1));

        await Task.Delay(50); // Wait for TTL to expire

        var result = await store.TryGetResultAsync("key-1");
        result.Should().BeNull(); // Expired → cache miss
    }

    [Fact]
    public async Task InMemoryIdempotencyStore_ShouldRetainEntries_WithinTtl()
    {
        // Entries should be retrievable within their TTL window
        var store = new InMemoryIdempotencyStore();
        await store.StoreResultAsync("key-1", "{\"status\":\"ok\"}", ttl: TimeSpan.FromMinutes(5));

        var result = await store.TryGetResultAsync("key-1");
        result.Should().NotBeNull();
        result.Should().Contain("ok");
    }
}
