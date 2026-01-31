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

        var properties = parameters.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        
        foreach (var prop in properties)
        {
            var value = prop.GetValue(parameters);
            var paramName = "@" + prop.Name;

            // Handle array/enumerable parameters (for IN clauses)
            if (value is IEnumerable enumerable and not string)
            {
                var (expandedParams, expandedSql) = ExpandArrayParameter(paramName, enumerable, modifiedSql);
                paramList.AddRange(expandedParams);
                modifiedSql = expandedSql;
            }
            else
            {
                // Regular parameter
                paramList.Add(new SqlParameter(paramName, value ?? DBNull.Value));
            }
        }

        return (paramList.ToArray(), modifiedSql);
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
            // Empty array - replace with (SELECT NULL WHERE 1=0) to ensure no matches
            var modifiedSql = sql.Replace(paramName, "(SELECT NULL WHERE 1=0)");
            return (Array.Empty<SqlParameter>(), modifiedSql);
        }

        // Generate @ParamName0, @ParamName1, etc.
        var parameterNames = new StringBuilder();
        for (int i = 0; i < valueList.Count; i++)
        {
            var indexedParamName = $"{paramName}{i}";
            paramList.Add(new SqlParameter(indexedParamName, valueList[i] ?? DBNull.Value));
            
            if (i > 0) parameterNames.Append(", ");
            parameterNames.Append(indexedParamName);
        }

        // Replace @ParamName with (@ParamName0, @ParamName1, ...)
        var expandedSql = sql.Replace(paramName, $"({parameterNames})");
        
        return (paramList.ToArray(), expandedSql);
    }
}
