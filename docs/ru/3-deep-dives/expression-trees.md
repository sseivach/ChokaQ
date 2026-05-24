# CHK-04: Expression Trees For Dispatch

![Expression tree dispatch](/diagrams/66-expression-tree-dispatch.png)

## Проблема: runtime type dispatch

Когда runtime использует pattern `IJobHandler<TJob>`, ему нужно:

1. Определить handler type по job type.
2. Resolve handler из DI.
3. **Invoke** `HandleAsync(job, ct)` на handler'е.

Шаг 3 требует аккуратности. Handler и job types известны только во время runtime, поэтому dispatcher работает с `object`, а не с `IChokaQJobHandler<SendEmailJob>`. Direct reflection invocation реализовать просто:

```csharp
// Reflection-based dispatch
var method = handlerType.GetMethod("HandleAsync");
await (Task)method.Invoke(handler, new object[] { job, ct });
```

`MethodInfo.Invoke` добавляет overhead на каждом execution. Для job engine handler dispatch находится на hot path, поэтому ChokaQ платит dynamic-binding cost один раз и затем переиспользует compiled delegate.

## Решение: compiled expression trees

ChokaQ компилирует **strongly-typed delegate** из Expression Tree **один раз**, cache'ит его и переиспользует для каждого следующего call:

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

### Как выглядит compiled delegate

Expression Tree выше генерирует **эквивалент такого C# code**:

```csharp
// What the JIT actually executes (conceptual):
(object handler, IChokaQJob job, CancellationToken ct) =>
    ((IChokaQJobHandler<SendEmailJob>)handler)
        .HandleAsync((SendEmailJob)job, ct);
```

Этот call path избегает `MethodInfo.Invoke` на repeated executions и выносит dynamic work за пределы per-job hot path.

## Caching layer

Delegates cache'ятся в `ConcurrentDictionary` с ключом по job type string:

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

После первого invocation каждый call использует cached delegate вместо rebuild dispatch path.

## Step-by-step: building the expression tree

Разберем, что происходит для `SendEmailJob` + `EmailHandler`:

### Step 1: Define parameters

```csharp
var handlerParam = Expression.Parameter(typeof(object), "handler");
var jobParam = Expression.Parameter(typeof(IChokaQJob), "job");
var ctParam = Expression.Parameter(typeof(CancellationToken), "ct");
```

Это input parameters lambda. `object` и `IChokaQJob` используются как base types, чтобы создать generic delegate signature.

### Step 2: Cast to concrete types

```csharp
var castedHandler = Expression.Convert(handlerParam,
    typeof(IChokaQJobHandler<SendEmailJob>));
    // Generates: (IChokaQJobHandler<SendEmailJob>)handler

var castedJob = Expression.Convert(jobParam, typeof(SendEmailJob));
    // Generates: (SendEmailJob)job
```

### Step 3: Build the method call

```csharp
var handleMethod = typeof(IChokaQJobHandler<SendEmailJob>)
    .GetMethod("HandleAsync");

var callExpr = Expression.Call(
    castedHandler, handleMethod, castedJob, ctParam);
    // Generates: ((IChokaQJobHandler<SendEmailJob>)handler)
    //                .HandleAsync((SendEmailJob)job, ct)
```

### Step 4: Wrap in lambda and compile

```csharp
var lambda = Expression.Lambda<
    Func<object, IChokaQJob, CancellationToken, Task>>(
    callExpr, handlerParam, jobParam, ctParam);

return lambda.Compile();
// Returns: a delegate that can be called like a normal method
```

## Alternatives

| Approach | Speed | Complexity | Trade-off |
|----------|-------|-----------|---------|
| `MethodInfo.Invoke` | Slower | Low | Просто, но платит invocation overhead на каждом job. |
| `DynamicMethod` + IL Emit | Very fast | Very High | Мощно, но сложнее maintain и review. |
| **Expression Trees** | Near direct-call | Medium | Хороший баланс speed, clarity и runtime flexibility. |
| Source Generators | Compile-time dispatch | High | Сильный вариант, когда все job types известны на compile time. |

::: tip 💡 Архитектурная деталь
Этот pattern широко используется в high-performance .NET libraries. EF Core использует Expression Trees для LINQ-to-SQL translation. ASP.NET Core использует их для model binding и request delegate compilation. AutoMapper использует их для property mapping. ChokaQ применяет ту же технику конкретно для обхода bottleneck DI -> Handler invocation.
:::

## Архитектурное решение

ChokaQ использует compiled expression trees, потому что job type и handler type становятся известны только после runtime resolution serialized job type key. Runtime поэтому нуждается в dynamic dispatch, но не должен платить reflection invocation cost за каждый job.

Expression trees дают middle ground: первый execution для job type строит и компилирует strongly typed delegate; следующие executions переиспользуют этот delegate. Получившийся call path близок к normal generic code, но runtime остается достаточно flexible для dispatch jobs, discovered через registry.

Главный trade-off - startup cost для первого job каждого type и небольшой cache surface. Это приемлемо, потому что cost платится один раз на job type, а не на каждое job execution.

## Дополнительные вопросы

**Почему reflection lookup приемлем, а `MethodInfo.Invoke` нет?**  
Reflection lookup происходит во время delegate creation и cache'ится. `Invoke` выполнялся бы для каждого job и оставлял бы hot execution path медленным.

**Почему не source generators?**  
Source generators могут быть быстрее, но требуют более сложного compile-time pipeline и затрудняют dynamic registration. Expression trees держат runtime package проще и убирают per-job reflection cost.

**Какой failure mode при неверной handler signature?**  
Delegate creation падает рано для этого job type. Это дает explicit startup или first-use failure вместо acceptance handler'а, который не может выполнить registered payload.

## Кто еще использует этот pattern?

| Framework | What It Compiles |
|-----------|-----------------|
| **EF Core** | LINQ expressions -> SQL queries |
| **ASP.NET Core** | Route handlers -> request delegates |
| **AutoMapper** | Property mapping rules -> copy delegates |
| **MediatR** | Handler resolution -> dispatch delegates |
| **ChokaQ** | Job handler invocation -> cached delegates |

<br>

> *Дальше: [Dynamic Concurrency Limiter](/ru/3-deep-dives/dynamic-concurrency-limiter) показывает runtime concurrency scaling без restarts.*
