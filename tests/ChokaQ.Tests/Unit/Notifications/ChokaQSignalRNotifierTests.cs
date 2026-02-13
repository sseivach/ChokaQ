using ChokaQ.Abstractions.DTOs;
using ChokaQ.TheDeck.Hubs;
using ChokaQ.TheDeck.Services;
using Microsoft.AspNetCore.SignalR;

namespace ChokaQ.Tests.Unit.Notifications;

public class ChokaQSignalRNotifierTests
{
    private readonly IHubContext<ChokaQHub> _hubContext;
    private readonly IHubClients _hubClients;
    private readonly IClientProxy _clientProxy;
    private readonly ChokaQSignalRNotifier _notifier;

    public ChokaQSignalRNotifierTests()
    {
        _hubContext = Substitute.For<IHubContext<ChokaQHub>>();
        _hubClients = Substitute.For<IHubClients>();
        _clientProxy = Substitute.For<IClientProxy>();

        _hubContext.Clients.Returns(_hubClients);
        _hubClients.All.Returns(_clientProxy);

        _notifier = new ChokaQSignalRNotifier(_hubContext);
    }

    [Fact]
    public async Task NotifyJobUpdatedAsync_ShouldSendToAllClients()
    {
        var update = new JobUpdateDto("j1", "t1", "q1", ChokaQ.Abstractions.Enums.JobStatus.Pending, 0, 10, null, "u1", null);
        await _notifier.NotifyJobUpdatedAsync(update);

        await _clientProxy.Received(1).SendCoreAsync("JobUpdated", Arg.Is<object[]>(args => (JobUpdateDto)args[0] == update), default);
    }

    [Fact]
    public async Task NotifyJobProgressAsync_ShouldSendCorrectMethod()
    {
        await _notifier.NotifyJobProgressAsync("j1", 50);

        await _clientProxy.Received(1).SendCoreAsync("JobProgress", Arg.Is<object[]>(args => (string)args[0] == "j1" && (int)args[1] == 50), default);
    }

    [Fact]
    public async Task NotifyJobArchivedAsync_ShouldSendCorrectMethod()
    {
        await _notifier.NotifyJobArchivedAsync("j1", "q1");

        await _clientProxy.Received(1).SendCoreAsync("JobArchived", Arg.Is<object[]>(args => (string)args[0] == "j1" && (string)args[1] == "q1"), default);
    }

    [Fact]
    public async Task NotifyJobFailedAsync_ShouldSendCorrectMethod()
    {
        await _notifier.NotifyJobFailedAsync("j1", "q1", "error");

        await _clientProxy.Received(1).SendCoreAsync("JobFailed", Arg.Is<object[]>(args => (string)args[0] == "j1" && (string)args[1] == "q1" && (string)args[2] == "error"), default);
    }

    [Fact]
    public async Task NotifyJobResurrectedAsync_ShouldSendCorrectMethod()
    {
        await _notifier.NotifyJobResurrectedAsync("j1", "q1");

        await _clientProxy.Received(1).SendCoreAsync("JobResurrected", Arg.Is<object[]>(args => (string)args[0] == "j1" && (string)args[1] == "q1"), default);
    }

    [Fact]
    public async Task NotifyJobsPurgedAsync_ShouldSendCorrectMethod()
    {
        var ids = new[] { "j1", "j2" };
        await _notifier.NotifyJobsPurgedAsync(ids, "dlq");

        await _clientProxy.Received(1).SendCoreAsync("JobsPurged", Arg.Is<object[]>(args => (string[])args[0] == ids && (string)args[1] == "dlq"), default);
    }

    [Fact]
    public async Task NotifyQueueStateChangedAsync_ShouldSendCorrectMethod()
    {
        await _notifier.NotifyQueueStateChangedAsync("q1", true);

        await _clientProxy.Received(1).SendCoreAsync("QueueStateChanged", Arg.Is<object[]>(args => (string)args[0] == "q1" && (bool)args[1] == true), default);
    }

    [Fact]
    public async Task NotifyStatsUpdatedAsync_ShouldSendCorrectMethod()
    {
        await _notifier.NotifyStatsUpdatedAsync();

        await _clientProxy.Received(1).SendCoreAsync("StatsUpdated", Arg.Any<object[]>(), default);
    }
}
