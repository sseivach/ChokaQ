# Minimal Dependency Philosophy

ChokaQ uses a minimal dependency philosophy, not a misleading claim that SQL
Server mode can avoid the official SQL Server driver. The practical rule is
stricter and more useful: ChokaQ should not force host applications to take
framework-level dependencies such as EF Core, Dapper, Polly, MediatR, or a
custom telemetry/exporter stack just to run background jobs.

Core stays on Microsoft.Extensions abstractions. SQL Server storage uses
`Microsoft.Data.SqlClient`, the official ADO.NET driver. Everything else is
owned by ChokaQ so the behavior is explicit, inspectable, and teachable.

## The Dependency Tax

Every NuGet package you add to a project carries hidden costs:

| Cost | Example |
|------|---------|
| **Version Conflicts** | Dapper 2.x vs Dapper 3.x — different apps in the same solution need different versions |
| **Transitive Dependencies** | Polly pulls in `Microsoft.Extensions.Http`, which pulls in `System.Net.Http` |
| **Security Surface** | Each dependency is a CVE waiting to happen — `log4j` taught the industry this |
| **Breaking Changes** | A minor version bump in a transitive dep can break your entire CI pipeline |
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
└── Microsoft.Data.SqlClient     ← The only "real" dependency
```

Everything else is built directly in ChokaQ.

## What We Built (and Why)

### Custom SqlMapper — Replacing Dapper

**172 lines** of extension methods on `SqlConnection`:

```csharp
// Our SqlMapper API — familiar if you've used Dapper
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

**Why not Dapper?**
- Dapper brings `System.Data` extension methods that may conflict with other versions
- Dapper's `DynamicParameters` allocates objects we don't need
- We only use 4 patterns: `QueryAsync<T>`, `QueryFirstOrDefaultAsync<T>`, `ExecuteAsync`, `ExecuteScalarAsync` — no need for 300+ methods

### Custom TypeMapper — Zero-Reflection Materialization

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

### Custom ParameterBuilder — Smart Parameter Expansion

Converts anonymous objects to `SqlParameter[]` with a killer feature — **automatic array expansion for `IN` clauses**:

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

No `OPENJSON`, no temp tables — just clean parameter expansion.

### Custom SqlRetryPolicy — Replacing Polly

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

**Why not Polly?**
- Polly v8 has a completely different API from Polly v7 — breaking change nightmare
- Polly brings `Microsoft.Extensions.Http` and other transitive deps
- We only need retry with backoff for SQL transient faults — not the full policy builder

### Custom InMemoryCircuitBreaker — Per-Job-Type Protection

Thread-safe circuit breaker with **lock-free reads** on the hot path:

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
    return true; // Half-Open: allow test execution
}
```

Three states per job type:

| State | Behavior | Transition |
|-------|----------|-----------|
| **Closed** | All executions allowed | → Open (after 5 failures) |
| **Open** | All executions blocked | → Half-Open (after 30s timeout) |
| **Half-Open** | One test execution | → Closed (success) or → Open (failure) |

::: tip 💡 Performance Insight
The Status field is marked as `volatile` because `IsExecutionPermitted()` is called on the hot path for every single job. Using `volatile` allows **lock-free reads** — we only acquire the lock for state mutations (reporting success/failure), not for the check itself. This is the same high-performance pattern used in `CancellationToken`.
:::

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

SQL Server mode therefore has one unavoidable external package: the official SQL
Server driver. The important architectural constraint is that ChokaQ does not
outsource its queueing, retry, circuit breaker, SQL mapping, dashboard, health,
or metrics behavior to opaque infrastructure libraries.

<br>

> *Next: See how the [Smart Worker](/1-architecture/smart-worker) classifies errors to save system resources.*
