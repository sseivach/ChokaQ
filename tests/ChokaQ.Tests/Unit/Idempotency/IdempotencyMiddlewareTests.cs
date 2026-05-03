using ChokaQ.Abstractions.Contexts;
using ChokaQ.Abstractions.Idempotency;
using ChokaQ.Abstractions.Jobs;
using ChokaQ.Abstractions.Middleware;
using ChokaQ.Core.Idempotency;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChokaQ.Tests.Unit.Idempotency;

[Trait(TestCategories.Category, TestCategories.Unit)]
public class IdempotencyMiddlewareTests
{
    private readonly IIdempotencyStore _store = new InMemoryIdempotencyStore();
    private readonly IJobContext _context;

    public IdempotencyMiddlewareTests()
    {
        var ctx = Substitute.For<IJobContext>();
        ctx.JobId.Returns("job-123");
        _context = ctx;
    }

    private IdempotencyMiddleware CreateMiddleware() =>
        new(_store, NullLogger<IdempotencyMiddleware>.Instance);

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
