namespace ChokaQ.Abstractions.Entities;

public class QueueEntity
{
    public string Name { get; set; } = string.Empty;
    public bool IsPaused { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime LastUpdatedUtc { get; set; }
    public int? ZombieTimeoutSeconds { get; set; }

    // --- Runtime Statistics (Populated by Dapper via GROUP BY) ---
    // Этих колонок нет в таблице Queues, но они прилетают из SQL-запроса статистики.

    public int PendingCount { get; set; }
    public int FetchedCount { get; set; }
    public int ProcessingCount { get; set; }
    public int SucceededCount { get; set; }
    public int FailedCount { get; set; }

    // Для расчета Duration в UI
    public DateTime? FirstJobAtUtc { get; set; }
    public DateTime? LastJobAtUtc { get; set; }
}