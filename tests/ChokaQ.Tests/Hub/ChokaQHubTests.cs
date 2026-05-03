using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Abstractions.Workers;
using ChokaQ.TheDeck;
using ChokaQ.TheDeck.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;

namespace ChokaQ.Tests.Hub;

public class ChokaQHubTests
{
    private readonly IWorkerManager _workerManager;
    private readonly IJobStorage _storage;
    private readonly IAuthorizationService _authorizationService;
    private readonly ChokaQTheDeckOptions _options;
    private readonly ChokaQHub _hub;

    public ChokaQHubTests()
    {
        _workerManager = Substitute.For<IWorkerManager>();
        _storage = Substitute.For<IJobStorage>();
        _authorizationService = Substitute.For<IAuthorizationService>();
        _options = new ChokaQTheDeckOptions();
        var logger = NullLogger<ChokaQHub>.Instance;
        _hub = new ChokaQHub(_workerManager, _storage, logger, _authorizationService, _options);
    }

    [Fact]
    public async Task CancelJob_ShouldDelegateToWorkerManager()
    {
        // Act
        await _hub.CancelJob("job1");

        // Assert
        await _workerManager.Received(1).CancelJobAsync("job1", "TheDeck Admin");
    }

    [Fact]
    public async Task RestartJob_ShouldDelegateToWorkerManager()
    {
        // Act
        await _hub.RestartJob("job1");

        // Assert
        await _workerManager.Received(1).RestartJobAsync("job1", "TheDeck Admin");
    }

    [Fact]
    public async Task ResurrectJob_ShouldCallStorage_WithUpdates()
    {
        // Act
        await _hub.ResurrectJob("job1", """{"status":"fixed"}""", 99);

        // Assert
        await _storage.Received(1).ResurrectAsync(
            "job1",
            Arg.Is<JobDataUpdateDto>(d => d.Payload == """{"status":"fixed"}""" && d.Priority == 99),
            Arg.Any<string>());
    }

    [Fact]
    public async Task ResurrectJob_NullUpdates_ShouldCallStorage_WithoutUpdates()
    {
        // Act
        await _hub.ResurrectJob("job1");

        // Assert
        await _storage.Received(1).ResurrectAsync("job1", null, Arg.Any<string>());
    }

    [Fact]
    public async Task RepairAndRequeueDLQJob_ShouldCallStorageWithValidatedUpdates()
    {
        // Arrange
        _storage.RepairAndRequeueDLQAsync(
                Arg.Any<string>(),
                Arg.Any<JobDataUpdateDto>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _hub.RepairAndRequeueDLQJob("job1", """{"status":"fixed"}""", "fixed", 20);

        // Assert
        result.Should().BeTrue();
        await _storage.Received(1).RepairAndRequeueDLQAsync(
            "job1",
            Arg.Is<JobDataUpdateDto>(d =>
                d.Payload == """{"status":"fixed"}""" &&
                d.Tags == "fixed" &&
                d.Priority == 20),
            "TheDeck Admin",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RepairAndRequeueDLQJob_WithMalformedPayload_ShouldReject()
    {
        // Act
        var result = await _hub.RepairAndRequeueDLQJob("job1", "{not-json", null, 20);

        // Assert
        result.Should().BeFalse();
        await _storage.DidNotReceive().RepairAndRequeueDLQAsync(
            Arg.Any<string>(),
            Arg.Any<JobDataUpdateDto>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RepairAndRequeueDLQJob_WhenDestructivePolicyFails_ShouldReject()
    {
        // Arrange
        _options.DestructiveAuthorizationPolicy = "ChokaQWrite";
        _authorizationService.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Is<object?>(resource => resource == null),
                Arg.Is<string>(policy => policy == "ChokaQWrite"))
            .Returns(Task.FromResult(AuthorizationResult.Failed()));

        // Act
        var result = await _hub.RepairAndRequeueDLQJob("job1", """{"status":"fixed"}""", null, 20);

        // Assert
        result.Should().BeFalse();
        await _storage.DidNotReceive().RepairAndRequeueDLQAsync(
            Arg.Any<string>(),
            Arg.Any<JobDataUpdateDto>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ToggleQueue_ShouldCallSetQueuePausedAsync()
    {
        // Act
        await _hub.ToggleQueue("default", true);

        // Assert
        await _storage.Received(1).SetQueuePausedAsync("default", true);
    }

    [Fact]
    public async Task ToggleQueue_WithInvalidQueue_ShouldReject()
    {
        // Act
        await _hub.ToggleQueue("", true);

        // Assert
        await _storage.DidNotReceive().SetQueuePausedAsync(Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task SetPriority_ShouldDelegateToWorkerManager()
    {
        // Act
        await _hub.SetPriority("job1", 5);

        // Assert
        await _workerManager.Received(1).SetJobPriorityAsync("job1", 5, "TheDeck Admin");
    }

    [Fact]
    public async Task UpdateQueueTimeout_ShouldCallStorage()
    {
        // Act
        await _hub.UpdateQueueTimeout("default", 60);

        // Assert
        await _storage.Received(1).SetQueueZombieTimeoutAsync("default", 60);
    }

    [Fact]
    public async Task PurgeDLQ_ShouldCallStorage()
    {
        // Arrange
        var jobIds = new[] { "job1", "job2" };

        // Act
        await _hub.PurgeDLQ(jobIds);

        // Assert
        await _storage.Received(1).PurgeDLQAsync(
            Arg.Is<string[]>(ids => ids.SequenceEqual(jobIds)));
    }

    [Fact]
    public async Task EditJob_ShouldTryHotFirst_ThenDLQ()
    {
        // Arrange
        _storage.UpdateJobDataAsync(Arg.Any<string>(), Arg.Any<JobDataUpdateDto>(), Arg.Any<string>())
            .Returns(false); // Not found in hot
        _storage.UpdateDLQJobDataAsync(Arg.Any<string>(), Arg.Any<JobDataUpdateDto>(), Arg.Any<string>())
            .Returns(true); // Found in DLQ

        // Act
        var result = await _hub.EditJob("job1", """{"status":"fixed"}""", "tags", 10);

        // Assert
        result.Should().BeTrue();
        await _storage.Received(1).UpdateJobDataAsync("job1", Arg.Any<JobDataUpdateDto>(), Arg.Any<string>());
        await _storage.Received(1).UpdateDLQJobDataAsync("job1", Arg.Any<JobDataUpdateDto>(), Arg.Any<string>());
    }

    [Fact]
    public async Task EditJob_WithAuthenticatedUser_ShouldUseActorForAudit()
    {
        // Arrange
        var context = Substitute.For<HubCallerContext>();
        context.User.Returns(new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "ops@example.com") },
            authenticationType: "TestAuth")));
        _hub.Context = context;

        _storage.UpdateJobDataAsync(Arg.Any<string>(), Arg.Any<JobDataUpdateDto>(), Arg.Any<string>())
            .Returns(true);

        // Act
        var result = await _hub.EditJob("job1", """{"status":"fixed"}""", "tags", 10);

        // Assert
        result.Should().BeTrue();
        await _storage.Received(1).UpdateJobDataAsync(
            "job1",
            Arg.Any<JobDataUpdateDto>(),
            "ops@example.com");
    }

    [Fact]
    public async Task EditJob_NotFound_ShouldReturnFalse()
    {
        // Arrange
        _storage.UpdateJobDataAsync(Arg.Any<string>(), Arg.Any<JobDataUpdateDto>(), Arg.Any<string>())
            .Returns(false);
        _storage.UpdateDLQJobDataAsync(Arg.Any<string>(), Arg.Any<JobDataUpdateDto>(), Arg.Any<string>())
            .Returns(false);

        // Act
        var result = await _hub.EditJob("job1", """{"status":"fixed"}""", "tags", 10);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task EditJob_WithMalformedPayload_ShouldReject()
    {
        // Act
        var result = await _hub.EditJob("job1", "{not-json", "tags", 10);

        // Assert
        result.Should().BeFalse();
        await _storage.DidNotReceive().UpdateJobDataAsync(
            Arg.Any<string>(), Arg.Any<JobDataUpdateDto>(), Arg.Any<string>());
        await _storage.DidNotReceive().UpdateDLQJobDataAsync(
            Arg.Any<string>(), Arg.Any<JobDataUpdateDto>(), Arg.Any<string>());
    }

    [Fact]
    public async Task CancelJob_WithOversizedJobId_ShouldReject()
    {
        // Act
        await _hub.CancelJob(new string('x', 51));

        // Assert
        await _workerManager.DidNotReceive().CancelJobAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task CancelJob_WhenDestructivePolicyFails_ShouldReject()
    {
        // Arrange
        _options.DestructiveAuthorizationPolicy = "ChokaQWrite";
        _authorizationService.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Is<object?>(resource => resource == null),
                Arg.Is<string>(policy => policy == "ChokaQWrite"))
            .Returns(Task.FromResult(AuthorizationResult.Failed()));

        // Act
        await _hub.CancelJob("job1");

        // Assert
        await _workerManager.DidNotReceive().CancelJobAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task CancelJob_WhenDestructivePolicySucceeds_ShouldDelegate()
    {
        // Arrange
        _options.DestructiveAuthorizationPolicy = "ChokaQWrite";
        _authorizationService.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Is<object?>(resource => resource == null),
                Arg.Is<string>(policy => policy == "ChokaQWrite"))
            .Returns(Task.FromResult(AuthorizationResult.Success()));

        // Act
        await _hub.CancelJob("job1");

        // Assert
        // Read-only dashboard access and command execution are separate concerns. This proves
        // the Hub method boundary honors the stronger destructive policy before mutating jobs.
        await _workerManager.Received(1).CancelJobAsync("job1", "TheDeck Admin");
    }

    // ========================================================================
    // BULK OPERATIONS
    // ========================================================================

    [Fact]
    public async Task CancelJobs_ShouldDelegateBatchToWorkerManager()
    {
        // Arrange
        var jobIds = new[] { "job1", "job2", "job3" };

        // Act
        await _hub.CancelJobs(jobIds);

        // Assert
        await _workerManager.Received(1).CancelJobsAsync(
            Arg.Is<string[]>(ids => ids.SequenceEqual(jobIds)),
            "TheDeck Admin");
    }

    [Fact]
    public async Task CancelJobs_WithDuplicateIds_ShouldDeduplicateBeforeDelegating()
    {
        // Arrange
        var jobIds = new[] { "job1", "job1", "job2" };

        // Act
        await _hub.CancelJobs(jobIds);

        // Assert
        // Hub validation normalizes batch commands so repeated UI selections do not inflate
        // destructive-operation counts or make downstream behavior depend on duplicate IDs.
        await _workerManager.Received(1).CancelJobsAsync(
            Arg.Is<string[]>(ids => ids.SequenceEqual(new[] { "job1", "job2" })),
            "TheDeck Admin");
    }

    [Fact]
    public async Task PurgeDLQ_WithEmptyBatch_ShouldReject()
    {
        // Act
        await _hub.PurgeDLQ(Array.Empty<string>());

        // Assert
        await _storage.DidNotReceive().PurgeDLQAsync(Arg.Any<string[]>());
    }

    [Fact]
    public async Task RestartJobs_ShouldDelegateBatchToWorkerManager()
    {
        // Arrange
        var jobIds = new[] { "job1", "job2" };

        // Act
        await _hub.RestartJobs(jobIds);

        // Assert
        await _workerManager.Received(1).RestartJobsAsync(
            Arg.Is<string[]>(ids => ids.SequenceEqual(jobIds)),
            "TheDeck Admin");
    }

    [Fact]
    public async Task ResurrectJobs_ShouldCallStorageBatchResurrect()
    {
        // Arrange
        var jobIds = new[] { "job1", "job2", "job3" };
        _storage.ResurrectBatchAsync(
                Arg.Is<string[]>(ids => ids.SequenceEqual(jobIds)),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(3);

        // Act
        var result = await _hub.ResurrectJobs(jobIds);

        // Assert
        result.Should().Be(3);
        await _storage.Received(1).ResurrectBatchAsync(
            Arg.Is<string[]>(ids => ids.SequenceEqual(jobIds)),
            "TheDeck Admin",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResurrectJobs_WithOversizedBatch_ShouldReject()
    {
        // Arrange
        var jobIds = Enumerable.Range(0, 1001).Select(i => $"job{i}").ToArray();

        // Act
        var result = await _hub.ResurrectJobs(jobIds);

        // Assert
        result.Should().Be(0);
        await _storage.DidNotReceive().ResurrectBatchAsync(
            Arg.Any<string[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PreviewDLQBulkOperation_ShouldNormalizeFilterAndCallStorage()
    {
        // Arrange
        var preview = new DlqBulkOperationPreviewDto(1, 100, new[] { "job1" });
        _storage.PreviewDLQBulkOperationAsync(
                Arg.Any<DlqBulkOperationFilterDto>(),
                Arg.Any<CancellationToken>())
            .Returns(preview);

        var filter = new DlqBulkOperationFilterDto(
            Queue: "  critical  ",
            FailureReason: FailureReason.FatalError,
            Type: " EmailJob ",
            SearchTerm: " bad ",
            MaxJobs: 100);

        // Act
        var result = await _hub.PreviewDLQBulkOperation(filter);

        // Assert
        result.Should().Be(preview);
        await _storage.Received(1).PreviewDLQBulkOperationAsync(
            Arg.Is<DlqBulkOperationFilterDto>(f =>
                f.Queue == "critical" &&
                f.Type == "EmailJob" &&
                f.SearchTerm == "bad" &&
                f.FailureReason == FailureReason.FatalError &&
                f.MaxJobs == 100),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PurgeDLQByFilter_ShouldRequireDestructivePolicy()
    {
        // Arrange
        _options.DestructiveAuthorizationPolicy = "ChokaQWrite";
        _authorizationService.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Is<object?>(resource => resource == null),
                Arg.Is<string>(policy => policy == "ChokaQWrite"))
            .Returns(Task.FromResult(AuthorizationResult.Failed()));

        var filter = new DlqBulkOperationFilterDto(Queue: "critical", MaxJobs: 10);

        // Act
        var result = await _hub.PurgeDLQByFilter(filter);

        // Assert
        result.Should().Be(0);
        await _storage.DidNotReceive().PurgeDLQByFilterAsync(
            Arg.Any<DlqBulkOperationFilterDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PurgeDLQByFilter_ShouldCallStorageWithNormalizedFilter()
    {
        // Arrange
        _storage.PurgeDLQByFilterAsync(
                Arg.Any<DlqBulkOperationFilterDto>(),
                Arg.Any<CancellationToken>())
            .Returns(5);

        var filter = new DlqBulkOperationFilterDto(
            Queue: "critical",
            FailureReason: FailureReason.Timeout,
            Type: "WebhookJob",
            MaxJobs: 5);

        // Act
        var result = await _hub.PurgeDLQByFilter(filter);

        // Assert
        result.Should().Be(5);
        await _storage.Received(1).PurgeDLQByFilterAsync(
            Arg.Is<DlqBulkOperationFilterDto>(f =>
                f.Queue == "critical" &&
                f.Type == "WebhookJob" &&
                f.FailureReason == FailureReason.Timeout &&
                f.MaxJobs == 5),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RequeueDLQByFilter_ShouldUseActorForAudit()
    {
        // Arrange
        var context = Substitute.For<HubCallerContext>();
        context.User.Returns(new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "ops@example.com") },
            authenticationType: "TestAuth")));
        _hub.Context = context;

        _storage.ResurrectDLQByFilterAsync(
                Arg.Any<DlqBulkOperationFilterDto>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(3);

        var filter = new DlqBulkOperationFilterDto(
            Queue: "critical",
            FailureReason: FailureReason.Throttled,
            MaxJobs: 3);

        // Act
        var result = await _hub.RequeueDLQByFilter(filter);

        // Assert
        result.Should().Be(3);
        await _storage.Received(1).ResurrectDLQByFilterAsync(
            Arg.Is<DlqBulkOperationFilterDto>(f => f.FailureReason == FailureReason.Throttled && f.MaxJobs == 3),
            "ops@example.com",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PreviewDLQBulkOperation_WithOversizedMaxJobs_ShouldReject()
    {
        // Arrange
        var filter = new DlqBulkOperationFilterDto(MaxJobs: 1001);

        // Act
        var result = await _hub.PreviewDLQBulkOperation(filter);

        // Assert
        result.Should().BeNull();
        await _storage.DidNotReceive().PreviewDLQBulkOperationAsync(
            Arg.Any<DlqBulkOperationFilterDto>(), Arg.Any<CancellationToken>());
    }
}
