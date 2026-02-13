using ChokaQ.Abstractions.Jobs;
using ChokaQ.Abstractions.Notifications;
using ChokaQ.Core.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChokaQ.Tests.Unit.Execution;

/// <summary>
/// Unit tests for BusJobDispatcher - resolves and executes IChokaQJob handlers.
/// </summary>
public class BusJobDispatcherTests
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServiceScope _scope;
    private readonly IServiceProvider _serviceProvider;
    private readonly JobTypeRegistry _registry;

    public BusJobDispatcherTests()
    {
        _scopeFactory = Substitute.For<IServiceScopeFactory>();
        _scope = Substitute.For<IServiceScope>();
        _serviceProvider = Substitute.For<IServiceProvider>();
        _registry = new JobTypeRegistry();

        _scopeFactory.CreateScope().Returns(_scope);
        _scope.ServiceProvider.Returns(_serviceProvider);
    }

    private void SetupJobContext()
    {
        // JobContext is internal, so we use reflection to create it
        var notifier = Substitute.For<IChokaQNotifier>();
        var jobContextType = typeof(BusJobDispatcher).Assembly.GetType("ChokaQ.Core.Contexts.JobContext")!;
        var jobContext = Activator.CreateInstance(jobContextType, notifier)!;
        _serviceProvider.GetService(jobContextType).Returns(jobContext);
    }

    private BusJobDispatcher CreateDispatcher()
    {
        SetupJobContext();
        return new BusJobDispatcher(_scopeFactory, _registry, NullLogger<BusJobDispatcher>.Instance);
    }

    [Fact]
    public async Task DispatchAsync_ShouldResolveHandler_FromRegistry()
    {
        // Arrange
        _registry.Register("test_job", typeof(TestJob));
        var handler = Substitute.For<IChokaQJobHandler<TestJob>>();
        _serviceProvider.GetService(typeof(IChokaQJobHandler<TestJob>)).Returns(handler);
        var dispatcher = CreateDispatcher();

        var payload = "{\"Id\":\"test123\",\"Message\":\"Hello\"}";

        // Act
        await dispatcher.DispatchAsync("job1", "test_job", payload, CancellationToken.None);

        // Assert
        await handler.Received(1).HandleAsync(Arg.Is<TestJob>(j => j.Message == "Hello"), Arg.Any<CancellationToken>());
        // JobContext.JobId is set internally by dispatcher
    }

    [Fact]
    public async Task DispatchAsync_ShouldFallbackToTypeGetType_WhenNotInRegistry()
    {
        // Arrange - Don't register in registry
        var handler = Substitute.For<IChokaQJobHandler<TestJob>>();
        _serviceProvider.GetService(typeof(IChokaQJobHandler<TestJob>)).Returns(handler);
        var dispatcher = CreateDispatcher();

        var payload = "{\"Id\":\"test123\",\"Message\":\"Hello\"}";
        var fullTypeName = typeof(TestJob).AssemblyQualifiedName!;

        // Act
        await dispatcher.DispatchAsync("job1", fullTypeName, payload, CancellationToken.None);

        // Assert
        await handler.Received(1).HandleAsync(Arg.Any<TestJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_ShouldThrow_WhenTypeUnknown()
    {
        // Arrange
        var dispatcher = CreateDispatcher();

        // Act & Assert
        await dispatcher.Invoking(d => d.DispatchAsync("job1", "unknown_type", "{}", CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Unknown Job Type*");
    }

    [Fact]
    public async Task DispatchAsync_ShouldThrow_WhenNoHandlerRegistered()
    {
        // Arrange
        _registry.Register("test_job", typeof(TestJob));
        _serviceProvider.GetService(typeof(IChokaQJobHandler<TestJob>)).Returns((object?)null);
        var dispatcher = CreateDispatcher();

        // Act & Assert
        await dispatcher.Invoking(d => d.DispatchAsync("job1", "test_job", "{\"Id\":\"test\"}", CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No IChokaQJobHandler*");
    }

    [Fact]
    public async Task DispatchAsync_ShouldThrow_WhenDeserializationFails()
    {
        // Arrange
        _registry.Register("test_job", typeof(TestJob));
        var handler = Substitute.For<IChokaQJobHandler<TestJob>>();
        _serviceProvider.GetService(typeof(IChokaQJobHandler<TestJob>)).Returns(handler);
        var dispatcher = CreateDispatcher();

        var invalidPayload = "{invalid json";

        // Act & Assert
        var act = async () => await dispatcher.DispatchAsync("job1", "test_job", invalidPayload, CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task DispatchAsync_ShouldUnwrapTargetInvocationException()
    {
        // Arrange
        _registry.Register("test_job", typeof(TestJob));
        var handler = Substitute.For<IChokaQJobHandler<TestJob>>();
        handler.HandleAsync(Arg.Any<TestJob>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Inner exception")));
        _serviceProvider.GetService(typeof(IChokaQJobHandler<TestJob>)).Returns(handler);
        var dispatcher = CreateDispatcher();

        // Act & Assert
        await dispatcher.Invoking(d => d.DispatchAsync("job1", "test_job", "{\"Id\":\"test\"}", CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Inner exception");
    }



    [Fact]
    public void ParseMetadata_ShouldExtractQueueAndPriority()
    {
        // Arrange
        var dispatcher = CreateDispatcher();
        var payload = "{\"Metadata\":{\"Queue\":\"critical\",\"Priority\":5},\"Message\":\"Test\"}";

        // Act
        var metadata = dispatcher.ParseMetadata(payload);

        // Assert
        metadata.Queue.Should().Be("critical");
        metadata.Priority.Should().Be(5);
    }

    [Fact]
    public void ParseMetadata_ShouldReturnDefaults_ForEmptyPayload()
    {
        // Arrange
        var dispatcher = CreateDispatcher();

        // Act
        var metadata = dispatcher.ParseMetadata("{}");

        // Assert
        metadata.Queue.Should().Be("default");
        metadata.Priority.Should().Be(10);
    }

    [Fact]
    public void ParseMetadata_ShouldReturnDefaults_ForInvalidJson()
    {
        // Arrange
        var dispatcher = CreateDispatcher();

        // Act
        var metadata = dispatcher.ParseMetadata("invalid");

        // Assert
        metadata.Queue.Should().Be("default");
        metadata.Priority.Should().Be(10);
    }

    // Test job class
    public class TestJob : IChokaQJob
    {
        public string Id { get; set; } = "";
        public string Message { get; set; } = "";
    }
}
