using ChokaQ.Abstractions.Storage;
using ChokaQ.Abstractions.Workers;
using ChokaQ.Core.Defaults;
using ChokaQ.Core.Extensions;
using ChokaQ.Core.Resilience;
using ChokaQ.Core.Workers;
using ChokaQ.Storage.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ChokaQ.Tests.Unit.Extensions;

public class ChokaQSqlServerExtensionsTests
{
    [Fact]
    public void UseSqlServer_ShouldReplaceInMemoryProducerAndWorker()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddChokaQ();
        services.UseSqlServer(options =>
        {
            options.ConnectionString = "Server=localhost;Database=ChokaQ;Trusted_Connection=True;TrustServerCertificate=True;";
        });

        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IJobStorage>().Should().BeOfType<SqlJobStorage>();
        sp.GetRequiredService<IChokaQQueue>().Should().BeOfType<SqlChokaQQueue>();
        sp.GetRequiredService<IWorkerManager>().Should().BeOfType<SqlJobWorker>();

        // The durable SQL provider owns the producer/consumer boundary. If the
        // in-memory queue or in-memory worker remains active, SQL mode silently
        // gains a second, process-local transport that can block or duplicate work.
        sp.GetService<InMemoryQueue>().Should().BeNull();

        var hostedServices = sp.GetServices<IHostedService>().ToList();
        hostedServices.Should().ContainSingle(service => service is SqlJobWorker);
        hostedServices.Should().ContainSingle(service => service is ZombieRescueService);
        hostedServices.Should().NotContain(service => service is JobWorker);
        hostedServices.Should().NotContain(service => service is JobWorkerHostedService);
    }

    [Fact]
    public void UseSqlServer_WithConfiguration_ShouldBindAllSqlRuntimeOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ChokaQ:SqlServer:ConnectionString"] = "Server=localhost;Database=ChokaQ;Trusted_Connection=True;TrustServerCertificate=True;",
                ["ChokaQ:SqlServer:SchemaName"] = "jobs",
                ["ChokaQ:SqlServer:AutoCreateSqlTable"] = "true",
                ["ChokaQ:SqlServer:PollingInterval"] = "00:00:00.250",
                ["ChokaQ:SqlServer:NoQueuesSleepInterval"] = "00:00:02",
                ["ChokaQ:SqlServer:MaxTransientRetries"] = "7",
                ["ChokaQ:SqlServer:TransientRetryBaseDelayMs"] = "150",
                ["ChokaQ:SqlServer:CommandTimeoutSeconds"] = "45",
                ["ChokaQ:SqlServer:CleanupBatchSize"] = "25"
            })
            .Build();

        services.AddChokaQ();
        services.UseSqlServer(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<SqlJobStorageOptions>>().Value;

        // SQL polling and transient retry policy are operational knobs, so the NuGet package
        // must bind them from appsettings instead of forcing admins to recompile Program.cs.
        options.SchemaName.Should().Be("jobs");
        options.AutoCreateSqlTable.Should().BeTrue();
        options.PollingInterval.Should().Be(TimeSpan.FromMilliseconds(250));
        options.NoQueuesSleepInterval.Should().Be(TimeSpan.FromSeconds(2));
        options.MaxTransientRetries.Should().Be(7);
        options.TransientRetryBaseDelayMs.Should().Be(150);
        options.CommandTimeoutSeconds.Should().Be(45);
        options.CleanupBatchSize.Should().Be(25);
    }

    [Fact]
    public void UseSqlServer_ShouldFailFastOnInvalidConfiguration()
    {
        var services = new ServiceCollection();

        var act = () => services.UseSqlServer(options =>
        {
            options.ConnectionString = "";
            options.PollingInterval = TimeSpan.Zero;
            options.CommandTimeoutSeconds = 0;
            options.CleanupBatchSize = 0;
        });

        // SQL storage is the durability boundary. Invalid configuration should stop startup
        // before a worker can claim jobs and then fail to persist state transitions.
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionString*PollingInterval*CommandTimeoutSeconds*CleanupBatchSize*");
    }
}
