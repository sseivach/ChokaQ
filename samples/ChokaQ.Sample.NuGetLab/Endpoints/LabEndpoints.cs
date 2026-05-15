using System.Globalization;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Abstractions.Workers;
using ChokaQ.Sample.NuGetLab.Jobs;

namespace ChokaQ.Sample.NuGetLab.Endpoints;

internal static class LabEndpoints
{
    public static IEndpointRouteBuilder MapLabEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/lab/snapshot", GetSnapshotAsync);
        app.MapPost("/api/lab/scenarios/mix", EnqueueMixedLoadAsync);
        app.MapPost("/api/lab/scenarios/idempotency", EnqueueIdempotencyProbeAsync);
        app.MapPost("/api/lab/scenarios/failures", EnqueueFailureMatrixAsync);
        app.MapPost("/api/lab/scenarios/delayed", EnqueueDelayedJobsAsync);
        app.MapPost("/api/lab/queues/{name}/pause", PauseQueueAsync);
        app.MapPost("/api/lab/queues/{name}/resume", ResumeQueueAsync);
        app.MapPost("/api/lab/queues/{name}/max-workers/{count:int}", SetQueueMaxWorkersAsync);
        app.MapPost("/api/lab/workers/{count:int}", SetWorkerCount);

        return app;
    }

    private static async Task<IResult> GetSnapshotAsync(
        IJobStorage storage,
        IWorkerManager workers,
        CancellationToken ct)
    {
        var summary = await storage.GetSummaryStatsAsync(ct);
        var queues = await storage.GetQueuesAsync(ct);
        var health = await storage.GetSystemHealthAsync(ct);

        return Results.Ok(new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            workers = new
            {
                workers.ActiveWorkers,
                workers.TotalWorkers,
                workers.IsRunning,
                workers.LastHeartbeatUtc,
                workers.MaxRetries,
                workers.RetryDelaySeconds
            },
            summary,
            queues = queues.OrderBy(q => q.Name),
            health
        });
    }

    private static async Task<IResult> EnqueueMixedLoadAsync(
        HttpRequest request,
        IChokaQQueue queue,
        CancellationToken ct)
    {
        var count = ReadBoundedInt(request, "count", 25, 1, 250);

        for (var i = 0; i < count; i++)
        {
            await EnqueueMixedScenarioJobAsync(queue, i, ct);
        }

        return Results.Ok(new { scenario = "mixed-load", requested = count, enqueued = count });
    }

    private static async Task EnqueueMixedScenarioJobAsync(IChokaQQueue queue, int index, CancellationToken ct)
    {
        TimeSpan? delay = index % 10 == 9 ? TimeSpan.FromSeconds(15) : null;

        switch (index % 6)
        {
            case 0:
                await queue.EnqueueAsync(
                    new ReceiptEmailJob($"user-{index}@example.test", $"receipt-{DateTime.UtcNow:HHmmss}-{index}"),
                    priority: 80,
                    queue: "critical",
                    createdBy: "NuGetLab",
                    tags: "lab,mix,email",
                    ct: ct,
                    delay: delay);
                break;
            case 1:
                await queue.EnqueueAsync(
                    new ReportRenderJob($"tenant-{index % 4}", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-index))),
                    priority: 20,
                    queue: "reports",
                    createdBy: "NuGetLab",
                    tags: "lab,mix,report",
                    ct: ct,
                    delay: delay);
                break;
            case 2:
                await queue.EnqueueAsync(
                    new WebhookDeliveryJob($"https://partner.example.test/hook/{index}", $$"""{"source":"lab","index":{{index}}}"""),
                    priority: 45,
                    queue: "unstable",
                    createdBy: "NuGetLab",
                    tags: "lab,mix,webhook",
                    ct: ct,
                    delay: delay);
                break;
            case 3:
                await queue.EnqueueAsync(
                    new PaymentCaptureJob($"order-{index % 5}", 25 + index),
                    priority: 90,
                    queue: "critical",
                    createdBy: "NuGetLab",
                    tags: "lab,mix,payment",
                    ct: ct,
                    delay: delay);
                break;
            case 4:
                await queue.EnqueueAsync(
                    new SlowJob(3, "normal slow path"),
                    priority: 15,
                    queue: "reports",
                    createdBy: "NuGetLab",
                    tags: "lab,mix,slow",
                    ct: ct,
                    delay: delay);
                break;
            default:
                await queue.EnqueueAsync(
                    new ThrottledPartnerJob($"partner-{index % 3}", SucceedAfterAttempts: 2),
                    priority: 30,
                    queue: "unstable",
                    createdBy: "NuGetLab",
                    tags: "lab,mix,throttle",
                    ct: ct,
                    delay: delay);
                break;
        }
    }

    private static async Task<IResult> EnqueueIdempotencyProbeAsync(IChokaQQueue queue, CancellationToken ct)
    {
        var orderId = $"duplicate-order-{DateTimeOffset.UtcNow:HHmmss}";
        var job = new PaymentCaptureJob(orderId, 199.95m);

        for (var i = 0; i < 5; i++)
        {
            await queue.EnqueueAsync(
                job with { Id = Guid.NewGuid().ToString() },
                priority: 95,
                queue: "critical",
                createdBy: "NuGetLab",
                tags: "lab,idempotency,payment",
                ct: ct);
        }

        return Results.Ok(new
        {
            scenario = "idempotency",
            requested = 5,
            logicalOperation = job.IdempotencyKey,
            expected = "one active SQL Hot row for this operation"
        });
    }

    private static async Task<IResult> EnqueueFailureMatrixAsync(IChokaQQueue queue, CancellationToken ct)
    {
        await queue.EnqueueAsync(
            new PoisonPillJob("schema-version", "missing required field"),
            priority: 100,
            queue: "critical",
            createdBy: "NuGetLab",
            tags: "lab,fatal,dlq",
            ct: ct);

        await queue.EnqueueAsync(
            new WebhookDeliveryJob("retry://partner/outage", """{"event":"retry-me"}"""),
            priority: 60,
            queue: "unstable",
            createdBy: "NuGetLab",
            tags: "lab,retry,transient",
            ct: ct);

        await queue.EnqueueAsync(
            new ThrottledPartnerJob("rate-limited-partner", SucceedAfterAttempts: 4),
            priority: 50,
            queue: "unstable",
            createdBy: "NuGetLab",
            tags: "lab,retry-after,throttle",
            ct: ct);

        await queue.EnqueueAsync(
            new SlowJob(20, "intentional timeout"),
            priority: 10,
            queue: "unstable",
            createdBy: "NuGetLab",
            tags: "lab,timeout",
            ct: ct);

        return Results.Ok(new
        {
            scenario = "failure-matrix",
            enqueued = 4,
            expected = "fatal DLQ, transient retry, retry-after throttling, and timeout"
        });
    }

    private static async Task<IResult> EnqueueDelayedJobsAsync(IChokaQQueue queue, CancellationToken ct)
    {
        await queue.EnqueueAsync(
            new ReceiptEmailJob("delayed@example.test", "delayed receipt"),
            priority: 75,
            queue: "critical",
            createdBy: "NuGetLab",
            tags: "lab,delay,email",
            ct: ct,
            delay: TimeSpan.FromSeconds(30));

        await queue.EnqueueAsync(
            new ReportRenderJob("tenant-delayed", DateOnly.FromDateTime(DateTime.UtcNow.Date)),
            priority: 25,
            queue: "reports",
            createdBy: "NuGetLab",
            tags: "lab,delay,report",
            ct: ct,
            delay: TimeSpan.FromSeconds(45));

        return Results.Ok(new { scenario = "delayed", enqueued = 2 });
    }

    private static async Task<IResult> PauseQueueAsync(string name, IJobStorage storage, CancellationToken ct)
    {
        await storage.SetQueuePausedAsync(name, isPaused: true, ct);
        return Results.Ok(new { queue = name, isPaused = true });
    }

    private static async Task<IResult> ResumeQueueAsync(string name, IJobStorage storage, CancellationToken ct)
    {
        await storage.SetQueuePausedAsync(name, isPaused: false, ct);
        return Results.Ok(new { queue = name, isPaused = false });
    }

    private static async Task<IResult> SetQueueMaxWorkersAsync(
        string name,
        int count,
        IJobStorage storage,
        CancellationToken ct)
    {
        int? maxWorkers = count <= 0 ? null : count;
        await storage.SetQueueMaxWorkersAsync(name, maxWorkers, ct);
        return Results.Ok(new { queue = name, maxWorkers });
    }

    private static IResult SetWorkerCount(int count, IWorkerManager workers)
    {
        var boundedCount = Math.Clamp(count, 1, 32);
        workers.UpdateWorkerCount(boundedCount);
        return Results.Ok(new { totalWorkers = boundedCount });
    }

    private static int ReadBoundedInt(HttpRequest request, string key, int fallback, int min, int max)
    {
        var raw = request.Query[key].ToString();
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            value = fallback;
        }

        return Math.Clamp(value, min, max);
    }
}
