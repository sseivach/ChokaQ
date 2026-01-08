using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Text.RegularExpressions;

namespace ChokaQ.Storage.SqlServer;

/// <summary>
/// Handles database schema provisioning.
/// Reads embedded SQL scripts, replaces schema placeholders, and executes them against the target database.
/// </summary>
public class SqlInitializer
{
    private readonly string _connectionString;
    private readonly string _schemaName;
    private readonly ILogger<SqlInitializer> _logger;

    public SqlInitializer(string connectionString, string schemaName, ILogger<SqlInitializer> logger)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _schemaName = schemaName ?? throw new ArgumentNullException(nameof(schemaName));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Executes the provisioning logic.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting ChokaQ database initialization. Target Schema: {Schema}", _schemaName);

        // 1. Security Validation
        // Prevent SQL Injection via schema name configuration
        if (!Regex.IsMatch(_schemaName, "^[a-zA-Z0-9_]+$"))
        {
            throw new ArgumentException($"Invalid schema name: '{_schemaName}'. Only alphanumeric characters and underscores are allowed.");
        }

        try
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            // 2. Define execution order
            // Tables must be created before Stored Procedures
            var scripts = new[]
            {
                "ChokaQ.Storage.SqlServer.Scripts.SchemaTemplate.sql",
                "ChokaQ.Storage.SqlServer.Scripts.CleanupProcTemplate.sql"
            };

            foreach (var resourceName in scripts)
            {
                await ExecuteScriptFromResourceAsync(connection, resourceName, ct);
            }

            _logger.LogInformation("ChokaQ database initialization completed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "ChokaQ database initialization failed. Application may not function correctly.");
            throw;
        }
    }

    private async Task ExecuteScriptFromResourceAsync(SqlConnection connection, string resourceName, CancellationToken ct)
    {
        // 3. Load Embedded Resource
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName);

        if (stream == null)
        {
            throw new FileNotFoundException($"Embedded SQL resource not found: {resourceName}. Ensure 'Build Action' is set to 'Embedded Resource'.");
        }

        using var reader = new StreamReader(stream);
        var template = await reader.ReadToEndAsync(ct);

        // 4. Replace Placeholders
        var finalScript = template.Replace("{SCHEMA}", _schemaName);

        // 5. Split by 'GO'
        // ADO.NET / Dapper cannot execute scripts containing 'GO' as a single batch.
        // We must split them and execute individually.
        // Regex logic: Matches "GO" on a separate line, case-insensitive, ignoring whitespace.
        var commands = Regex.Split(finalScript, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        foreach (var commandText in commands)
        {
            if (string.IsNullOrWhiteSpace(commandText)) continue;

            try
            {
                await connection.ExecuteAsync(new CommandDefinition(commandText, cancellationToken: ct));
            }
            catch (SqlException ex)
            {
                _logger.LogError(ex, "Error executing SQL block from {Resource}.", resourceName);
                throw; // Rethrow to stop initialization
            }
        }
    }
}