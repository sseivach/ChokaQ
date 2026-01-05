namespace ChokaQ.Core.Workers;

public interface IWorkerManager
{
    /// <summary>
    /// Gets the current number of active background workers.
    /// </summary>
    int ActiveWorkers { get; }

    /// <summary>
    /// Dynamically scales the number of workers up or down.
    /// </summary>
    /// <param name="count">The target number of workers.</param>
    void UpdateWorkerCount(int count);
}