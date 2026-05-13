using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Abstractions.Workers;
using ChokaQ.Abstractions.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using System.Text.Json;

namespace ChokaQ.TheDeck.Hubs;

/// <summary>
/// SignalR Hub for The Deck UI.
/// Provides real-time bidirectional communication and command handling.
/// </summary>
public class ChokaQHub : Hub
{
    private const int MaxJobIdLength = 50;
    private const int MaxQueueNameLength = 255;
    private const int MaxJobTypeLength = 255;
    private const int MaxTagsLength = 1000;
    private const int MaxSearchTermLength = 1000;
    private const int MaxPayloadLength = 1_000_000;
    private const int MaxBatchSize = 1000;
    private const int MaxPriority = 1_000_000;
    private const int MaxQueueTimeoutSeconds = 86_400;
    private const int MaxQueueWorkers = 10_000;

    private readonly IWorkerManager _workerManager;
    private readonly IJobStorage _storage;
    private readonly ILogger<ChokaQHub> _logger;
    private readonly IAuthorizationService _authorizationService;
    private readonly ChokaQTheDeckOptions _options;

    public ChokaQHub(
        IWorkerManager workerManager,
        IJobStorage storage,
        ILogger<ChokaQHub> logger,
        IAuthorizationService authorizationService,
        ChokaQTheDeckOptions options)
    {
        _workerManager = workerManager;
        _storage = storage;
        _logger = logger;
        _authorizationService = authorizationService;
        _options = options;
    }

    public async Task CancelJob(string? jobId)
    {
        if (!IsValidJobId(jobId, nameof(CancelJob)))
            return;
        if (!await CanRunDestructiveCommandAsync(nameof(CancelJob)))
            return;

        var id = jobId!;
        var actor = GetActor();
        _logger.LogInformation("TheDeck: CancelJob requested for {JobId}", id);
        await _workerManager.CancelJobAsync(id, actor);
    }

    public async Task RestartJob(string? jobId)
    {
        if (!IsValidJobId(jobId, nameof(RestartJob)))
            return;
        if (!await CanRunDestructiveCommandAsync(nameof(RestartJob)))
            return;

        var id = jobId!;
        var actor = GetActor();
        _logger.LogInformation("TheDeck: RestartJob requested for {JobId}", id);
        await _workerManager.RestartJobAsync(id, actor);
    }

    public async Task ResurrectJob(string? jobId, string? newPayload = null, int? newPriority = null)
    {
        if (!IsValidJobId(jobId, nameof(ResurrectJob)) ||
            !IsValidPayload(newPayload, nameof(ResurrectJob)) ||
            !IsValidPriority(newPriority, nameof(ResurrectJob)))
        {
            return;
        }
        if (!await CanRunDestructiveCommandAsync(nameof(ResurrectJob)))
            return;

        var id = jobId!;
        var actor = GetActor();
        _logger.LogInformation("TheDeck: ResurrectJob requested for {JobId}", id);
        var updates = new JobDataUpdateDto(newPayload, null, newPriority);
        await _storage.ResurrectAsync(id, updates.HasChanges ? updates : null, actor);
    }

    public async Task<bool> RepairAndRequeueDLQJob(string? jobId, string? newPayload, string? newTags, int? newPriority)
    {
        if (!IsValidJobId(jobId, nameof(RepairAndRequeueDLQJob)) ||
            !IsValidPayload(newPayload, nameof(RepairAndRequeueDLQJob)) ||
            !IsValidTags(newTags, nameof(RepairAndRequeueDLQJob)) ||
            !IsValidPriority(newPriority, nameof(RepairAndRequeueDLQJob)))
        {
            return false;
        }
        if (!await CanRunDestructiveCommandAsync(nameof(RepairAndRequeueDLQJob)))
            return false;

        var id = jobId!;
        var actor = GetActor();
        var updates = new JobDataUpdateDto(newPayload, newTags, newPriority);

        _logger.LogInformation("TheDeck: RepairAndRequeueDLQJob requested for {JobId}", id);

        // This is the safe poison-pill recovery path: validate JSON at the Hub boundary, then let
        // storage repair and move the DLQ row in one atomic operation. Operators should not need
        // to remember a fragile two-step "save, then retry" sequence during an incident.
        var moved = await _storage.RepairAndRequeueDLQAsync(id, updates, actor);
        if (!moved)
        {
            _logger.LogWarning("TheDeck: RepairAndRequeueDLQJob failed. Job {JobId} was not found in DLQ.", id);
        }

        return moved;
    }

    public async Task ToggleQueue(string? queueName, bool pause)
    {
        if (!IsValidQueueName(queueName, nameof(ToggleQueue)))
            return;
        if (!await CanRunDestructiveCommandAsync(nameof(ToggleQueue)))
            return;

        var queue = queueName!;
        _logger.LogInformation("TheDeck: ToggleQueue {Queue} -> Paused={Pause}", queue, pause);
        await _storage.SetQueuePausedAsync(queue, pause);
    }

    public async Task SetPriority(string? jobId, int priority)
    {
        if (!IsValidJobId(jobId, nameof(SetPriority)) ||
            !IsValidPriority(priority, nameof(SetPriority)))
        {
            return;
        }
        if (!await CanRunDestructiveCommandAsync(nameof(SetPriority)))
            return;

        var id = jobId!;
        var actor = GetActor();
        _logger.LogInformation("TheDeck: SetPriority {JobId} -> {Priority}", id, priority);
        await _workerManager.SetJobPriorityAsync(id, priority, actor);
    }

    public async Task UpdateQueueTimeout(string? queueName, int? timeoutSeconds)
    {
        if (!IsValidQueueName(queueName, nameof(UpdateQueueTimeout)) ||
            !IsValidQueueTimeout(timeoutSeconds, nameof(UpdateQueueTimeout)))
        {
            return;
        }
        if (!await CanRunDestructiveCommandAsync(nameof(UpdateQueueTimeout)))
            return;

        var queue = queueName!;
        _logger.LogInformation("TheDeck: UpdateQueueTimeout {Queue} -> {Timeout}s", queue, timeoutSeconds);
        await _storage.SetQueueZombieTimeoutAsync(queue, timeoutSeconds);
    }

    public async Task UpdateQueueMaxWorkers(string? queueName, int? maxWorkers)
    {
        if (!IsValidQueueName(queueName, nameof(UpdateQueueMaxWorkers)) ||
            !IsValidQueueWorkers(maxWorkers, nameof(UpdateQueueMaxWorkers)))
        {
            return;
        }
        if (!await CanRunDestructiveCommandAsync(nameof(UpdateQueueMaxWorkers)))
            return;

        var queue = queueName!;
        _logger.LogInformation("TheDeck: UpdateQueueMaxWorkers {Queue} -> {Limit}", queue, maxWorkers);
        await _storage.SetQueueMaxWorkersAsync(queue, maxWorkers);
    }

    public async Task PurgeDLQ(string[]? jobIds)
    {
        var normalizedIds = NormalizeJobIdBatch(jobIds, nameof(PurgeDLQ));
        if (normalizedIds is null)
            return;
        if (!await CanRunDestructiveCommandAsync(nameof(PurgeDLQ)))
            return;

        _logger.LogWarning("TheDeck: PurgeDLQ requested for {Count} jobs", normalizedIds.Length);
        await _storage.PurgeDLQAsync(normalizedIds);
    }

    public async Task<bool> EditJob(string? jobId, string? newPayload, string? newTags, int? newPriority)
    {
        if (!IsValidJobId(jobId, nameof(EditJob)) ||
            !IsValidPayload(newPayload, nameof(EditJob)) ||
            !IsValidTags(newTags, nameof(EditJob)) ||
            !IsValidPriority(newPriority, nameof(EditJob)))
        {
            return false;
        }
        if (!await CanRunDestructiveCommandAsync(nameof(EditJob)))
            return false;

        var id = jobId!;
        var actor = GetActor();
        _logger.LogInformation("TheDeck: EditJob requested for {JobId}", id);

        // Create DTO with changes
        var updates = new JobDataUpdateDto(newPayload, newTags, newPriority);

        // 1. Try to update in Active (Hot) Storage first
        bool updated = await _storage.UpdateJobDataAsync(id, updates, actor);

        // 2. If not found in Active, try to find and update in DLQ (Morgue)
        if (!updated)
        {
            updated = await _storage.UpdateDLQJobDataAsync(id, updates, actor);
        }

        if (!updated)
        {
            _logger.LogWarning("TheDeck: EditJob failed. Job {JobId} not found in Hot or DLQ.", id);
        }

        return updated;
    }

    public async Task SetQueueActive(string? queueName, bool isActive)
    {
        if (!IsValidQueueName(queueName, nameof(SetQueueActive)))
            return;
        if (!await CanRunDestructiveCommandAsync(nameof(SetQueueActive)))
            return;

        var queue = queueName!;
        _logger.LogInformation("TheDeck: SetQueueActive {Queue} -> {Active}", queue, isActive);
        await _storage.SetQueueActiveAsync(queue, isActive);
    }

    // ========================================================================
    // BULK OPERATIONS
    // ========================================================================

    public async Task CancelJobs(string[]? jobIds)
    {
        var normalizedIds = NormalizeJobIdBatch(jobIds, nameof(CancelJobs));
        if (normalizedIds is null)
            return;
        if (!await CanRunDestructiveCommandAsync(nameof(CancelJobs)))
            return;

        _logger.LogInformation("TheDeck: Bulk CancelJobs requested for {Count} jobs", normalizedIds.Length);
        await _workerManager.CancelJobsAsync(normalizedIds, GetActor());
    }

    public async Task RestartJobs(string[]? jobIds)
    {
        var normalizedIds = NormalizeJobIdBatch(jobIds, nameof(RestartJobs));
        if (normalizedIds is null)
            return;
        if (!await CanRunDestructiveCommandAsync(nameof(RestartJobs)))
            return;

        _logger.LogInformation("TheDeck: Bulk RestartJobs requested for {Count} jobs", normalizedIds.Length);
        await _workerManager.RestartJobsAsync(normalizedIds, GetActor());
    }

    public async Task<int> ResurrectJobs(string[]? jobIds)
    {
        var normalizedIds = NormalizeJobIdBatch(jobIds, nameof(ResurrectJobs));
        if (normalizedIds is null)
            return 0;
        if (!await CanRunDestructiveCommandAsync(nameof(ResurrectJobs)))
            return 0;

        _logger.LogInformation("TheDeck: Bulk ResurrectJobs requested for {Count} jobs", normalizedIds.Length);
        return await _storage.ResurrectBatchAsync(normalizedIds, GetActor());
    }

    public async Task<DlqBulkOperationPreviewDto?> PreviewDLQBulkOperation(DlqBulkOperationFilterDto? filter)
    {
        var normalizedFilter = NormalizeDlqBulkFilter(filter, nameof(PreviewDLQBulkOperation));
        if (normalizedFilter is null)
            return null;

        _logger.LogInformation(
            "TheDeck: PreviewDLQBulkOperation requested. Queue={Queue}, Type={Type}, Reason={Reason}, MaxJobs={MaxJobs}",
            normalizedFilter.Queue,
            normalizedFilter.Type,
            normalizedFilter.FailureReason,
            normalizedFilter.MaxJobs);

        return await _storage.PreviewDLQBulkOperationAsync(normalizedFilter);
    }

    public async Task<int> PurgeDLQByFilter(DlqBulkOperationFilterDto? filter)
    {
        var normalizedFilter = NormalizeDlqBulkFilter(filter, nameof(PurgeDLQByFilter));
        if (normalizedFilter is null)
            return 0;
        if (!await CanRunDestructiveCommandAsync(nameof(PurgeDLQByFilter)))
            return 0;

        _logger.LogWarning(
            "TheDeck: PurgeDLQByFilter requested by {Actor}. Queue={Queue}, Type={Type}, Reason={Reason}, MaxJobs={MaxJobs}",
            GetActor(),
            normalizedFilter.Queue,
            normalizedFilter.Type,
            normalizedFilter.FailureReason,
            normalizedFilter.MaxJobs);

        return await _storage.PurgeDLQByFilterAsync(normalizedFilter);
    }

    public async Task<int> RequeueDLQByFilter(DlqBulkOperationFilterDto? filter)
    {
        var normalizedFilter = NormalizeDlqBulkFilter(filter, nameof(RequeueDLQByFilter));
        if (normalizedFilter is null)
            return 0;
        if (!await CanRunDestructiveCommandAsync(nameof(RequeueDLQByFilter)))
            return 0;

        _logger.LogInformation(
            "TheDeck: RequeueDLQByFilter requested by {Actor}. Queue={Queue}, Type={Type}, Reason={Reason}, MaxJobs={MaxJobs}",
            GetActor(),
            normalizedFilter.Queue,
            normalizedFilter.Type,
            normalizedFilter.FailureReason,
            normalizedFilter.MaxJobs);

        return await _storage.ResurrectDLQByFilterAsync(normalizedFilter, GetActor());
    }

    private async Task<bool> CanRunDestructiveCommandAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(_options.DestructiveAuthorizationPolicy))
            return true;

        var user = Context?.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        var result = await _authorizationService.AuthorizeAsync(
            user,
            resource: null,
            policyName: _options.DestructiveAuthorizationPolicy);

        if (result.Succeeded)
            return true;

        // The dashboard connection may be read-only while command execution requires a stronger
        // policy. Enforcing this at the Hub method boundary lets read-only operators observe the
        // system without giving them purge/edit/retry privileges.
        _logger.LogWarning(
            "TheDeck: Rejected {Command}. User {Actor} does not satisfy destructive policy {Policy}.",
            command,
            GetActor(),
            _options.DestructiveAuthorizationPolicy);
        return false;
    }

    private string GetActor()
    {
        var user = Context?.User;
        var name = user?.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        var subject = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                      ?? user?.FindFirst("sub")?.Value;

        // Unit tests and explicit anonymous demo mode do not always have a ClaimsPrincipal.
        // Production deployments should see a real identity here because The Deck is secured by default.
        return string.IsNullOrWhiteSpace(subject) ? "TheDeck Admin" : subject;
    }

    private bool IsValidJobId(string? jobId, string command)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > MaxJobIdLength)
        {
            _logger.LogWarning("TheDeck: Rejected {Command}. Invalid job id.", command);
            return false;
        }

        return true;
    }

    private bool IsValidQueueName(string? queueName, string command)
    {
        if (string.IsNullOrWhiteSpace(queueName) || queueName.Length > MaxQueueNameLength)
        {
            _logger.LogWarning("TheDeck: Rejected {Command}. Invalid queue name.", command);
            return false;
        }

        return true;
    }

    private bool IsValidPayload(string? payload, string command)
    {
        if (payload is null)
            return true;

        if (payload.Length > MaxPayloadLength)
        {
            _logger.LogWarning("TheDeck: Rejected {Command}. Payload exceeds {MaxLength} characters.", command, MaxPayloadLength);
            return false;
        }

        try
        {
            using var _ = JsonDocument.Parse(payload);
            return true;
        }
        catch (JsonException)
        {
            // Operator edits are powerful: a malformed payload would create a future poison pill.
            // Rejecting invalid JSON at the Hub boundary keeps storage clean and gives the UI a
            // simple false result instead of persisting data that cannot be dispatched safely.
            _logger.LogWarning("TheDeck: Rejected {Command}. Payload is not valid JSON.", command);
            return false;
        }
    }

    private bool IsValidTags(string? tags, string command)
    {
        if (tags is { Length: > MaxTagsLength })
        {
            _logger.LogWarning("TheDeck: Rejected {Command}. Tags exceed {MaxLength} characters.", command, MaxTagsLength);
            return false;
        }

        return true;
    }

    private bool IsValidPriority(int? priority, string command)
    {
        if (priority is < 0 or > MaxPriority)
        {
            _logger.LogWarning("TheDeck: Rejected {Command}. Priority is outside the supported range.", command);
            return false;
        }

        return true;
    }

    private bool IsValidQueueTimeout(int? timeoutSeconds, string command)
    {
        if (timeoutSeconds is < 1 or > MaxQueueTimeoutSeconds)
        {
            _logger.LogWarning("TheDeck: Rejected {Command}. Queue timeout is outside the supported range.", command);
            return false;
        }

        return true;
    }

    private bool IsValidQueueWorkers(int? maxWorkers, string command)
    {
        if (maxWorkers is < 1 or > MaxQueueWorkers)
        {
            _logger.LogWarning("TheDeck: Rejected {Command}. MaxWorkers is outside the supported range.", command);
            return false;
        }

        return true;
    }

    private string[]? NormalizeJobIdBatch(string[]? jobIds, string command)
    {
        if (jobIds is null || jobIds.Length == 0 || jobIds.Length > MaxBatchSize)
        {
            _logger.LogWarning("TheDeck: Rejected {Command}. Invalid batch size.", command);
            return null;
        }

        var trimmedIds = jobIds
            .Select(id => id?.Trim())
            .ToArray();

        if (trimmedIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > MaxJobIdLength))
        {
            _logger.LogWarning("TheDeck: Rejected {Command}. Batch contains invalid job ids.", command);
            return null;
        }

        var normalizedIds = trimmedIds
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Batch operations are destructive or high-impact. Deduplicating before the worker/storage
        // boundary makes the operation idempotent from the operator's point of view and prevents
        // one repeated id from inflating logs, counters, or confirmation counts.
        return normalizedIds;
    }

    private DlqBulkOperationFilterDto? NormalizeDlqBulkFilter(DlqBulkOperationFilterDto? filter, string command)
    {
        if (filter is null)
        {
            _logger.LogWarning("TheDeck: Rejected {Command}. Missing DLQ bulk filter.", command);
            return null;
        }

        var queue = NormalizeOptionalText(filter.Queue);
        var type = NormalizeOptionalText(filter.Type);
        var searchTerm = NormalizeOptionalText(filter.SearchTerm);

        if (queue is { Length: > MaxQueueNameLength })
        {
            _logger.LogWarning("TheDeck: Rejected {Command}. Queue filter exceeds {MaxLength} characters.", command, MaxQueueNameLength);
            return null;
        }

        if (type is { Length: > MaxJobTypeLength })
        {
            _logger.LogWarning("TheDeck: Rejected {Command}. Type filter exceeds {MaxLength} characters.", command, MaxJobTypeLength);
            return null;
        }

        if (searchTerm is { Length: > MaxSearchTermLength })
        {
            _logger.LogWarning("TheDeck: Rejected {Command}. Search filter exceeds {MaxLength} characters.", command, MaxSearchTermLength);
            return null;
        }

        if (filter.FailureReason.HasValue &&
            !Enum.IsDefined(typeof(FailureReason), filter.FailureReason.Value))
        {
            _logger.LogWarning("TheDeck: Rejected {Command}. FailureReason is not a known taxonomy value.", command);
            return null;
        }

        if (filter.FromUtc.HasValue && filter.ToUtc.HasValue && filter.FromUtc > filter.ToUtc)
        {
            _logger.LogWarning("TheDeck: Rejected {Command}. Date range is inverted.", command);
            return null;
        }

        if (filter.MaxJobs > MaxBatchSize)
        {
            _logger.LogWarning("TheDeck: Rejected {Command}. MaxJobs exceeds the dashboard batch cap.", command);
            return null;
        }

        var maxJobs = filter.MaxJobs <= 0
            ? DlqBulkOperationFilterDto.DefaultMaxJobs
            : Math.Clamp(filter.MaxJobs, 1, MaxBatchSize);

        // The Hub normalizes operator input before it reaches storage. That gives every backend the
        // same validated contract and prevents a custom SignalR client from bypassing UI limits.
        return new DlqBulkOperationFilterDto(
            Queue: queue,
            FailureReason: filter.FailureReason,
            Type: type,
            FromUtc: filter.FromUtc,
            ToUtc: filter.ToUtc,
            SearchTerm: searchTerm,
            MaxJobs: maxJobs);
    }

    private static string? NormalizeOptionalText(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
