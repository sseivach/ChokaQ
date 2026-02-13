using ChokaQ.Abstractions.Storage;
using ChokaQ.Abstractions.Workers;
using ChokaQ.Core.Execution;
using ChokaQ.Core.Extensions;
using ChokaQ.Core.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace ChokaQ.Tests.Unit.Extensions;

public class ChokaQCoreExtensionsTests
{
    [Fact]
    public void AddChokaQ_ShouldRegisterCoreServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChokaQ();

        var sp = services.BuildServiceProvider();

        sp.GetService<IJobProcessor>().Should().NotBeNull();
        sp.GetService<IJobStorage>().Should().NotBeNull();
        sp.GetService<IWorkerManager>().Should().NotBeNull();
    }

    [Fact]
    public void AddChokaQ_Default_ShouldRegisterBusJobDispatcher()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChokaQ();

        var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetService<IJobDispatcher>();

        dispatcher.Should().BeOfType<BusJobDispatcher>();
    }

    [Fact]
    public void AddChokaQ_PipeMode_ShouldRegisterPipeJobDispatcher()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChokaQ(o => o.UsePipe<TestPipeHandler>());

        var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetService<IJobDispatcher>();

        dispatcher.Should().BeOfType<PipeJobDispatcher>();
    }

    private class TestPipeHandler : ChokaQ.Abstractions.Jobs.IChokaQPipeHandler
    {
        public Task HandleAsync(string jobType, string payload, CancellationToken ct) => Task.CompletedTask;
    }
}
