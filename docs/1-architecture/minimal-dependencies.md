# Minimal Dependency Philosophy

ChokaQ uses a minimal dependency philosophy, not a misleading claim that SQL
Server mode can avoid the official SQL Server driver. The practical rule is
stricter and more useful: ChokaQ should not force host applications to take
framework-level dependencies such as a general ORM, SQL mapper, resilience
policy builder, mediator, or custom telemetry/exporter stack just to run
background jobs.

Core stays on Microsoft.Extensions abstractions. SQL Server storage uses
`Microsoft.Data.SqlClient`, the official ADO.NET driver. Everything else is
owned by ChokaQ so the behavior is explicit and inspectable.

## Dependency Cost

Every NuGet package added to a runtime component carries engineering cost:

| Cost | Example |
|------|---------|
| **Version Conflicts** | Different apps in the same solution may need different package versions |
| **Transitive Dependencies** | A small direct dependency can bring additional runtime packages through its graph |
| **Security Surface** | Each transitive package becomes part of the audited runtime surface |
| **Breaking Changes** | A minor version bump in a transitive dependency can break CI or host integration |
| **License Compliance** | Enterprise legal teams must audit every transitive dependency |

## ChokaQ's Dependency Tree

```
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

Everything else is built directly in ChokaQ.

## What ChokaQ Owns

### Small SQL Mapper

**172 lines** of extension methods on `SqlConnection`:

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

**Why own this layer?**
- ChokaQ needs only four SQL access patterns: `QueryAsync<T>`,
  `QueryFirstOrDefaultAsync<T>`, `ExecuteAsync`, and `ExecuteScalarAsync`.
- Keeping this layer internal avoids adding a general-purpose mapper to every
  host application that installs ChokaQ.
- The mapper stays narrow enough that the SQL behavior is easy to inspect during
  incident review.

### Small TypeMapper

Maps `IDataReader` columns to object properties:

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

Converts anonymous objects to `SqlParameter[]`, including automatic array
expansion for `IN` clauses:

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

This keeps the common small-list path explicit and parameterized. If a future
workload needs very large sets, that can be handled as a separate SQL shape
rather than making the default path heavier.

### SQL Retry Policy

Transient fault handling tailored to SQL Server:

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

**Why own this layer?**
- ChokaQ storage needs one narrow policy: retry SQL Server transient faults with
  backoff.
- The retry rules are tied to SQL error numbers and storage commands, not to
  general HTTP or application resilience policy.
- Host applications remain free to use their own resilience package versions
  without ChokaQ forcing a policy-builder dependency into the app graph.

### InMemoryCircuitBreaker

Thread-safe circuit breaker with lock-free reads on the hot path:

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

Three states per job type:

| State | Behavior | Transition |
|-------|----------|-----------|
| **Closed** | All executions allowed | → Open (after 5 failures) |
| **Open** | All executions blocked | → Half-Open (after 30s timeout) |
| **Half-Open** | One test execution | → Closed (success) or → Open (failure) |

::: tip Performance note
The Status field is marked as `volatile` because `IsExecutionPermitted()` is
called on the hot path for every job. Using `volatile` allows lock-free reads.
The implementation acquires a lock for state mutations such as reporting
success or failure, not for the common permission check.
:::

Half-open probes are permits, not just booleans. If a worker is allowed through
the circuit and then skips user code because the job lease was stale, the host
is shutting down, or an admin cancelled before dispatch, ChokaQ releases that
permit without opening or closing the circuit. If a probe disappears without a
success, failure, or release, `CircuitPolicy.HalfOpenProbeTimeoutSeconds` lets a
later job acquire a fresh probe instead of leaving the circuit stuck HalfOpen.

Failure counts are windowed by `CircuitPolicy.FailureWindowSeconds`. Old
isolated failures age out before they can combine with a much later failure and
open the circuit for a dependency that is no longer unhealthy.

## The Result: Small, Explicit Dependencies

```
dotnet list package --include-transitive

ChokaQ.Core:
  → no non-Microsoft infrastructure packages
  → Microsoft.Extensions.*.Abstractions only

ChokaQ.Storage.SqlServer:
  → Microsoft.Data.SqlClient
  → the official SQL Server ADO.NET driver
```

SQL Server mode therefore has one expected external package: the official SQL
Server driver. The important architectural constraint is that ChokaQ keeps its
queueing, retry, circuit breaker, SQL mapping, dashboard, health, and metrics
behavior inside the project boundary so those decisions remain inspectable.

<br>

> *Next: See how the [Smart Worker](/1-architecture/smart-worker) classifies errors to save system resources.*
