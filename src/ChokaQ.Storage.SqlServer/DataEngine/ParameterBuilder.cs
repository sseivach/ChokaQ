using Microsoft.Data.SqlClient;
using System.Collections;
using System.Reflection;
using System.Text;

namespace ChokaQ.Storage.SqlServer.DataEngine;

/// <summary>
/// Converts anonymous objects to SqlParameter arrays and handles IN clause expansion.
/// </summary>
internal static class ParameterBuilder
{
    /// <summary>
    /// Builds SqlParameter array from an anonymous object.
    /// Expands array properties for IN clauses.
    /// </summary>
    public static (SqlParameter[], string) BuildParameters(object? parameters, string sql)
    {
        if (parameters == null)
            return (Array.Empty<SqlParameter>(), sql);

        var paramList = new List<SqlParameter>();
        var modifiedSql = sql;

        if (parameters is IDictionary<string, object?> dict)
        {
            foreach (var kvp in dict)
            {
                ProcessParameter(kvp.Key, kvp.Value, ref modifiedSql, paramList);
            }
        }
        else
        {
            var properties = parameters.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in properties)
            {
                var value = prop.GetValue(parameters);
                ProcessParameter(prop.Name, value, ref modifiedSql, paramList);
            }
        }

        return (paramList.ToArray(), modifiedSql);
    }

    private static void ProcessParameter(string name, object? value, ref string sql, List<SqlParameter> paramList)
    {
        var paramName = name.StartsWith("@") ? name : "@" + name;

        // Handle array/enumerable parameters (for IN clauses)
        if (value is IEnumerable enumerable and not string)
        {
            var (expandedParams, expandedSql) = ExpandArrayParameter(paramName, enumerable, sql);
            paramList.AddRange(expandedParams);
            sql = expandedSql;
        }
        else
        {
            paramList.Add(new SqlParameter(paramName, value ?? DBNull.Value));
        }
    }

    private static (SqlParameter[], string) ExpandArrayParameter(string paramName, IEnumerable values, string sql)
    {
        var paramList = new List<SqlParameter>();
        var valueList = new List<object?>();

        foreach (var item in values)
        {
            valueList.Add(item);
        }

        if (valueList.Count == 0)
        {
            var modifiedSql = sql.Replace(paramName, "(SELECT NULL WHERE 1=0)");
            return (Array.Empty<SqlParameter>(), modifiedSql);
        }

        var parameterNames = new StringBuilder();
        for (int i = 0; i < valueList.Count; i++)
        {
            var indexedParamName = $"{paramName}{i}";
            paramList.Add(new SqlParameter(indexedParamName, valueList[i] ?? DBNull.Value));

            if (i > 0) parameterNames.Append(", ");
            parameterNames.Append(indexedParamName);
        }

        var expandedSql = sql.Replace(paramName, $"({parameterNames})");
        return (paramList.ToArray(), expandedSql);
    }
}