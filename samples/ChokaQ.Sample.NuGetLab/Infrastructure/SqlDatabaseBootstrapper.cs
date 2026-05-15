using Microsoft.Data.SqlClient;

namespace ChokaQ.Sample.NuGetLab.Infrastructure;

internal static class SqlDatabaseBootstrapper
{
    public static void EnsureDatabaseExists(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var databaseName = builder.InitialCatalog;

        if (string.IsNullOrWhiteSpace(databaseName) ||
            databaseName.Equals("master", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        builder.InitialCatalog = "master";

        using var connection = new SqlConnection(builder.ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
IF DB_ID(@databaseName) IS NULL
BEGIN
    DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@databaseName);
    EXEC(@sql);
END
""";
        command.Parameters.AddWithValue("@databaseName", databaseName);
        command.ExecuteNonQuery();
    }
}
