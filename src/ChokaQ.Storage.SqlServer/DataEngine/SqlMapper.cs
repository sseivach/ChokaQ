using Microsoft.Data.SqlClient;
using System.Runtime.CompilerServices;

namespace ChokaQ.Storage.SqlServer.DataEngine;

/// <summary>
/// Extension methods for SqlConnection providing Dapper-like functionality.
/// </summary>
public static class SqlMapper
{
    private static readonly ConditionalWeakTable<SqlConnection, CommandOptions> ConnectionOptions = new();

    /// <summary>
    /// Associates ChokaQ command settings with an open connection.
    /// </summary>
    /// <remarks>
    /// ADO.NET stores CommandTimeout on SqlCommand, not on SqlConnection. SqlJobStorage opens
    /// connections while SqlMapper creates commands, so we attach the timeout to the connection
    /// and apply it every time a command is built. ConditionalWeakTable keeps this bookkeeping
    /// from extending the lifetime of disposed connections.
    /// </remarks>
    internal static void SetCommandTimeout(this SqlConnection conn, int commandTimeoutSeconds)
    {
        if (commandTimeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandTimeoutSeconds),
                "SQL command timeout must be greater than zero seconds.");
        }

        ConnectionOptions.Remove(conn);
        ConnectionOptions.Add(conn, new CommandOptions(commandTimeoutSeconds));
    }

    /// <summary>
    /// Executes a query and returns multiple rows.
    /// </summary>
    public static async Task<IEnumerable<T>> QueryAsync<T>(
        this SqlConnection conn,
        string sql,
        object? parameters = null,
        CancellationToken ct = default)
    {
        var (sqlParams, modifiedSql) = ParameterBuilder.BuildParameters(parameters, sql);

        using var cmd = CreateCommand(conn, modifiedSql);
        cmd.Parameters.AddRange(sqlParams);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<T>();

        // Check if T is a primitive/value type
        var isPrimitive = IsPrimitiveType(typeof(T));

        while (await reader.ReadAsync(ct))
        {
            if (isPrimitive)
            {
                var value = reader.GetValue(0);
                results.Add(value == DBNull.Value ? default! : (T)Convert.ChangeType(value, typeof(T)));
            }
            else
            {
                results.Add(TypeMapper.MapRow<T>(reader));
            }
        }

        return results;
    }

    /// <summary>
    /// Executes a query and returns the first row or null.
    /// </summary>
    public static async Task<T?> QueryFirstOrDefaultAsync<T>(
        this SqlConnection conn,
        string sql,
        object? parameters = null,
        CancellationToken ct = default)
    {
        var (sqlParams, modifiedSql) = ParameterBuilder.BuildParameters(parameters, sql);

        using var cmd = CreateCommand(conn, modifiedSql);
        cmd.Parameters.AddRange(sqlParams);

        using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            // Handle primitive types (string, int, etc.)
            if (IsPrimitiveType(typeof(T)))
            {
                var value = reader.GetValue(0);
                return value == DBNull.Value ? default : (T)Convert.ChangeType(value, typeof(T));
            }

            // Handle complex types
            return TypeMapper.MapRow<T>(reader);
        }

        return default;
    }

    /// <summary>
    /// Executes a query and returns exactly one row. Throws if 0 or more than 1.
    /// </summary>
    public static async Task<T> QuerySingleAsync<T>(
        this SqlConnection conn,
        string sql,
        object? parameters = null,
        CancellationToken ct = default)
    {
        var (sqlParams, modifiedSql) = ParameterBuilder.BuildParameters(parameters, sql);

        using var cmd = CreateCommand(conn, modifiedSql);
        cmd.Parameters.AddRange(sqlParams);

        using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException("Sequence contains no elements");
        }

        T result;
        if (IsPrimitiveType(typeof(T)))
        {
            var value = reader.GetValue(0);
            result = value == DBNull.Value ? default! : (T)Convert.ChangeType(value, typeof(T));
        }
        else
        {
            result = TypeMapper.MapRow<T>(reader);
        }

        if (await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException("Sequence contains more than one element");
        }

        return result;
    }

    /// <summary>
    /// Executes a command and returns the number of affected rows.
    /// </summary>
    public static async Task<int> ExecuteAsync(
        this SqlConnection conn,
        string sql,
        object? parameters = null,
        CancellationToken ct = default)
    {
        var (sqlParams, modifiedSql) = ParameterBuilder.BuildParameters(parameters, sql);

        using var cmd = CreateCommand(conn, modifiedSql);
        cmd.Parameters.AddRange(sqlParams);

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Executes a query and returns a scalar value.
    /// </summary>
    public static async Task<T> ExecuteScalarAsync<T>(
        this SqlConnection conn,
        string sql,
        object? parameters = null,
        CancellationToken ct = default)
    {
        var (sqlParams, modifiedSql) = ParameterBuilder.BuildParameters(parameters, sql);

        using var cmd = CreateCommand(conn, modifiedSql);
        cmd.Parameters.AddRange(sqlParams);

        var result = await cmd.ExecuteScalarAsync(ct);

        if (result == null || result == DBNull.Value)
        {
            return default!;
        }

        return (T)Convert.ChangeType(result, typeof(T));
    }

    private static bool IsPrimitiveType(Type type)
    {
        return type.IsPrimitive
            || type.IsValueType
            || type == typeof(string)
            || type == typeof(decimal)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(TimeSpan)
            || type == typeof(Guid)
            || (Nullable.GetUnderlyingType(type) != null && IsPrimitiveType(Nullable.GetUnderlyingType(type)!));
    }

    private static SqlCommand CreateCommand(SqlConnection conn, string sql)
    {
        var command = new SqlCommand(sql, conn);

        if (ConnectionOptions.TryGetValue(conn, out var options))
        {
            command.CommandTimeout = options.CommandTimeoutSeconds;
        }

        return command;
    }

    private sealed record CommandOptions(int CommandTimeoutSeconds);
}
