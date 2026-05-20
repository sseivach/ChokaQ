# CHK-04: Expression Trees — Killing Reflection

![Expression tree dispatch](/diagrams/66-expression-tree-dispatch.png)

## The Problem: Reflection is Slow

In a typical job framework using the `IJobHandler<TJob>` pattern, you need to:
1. Determine the handler type from the job type
2. Resolve the handler from DI
3. **Invoke** `HandleAsync(job, ct)` on the handler

Step 3 is the problem. The handler and job types are known only at runtime — the dispatcher works with `object`, not `IChokaQJobHandler<SendEmailJob>`. The naive solution is reflection:

```csharp
// ❌ SLOW — Reflection-based dispatch
var method = handlerType.GetMethod("HandleAsync");
await (Task)method.Invoke(handler, new object[] { job, ct });
```

`MethodInfo.Invoke` is 10–100x slower than a direct method call. For a job engine processing thousands of jobs per second, this overhead is unacceptable.

## The Solution: Compiled Expression Trees

ChokaQ compiles a **strongly-typed delegate** from an Expression Tree **once**, caches it, and reuses it for every subsequent call:

```csharp
// From: ChokaQ.Core/Execution/BusJobDispatcher.cs

private Func<object, IChokaQJob, CancellationToken, Task> CreateDelegate(
    Type handlerType, Type jobType)
{
    // Parameters: (object handler, IChokaQJob job, CancellationToken ct)
    var handlerParam = Expression.Parameter(typeof(object), "handler");
    var jobParam = Expression.Parameter(typeof(IChokaQJob), "job");
    var ctParam = Expression.Parameter(typeof(CancellationToken), "ct");

    // Cast: (IChokaQJobHandler<TJob>)handler
    var castedHandler = Expression.Convert(handlerParam, handlerType);

    // Cast: (TJob)job
    var castedJob = Expression.Convert(jobParam, jobType);

    // Find the method: HandleAsync(TJob, CancellationToken)
    var handleMethod = handlerType.GetMethod("HandleAsync",
        new[] { jobType, typeof(CancellationToken) })!;

    // Build the call: ((THandler)handler).HandleAsync((TJob)job, ct)
    var callExpr = Expression.Call(castedHandler, handleMethod, castedJob, ctParam);

    // Compile into a reusable delegate
    var lambda = Expression.Lambda<Func<object, IChokaQJob, CancellationToken, Task>>(
        callExpr, handlerParam, jobParam, ctParam);

    return lambda.Compile();
}
```

### What the Compiled Delegate Looks Like

The Expression Tree above generates the **equivalent of this C# code**:

```csharp
// What the JIT actually executes (conceptual):
(object handler, IChokaQJob job, CancellationToken ct) =>
    ((IChokaQJobHandler<SendEmailJob>)handler)
        .HandleAsync((SendEmailJob)job, ct);
```

This is a **direct method call** — no `MethodInfo.Invoke`, no boxing (beyond the initial cast), no reflection lookup.

## The Caching Layer

Delegates are cached in a `ConcurrentDictionary` keyed by job type string:

```csharp
private readonly ConcurrentDictionary<string, Func<object, IChokaQJob, CancellationToken, Task>>
    _delegateCache = new();

public async Task DispatchAsync(JobHotEntity job, CancellationToken ct)
{
    // 1. Resolve job type from registry
    var jobType = _registry.GetJobType(job.Type);

    // 2. Get or create the compiled delegate
    var invoker = _delegateCache.GetOrAdd(job.Type,
        _ => CreateDelegate(handlerType, jobType));

    // 3. Resolve handler from DI (scoped)
    var handler = scope.ServiceProvider.GetRequiredService(handlerInterfaceType);

    // 4. Deserialize through the shared ChokaQ serializer contract
    var jobInstance = _serializer.Deserialize(job.Payload, jobType);

    // 5. Invoke — this is now a DIRECT CALL, not reflection
    await invoker(handler, jobInstance, ct);
}
```

**Performance characteristics:**

| Operation | First Call | Every Subsequent Call |
|-----------|-----------|---------------------|
| Find method (`GetMethod`) | ✅ Once | ❌ Skipped |
| Build expression tree | ✅ Once | ❌ Skipped |
| Compile to delegate | ✅ Once (~1ms) | ❌ Skipped |
| Execute delegate | ✅ Direct call | ✅ Direct call |

After the first invocation, every call is **zero-overhead** — just a dictionary lookup + delegate invoke.

## Step-by-Step: Building the Expression Tree

Let's walk through exactly what happens for a `SendEmailJob` + `EmailHandler`:

### Step 1: Define Parameters

```csharp
var handlerParam = Expression.Parameter(typeof(object), "handler");
var jobParam = Expression.Parameter(typeof(IChokaQJob), "job");
var ctParam = Expression.Parameter(typeof(CancellationToken), "ct");
```

These are the lambda's input parameters. We use `object` and `IChokaQJob` as the base types to create a generic delegate signature.

### Step 2: Cast to Concrete Types

```csharp
var castedHandler = Expression.Convert(handlerParam,
    typeof(IChokaQJobHandler<SendEmailJob>));
    // Generates: (IChokaQJobHandler<SendEmailJob>)handler

var castedJob = Expression.Convert(jobParam, typeof(SendEmailJob));
    // Generates: (SendEmailJob)job
```

### Step 3: Build the Method Call

```csharp
var handleMethod = typeof(IChokaQJobHandler<SendEmailJob>)
    .GetMethod("HandleAsync");

var callExpr = Expression.Call(
    castedHandler, handleMethod, castedJob, ctParam);
    // Generates: ((IChokaQJobHandler<SendEmailJob>)handler)
    //                .HandleAsync((SendEmailJob)job, ct)
```

### Step 4: Wrap in Lambda and Compile

```csharp
var lambda = Expression.Lambda<
    Func<object, IChokaQJob, CancellationToken, Task>>(
    callExpr, handlerParam, jobParam, ctParam);

return lambda.Compile();
// Returns: a delegate that can be called like a normal method
```

## Why Not Alternatives?

| Approach | Speed | Complexity | Why Not |
|----------|-------|-----------|---------|
| `MethodInfo.Invoke` | 🐌 Slow | Low | 10–100x overhead per call |
| `DynamicMethod` + IL Emit | 🚀 Fastest | Very High | Writing raw IL is error-prone, hard to maintain |
| **Expression Trees** | 🚀 Near-native | Medium | ✅ **Best balance of speed and readability** |
| Source Generators | 🚀 Zero-runtime | High | Requires compile-time knowledge of all types |

::: tip 💡 Architecture Insight
This pattern is widely used in high-performance .NET libraries. EF Core uses Expression Trees for LINQ-to-SQL translation. ASP.NET Core uses them for model binding and request delegate compilation. AutoMapper uses them for property mapping. ChokaQ applies this same technique specifically to bypass the DI → Handler invocation bottleneck.
:::

## Architecture Decision

ChokaQ uses compiled expression trees because job type and handler type are
known only after the runtime resolves the serialized job type key. The runtime
therefore needs dynamic dispatch, but it should not pay reflection invocation
cost for every job.

Expression trees give a middle ground: the first execution for a job type builds
and compiles a strongly typed delegate; later executions reuse that delegate.
The resulting call path is close to normal generic code while keeping the
runtime flexible enough to dispatch jobs discovered through the registry.

The main trade-off is startup cost for the first job of each type and a small
cache surface. That is acceptable because the cost is paid once per job type,
not once per job execution.

## Interview Questions

**Why is reflection lookup acceptable but `MethodInfo.Invoke` is not?**  
Reflection lookup happens during delegate creation and is cached. `Invoke` would
run for every job and would keep the hot execution path slow.

**Why not use source generators?**  
Source generators can be faster, but they require a more complex compile-time
pipeline and make dynamic registration harder. Expression trees keep the runtime
package simpler while removing the per-job reflection cost.

**What is the failure mode if a handler signature is wrong?**  
Delegate creation fails early for that job type. That is better than silently
accepting a handler that cannot execute the registered payload.

## Who Else Uses This Pattern?

| Framework | What It Compiles |
|-----------|-----------------|
| **EF Core** | LINQ expressions → SQL queries |
| **ASP.NET Core** | Route handlers → request delegates |
| **AutoMapper** | Property mapping rules → copy delegates |
| **MediatR** | Handler resolution → dispatch delegates |
| **ChokaQ** | Job handler invocation → cached delegates |

<br>

> *Next: See how the [Dynamic Concurrency Limiter](/3-deep-dives/dynamic-concurrency-limiter) enables runtime concurrency scaling without restarts.*
