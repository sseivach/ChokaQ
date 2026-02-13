using ChokaQ.Storage.SqlServer;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace ChokaQ.Tests.Fixtures;

/// <summary>
/// Shared SQL Server Testcontainer fixture for integration tests.
/// Spins up a real SQL Server container ONCE per test collection.
/// Runs SqlInitializer to create schema.
/// Provides connection string to all integration tests.
/// Cleans tables between tests (TRUNCATE, not DROP/CREATE).
/// </summary>
public class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container;
    public string Schema => "chokaq_test";

    public SqlServerFixture()
    {
        _container = new MsSqlBuilder()
            .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
            .WithPassword("YourStrong!Passw0rd")
            .Build();
    }

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        // Start the container
        await _container.StartAsync();

        // Initialize schema using SqlInitializer
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<SqlInitializer>>();
        var initializer = new SqlInitializer(ConnectionString, Schema, logger);
        await initializer.InitializeAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    /// <summary>
    /// Truncates all 5 tables to ensure test isolation.
    /// Called at the start of each test.
    /// </summary>
    public async Task CleanTablesAsync()
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        var tables = new[]
        {
            $"[{Schema}].[JobsHot]",
            $"[{Schema}].[JobsArchive]",
            $"[{Schema}].[JobsDLQ]",
            $"[{Schema}].[StatsSummary]",
            $"[{Schema}].[Queues]"
        };

        foreach (var table in tables)
        {
            await using var cmd = new SqlCommand($"TRUNCATE TABLE {table}", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        // Re-seed default queue and stats
        await using var seedCmd = new SqlCommand($@"
            INSERT INTO [{Schema}].[Queues] ([Name], [IsPaused], [IsActive], [ZombieTimeoutSeconds], [LastUpdatedUtc])
            VALUES ('default', 0, 1, NULL, SYSUTCDATETIME());

            INSERT INTO [{Schema}].[StatsSummary] ([Queue], [SucceededTotal], [FailedTotal], [RetriedTotal], [LastActivityUtc])
            VALUES ('default', 0, 0, 0, SYSUTCDATETIME());
        ", conn);
        await seedCmd.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Collection definition for SQL Server integration tests.
/// All tests in this collection share the same SqlServerFixture instance.
/// </summary>
[CollectionDefinition("SqlServer")]
public class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}
