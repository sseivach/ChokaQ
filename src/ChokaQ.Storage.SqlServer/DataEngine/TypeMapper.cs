using System.Collections.Concurrent;
using System.Data;
using System.Reflection;

namespace ChokaQ.Storage.SqlServer.DataEngine;

/// <summary>
/// Maps SqlDataReader rows to typed objects using cached reflection.
/// </summary>
internal static class TypeMapper
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _propertyCache = new();
    private static readonly ConcurrentDictionary<Type, ConstructorInfo?> _constructorCache = new();

    /// <summary>
    /// Maps a single row from SqlDataReader to type T.
    /// </summary>
    public static T MapRow<T>(IDataReader reader)
    {
        var type = typeof(T);
        var properties = GetProperties(type);
        
        // Try to find a suitable constructor
        var ctor = GetConstructor(type);
        
        T instance;
        if (ctor != null && ctor.GetParameters().Length > 0)
        {
            // Use constructor with parameters (for record types)
            instance = CreateInstanceViaConstructor<T>(reader, ctor, properties);
        }
        else
        {
            // Use parameterless constructor or FormatterServices for classes
            instance = CreateInstanceDefault<T>(type);
            PopulateProperties(instance, reader, properties);
        }

        return instance;
    }

    private static T CreateInstanceDefault<T>(Type type)
    {
        try
        {
            return (T)Activator.CreateInstance(type)!;
        }
        catch
        {
            // Fallback for types without parameterless constructor
            return (T)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
        }
    }

    private static T CreateInstanceViaConstructor<T>(IDataReader reader, ConstructorInfo ctor, PropertyInfo[] properties)
    {
        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            var property = Array.Find(properties, p =>
                string.Equals(p.Name, param.Name, StringComparison.OrdinalIgnoreCase));

            if (property != null)
            {
                // Find column by property name
                var value = GetColumnValue(reader, property.Name);
                args[i] = ConvertValue(value, param.ParameterType);
            }
            else
            {
                args[i] = param.HasDefaultValue ? param.DefaultValue : GetDefaultValue(param.ParameterType);
            }
        }

        return (T)ctor.Invoke(args);
    }

    private static void PopulateProperties<T>(T instance, IDataReader reader, PropertyInfo[] properties)
    {
        for (int i = 0; i < reader.FieldCount; i++)
        {
            var columnName = reader.GetName(i);
            var property = Array.Find(properties, p =>
                string.Equals(p.Name, columnName, StringComparison.OrdinalIgnoreCase));

            if (property != null && property.CanWrite)
            {
                var value = reader.GetValue(i);
                if (value != DBNull.Value)
                {
                    var convertedValue = ConvertValue(value, property.PropertyType);
                    property.SetValue(instance, convertedValue);
                }
            }
        }
    }

    private static object? GetColumnValue(IDataReader reader, string columnName)
    {
        for (int i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), columnName, StringComparison.OrdinalIgnoreCase))
            {
                var value = reader.GetValue(i);
                return value == DBNull.Value ? null : value;
            }
        }
        return null;
    }

    private static object? ConvertValue(object? value, Type targetType)
    {
        if (value == null || value == DBNull.Value)
            return GetDefaultValue(targetType);

        var targetBaseType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (targetBaseType.IsEnum)
            return Enum.ToObject(targetBaseType, value);

        if (targetBaseType == value.GetType())
            return value;

        return Convert.ChangeType(value, targetBaseType);
    }

    private static object? GetDefaultValue(Type type)
    {
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    private static PropertyInfo[] GetProperties(Type type)
    {
        return _propertyCache.GetOrAdd(type, t =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance));
    }

    private static ConstructorInfo? GetConstructor(Type type)
    {
        return _constructorCache.GetOrAdd(type, t =>
        {
            // First try parameterless
            var parameterless = t.GetConstructor(BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (parameterless != null)
                return parameterless;

            // Then try to find constructor with most parameters (for records)
            var ctors = t.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            return ctors.OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        });
    }
}
