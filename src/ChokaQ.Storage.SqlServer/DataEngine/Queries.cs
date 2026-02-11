namespace ChokaQ.Storage.SqlServer.DataEngine;

/// <summary>
/// Contains all SQL query templates for ChokaQ storage operations.
/// Queries are initialized once with schema name and cached for the application lifetime.
/// </summary>
internal sealed class Queries
{
    // ========================================================================
    // CORE OPERATIONS
    // ========================================================================

    public readonly string CheckIdempotency;
    public readonly string EnqueueJob;
    public readonly string FetchNextBatch;
    public readonly string MarkAsProcessing;
    public readonly string KeepAlive;
    public readonly string GetJob;
    public readonly string ReleaseJob;

    // ========================================================================
    // ARCHIVE OPERATIONS
    // ========================================================================

    public readonly string ArchiveSucceeded;
    public readonly string MoveToDLQ;
    public readonly string Resurrect;
    public readonly string ResurrectBatch;
    public readonly string RescheduleForRetry;

    // ========================================================================
    // DIVINE MODE (Admin Operations)
    // ========================================================================

    public readonly string UpdateJobData;
    public readonly string PurgeDLQ;
    public readonly string PurgeArchive;
    public readonly string UpdateDLQData;

    // ========================================================================
    // OBSERVABILITY (Dashboard)
    // ========================================================================

    public readonly string GetSummaryStats;
    public readonly string GetQueueStats;
    public readonly string GetActiveJobs;
    public readonly string GetArchiveJobs;
    public readonly string GetArchiveJob;
    public readonly string GetDLQJobs;
    public readonly string GetDLQJob;

    // ========================================================================
    // QUEUE MANAGEMENT
    // ========================================================================

    public readonly string GetQueues;
    public readonly string SetQueuePaused;
    public readonly string SetQueueZombieTimeout;
    public readonly string SetQueueActive;

    // ========================================================================
    // ZOMBIE DETECTION
    // ========================================================================

    public readonly string ArchiveZombies;

    // ========================================================================
    // HISTORY
    // ========================================================================

    public readonly string GetArchivePaged;
    public readonly string GetArchiveCount;
    public readonly string GetDLQPaged;
    public readonly string GetDLQCount;

    public Queries(string schema)
    {
        // CORE OPERATIONS
        CheckIdempotency = $"SELECT [Id] FROM [{schema}].[JobsHot] WHERE [IdempotencyKey] = @Key";

        EnqueueJob = $@"
            INSERT INTO [{schema}].[JobsHot] 
            ([Id], [Queue], [Type], [Payload], [Tags], [IdempotencyKey], [Priority], [Status], 
             [AttemptCount], [ScheduledAtUtc], [CreatedAtUtc], [LastUpdatedUtc], [CreatedBy])
            VALUES 
            (@Id, @Queue, @Type, @Payload, @Tags, @IdempotencyKey, @Priority, 0,
             0, @ScheduledAt, SYSUTCDATETIME(), SYSUTCDATETIME(), @CreatedBy);
            
            -- Ensure queue exists in config
            IF NOT EXISTS (SELECT 1 FROM [{schema}].[Queues] WHERE [Name] = @Queue)
                INSERT INTO [{schema}].[Queues] ([Name], [IsPaused], [IsActive], [LastUpdatedUtc])
                VALUES (@Queue, 0, 1, SYSUTCDATETIME());
            
            -- Ensure stats row exists (MERGE for atomicity)
            MERGE [{schema}].[StatsSummary] AS target
            USING (SELECT @Queue AS Queue) AS source
            ON target.[Queue] = source.Queue
            WHEN NOT MATCHED THEN
                INSERT ([Queue], [SucceededTotal], [FailedTotal], [RetriedTotal])
                VALUES (@Queue, 0, 0, 0);";

        FetchNextBatch = $@"
            WITH CTE AS (
                SELECT TOP (@Limit) h.*
                FROM [{schema}].[JobsHot] h WITH (UPDLOCK, READPAST)
                LEFT JOIN [{schema}].[Queues] q ON q.[Name] = h.[Queue]
                WHERE h.[Status] = 0
                  AND (h.[ScheduledAtUtc] IS NULL OR h.[ScheduledAtUtc] <= SYSUTCDATETIME())
                  AND (q.[IsPaused] = 0 OR q.[IsPaused] IS NULL)
                  AND (q.[IsActive] = 1 OR q.[IsActive] IS NULL)
                  {{QUEUE_FILTER}}
                ORDER BY h.[Priority] DESC, ISNULL(h.[ScheduledAtUtc], h.[CreatedAtUtc]) ASC
            )
            UPDATE CTE 
            SET [Status] = 1,
                [WorkerId] = @WorkerId,
                [AttemptCount] = [AttemptCount] + 1,
                [LastUpdatedUtc] = SYSUTCDATETIME()
            OUTPUT inserted.*;";

        MarkAsProcessing = $@"
            UPDATE [{schema}].[JobsHot]
            SET [Status] = 2,
                [StartedAtUtc] = SYSUTCDATETIME(),
                [HeartbeatUtc] = SYSUTCDATETIME(),
                [LastUpdatedUtc] = SYSUTCDATETIME()
            WHERE [Id] = @Id";

        KeepAlive = $@"
            UPDATE [{schema}].[JobsHot]
            SET [HeartbeatUtc] = SYSUTCDATETIME(),
                [LastUpdatedUtc] = SYSUTCDATETIME()
            WHERE [Id] = @Id";

        GetJob = $"SELECT * FROM [{schema}].[JobsHot] WHERE [Id] = @Id";

        // Reverts a job from Fetched (1) to Pending (0).
        // Decrements AttemptCount because the execution did not actually start.
        ReleaseJob = $@"
            UPDATE [{schema}].[JobsHot]
            SET [Status] = 0,
                [WorkerId] = NULL,
                [AttemptCount] = CASE WHEN [AttemptCount] > 0 THEN [AttemptCount] - 1 ELSE 0 END,
                [LastUpdatedUtc] = SYSUTCDATETIME()
            WHERE [Id] = @Id;";

        // ARCHIVE OPERATIONS
        ArchiveSucceeded = $@"
            DECLARE @Queue varchar(255);
            SELECT @Queue = [Queue] FROM [{schema}].[JobsHot] WHERE [Id] = @Id;

            -- 1. Archive to JobsArchive using OUTPUT from DELETE
            INSERT INTO [{schema}].[JobsArchive]
            ([Id], [Queue], [Type], [Payload], [Tags], [AttemptCount], [WorkerId], 
             [CreatedBy], [LastModifiedBy], [CreatedAtUtc], [StartedAtUtc], [FinishedAtUtc], [DurationMs])
            SELECT [Id], [Queue], [Type], [Payload], [Tags], [AttemptCount], [WorkerId],
                   [CreatedBy], [LastModifiedBy], [CreatedAtUtc], [StartedAtUtc], SYSUTCDATETIME(), @DurationMs
            FROM [{schema}].[JobsHot]
            WHERE [Id] = @Id;

            -- 2. Delete from Hot
            DELETE FROM [{schema}].[JobsHot] WHERE [Id] = @Id;

            -- 3. Update stats (MERGE for upsert)
            MERGE [{schema}].[StatsSummary] AS target
            USING (SELECT @Queue AS Queue) AS source
            ON target.[Queue] = source.Queue
            WHEN MATCHED THEN
                UPDATE SET [SucceededTotal] = [SucceededTotal] + 1,
                           [LastActivityUtc] = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT ([Queue], [SucceededTotal], [FailedTotal], [RetriedTotal], [LastActivityUtc])
                VALUES (@Queue, 1, 0, 0, SYSUTCDATETIME());";

        MoveToDLQ = $@"
            DECLARE @Queue varchar(255);
            SELECT @Queue = [Queue] FROM [{schema}].[JobsHot] WHERE [Id] = @Id;

            IF @Queue IS NULL RETURN;

            -- 1. Insert into DLQ
            INSERT INTO [{schema}].[JobsDLQ]
            ([Id], [Queue], [Type], [Payload], [Tags], [FailureReason], [ErrorDetails], [AttemptCount],
             [WorkerId], [CreatedBy], [LastModifiedBy], [CreatedAtUtc], [FailedAtUtc])
            SELECT [Id], [Queue], [Type], [Payload], [Tags], @Reason, @Error, [AttemptCount],
                   [WorkerId], [CreatedBy], [LastModifiedBy], [CreatedAtUtc], SYSUTCDATETIME()
            FROM [{schema}].[JobsHot]
            WHERE [Id] = @Id;

            -- 2. Delete from Hot
            DELETE FROM [{schema}].[JobsHot] WHERE [Id] = @Id;

            -- 3. Update stats
            MERGE [{schema}].[StatsSummary] AS target
            USING (SELECT @Queue AS Queue) AS source
            ON target.[Queue] = source.Queue
            WHEN MATCHED THEN
                UPDATE SET [FailedTotal] = [FailedTotal] + 1,
                           [LastActivityUtc] = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT ([Queue], [SucceededTotal], [FailedTotal], [RetriedTotal], [LastActivityUtc])
                VALUES (@Queue, 0, 1, 0, SYSUTCDATETIME());";

        Resurrect = $@"
            DECLARE @Queue varchar(255);
            SELECT @Queue = [Queue] FROM [{schema}].[JobsDLQ] WHERE [Id] = @Id;

            -- 1. Move to Hot (reset state)
            INSERT INTO [{schema}].[JobsHot]
            ([Id], [Queue], [Type], [Payload], [Tags], [Priority], [Status], [AttemptCount],
             [CreatedAtUtc], [LastUpdatedUtc], [CreatedBy], [LastModifiedBy])
            SELECT [Id], [Queue], [Type], 
                   ISNULL(@NewPayload, [Payload]), 
                   ISNULL(@NewTags, [Tags]), 
                   ISNULL(@NewPriority, 10), 
                   0, 0,
                   [CreatedAtUtc], SYSUTCDATETIME(), [CreatedBy], @ResurrectedBy
            FROM [{schema}].[JobsDLQ]
            WHERE [Id] = @Id;

            -- 2. Remove from DLQ
            DELETE FROM [{schema}].[JobsDLQ] WHERE [Id] = @Id;

            -- 3. Decrement failed stats
            UPDATE [{schema}].[StatsSummary]
            SET [FailedTotal] = CASE WHEN [FailedTotal] > 0 THEN [FailedTotal] - 1 ELSE 0 END,
                [LastActivityUtc] = SYSUTCDATETIME()
            WHERE [Queue] = @Queue;";

        ResurrectBatch = $@"
                -- Move batch to Hot
                INSERT INTO [{schema}].[JobsHot]
                ([Id], [Queue], [Type], [Payload], [Tags], [Priority], [Status], [AttemptCount],
                 [CreatedAtUtc], [LastUpdatedUtc], [CreatedBy], [LastModifiedBy])
                SELECT [Id], [Queue], [Type], [Payload], [Tags], 10, 0, 0,
                       [CreatedAtUtc], SYSUTCDATETIME(), [CreatedBy], @ResurrectedBy
                FROM [{schema}].[JobsDLQ]
                WHERE [Id] IN @Ids;

                -- Remove from DLQ
                DELETE FROM [{schema}].[JobsDLQ] WHERE [Id] IN @Ids;";

        RescheduleForRetry = $@"
            DECLARE @Queue varchar(255);
            SELECT @Queue = [Queue] FROM [{schema}].[JobsHot] WHERE [Id] = @Id;

            UPDATE [{schema}].[JobsHot]
            SET [Status] = 0,
                [AttemptCount] = @Attempt,
                [ScheduledAtUtc] = @ScheduledAt,
                [WorkerId] = NULL,
                [HeartbeatUtc] = NULL,
                [LastUpdatedUtc] = SYSUTCDATETIME()
            WHERE [Id] = @Id;

            -- Increment retry counter (MERGE for upsert)
            MERGE [{schema}].[StatsSummary] AS target
            USING (SELECT @Queue AS Queue) AS source
            ON target.[Queue] = source.Queue
            WHEN MATCHED THEN
                UPDATE SET [RetriedTotal] = [RetriedTotal] + 1,
                           [LastActivityUtc] = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT ([Queue], [SucceededTotal], [FailedTotal], [RetriedTotal], [LastActivityUtc])
                VALUES (@Queue, 0, 0, 1, SYSUTCDATETIME());";

        // DIVINE MODE
        UpdateJobData = $@"
            UPDATE [{schema}].[JobsHot]
            SET [Payload] = ISNULL(@Payload, [Payload]),
                [Tags] = ISNULL(@Tags, [Tags]),
                [Priority] = ISNULL(@Priority, [Priority]),
                [LastModifiedBy] = @ModifiedBy,
                [LastUpdatedUtc] = SYSUTCDATETIME()
            WHERE [Id] = @Id AND [Status] = 0";

        PurgeDLQ = $@"
            DELETE FROM [{schema}].[JobsDLQ] 
            WHERE [Id] IN @Ids;

            UPDATE s
            SET s.[FailedTotal] = (
                SELECT COUNT(1) 
                FROM [{schema}].[JobsDLQ] d WITH (NOLOCK)
                WHERE d.[Queue] = s.[Queue]
            ),
            s.[LastActivityUtc] = SYSUTCDATETIME()
            FROM [{schema}].[StatsSummary] s;";


        PurgeArchive = $@"
            DECLARE @DeletedIds TABLE (Id nvarchar(50));

            DELETE FROM [{schema}].[JobsArchive] 
            OUTPUT deleted.Id INTO @DeletedIds
            WHERE [FinishedAtUtc] < @CutOff;

            IF NOT EXISTS (SELECT 1 FROM @DeletedIds) RETURN;

            SET NOCOUNT ON; 

            UPDATE s
            SET s.[SucceededTotal] = (
                SELECT COUNT(1) 
                FROM [{schema}].[JobsArchive] a WITH (NOLOCK)
                WHERE a.[Queue] = s.[Queue]
            ),
            s.[LastActivityUtc] = SYSUTCDATETIME()
            FROM [{schema}].[StatsSummary] s;";

        UpdateDLQData = $@"
            UPDATE [{schema}].[JobsDLQ]
            SET [Payload] = ISNULL(@Payload, [Payload]),
                [Tags] = ISNULL(@Tags, [Tags]),
                [LastModifiedBy] = @ModifiedBy
            WHERE [Id] = @Id";

        // OBSERVABILITY
        GetSummaryStats = $@"
            SELECT 
                CAST(NULL AS NVARCHAR(100)) AS [Queue],
                (SELECT COUNT(1) FROM [{schema}].[JobsHot] WITH (NOLOCK) WHERE [Status] = 0) AS [Pending],
                (SELECT COUNT(1) FROM [{schema}].[JobsHot] WITH (NOLOCK) WHERE [Status] = 1) AS [Fetched],
                (SELECT COUNT(1) FROM [{schema}].[JobsHot] WITH (NOLOCK) WHERE [Status] = 2) AS [Processing],
                (SELECT ISNULL(SUM([SucceededTotal]), 0) FROM [{schema}].[StatsSummary] WITH (NOLOCK)) AS [SucceededTotal],
                (SELECT ISNULL(SUM([FailedTotal]), 0) FROM [{schema}].[StatsSummary] WITH (NOLOCK)) AS [FailedTotal],
                (SELECT ISNULL(SUM([RetriedTotal]), 0) FROM [{schema}].[StatsSummary] WITH (NOLOCK)) AS [RetriedTotal],
                CAST(
                    (SELECT COUNT(1) FROM [{schema}].[JobsHot] WITH (NOLOCK)) + 
                    (SELECT COUNT(1) FROM [{schema}].[JobsArchive] WITH (NOLOCK)) + 
                    (SELECT COUNT(1) FROM [{schema}].[JobsDLQ] WITH (NOLOCK)) 
                AS BIGINT) AS [Total],
                (SELECT MAX([LastActivityUtc]) FROM [{schema}].[StatsSummary] WITH (NOLOCK)) AS [LastActivityUtc]";

        GetQueueStats = $@"
            SELECT 
                q.[Name] AS [Queue],
                (SELECT COUNT(1) FROM [{schema}].[JobsHot] WITH (NOLOCK) WHERE [Queue] = q.[Name] AND [Status] = 0) AS [Pending],
                (SELECT COUNT(1) FROM [{schema}].[JobsHot] WITH (NOLOCK) WHERE [Queue] = q.[Name] AND [Status] = 1) AS [Fetched],
                (SELECT COUNT(1) FROM [{schema}].[JobsHot] WITH (NOLOCK) WHERE [Queue] = q.[Name] AND [Status] = 2) AS [Processing],
                ISNULL(s.[SucceededTotal], 0) AS [SucceededTotal],
                ISNULL(s.[FailedTotal], 0) AS [FailedTotal],
                ISNULL(s.[RetriedTotal], 0) AS [RetriedTotal],
                CAST(
                    (SELECT COUNT(1) FROM [{schema}].[JobsHot] WITH (NOLOCK) WHERE [Queue] = q.[Name]) + 
                    (SELECT COUNT(1) FROM [{schema}].[JobsArchive] WITH (NOLOCK) WHERE [Queue] = q.[Name]) + 
                    (SELECT COUNT(1) FROM [{schema}].[JobsDLQ] WITH (NOLOCK) WHERE [Queue] = q.[Name])
                AS BIGINT) AS [Total],
                s.[LastActivityUtc]
            FROM [{schema}].[Queues] q WITH (NOLOCK)
            LEFT JOIN [{schema}].[StatsSummary] s WITH (NOLOCK) ON s.[Queue] = q.[Name]
            ORDER BY q.[Name]";

        GetActiveJobs = $@"
            SELECT TOP (@Limit) * 
            FROM [{schema}].[JobsHot] WITH (NOLOCK)
            {{WHERE_CLAUSE}}
            ORDER BY [CreatedAtUtc] DESC";

        GetArchiveJobs = $@"
            SELECT TOP (@Limit) * 
            FROM [{schema}].[JobsArchive] WITH (NOLOCK)
            {{WHERE_CLAUSE}}
            ORDER BY [FinishedAtUtc] DESC";

        GetArchiveJob = $"SELECT * FROM [{schema}].[JobsArchive] WITH (NOLOCK) WHERE [Id] = @Id";

        GetDLQJobs = $@"
            SELECT TOP (@Limit) * 
            FROM [{schema}].[JobsDLQ] WITH (NOLOCK)
            {{WHERE_CLAUSE}}
            ORDER BY [FailedAtUtc] DESC";

        GetDLQJob = $"SELECT * FROM [{schema}].[JobsDLQ] WITH (NOLOCK) WHERE [Id] = @Id";

        // QUEUE MANAGEMENT
        GetQueues = $@"
            SELECT q.[Name], q.[IsPaused], q.[IsActive], q.[ZombieTimeoutSeconds], q.[LastUpdatedUtc]
            FROM [{schema}].[Queues] q WITH (NOLOCK)
            ORDER BY q.[Name]";

        SetQueuePaused = $@"
            MERGE [{schema}].[Queues] AS t
            USING (SELECT @Name AS Name) AS s ON (t.[Name] = s.Name)
            WHEN MATCHED THEN 
                UPDATE SET [IsPaused] = @IsPaused, [LastUpdatedUtc] = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN 
                INSERT ([Name], [IsPaused], [IsActive], [LastUpdatedUtc]) 
                VALUES (@Name, @IsPaused, 1, SYSUTCDATETIME());";

        SetQueueZombieTimeout = $@"
            MERGE [{schema}].[Queues] AS t
            USING (SELECT @Name AS Name) AS s ON (t.[Name] = s.Name)
            WHEN MATCHED THEN 
                UPDATE SET [ZombieTimeoutSeconds] = @Timeout, [LastUpdatedUtc] = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN 
                INSERT ([Name], [IsPaused], [IsActive], [ZombieTimeoutSeconds], [LastUpdatedUtc]) 
                VALUES (@Name, 0, 1, @Timeout, SYSUTCDATETIME());";

        SetQueueActive = $@"
            UPDATE [{schema}].[Queues]
            SET [IsActive] = @IsActive, 
                [IsPaused] = 1,
                [LastUpdatedUtc] = SYSUTCDATETIME()
            WHERE [Name] = @Name";

        // ZOMBIE DETECTION
        ArchiveZombies = $@"
            DECLARE @ZombieIds TABLE (Id varchar(50), Queue varchar(255));

            -- Find zombies (Processing or Fetched with expired heartbeat)
            INSERT INTO @ZombieIds (Id, Queue)
            SELECT h.[Id], h.[Queue]
            FROM [{schema}].[JobsHot] h
            LEFT JOIN [{schema}].[Queues] q ON q.[Name] = h.[Queue]
            WHERE h.[Status] IN (1, 2)
              AND DATEDIFF(SECOND, ISNULL(h.[HeartbeatUtc], h.[LastUpdatedUtc]), SYSUTCDATETIME()) 
                  > ISNULL(q.[ZombieTimeoutSeconds], @GlobalTimeout);

            -- Archive to DLQ
            INSERT INTO [{schema}].[JobsDLQ]
            ([Id], [Queue], [Type], [Payload], [Tags], [FailureReason], [ErrorDetails], [AttemptCount],
             [WorkerId], [CreatedBy], [LastModifiedBy], [CreatedAtUtc], [FailedAtUtc])
            SELECT h.[Id], h.[Queue], h.[Type], h.[Payload], h.[Tags], 
                   2, -- Zombie
                   'Zombie: Worker heartbeat expired',
                   h.[AttemptCount], h.[WorkerId], h.[CreatedBy], h.[LastModifiedBy], 
                   h.[CreatedAtUtc], SYSUTCDATETIME()
            FROM [{schema}].[JobsHot] h
            INNER JOIN @ZombieIds z ON z.Id = h.Id;

            -- Delete from Hot
            DELETE FROM [{schema}].[JobsHot] 
            WHERE [Id] IN (SELECT Id FROM @ZombieIds);

            -- Update stats per queue
            UPDATE s
            SET s.[FailedTotal] = s.[FailedTotal] + counts.ZombieCount,
                s.[LastActivityUtc] = SYSUTCDATETIME()
            FROM [{schema}].[StatsSummary] s
            INNER JOIN (
                SELECT Queue, COUNT(*) as ZombieCount FROM @ZombieIds GROUP BY Queue
            ) counts ON counts.Queue = s.[Queue];

            SELECT COUNT(*) FROM @ZombieIds;";

        // HISTORY - 1. ARCHIVE PAGED
        // Note: {ORDER_BY} and {WHERE_CLAUSE} are replaced at runtime in SqlJobStorage
        GetArchivePaged = $@"
            SELECT * FROM [{schema}].[JobsArchive] WITH (NOLOCK)
            {{WHERE_CLAUSE}}
            {{ORDER_BY}}
            OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY";

        GetArchiveCount = $@"
            SELECT COUNT(1) 
            FROM [{schema}].[JobsArchive] WITH (NOLOCK)
            {{WHERE_CLAUSE}}";

        // HISTORY - 2. DLQ PAGED
        GetDLQPaged = $@"
            SELECT * FROM [{schema}].[JobsDLQ] WITH (NOLOCK)
            {{WHERE_CLAUSE}}
            {{ORDER_BY}}
            OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY";

        GetDLQCount = $@"
            SELECT COUNT(1) 
            FROM [{schema}].[JobsDLQ] WITH (NOLOCK)
            {{WHERE_CLAUSE}}";
    }
}
