using ChokaQ.Abstractions;
using ChokaQ.Dashboard.Models;

namespace ChokaQ.Dashboard.Services;

/// <summary>
/// The "Waiter" service. 
/// It fetches raw ingredients from Storage and plates them nicely (ViewModel) for the UI.
/// </summary>
public class DashboardService
{
    private readonly IJobStorage _storage;

    public DashboardService(IJobStorage storage)
    {
        _storage = storage;
    }

    // --- TAB 1: ACTIVE (Hot Jobs) ---
    public async Task<List<JobViewModel>> GetActiveJobsAsync(string queue, int skip = 0, int take = 50)
    {
        var entities = await _storage.GetActiveJobsAsync(queue, skip, take);
        return entities.Select(JobViewModel.FromEntity).ToList();
    }

    // --- TAB 2: HISTORY (Archive) ---
    public async Task<List<JobViewModel>> GetHistoryJobsAsync(string queue, int skip = 0, int take = 50)
    {
        var entities = await _storage.GetHistoryJobsAsync(queue, skip, take);
        return entities.Select(JobViewModel.FromSucceeded).ToList();
    }

    // --- TAB 3: MORGUE (Dead Letter Queue) ---
    public async Task<List<JobViewModel>> GetMorgueJobsAsync(string queue, int skip = 0, int take = 50)
    {
        var entities = await _storage.GetMorgueJobsAsync(queue, skip, take);
        return entities.Select(JobViewModel.FromMorgue).ToList();
    }

    // --- ACTIONS (Admin Commands) ---

    public async Task ResurrectAsync(string id)
    {
        await _storage.ResurrectJobAsync(id);
    }

    public async Task PurgeAsync(string id)
    {
        await _storage.PurgeJobAsync(id);
    }
}