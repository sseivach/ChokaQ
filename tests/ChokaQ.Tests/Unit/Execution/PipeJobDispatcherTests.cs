using ChokaQ.Abstractions.Jobs;
using ChokaQ.Core.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChokaQ.Tests.Unit.Execution;

/// <summary>
/// Unit tests for PipeJobDispatcher - delegates to single pipe handler.
/// </summary>
public class PipeJobDispatcherTests
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServiceScope _scope;
    private readonly IServiceProvider _serviceProvider;
    private readonly IChokaQPipeHandler _handler;

    public PipeJobDispatcherTests()
    {
        _scopeFactory = Substitute.For<IServiceScopeFactory>();
        _scope = Substitute.For<IServiceScope>();
        _serviceProvider = Substitute.For<IServiceProvider>();
        _handler = Substitute.For<IChokaQPipeHandler>();

        _scopeFactory.CreateScope().Returns(_scope);
        _scope.ServiceProvider.Returns(_serviceProvider);
        _serviceProvider.GetService(typeof(IChokaQPipeHandler)).Returns(_handler);

        // Setup internal JobContext via reflection
        var notifier = Substitute.For<ChokaQ.Abstractions.Notifications.IChokaQNotifier>();
        var jobContextType = typeof(PipeJobDispatcher).Assembly.GetType("ChokaQ.Core.Contexts.JobContext")!;
        var jobContext = Activator.CreateInstance(jobContextType, notifier)!;

        // Mock GetRequiredService for JobContext (concrete type)
        // Since GetRequiredService is an extension, we mock GetService
        _serviceProvider.GetService(jobContextType).Returns(jobContext);
    }

    private PipeJobDispatcher CreateDispatcher()
    {
        return new PipeJobDispatcher(_scopeFactory, NullLogger<PipeJobDispatcher>.Instance);
    }

    [Fact]
    public async Task DispatchAsync_ShouldDelegateToHandler()
    {
        // Arrange
        var dispatcher = CreateDispatcher();
        var payload = "{\"Message\":\"Hello\"}";

        // Act
        await dispatcher.DispatchAsync("job1", "test_job", payload, CancellationToken.None);

        // Assert
        await _handler.Received(1).HandleAsync("test_job", payload, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_ShouldSetJobContextId()
    {
        // Arrange
        var dispatcher = CreateDispatcher();
        var jobContextType = typeof(PipeJobDispatcher).Assembly.GetType("ChokaQ.Core.Contexts.JobContext")!;

        // Act
        await dispatcher.DispatchAsync("job123", "test_job", "{}", CancellationToken.None);

        // Assert
        _serviceProvider.Received(1).GetService(jobContextType);
    }

    [Fact]
    public async Task DispatchAsync_ShouldThrow_WhenNoHandlerRegistered()
    {
        // Arrange
        _serviceProvider.GetService(typeof(IChokaQPipeHandler)).Returns((object?)null);
        var dispatcher = CreateDispatcher();

        // Act & Assert
        await dispatcher.Invoking(d => d.DispatchAsync("job1", "test_job", "{}", CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no implementation of IChokaQPipeHandler*");
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
}
