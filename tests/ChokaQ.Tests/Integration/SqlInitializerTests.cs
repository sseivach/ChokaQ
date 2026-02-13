using ChokaQ.Storage.SqlServer;
using ChokaQ.Tests.Fixtures;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChokaQ.Tests.Integration;

/// <summary>
/// Integration tests for SqlInitializer - schema creation and migration.
/// </summary>
[Collection("SqlServer")]
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

        // Assert - Verify all 5 tables exist
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        var tables = new[] { "JobsHot", "JobsArchive", "JobsDLQ", "StatsSummary", "Queues" };
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
        count.Should().Be(5, "All 5 tables should exist after double initialization");
    }
}
