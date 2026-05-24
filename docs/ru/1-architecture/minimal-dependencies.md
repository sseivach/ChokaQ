# Minimal Dependency Philosophy

ChokaQ использует философию минимальных зависимостей, но не делает
misleading-claim, будто SQL Server mode может обойтись без официального SQL
Server driver. Практическое правило строже и полезнее: ChokaQ не должен
заставлять host applications брать framework-level dependencies вроде общего
ORM, SQL mapper, resilience policy builder, mediator или custom
telemetry/exporter stack только ради background jobs.

Core остается на Microsoft.Extensions abstractions. SQL Server storage
использует `Microsoft.Data.SqlClient`, официальный ADO.NET driver. Все остальное
принадлежит ChokaQ, чтобы behavior был explicit и inspectable.

## Dependency cost

Каждый NuGet package, добавленный в runtime component, несет engineering cost:

| Cost | Example |
|------|---------|
| **Version Conflicts** | Разным apps в одном solution могут требоваться разные package versions |
| **Transitive Dependencies** | Маленькая direct dependency может принести дополнительные runtime packages через свой graph |
| **Security Surface** | Каждый transitive package становится частью audited runtime surface |
| **Breaking Changes** | Minor version bump в transitive dependency может сломать CI или host integration |
| **License Compliance** | Enterprise legal teams должны audit каждую transitive dependency |

## Dependency tree ChokaQ

```text
ChokaQ.Abstractions
└── (zero references)

ChokaQ.Core
├── ChokaQ.Abstractions
├── Microsoft.Extensions.Hosting.Abstractions
├── Microsoft.Extensions.DependencyInjection.Abstractions
└── Microsoft.Extensions.Logging.Abstractions

ChokaQ.Storage.SqlServer
├── ChokaQ.Core
└── Microsoft.Data.SqlClient     ← SQL Server provider dependency
```

Все остальное built directly in ChokaQ.

## Чем владеет ChokaQ

### Small SQL Mapper

**172 строки** extension methods на `SqlConnection`:

```csharp
// ChokaQ's small SQL mapper API.
public static async Task<IEnumerable<T>> QueryAsync<T>(
    this SqlConnection conn,
    string sql,
    object? param = null,
    CancellationToken ct = default) where T : new()
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    ParameterBuilder.AddParameters(cmd, param);

    using var reader = await cmd.ExecuteReaderAsync(ct);
    return TypeMapper.MapAll<T>(reader);
}
```

**Зачем владеть этим layer?**

- ChokaQ нужны только четыре SQL access patterns: `QueryAsync<T>`,
  `QueryFirstOrDefaultAsync<T>`, `ExecuteAsync` и `ExecuteScalarAsync`.
- Internal layer не добавляет general-purpose mapper в каждое host application,
  которое устанавливает ChokaQ.
- Mapper остается достаточно узким, чтобы SQL behavior было легко inspect во
  время incident review.

### Small TypeMapper

Мапит columns из `IDataReader` в object properties:

```csharp
public static T MapSingle<T>(IDataReader reader) where T : new()
{
    var obj = new T();
    var props = typeof(T).GetProperties();

    for (int i = 0; i < reader.FieldCount; i++)
    {
        var columnName = reader.GetName(i);
        var prop = props.FirstOrDefault(p =>
            p.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));

        if (prop != null && !reader.IsDBNull(i))
        {
            prop.SetValue(obj, Convert.ChangeType(reader[i], prop.PropertyType));
        }
    }
    return obj;
}
```

### ParameterBuilder

Преобразует anonymous objects в `SqlParameter[]`, включая automatic array
expansion для `IN` clauses:

```csharp
// Application code:
var jobs = await conn.QueryAsync<JobHotEntity>(
    "SELECT * FROM Jobs WHERE Id IN (@Ids)",
    new { Ids = new[] { "job-1", "job-2", "job-3" } }
);

// ParameterBuilder generates:
// SELECT * FROM Jobs WHERE Id IN (@Ids_0, @Ids_1, @Ids_2)
// Parameters: @Ids_0="job-1", @Ids_1="job-2", @Ids_2="job-3"
```

Это держит common small-list path explicit и parameterized. Если future workload
потребует очень больших sets, это можно обработать отдельной SQL shape, а не
утяжелять default path.

### SQL Retry Policy

Transient fault handling, настроенный под SQL Server:

```csharp
public static async Task<T> ExecuteAsync<T>(Func<Task<T>> action, int maxRetries = 3)
{
    for (int attempt = 0; ; attempt++)
    {
        try
        {
            return await action();
        }
        catch (SqlException ex) when (IsTransient(ex) && attempt < maxRetries)
        {
            var delay = CalculateBackoff(attempt);
            await Task.Delay(delay);
        }
    }
}

private static bool IsTransient(SqlException ex)
{
    // SQL Server transient error numbers:
    // -2: Timeout, 1205: Deadlock, 40613: Azure SQL DB unavailable
    return ex.Number is -2 or 1205 or 40613 or 40197 or 40501 or 49918;
}
```

**Зачем владеть этим layer?**

- ChokaQ storage нужна одна узкая policy: retry SQL Server transient faults с backoff.
- Retry rules привязаны к SQL error numbers и storage commands, а не к general HTTP или application resilience policy.
- Host applications остаются свободны использовать собственные resilience package versions без policy-builder dependency, навязанной ChokaQ.

### InMemoryCircuitBreaker

Thread-safe circuit breaker с lock-free reads на hot path:

```csharp
public bool IsExecutionPermitted(string jobType)
{
    var entry = GetEntry(jobType);
    // FAST PATH: Lock-free volatile read
    if (entry.Status == CircuitStatus.Closed) return true;

    if (entry.Status == CircuitStatus.Open)
    {
        if (now >= entry.LastFailureUtc.AddSeconds(BreakDurationSeconds))
        {
            if (entry.TryTransitionToHalfOpen()) return true;
        }
        return false;
    }
    return entry.TryAcquireHalfOpenPermit(now);
}
```

Три состояния на job type:

| State | Behavior | Transition |
|-------|----------|-----------|
| **Closed** | All executions allowed | -> Open после 5 failures |
| **Open** | All executions blocked | -> Half-Open после 30s timeout |
| **Half-Open** | One test execution | -> Closed при success или -> Open при failure |

::: tip Performance note
Поле Status помечено `volatile`, потому что `IsExecutionPermitted()` вызывается
на hot path для каждого job. `volatile` позволяет lock-free reads. Lock берется
для state mutations, например reporting success/failure, а не для common
permission check.
:::

Half-open probes - это permits, а не просто booleans. Если worker был пропущен
через circuit, но затем пропустил user code из-за stale job lease, shutdown или
admin cancellation before dispatch, ChokaQ releases that permit without opening
or closing circuit. Если probe исчезает без success, failure или release,
`CircuitPolicy.HalfOpenProbeTimeoutSeconds` позволяет позднему job получить
fresh probe, а не оставить circuit stuck HalfOpen.

Failure counts windowed через `CircuitPolicy.FailureWindowSeconds`. Старые
isolated failures aging out до того, как могут сложиться с намного более
поздним failure и открыть circuit для dependency, которая уже не unhealthy.

## Результат: маленькие и явные dependencies

```text
dotnet list package --include-transitive

ChokaQ.Core:
  → no non-Microsoft infrastructure packages
  → Microsoft.Extensions.*.Abstractions only

ChokaQ.Storage.SqlServer:
  → Microsoft.Data.SqlClient
  → the official SQL Server ADO.NET driver
```

SQL Server mode поэтому имеет один ожидаемый external package: официальный SQL
Server driver. Важное архитектурное ограничение в том, что ChokaQ держит
queueing, retry, circuit breaker, SQL mapping, dashboard, health и metrics
behavior внутри project boundary, чтобы эти решения оставались inspectable.

<br>

> Дальше: [Smart Worker](/ru/1-architecture/smart-worker) классифицирует ошибки и экономит runtime capacity.
