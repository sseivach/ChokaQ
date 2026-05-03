using ChokaQ.Storage.SqlServer;
using ChokaQ.Storage.SqlServer.Health;
using ChokaQ.Tests.Fixtures;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace ChokaQ.Tests.Integration;

[Collection("SqlServer")]
public class SqlHealthCheckTests
{
    private readonly SqlServerFixture _fixture;

    public SqlHealthCheckTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SqlConnectivityHealthCheck_ShouldBeHealthy_WhenSchemaIsInitialized()
    {
        var check = new ChokaQSqlConnectivityHealthCheck(
            Options.Create(new SqlJobStorageOptions
            {
                ConnectionString = _fixture.ConnectionString,
                SchemaName = _fixture.Schema,
                CommandTimeoutSeconds = 30
            }));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["schema"].Should().Be(_fixture.Schema);
    }
}
