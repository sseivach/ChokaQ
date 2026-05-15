namespace ChokaQ.Sample.NuGetLab.Infrastructure;

internal static class SqlConnectionStringResolver
{
    public static string Resolve(IConfiguration configuration)
    {
        var configured = configuration.GetValue<string>("ChokaQ:SqlServer:ConnectionString");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        configured = configuration.GetConnectionString("ChokaQDb");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        configured = Environment.GetEnvironmentVariable("CHOKAQ_NUGET_LAB_SQL");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        if (IsDevelopment(configuration) &&
            LocalDockerSqlDiscovery.TryResolve("chokaq-sql", "ChokaQNuGetLab", out var dockerConnectionString))
        {
            return dockerConnectionString;
        }

        throw new InvalidOperationException(
            "Configure ChokaQ:SqlServer:ConnectionString, ConnectionStrings:ChokaQDb, or CHOKAQ_NUGET_LAB_SQL before running the NuGet lab sample. In Development, a running Docker container named chokaq-sql is also accepted.");
    }

    private static bool IsDevelopment(IConfiguration configuration)
    {
        var environmentName = configuration["ASPNETCORE_ENVIRONMENT"]
            ?? configuration["DOTNET_ENVIRONMENT"]
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        return string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase);
    }
}
