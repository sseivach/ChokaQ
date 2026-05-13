using ChokaQ.Storage.SqlServer;
using ChokaQ.Tests.Fixtures;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChokaQ.Tests.Integration;

/// <summary>
/// Integration tests for SqlInitializer - schema creation and migration.
/// </summary>
[Collection("SqlServer")]
[Trait(TestCategories.Category, TestCategories.Integration)]
public class SqlInitializerTests
{
    private readonly SqlServerFixture _fixture;

    public SqlInitializerTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task InitializeAsync_ShouldCreateAllTables()
    {
        // Arrange
        var testSchema = "test_schema_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var initializer = new SqlInitializer(_fixture.ConnectionString, testSchema, NullLogger<SqlInitializer>.Instance);

        // Act
        await initializer.InitializeAsync(CancellationToken.None);

        // Assert - Verify all tables exist, including the migration ledger.
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        var tables = new[] { "JobsHot", "JobsArchive", "JobsDLQ", "StatsSummary", "MetricBuckets", "Queues", "SchemaMigrations" };
        foreach (var table in tables)
        {
            var cmd = new SqlCommand($@"
                SELECT COUNT(*) 
                FROM INFORMATION_SCHEMA.TABLES 
                WHERE TABLE_SCHEMA = @Schema AND TABLE_NAME = @Table", conn);
            cmd.Parameters.AddWithValue("@Schema", testSchema);
            cmd.Parameters.AddWithValue("@Table", table);

            var count = (int)(await cmd.ExecuteScalarAsync())!;
            count.Should().Be(1, $"Table {testSchema}.{table} should exist");
        }
    }

    [Fact]
    public async Task InitializeAsync_ShouldRecordSchemaVersion()
    {
        // Arrange
        var testSchema = "test_schema_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var initializer = new SqlInitializer(_fixture.ConnectionString, testSchema, NullLogger<SqlInitializer>.Instance);

        // Act
        await initializer.InitializeAsync(CancellationToken.None);

        // Assert
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        var version1Cmd = new SqlCommand($@"
            SELECT [Name]
            FROM [{testSchema}].[SchemaMigrations]
            WHERE [Version] = 1", conn);

        var version2Cmd = new SqlCommand($@"
            SELECT [Name]
            FROM [{testSchema}].[SchemaMigrations]
            WHERE [Version] = 2", conn);

        var version3Cmd = new SqlCommand($@"
            SELECT [Name]
            FROM [{testSchema}].[SchemaMigrations]
            WHERE [Version] = 3", conn);

        var version1Name = (string?)(await version1Cmd.ExecuteScalarAsync());
        var version2Name = (string?)(await version2Cmd.ExecuteScalarAsync());
        var version3Name = (string?)(await version3Cmd.ExecuteScalarAsync());

        // The migration ledger is an operational audit tool. During incidents and upgrades,
        // operators need an authoritative database-side answer for the installed schema version.
        version1Name.Should().Be("Initial Three Pillars schema");
        version2Name.Should().Be("Hot path index hardening");
        version3Name.Should().Be("Rolling metric buckets");
    }

    [Fact]
    public async Task InitializeAsync_ShouldCreateCriticalPerformanceIndexes()
    {
        // Arrange
        var testSchema = "test_schema_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var initializer = new SqlInitializer(_fixture.ConnectionString, testSchema, NullLogger<SqlInitializer>.Instance);

        // Act
        await initializer.InitializeAsync(CancellationToken.None);

        // Assert
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        var expectedIndexes = new[]
        {
            $"IX_{testSchema}_JobsHot_Fetch",
            $"IX_{testSchema}_JobsHot_StatusCreated",
            $"IX_{testSchema}_JobsHot_FetchedRecovery",
            $"IX_{testSchema}_JobsHot_ProcessingHeartbeat",
            $"IX_{testSchema}_JobsDLQ_Type",
            $"IX_{testSchema}_JobsDLQ_CreatedAt",
            $"IX_{testSchema}_MetricBuckets_Recent"
        };

        foreach (var indexName in expectedIndexes)
        {
            var cmd = new SqlCommand(@"
                SELECT COUNT(1)
                FROM sys.indexes
                WHERE [name] = @IndexName", conn);
            cmd.Parameters.AddWithValue("@IndexName", indexName);

            var count = (int)(await cmd.ExecuteScalarAsync())!;
            count.Should().Be(1, $"Index {indexName} should exist");
        }

        var fetchKeyCmd = new SqlCommand($@"
            SELECT COUNT(1)
            FROM sys.indexes i
            INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.name = @IndexName
              AND i.object_id = OBJECT_ID(@TableName)
              AND c.name = 'CreatedAtUtc'
              AND ic.key_ordinal > 0", conn);
        fetchKeyCmd.Parameters.AddWithValue("@IndexName", $"IX_{testSchema}_JobsHot_Fetch");
        fetchKeyCmd.Parameters.AddWithValue("@TableName", $"[{testSchema}].[JobsHot]");

        var fetchHasCreatedAtKey = (int)(await fetchKeyCmd.ExecuteScalarAsync())!;

        // The fetch query orders by ISNULL(ScheduledAtUtc, CreatedAtUtc). Including CreatedAtUtc
        // as a key column documents that the index shape was reviewed against the actual query,
        // not just copied from an older simplified example.
        fetchHasCreatedAtKey.Should().Be(1);
    }

    [Fact]
    public async Task InitializeAsync_ShouldCreateDefaultQueue()
    {
        // Arrange
        var testSchema = "test_schema_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var initializer = new SqlInitializer(_fixture.ConnectionString, testSchema, NullLogger<SqlInitializer>.Instance);

        // Act
        await initializer.InitializeAsync(CancellationToken.None);

        // Assert - Verify default queue exists
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        var cmd = new SqlCommand($"SELECT COUNT(*) FROM [{testSchema}].[Queues] WHERE [Name] = 'default'", conn);
        var count = (int)(await cmd.ExecuteScalarAsync())!;
        count.Should().Be(1, "Default queue should be created");
    }

    [Fact]
    public async Task InitializeAsync_ShouldCreateDefaultStats()
    {
        // Arrange
        var testSchema = "test_schema_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var initializer = new SqlInitializer(_fixture.ConnectionString, testSchema, NullLogger<SqlInitializer>.Instance);

        // Act
        await initializer.InitializeAsync(CancellationToken.None);

        // Assert - Verify default stats exist
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        var cmd = new SqlCommand($"SELECT COUNT(*) FROM [{testSchema}].[StatsSummary] WHERE [Queue] = 'default'", conn);
        var count = (int)(await cmd.ExecuteScalarAsync())!;
        count.Should().Be(1, "Default stats should be created");
    }

    [Fact]
    public async Task InitializeAsync_CalledTwice_ShouldBeIdempotent()
    {
        // Arrange
        var testSchema = "test_schema_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var initializer = new SqlInitializer(_fixture.ConnectionString, testSchema, NullLogger<SqlInitializer>.Instance);

        // Act - Call twice
        await initializer.InitializeAsync(CancellationToken.None);
        await initializer.InitializeAsync(CancellationToken.None); // Should not throw

        // Assert - Tables should still exist
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        var cmd = new SqlCommand($@"
            SELECT COUNT(*) 
            FROM INFORMATION_SCHEMA.TABLES 
            WHERE TABLE_SCHEMA = @Schema", conn);
        cmd.Parameters.AddWithValue("@Schema", testSchema);

        var count = (int)(await cmd.ExecuteScalarAsync())!;
        count.Should().Be(7, "All ChokaQ tables, including MetricBuckets and SchemaMigrations, should exist after double initialization");
    }
}
