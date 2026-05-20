using System.Diagnostics;

namespace ChokaQ.Sample.Bus.Infrastructure;

internal static class LocalDockerSqlDiscovery
{
    public static bool TryResolve(string containerName, string databaseName, out string connectionString)
    {
        connectionString = string.Empty;

        var hostPort = RunDocker(
            "inspect",
            containerName,
            "--format",
            "{{(index (index .NetworkSettings.Ports \"1433/tcp\") 0).HostPort}}");

        if (string.IsNullOrWhiteSpace(hostPort) || hostPort.Contains("no value", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var env = RunDocker("inspect", containerName, "--format", "{{range .Config.Env}}{{println .}}{{end}}");
        var password = env
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .Where(parts => parts[0] is "MSSQL_SA_PASSWORD" or "SA_PASSWORD")
            .Select(parts => parts[1])
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        connectionString =
            $"Server=localhost,{hostPort};Database={databaseName};User Id=sa;Password={password};Encrypt=True;TrustServerCertificate=True;";

        return true;
    }

    private static string RunDocker(params string[] arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            }.AddArguments(arguments));

            if (process is null)
            {
                return string.Empty;
            }

            if (!process.WaitForExit(3000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort only. Docker discovery is optional for the sample.
                }

                return string.Empty;
            }

            var output = process.StandardOutput.ReadToEnd();
            return process.ExitCode == 0 ? output.Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static ProcessStartInfo AddArguments(this ProcessStartInfo startInfo, IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
