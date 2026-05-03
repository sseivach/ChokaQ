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
    public readonly string GetDLQBulkIds;
    public readonly string PurgeDLQByFilter;
    public readonly string ResurrectDLQByFilter;
    public readonly string PurgeArchive;
    public readonly string UpdateDLQData;

    // ========================================================================
    // OBSERVABILITY (Dashboard)
    // ========================================================================

    public readonly string GetSummaryStats;
    public readonly string GetQueueStats;
    public readonly string GetQueueHealth;
    public readonly string GetThroughputStats;
    public readonly string GetTopDlqErrors;
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
    public readonly string SetQueueMaxWorkers;
    public readonly string SetQueueActive;

    // ========================================================================
    // RECOVERY & ZOMBIE DETECTION
    // ========================================================================

    public readonly string RecoverAbandoned;
    public readonly string ArchiveZombies;

    // ========================================================================
    // HISTORY
    // ========================================================================

    public readonly string GetArchivePaged;
    public readonly string GetArchiveCount;
    public readonly string GetDLQPaged;
    public readonly string GetDLQCount;

    public readonly string ArchiveCancelledBatch;

    public Queries(string schema)
    {
        // CORE OPERATIONS
        // Enqueue idempotency is scoped to active work. We only check JobsHot because Archive and
        // DLQ are historical/operator records; blocking future submissions from those tables would
        // turn audit retention into accidental admission control.
        CheckIdempotency = $"SELECT [Id] FROM [{schema}].[JobsHot] WHERE [IdempotencyKey] = @Key";

        EnqueueJob = $@"
            INSERT INTO [{schema}].[JobsHot] 
            ([Id], [Queue], [Type], [Payload], [Tags], [IdempotencyKey], [Priority], [Status], 
             [AttemptCount], [ScheduledAtUtc], [CreatedAtUtc], [LastUpdatedUtc], [CreatedBy])
            VALUES 
            (@Id, @Queue, @Type, @Payload, @Tags, @IdempotencyKey, @Priority, 0,
             0, @ScheduledAt, SYSUTCDATETIME(), SYSUTCDATETIME(), @CreatedBy);
            
            -- Ensure queue exists in config (Added MaxWorkers)
            IF NOT EXISTS (SELECT 1 FROM [{schema}].[Queues] WHERE [Name] = @Queue)
                INSERT INTO [{schema}].[Queues] ([Name], [IsPaused], [IsActive], [MaxWorkers], [LastUpdatedUtc])
                VALUES (@Queue, 0, 1, NULL, SYSUTCDATETIME());
            
            -- Ensure stats row exists (MERGE for atomicity)
            MERGE [{schema}].[StatsSummary] AS target
            USING (SELECT @Queue AS Queue) AS source
            ON target.[Queue] = source.Queue
            WHEN NOT MATCHED THEN
                INSERT ([Queue], [SucceededTotal], [FailedTotal], [RetriedTotal])
                VALUES (@Queue, 0, 0, 0);";

        // THE BULKHEAD PATTERN:
        // Calculates currently active jobs per queue and also ranks candidates inside each queue.
        // The row-number cap prevents a single large fetch statement from claiming more rows
        // than the remaining per-queue worker budget.
        // ActiveCounts uses the default READ COMMITTED isolation because it participates in
        // capacity decisions. A dirty count can temporarily exceed MaxWorkers or starve a queue
        // based on work that later rolls back; telemetry queries may be approximate, fetch cannot.
        FetchNextBatch = $@"
            WITH ActiveCounts AS (
                SELECT [Queue], COUNT(1) AS CurrentActive
                FROM [{schema}].[JobsHot]
                WHERE [Status] IN (1, 2)
                GROUP BY [Queue]
            ),
            Candidates AS (
                SELECT
                    h.[Id],
                    h.[Priority],
                    ISNULL(h.[ScheduledAtUtc], h.[CreatedAtUtc]) AS SortUtc,
                    q.[MaxWorkers],
                    ISNULL(ac.CurrentActive, 0) AS CurrentActive,
                    ROW_NUMBER() OVER (
                        PARTITION BY h.[Queue]
                        ORDER BY h.[Priority] DESC, ISNULL(h.[ScheduledAtUtc], h.[CreatedAtUtc]) ASC
                    ) AS QueueRank
                FROM [{schema}].[JobsHot] h WITH (UPDLOCK, READPAST)
                LEFT JOIN [{schema}].[Queues] q ON q.[Name] = h.[Queue]
                LEFT JOIN ActiveCounts ac ON ac.[Queue] = h.[Queue]
                WHERE h.[Status] = 0
                  AND (h.[ScheduledAtUtc] IS NULL OR h.[ScheduledAtUtc] <= SYSUTCDATETIME())
                  AND (q.[IsPaused] = 0 OR q.[IsPaused] IS NULL)
                  AND (q.[IsActive] = 1 OR q.[IsActive] IS NULL)
                  {{QUEUE_FILTER}}
            ),
            Picked AS (
                SELECT TOP (@Limit) [Id]
                FROM Candidates
                WHERE [MaxWorkers] IS NULL
                   OR ([CurrentActive] + [QueueRank]) <= [MaxWorkers]
                ORDER BY [Priority] DESC, [SortUtc] ASC
            )
            UPDATE h
            SET [Status] = 1,
                [WorkerId] = @WorkerId,
                [LastUpdatedUtc] = SYSUTCDATETIME()
            OUTPUT inserted.*
            FROM [{schema}].[JobsHot] h
            INNER JOIN Picked p ON p.[Id] = h.[Id];";

        MarkAsProcessing = $@"
            -- Worker-owned Processing is a lease validation step, not just a cosmetic status update.
            -- If the row was released or reclaimed after fetch, WorkerId/Status no longer match and
            -- @@ROWCOUNT becomes 0 so the processor can skip dispatching stale in-memory work.
            -- AttemptCount advances here because Processing is the first point where user code will run.
            UPDATE [{schema}].[JobsHot]
            SET [Status] = 2,
                [AttemptCount] = [AttemptCount] + 1,
                [StartedAtUtc] = SYSUTCDATETIME(),
                [HeartbeatUtc] = SYSUTCDATETIME(),
                [LastUpdatedUtc] = SYSUTCDATETIME()
            WHERE [Id] = @Id
              AND (@WorkerId IS NULL OR ([WorkerId] = @WorkerId AND [Status] = 1));

            SELECT @@ROWCOUNT;";

        KeepAlive = $@"
            UPDATE [{schema}].[JobsHot]
            SET [HeartbeatUtc] = SYSUTCDATETIME(),
                [LastUpdatedUtc] = SYSUTCDATETIME()
            WHERE [Id] = @Id";

        GetJob = $"SELECT * FROM [{schema}].[JobsHot] WHERE [Id] = @Id";

        ReleaseJob = $@"
            UPDATE [{schema}].[JobsHot]
            SET [Status] = 0,
                [WorkerId] = NULL,
                [LastUpdatedUtc] = SYSUTCDATETIME()
            WHERE [Id] = @Id;";

        // ARCHIVE OPERATIONS
        ArchiveSucceeded = $@"
            SET XACT_ABORT ON;

            BEGIN TRY
                BEGIN TRANSACTION;

                DECLARE @Moved TABLE
                (
                    [Id] varchar(50) NOT NULL,
                    [Queue] varchar(255) NOT NULL,
                    [Type] varchar(255) NOT NULL,
                    [Payload] nvarchar(max) NULL,
                    [Tags] varchar(1000) NULL,
                    [AttemptCount] int NOT NULL,
                    [WorkerId] varchar(100) NULL,
                    [CreatedBy] varchar(100) NULL,
                    [LastModifiedBy] varchar(100) NULL,
                    [CreatedAtUtc] datetime2(7) NOT NULL,
                    [StartedAtUtc] datetime2(7) NULL
                );

                -- Delete first and capture the exact deleted row. This makes Hot -> Archive a true move:
                -- either the archive insert and counter update also commit, or the original Hot row is restored.
                -- Worker ownership is part of the predicate so a stale worker cannot finalize a row
                -- that zombie rescue or another owner has already reclaimed.
                DELETE FROM [{schema}].[JobsHot]
                OUTPUT deleted.[Id], deleted.[Queue], deleted.[Type], deleted.[Payload], deleted.[Tags],
                       deleted.[AttemptCount], deleted.[WorkerId], deleted.[CreatedBy], deleted.[LastModifiedBy],
                       deleted.[CreatedAtUtc], deleted.[StartedAtUtc]
                INTO @Moved
                WHERE [Id] = @Id
                  AND (@WorkerId IS NULL OR [WorkerId] = @WorkerId);

                INSERT INTO [{schema}].[JobsArchive]
                ([Id], [Queue], [Type], [Payload], [Tags], [AttemptCount], [WorkerId],
                 [CreatedBy], [LastModifiedBy], [CreatedAtUtc], [StartedAtUtc], [FinishedAtUtc], [DurationMs])
                SELECT [Id], [Queue], [Type], [Payload], [Tags], [AttemptCount], [WorkerId],
                       [CreatedBy], [LastModifiedBy], [CreatedAtUtc], [StartedAtUtc], SYSUTCDATETIME(), @DurationMs
                FROM @Moved;

                MERGE [{schema}].[StatsSummary] AS target
                USING (
                    SELECT [Queue], COUNT_BIG(1) AS SucceededCount
                    FROM @Moved
                    GROUP BY [Queue]
                ) AS source
                ON target.[Queue] = source.[Queue]
                WHEN MATCHED THEN
                    UPDATE SET [SucceededTotal] = [SucceededTotal] + source.SucceededCount,
                               [LastActivityUtc] = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN
                    INSERT ([Queue], [SucceededTotal], [FailedTotal], [RetriedTotal], [LastActivityUtc])
                    VALUES (source.[Queue], source.SucceededCount, 0, 0, SYSUTCDATETIME());

                {BuildMetricBucketUpsert(schema, "0", "-1", "@DurationMs")}

                DECLARE @MovedCount int = (SELECT COUNT(1) FROM @Moved);
                COMMIT TRANSACTION;
                SELECT @MovedCount;
            END TRY
            BEGIN CATCH
                IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                THROW;
            END CATCH;";

        MoveToDLQ = $@"
            SET XACT_ABORT ON;

            BEGIN TRY
                BEGIN TRANSACTION;

                DECLARE @Moved TABLE
                (
                    [Id] varchar(50) NOT NULL,
                    [Queue] varchar(255) NOT NULL,
                    [Type] varchar(255) NOT NULL,
                    [Payload] nvarchar(max) NULL,
                    [Tags] varchar(1000) NULL,
                    [AttemptCount] int NOT NULL,
                    [WorkerId] varchar(100) NULL,
                    [CreatedBy] varchar(100) NULL,
                    [LastModifiedBy] varchar(100) NULL,
                    [CreatedAtUtc] datetime2(7) NOT NULL
                );

                -- A DLQ move is a data-integrity boundary, not two casual statements.
                -- The deleted-output buffer lets SQL Server roll the source row back if the DLQ insert fails.
                -- The ownership guard turns stale finalization into a zero-row no-op instead of
                -- allowing a frozen worker to overwrite another recovery decision.
                DELETE FROM [{schema}].[JobsHot]
                OUTPUT deleted.[Id], deleted.[Queue], deleted.[Type], deleted.[Payload], deleted.[Tags],
                       deleted.[AttemptCount], deleted.[WorkerId], deleted.[CreatedBy], deleted.[LastModifiedBy],
                       deleted.[CreatedAtUtc]
                INTO @Moved
                WHERE [Id] = @Id
                  AND (@WorkerId IS NULL OR [WorkerId] = @WorkerId);

                INSERT INTO [{schema}].[JobsDLQ]
                ([Id], [Queue], [Type], [Payload], [Tags], [FailureReason], [ErrorDetails], [AttemptCount],
                 [WorkerId], [CreatedBy], [LastModifiedBy], [CreatedAtUtc], [FailedAtUtc])
                SELECT [Id], [Queue], [Type], [Payload], [Tags], @Reason, @Error, [AttemptCount],
                       [WorkerId], [CreatedBy], [LastModifiedBy], [CreatedAtUtc], SYSUTCDATETIME()
                FROM @Moved;

                MERGE [{schema}].[StatsSummary] AS target
                USING (
                    SELECT [Queue], COUNT_BIG(1) AS FailedCount
                    FROM @Moved
                    GROUP BY [Queue]
                ) AS source
                ON target.[Queue] = source.[Queue]
                WHEN MATCHED THEN
                    UPDATE SET [FailedTotal] = [FailedTotal] + source.FailedCount,
                               [LastActivityUtc] = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN
                    INSERT ([Queue], [SucceededTotal], [FailedTotal], [RetriedTotal], [LastActivityUtc])
                    VALUES (source.[Queue], 0, source.FailedCount, 0, SYSUTCDATETIME());

                {BuildMetricBucketUpsert(schema, "1", "@Reason", "NULL")}

                DECLARE @MovedCount int = (SELECT COUNT(1) FROM @Moved);
                COMMIT TRANSACTION;
                SELECT @MovedCount;
            END TRY
            BEGIN CATCH
                IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                THROW;
            END CATCH;";

        Resurrect = $@"
            SET XACT_ABORT ON;

            BEGIN TRY
                BEGIN TRANSACTION;

                DECLARE @Moved TABLE
                (
                    [Id] varchar(50) NOT NULL,
                    [Queue] varchar(255) NOT NULL,
                    [Type] varchar(255) NOT NULL,
                    [Payload] nvarchar(max) NULL,
                    [Tags] varchar(1000) NULL,
                    [CreatedBy] varchar(100) NULL,
                    [CreatedAtUtc] datetime2(7) NOT NULL
                );

                -- Resurrection is also a move. Deleting from DLQ first prevents two admins or tabs
                -- from inserting duplicate Hot rows; a failed Hot insert rolls the DLQ row back.
                DELETE FROM [{schema}].[JobsDLQ]
                OUTPUT deleted.[Id], deleted.[Queue], deleted.[Type], deleted.[Payload], deleted.[Tags],
                       deleted.[CreatedBy], deleted.[CreatedAtUtc]
                INTO @Moved
                WHERE [Id] = @Id;

                INSERT INTO [{schema}].[JobsHot]
                ([Id], [Queue], [Type], [Payload], [Tags], [Priority], [Status], [AttemptCount],
                 [CreatedAtUtc], [LastUpdatedUtc], [CreatedBy], [LastModifiedBy])
                SELECT [Id], [Queue], [Type],
                       ISNULL(@NewPayload, [Payload]),
                       ISNULL(@NewTags, [Tags]),
                       ISNULL(@NewPriority, 10),
                       0, 0,
                       [CreatedAtUtc], SYSUTCDATETIME(), [CreatedBy], @ResurrectedBy
                FROM @Moved;

                UPDATE s
                SET s.[FailedTotal] = CASE WHEN s.[FailedTotal] > counts.ResurrectedCount
                                            THEN s.[FailedTotal] - counts.ResurrectedCount
                                            ELSE 0 END,
                    s.[LastActivityUtc] = SYSUTCDATETIME()
                FROM [{schema}].[StatsSummary] s
                INNER JOIN (
                    SELECT [Queue], COUNT_BIG(1) AS ResurrectedCount
                    FROM @Moved
                    GROUP BY [Queue]
                ) counts ON counts.[Queue] = s.[Queue];

                DECLARE @MovedCount int = (SELECT COUNT(1) FROM @Moved);
                COMMIT TRANSACTION;
                SELECT @MovedCount;
            END TRY
            BEGIN CATCH
                IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                THROW;
            END CATCH;";

        ResurrectBatch = $@"
            SET XACT_ABORT ON;

            BEGIN TRY
                BEGIN TRANSACTION;

                DECLARE @Moved TABLE
                (
                    [Id] varchar(50) NOT NULL,
                    [Queue] varchar(255) NOT NULL,
                    [Type] varchar(255) NOT NULL,
                    [Payload] nvarchar(max) NULL,
                    [Tags] varchar(1000) NULL,
                    [CreatedBy] varchar(100) NULL,
                    [CreatedAtUtc] datetime2(7) NOT NULL
                );

                -- Batch resurrection uses the same atomic move pattern as single resurrection.
                -- The returned count is based on rows actually deleted from DLQ, not requested IDs.
                DELETE FROM [{schema}].[JobsDLQ]
                OUTPUT deleted.[Id], deleted.[Queue], deleted.[Type], deleted.[Payload], deleted.[Tags],
                       deleted.[CreatedBy], deleted.[CreatedAtUtc]
                INTO @Moved
                WHERE [Id] IN (SELECT CONVERT(varchar(50), [value]) FROM OPENJSON(@JsonIds));

                INSERT INTO [{schema}].[JobsHot]
                ([Id], [Queue], [Type], [Payload], [Tags], [Priority], [Status], [AttemptCount],
                 [CreatedAtUtc], [LastUpdatedUtc], [CreatedBy], [LastModifiedBy])
                SELECT [Id], [Queue], [Type], [Payload], [Tags], 10, 0, 0,
                       [CreatedAtUtc], SYSUTCDATETIME(), [CreatedBy], @ResurrectedBy
                FROM @Moved;

                UPDATE s
                SET s.[FailedTotal] = CASE WHEN s.[FailedTotal] > counts.ResurrectedCount
                                            THEN s.[FailedTotal] - counts.ResurrectedCount
                                            ELSE 0 END,
                    s.[LastActivityUtc] = SYSUTCDATETIME()
                FROM [{schema}].[StatsSummary] s
                INNER JOIN (
                    SELECT [Queue], COUNT_BIG(1) AS ResurrectedCount
                    FROM @Moved
                    GROUP BY [Queue]
                ) counts ON counts.[Queue] = s.[Queue];

                DECLARE @MovedCount int = (SELECT COUNT(1) FROM @Moved);
                COMMIT TRANSACTION;
                SELECT @MovedCount;
            END TRY
            BEGIN CATCH
                IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                THROW;
            END CATCH;";

        RescheduleForRetry = $@"
            SET XACT_ABORT ON;

            BEGIN TRY
                BEGIN TRANSACTION;

                DECLARE @Moved TABLE ([Queue] varchar(255) NOT NULL);

                -- Retry is a worker-owned finalization path even though the row stays in Hot.
                -- AttemptCount stores executions that actually started, so retry scheduling
                -- preserves the current count and lets the next MarkAsProcessing increment it.
                -- If the worker no longer owns the row, we must not clear another owner's WorkerId
                -- or increment retry counters for work that was already reclaimed.
                UPDATE [{schema}].[JobsHot]
                SET [Status] = 0,
                    [AttemptCount] = @Attempt,
                    [ScheduledAtUtc] = @ScheduledAt,
                    [WorkerId] = NULL,
                    [HeartbeatUtc] = NULL,
                    [LastUpdatedUtc] = SYSUTCDATETIME()
                OUTPUT inserted.[Queue] INTO @Moved
                WHERE [Id] = @Id
                  AND (@WorkerId IS NULL OR [WorkerId] = @WorkerId);

                MERGE [{schema}].[StatsSummary] AS target
                USING (
                    SELECT [Queue], COUNT_BIG(1) AS RetriedCount
                    FROM @Moved
                    GROUP BY [Queue]
                ) AS source
                ON target.[Queue] = source.[Queue]
                WHEN MATCHED THEN
                    UPDATE SET [RetriedTotal] = [RetriedTotal] + source.RetriedCount,
                               [LastActivityUtc] = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN
                    INSERT ([Queue], [SucceededTotal], [FailedTotal], [RetriedTotal], [LastActivityUtc])
                    VALUES (source.[Queue], 0, 0, source.RetriedCount, SYSUTCDATETIME());

                DECLARE @MovedCount int = (SELECT COUNT(1) FROM @Moved);
                COMMIT TRANSACTION;
                SELECT @MovedCount;
            END TRY
            BEGIN CATCH
                IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                THROW;
            END CATCH;";

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
            SET XACT_ABORT ON;

            BEGIN TRY
                BEGIN TRANSACTION;

                DECLARE @Deleted TABLE ([Queue] varchar(255) NOT NULL);

                ;WITH Requested AS (
                    SELECT CONVERT(varchar(50), [value]) AS [Id]
                    FROM OPENJSON(@JsonIds)
                ),
                Picked AS (
                    SELECT TOP (@BatchSize) d.[Id]
                    FROM [{schema}].[JobsDLQ] d WITH (UPDLOCK, READPAST)
                    INNER JOIN Requested r ON r.[Id] = d.[Id]
                    ORDER BY d.[FailedAtUtc] ASC, d.[Id] ASC
                )
                DELETE d
                OUTPUT deleted.[Queue] INTO @Deleted
                FROM [{schema}].[JobsDLQ] d
                INNER JOIN Picked p ON p.[Id] = d.[Id];

                -- Selected DLQ purge is batched even though the operator chose explicit IDs.
                -- The important production constraint is the size of one transaction, not the UI
                -- selection model. Decrementing by rows actually deleted keeps counters correct
                -- when another operator requeues or purges some of the same IDs first.
                UPDATE s
                SET s.[FailedTotal] = CASE WHEN s.[FailedTotal] > counts.DeletedCount
                                            THEN s.[FailedTotal] - counts.DeletedCount
                                            ELSE 0 END,
                    s.[LastActivityUtc] = SYSUTCDATETIME()
                FROM [{schema}].[StatsSummary] s
                INNER JOIN (
                    SELECT [Queue], COUNT_BIG(1) AS DeletedCount
                    FROM @Deleted
                    GROUP BY [Queue]
                ) counts ON counts.[Queue] = s.[Queue];

                DECLARE @DeletedCount int = (SELECT COUNT(1) FROM @Deleted);
                COMMIT TRANSACTION;
                SELECT @DeletedCount;
            END TRY
            BEGIN CATCH
                IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                THROW;
            END CATCH;";

        // Bulk previews are operator decision support, not passive telemetry. They intentionally
        // read committed rows so the displayed sample IDs match rows that destructive actions can
        // actually target. The mutation still remains authoritative and handles concurrent changes.
        GetDLQBulkIds = $@"
            SELECT TOP (@MaxJobs) [Id]
            FROM [{schema}].[JobsDLQ]
            {{WHERE_CLAUSE}}
            ORDER BY [FailedAtUtc] DESC, [Id] ASC";

        PurgeDLQByFilter = $@"
            SET XACT_ABORT ON;

            BEGIN TRY
                BEGIN TRANSACTION;

                DECLARE @Deleted TABLE ([Queue] varchar(255) NOT NULL);

                ;WITH Picked AS (
                    SELECT TOP (@MaxJobs) [Id]
                    FROM [{schema}].[JobsDLQ] WITH (UPDLOCK, READPAST)
                    {{WHERE_CLAUSE}}
                    ORDER BY [FailedAtUtc] DESC, [Id] ASC
                )
                DELETE d
                OUTPUT deleted.[Queue] INTO @Deleted
                FROM [{schema}].[JobsDLQ] d
                INNER JOIN Picked p ON p.[Id] = d.[Id];

                -- We decrement by the rows actually deleted inside this transaction, not by the
                -- preview count. Another operator may have acted after preview, so the storage
                -- layer must report and account for the real mutation.
                UPDATE s
                SET s.[FailedTotal] = CASE WHEN s.[FailedTotal] > counts.DeletedCount
                                            THEN s.[FailedTotal] - counts.DeletedCount
                                            ELSE 0 END,
                    s.[LastActivityUtc] = SYSUTCDATETIME()
                FROM [{schema}].[StatsSummary] s
                INNER JOIN (
                    SELECT [Queue], COUNT_BIG(1) AS DeletedCount
                    FROM @Deleted
                    GROUP BY [Queue]
                ) counts ON counts.[Queue] = s.[Queue];

                DECLARE @DeletedCount int = (SELECT COUNT(1) FROM @Deleted);
                COMMIT TRANSACTION;
                SELECT @DeletedCount;
            END TRY
            BEGIN CATCH
                IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                THROW;
            END CATCH;";

        ResurrectDLQByFilter = $@"
            SET XACT_ABORT ON;

            BEGIN TRY
                BEGIN TRANSACTION;

                DECLARE @Moved TABLE
                (
                    [Id] varchar(50) NOT NULL,
                    [Queue] varchar(255) NOT NULL,
                    [Type] varchar(255) NOT NULL,
                    [Payload] nvarchar(max) NULL,
                    [Tags] varchar(1000) NULL,
                    [CreatedBy] varchar(100) NULL,
                    [CreatedAtUtc] datetime2(7) NOT NULL
                );

                ;WITH Picked AS (
                    SELECT TOP (@MaxJobs) [Id]
                    FROM [{schema}].[JobsDLQ] WITH (UPDLOCK, READPAST)
                    {{WHERE_CLAUSE}}
                    ORDER BY [FailedAtUtc] DESC, [Id] ASC
                )
                DELETE d
                OUTPUT deleted.[Id], deleted.[Queue], deleted.[Type], deleted.[Payload], deleted.[Tags],
                       deleted.[CreatedBy], deleted.[CreatedAtUtc]
                INTO @Moved
                FROM [{schema}].[JobsDLQ] d
                INNER JOIN Picked p ON p.[Id] = d.[Id];

                -- Requeue is a DLQ -> Hot move, not a copy. Using DELETE ... OUTPUT makes the DLQ
                -- row the source of truth and prevents duplicate resurrection if two admins run the
                -- same filtered operation concurrently.
                INSERT INTO [{schema}].[JobsHot]
                ([Id], [Queue], [Type], [Payload], [Tags], [Priority], [Status], [AttemptCount],
                 [CreatedAtUtc], [LastUpdatedUtc], [CreatedBy], [LastModifiedBy])
                SELECT [Id], [Queue], [Type], [Payload], [Tags], 10, 0, 0,
                       [CreatedAtUtc], SYSUTCDATETIME(), [CreatedBy], @ResurrectedBy
                FROM @Moved;

                UPDATE s
                SET s.[FailedTotal] = CASE WHEN s.[FailedTotal] > counts.ResurrectedCount
                                            THEN s.[FailedTotal] - counts.ResurrectedCount
                                            ELSE 0 END,
                    s.[LastActivityUtc] = SYSUTCDATETIME()
                FROM [{schema}].[StatsSummary] s
                INNER JOIN (
                    SELECT [Queue], COUNT_BIG(1) AS ResurrectedCount
                    FROM @Moved
                    GROUP BY [Queue]
                ) counts ON counts.[Queue] = s.[Queue];

                DECLARE @MovedCount int = (SELECT COUNT(1) FROM @Moved);
                COMMIT TRANSACTION;
                SELECT @MovedCount;
            END TRY
            BEGIN CATCH
                IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                THROW;
            END CATCH;";

        PurgeArchive = $@"
            SET XACT_ABORT ON;

            BEGIN TRY
                BEGIN TRANSACTION;

                DECLARE @Deleted TABLE ([Queue] varchar(255) NOT NULL);

                ;WITH Picked AS (
                    SELECT TOP (@BatchSize) [Id]
                    FROM [{schema}].[JobsArchive] WITH (UPDLOCK, READPAST)
                    WHERE [FinishedAtUtc] < @CutOff
                    ORDER BY [FinishedAtUtc] ASC, [Id] ASC
                )
                DELETE a
                OUTPUT deleted.[Queue] INTO @Deleted
                FROM [{schema}].[JobsArchive] a
                INNER JOIN Picked p ON p.[Id] = a.[Id];

                -- Archive retention is maintenance work, so it must be a polite database citizen.
                -- A bounded transaction reduces lock duration and transaction-log spikes, while
                -- the C# caller repeats this template until the retention window is clean.
                UPDATE s
                SET s.[SucceededTotal] = CASE WHEN s.[SucceededTotal] > counts.DeletedCount
                                               THEN s.[SucceededTotal] - counts.DeletedCount
                                               ELSE 0 END,
                    s.[LastActivityUtc] = SYSUTCDATETIME()
                FROM [{schema}].[StatsSummary] s
                INNER JOIN (
                    SELECT [Queue], COUNT_BIG(1) AS DeletedCount
                    FROM @Deleted
                    GROUP BY [Queue]
                ) counts ON counts.[Queue] = s.[Queue];

                DECLARE @DeletedCount int = (SELECT COUNT(1) FROM @Deleted);
                COMMIT TRANSACTION;
                SELECT @DeletedCount;
            END TRY
            BEGIN CATCH
                IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                THROW;
            END CATCH;";

        UpdateDLQData = $@"
            UPDATE [{schema}].[JobsDLQ]
            SET [Payload] = ISNULL(@Payload, [Payload]),
                [Tags] = ISNULL(@Tags, [Tags]),
                [LastModifiedBy] = @ModifiedBy
            WHERE [Id] = @Id";

        // OBSERVABILITY
        // The summary/health/throughput/top-error queries below are dashboard telemetry. They may
        // trade perfect read consistency for low observer impact, so their NOLOCK usage is explicit
        // and documented. Operator decision queries (job lists, inspectors, bulk previews, fetch)
        // use committed reads instead.
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

        // Queue health is the dashboard's saturation query. It is deliberately built from
        // eligible Pending rows only: delayed jobs are not actionable until their schedule time
        // arrives, so counting them as lag would page operators for work the system is correctly
        // waiting to run.
        GetQueueHealth = $@"
            WITH EligiblePending AS (
                SELECT
                    h.[Queue],
                    CASE
                        WHEN DATEDIFF_BIG(MILLISECOND, ISNULL(h.[ScheduledAtUtc], h.[CreatedAtUtc]), SYSUTCDATETIME()) < 0 THEN 0
                        ELSE DATEDIFF_BIG(MILLISECOND, ISNULL(h.[ScheduledAtUtc], h.[CreatedAtUtc]), SYSUTCDATETIME())
                    END AS [LagMs]
                FROM [{schema}].[JobsHot] h WITH (NOLOCK)
                WHERE h.[Status] = 0
                  AND (h.[ScheduledAtUtc] IS NULL OR h.[ScheduledAtUtc] <= SYSUTCDATETIME())
            ),
            LagByQueue AS (
                SELECT
                    [Queue],
                    COUNT_BIG(1) AS [Pending],
                    AVG(CAST([LagMs] AS FLOAT)) AS [AverageLagMs],
                    MAX([LagMs]) AS [MaxLagMs]
                FROM EligiblePending
                GROUP BY [Queue]
            )
            SELECT
                q.[Name] AS [Queue],
                CAST(ISNULL(l.[Pending], 0) AS INT) AS [Pending],
                ISNULL(l.[AverageLagMs] / 1000.0, 0) AS [AverageLagSeconds],
                ISNULL(CAST(l.[MaxLagMs] AS FLOAT) / 1000.0, 0) AS [MaxLagSeconds]
            FROM [{schema}].[Queues] q WITH (NOLOCK)
            LEFT JOIN LagByQueue l ON l.[Queue] = q.[Name]
            ORDER BY q.[Name]";

        // Recent throughput now comes from materialized event buckets. The older bounded-lookback
        // version counted Archive/DLQ rows directly, which was simple but forced the dashboard to
        // touch lifecycle history and made requeue/purge operations change past failure windows.
        // Buckets are updated inside the final state-transition transaction, so the dashboard reads
        // small immutable outcome aggregates instead of asking history tables to double as metrics.
        GetThroughputStats = $@"
            SELECT
                CAST(ISNULL(SUM(CASE WHEN [BucketStartUtc] >= @OneMinuteCutoff THEN [CompletedCount] ELSE 0 END), 0) AS BIGINT) AS [ProcessedLastMinute],
                CAST(ISNULL(SUM(CASE WHEN [BucketStartUtc] >= @OneMinuteCutoff AND [Outcome] = 1 THEN [CompletedCount] ELSE 0 END), 0) AS BIGINT) AS [FailedLastMinute],
                CAST(ISNULL(SUM([CompletedCount]), 0) AS BIGINT) AS [ProcessedLastFiveMinutes],
                CAST(ISNULL(SUM(CASE WHEN [Outcome] = 1 THEN [CompletedCount] ELSE 0 END), 0) AS BIGINT) AS [FailedLastFiveMinutes]
            FROM [{schema}].[MetricBuckets] WITH (NOLOCK)
            WHERE [BucketStartUtc] >= @FiveMinuteCutoff";

        // Top-error grouping is intentionally bounded to the most recent DLQ sample. Operator
        // triage cares about what is breaking now, and bounding the sample prevents a large DLQ
        // from turning the dashboard into an accidental analytics scan.
        GetTopDlqErrors = $@"
            WITH RecentFailures AS (
                SELECT TOP (@TopErrorSampleSize)
                    [FailureReason],
                    CASE
                        WHEN [ErrorDetails] IS NULL OR LEN(LTRIM(RTRIM(CAST([ErrorDetails] AS NVARCHAR(4000))))) = 0
                            THEN N'(empty error details)'
                        ELSE LEFT(
                            REPLACE(REPLACE(LTRIM(RTRIM(CAST([ErrorDetails] AS NVARCHAR(4000)))), CHAR(13), N' '), CHAR(10), N' '),
                            @ErrorPrefixLength)
                    END AS [ErrorPrefix],
                    [FailedAtUtc]
                FROM [{schema}].[JobsDLQ] WITH (NOLOCK)
                ORDER BY [FailedAtUtc] DESC
            )
            SELECT TOP (@TopErrorLimit)
                [FailureReason],
                [ErrorPrefix],
                COUNT_BIG(1) AS [Count],
                MAX([FailedAtUtc]) AS [LatestFailedAtUtc]
            FROM RecentFailures
            GROUP BY [FailureReason], [ErrorPrefix]
            ORDER BY [Count] DESC, [LatestFailedAtUtc] DESC";

        GetActiveJobs = $@"
            SELECT TOP (@Limit) * FROM [{schema}].[JobsHot]
            {{WHERE_CLAUSE}}
            ORDER BY [CreatedAtUtc] DESC";

        GetArchiveJobs = $@"
            SELECT TOP (@Limit) * FROM [{schema}].[JobsArchive]
            {{WHERE_CLAUSE}}
            ORDER BY [FinishedAtUtc] DESC";

        GetArchiveJob = $"SELECT * FROM [{schema}].[JobsArchive] WHERE [Id] = @Id";

        GetDLQJobs = $@"
            SELECT TOP (@Limit) * FROM [{schema}].[JobsDLQ]
            {{WHERE_CLAUSE}}
            ORDER BY [FailedAtUtc] DESC";

        GetDLQJob = $"SELECT * FROM [{schema}].[JobsDLQ] WHERE [Id] = @Id";

        GetQueues = $@"
            SELECT q.[Name], q.[IsPaused], q.[IsActive], q.[ZombieTimeoutSeconds], q.[MaxWorkers], q.[LastUpdatedUtc]
            FROM [{schema}].[Queues] q
            ORDER BY q.[Name]";

        SetQueuePaused = $@"
            MERGE [{schema}].[Queues] AS t
            USING (SELECT @Name AS Name) AS s ON (t.[Name] = s.Name)
            WHEN MATCHED THEN 
                UPDATE SET [IsPaused] = @IsPaused, [LastUpdatedUtc] = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN 
                INSERT ([Name], [IsPaused], [IsActive], [MaxWorkers], [LastUpdatedUtc]) 
                VALUES (@Name, @IsPaused, 1, NULL, SYSUTCDATETIME());";

        SetQueueZombieTimeout = $@"
            MERGE [{schema}].[Queues] AS t
            USING (SELECT @Name AS Name) AS s ON (t.[Name] = s.Name)
            WHEN MATCHED THEN 
                UPDATE SET [ZombieTimeoutSeconds] = @Timeout, [LastUpdatedUtc] = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN 
                INSERT ([Name], [IsPaused], [IsActive], [ZombieTimeoutSeconds], [MaxWorkers], [LastUpdatedUtc]) 
                VALUES (@Name, 0, 1, @Timeout, NULL, SYSUTCDATETIME());";

        SetQueueMaxWorkers = $@"
            MERGE [{schema}].[Queues] AS t
            USING (SELECT @Name AS Name) AS s ON (t.[Name] = s.Name)
            WHEN MATCHED THEN 
                UPDATE SET [MaxWorkers] = @MaxWorkers, [LastUpdatedUtc] = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN 
                INSERT ([Name], [IsPaused], [IsActive], [MaxWorkers], [LastUpdatedUtc]) 
                VALUES (@Name, 0, 1, @MaxWorkers, SYSUTCDATETIME());";

        SetQueueActive = $@"
            UPDATE [{schema}].[Queues]
            SET [IsActive] = @IsActive, 
                [IsPaused] = 1,
                [LastUpdatedUtc] = SYSUTCDATETIME()
            WHERE [Name] = @Name";

        // ========================================================================
        // RECOVERY & ZOMBIE DETECTION
        // ========================================================================

        RecoverAbandoned = $@"
            -- Abandoned-fetch recovery intentionally uses a Fetched-specific timeout supplied by
            -- ZombieRescueService. Fetched rows have not executed user code, but they may wait in
            -- a healthy worker's prefetch buffer; do not couple this to Processing heartbeat TTL.
            UPDATE [{schema}].[JobsHot]
            SET [Status] = 0,
                [WorkerId] = NULL,
                [LastUpdatedUtc] = SYSUTCDATETIME()
            WHERE [Status] = 1 
              -- Compare the stored timestamp to a computed cutoff instead of wrapping the
              -- column in DATEDIFF. That keeps IX_JobsHot_FetchedRecovery useful as a seekable
              -- recovery path when the Hot table is large.
              AND [LastUpdatedUtc] < DATEADD(SECOND, -@TimeoutSeconds, SYSUTCDATETIME());";

        ArchiveZombies = $@"
            SET XACT_ABORT ON;

            BEGIN TRY
                BEGIN TRANSACTION;

                DECLARE @Moved TABLE
                (
                    [Id] varchar(50) NOT NULL,
                    [Queue] varchar(255) NOT NULL,
                    [Type] varchar(255) NOT NULL,
                    [Payload] nvarchar(max) NULL,
                    [Tags] varchar(1000) NULL,
                    [AttemptCount] int NOT NULL,
                    [WorkerId] varchar(100) NULL,
                    [CreatedBy] varchar(100) NULL,
                    [LastModifiedBy] varchar(100) NULL,
                    [CreatedAtUtc] datetime2(7) NOT NULL
                );

                -- Zombie rescue is a bulk Hot -> DLQ move. The delete predicate is the authoritative
                -- claim: rows captured here are exactly the rows archived, and rollback restores them.
                DELETE h
                OUTPUT deleted.[Id], deleted.[Queue], deleted.[Type], deleted.[Payload], deleted.[Tags],
                       deleted.[AttemptCount], deleted.[WorkerId], deleted.[CreatedBy], deleted.[LastModifiedBy],
                       deleted.[CreatedAtUtc]
                INTO @Moved
                FROM [{schema}].[JobsHot] h
                LEFT JOIN [{schema}].[Queues] q ON q.[Name] = h.[Queue]
                WHERE h.[Status] = 2
                  -- Keep the heartbeat columns visible to the optimizer. DATEDIFF(column)
                  -- would force SQL Server to evaluate the function for every Processing row;
                  -- timestamp < DATEADD(...) lets the filtered heartbeat index participate.
                  AND (
                      (h.[HeartbeatUtc] IS NOT NULL
                       AND h.[HeartbeatUtc] < DATEADD(SECOND, -ISNULL(q.[ZombieTimeoutSeconds], @GlobalTimeout), SYSUTCDATETIME()))
                      OR
                      (h.[HeartbeatUtc] IS NULL
                       AND h.[LastUpdatedUtc] < DATEADD(SECOND, -ISNULL(q.[ZombieTimeoutSeconds], @GlobalTimeout), SYSUTCDATETIME()))
                  );

                INSERT INTO [{schema}].[JobsDLQ]
                ([Id], [Queue], [Type], [Payload], [Tags], [FailureReason], [ErrorDetails], [AttemptCount],
                 [WorkerId], [CreatedBy], [LastModifiedBy], [CreatedAtUtc], [FailedAtUtc])
                SELECT [Id], [Queue], [Type], [Payload], [Tags],
                       2,
                       'Zombie: Worker heartbeat expired during execution',
                       [AttemptCount], [WorkerId], [CreatedBy], [LastModifiedBy],
                       [CreatedAtUtc], SYSUTCDATETIME()
                FROM @Moved;

                MERGE [{schema}].[StatsSummary] AS target
                USING (
                    SELECT [Queue], COUNT_BIG(1) AS ZombieCount
                    FROM @Moved
                    GROUP BY [Queue]
                ) AS source
                ON target.[Queue] = source.[Queue]
                WHEN MATCHED THEN
                    UPDATE SET [FailedTotal] = [FailedTotal] + source.ZombieCount,
                               [LastActivityUtc] = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN
                    INSERT ([Queue], [SucceededTotal], [FailedTotal], [RetriedTotal], [LastActivityUtc])
                    VALUES (source.[Queue], 0, source.ZombieCount, 0, SYSUTCDATETIME());

                {BuildMetricBucketUpsert(schema, "1", "2", "NULL")}

                DECLARE @MovedCount int = (SELECT COUNT(1) FROM @Moved);
                COMMIT TRANSACTION;
                SELECT @MovedCount;
            END TRY
            BEGIN CATCH
                IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                THROW;
            END CATCH;";

        // HISTORY
        // History pages power inspection and follow-up operator actions. They use committed reads
        // so a row shown in the UI is a row that actually committed to Archive or DLQ.
        GetArchivePaged = $@"
            SELECT * FROM [{schema}].[JobsArchive]
            {{WHERE_CLAUSE}}
            {{ORDER_BY}}
            OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY";

        GetArchiveCount = $@"
            SELECT COUNT(1) 
            FROM [{schema}].[JobsArchive]
            {{WHERE_CLAUSE}}";

        GetDLQPaged = $@"
            SELECT * FROM [{schema}].[JobsDLQ]
            {{WHERE_CLAUSE}}
            {{ORDER_BY}}
            OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY";

        GetDLQCount = $@"
            SELECT COUNT(1) 
            FROM [{schema}].[JobsDLQ]
            {{WHERE_CLAUSE}}";

        ArchiveCancelledBatch = $@"
            SET XACT_ABORT ON;

            BEGIN TRY
                BEGIN TRANSACTION;

                DECLARE @Moved TABLE
                (
                    [Id] varchar(50) NOT NULL,
                    [Queue] varchar(255) NOT NULL,
                    [Type] varchar(255) NOT NULL,
                    [Payload] nvarchar(max) NULL,
                    [Tags] varchar(1000) NULL,
                    [AttemptCount] int NOT NULL,
                    [WorkerId] varchar(100) NULL,
                    [CreatedBy] varchar(100) NULL,
                    [CreatedAtUtc] datetime2(7) NOT NULL
                );

                -- Batch cancellation is intentionally limited to Pending/Fetched jobs.
                -- Processing jobs need worker-aware finalization, otherwise an admin can race live execution.
                DELETE FROM [{schema}].[JobsHot]
                OUTPUT deleted.[Id], deleted.[Queue], deleted.[Type], deleted.[Payload], deleted.[Tags],
                       deleted.[AttemptCount], deleted.[WorkerId], deleted.[CreatedBy], deleted.[CreatedAtUtc]
                INTO @Moved
                WHERE [Id] IN (SELECT CONVERT(varchar(50), [value]) FROM OPENJSON(@JsonIds))
                  AND [Status] IN (0, 1);

                INSERT INTO [{schema}].[JobsDLQ]
                ([Id], [Queue], [Type], [Payload], [Tags], [FailureReason], [ErrorDetails], [AttemptCount],
                 [WorkerId], [CreatedBy], [LastModifiedBy], [CreatedAtUtc], [FailedAtUtc])
                SELECT [Id], [Queue], [Type], [Payload], [Tags], 1, @Error, [AttemptCount],
                       [WorkerId], [CreatedBy], @CancelledBy, [CreatedAtUtc], SYSUTCDATETIME()
                FROM @Moved;

                MERGE [{schema}].[StatsSummary] AS target
                USING (
                    SELECT [Queue], COUNT_BIG(1) AS CancelledCount
                    FROM @Moved
                    GROUP BY [Queue]
                ) AS source
                ON target.[Queue] = source.[Queue]
                WHEN MATCHED THEN
                    UPDATE SET [FailedTotal] = [FailedTotal] + source.CancelledCount,
                               [LastActivityUtc] = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN
                    INSERT ([Queue], [SucceededTotal], [FailedTotal], [RetriedTotal], [LastActivityUtc])
                    VALUES (source.[Queue], 0, source.CancelledCount, 0, SYSUTCDATETIME());

                {BuildMetricBucketUpsert(schema, "1", "1", "NULL")}

                DECLARE @MovedCount int = (SELECT COUNT(1) FROM @Moved);
                COMMIT TRANSACTION;
                SELECT @MovedCount;
            END TRY
            BEGIN CATCH
                IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                THROW;
            END CATCH;";
    }

    private static string BuildMetricBucketUpsert(
        string schema,
        string outcomeSql,
        string failureReasonSql,
        string durationMsSql)
    {
        return $@"
                -- MetricBuckets are the durable rolling observability layer. StatsSummary answers
                -- lifetime counters; Archive/DLQ answer investigation questions; buckets answer
                -- recent-rate questions without making the dashboard scan mutable history tables.
                -- The write is in the same transaction as the final state transition, so the
                -- outcome event cannot exist without the Archive/DLQ move or vice versa.
                DECLARE @MetricObservedAtUtc datetime2(7) = SYSUTCDATETIME();
                DECLARE @MetricBucketStartUtc datetime2(0) =
                    DATEADD(SECOND,
                        DATEDIFF(SECOND, CONVERT(datetime2(0), '20000101'), @MetricObservedAtUtc),
                        CONVERT(datetime2(0), '20000101'));

                MERGE [{schema}].[MetricBuckets] AS target
                USING (
                    SELECT
                        @MetricBucketStartUtc AS [BucketStartUtc],
                        [Queue],
                        CAST({outcomeSql} AS tinyint) AS [Outcome],
                        CAST({failureReasonSql} AS int) AS [FailureReason],
                        COUNT_BIG(1) AS [CompletedCount],
                        COUNT_BIG(CASE WHEN {durationMsSql} IS NULL THEN NULL ELSE 1 END) AS [DurationCount],
                        SUM(CASE WHEN {durationMsSql} IS NULL THEN 0.0 ELSE CAST({durationMsSql} AS float) END) AS [TotalDurationMs],
                        MAX(CASE WHEN {durationMsSql} IS NULL THEN NULL ELSE CAST({durationMsSql} AS float) END) AS [MaxDurationMs]
                    FROM @Moved
                    GROUP BY [Queue]
                ) AS source
                ON target.[BucketStartUtc] = source.[BucketStartUtc]
                   AND target.[Queue] = source.[Queue]
                   AND target.[Outcome] = source.[Outcome]
                   AND target.[FailureReason] = source.[FailureReason]
                WHEN MATCHED THEN
                    UPDATE SET
                        [CompletedCount] = target.[CompletedCount] + source.[CompletedCount],
                        [DurationCount] = target.[DurationCount] + source.[DurationCount],
                        [TotalDurationMs] = target.[TotalDurationMs] + source.[TotalDurationMs],
                        [MaxDurationMs] = CASE
                            WHEN source.[MaxDurationMs] IS NULL THEN target.[MaxDurationMs]
                            WHEN target.[MaxDurationMs] IS NULL OR source.[MaxDurationMs] > target.[MaxDurationMs] THEN source.[MaxDurationMs]
                            ELSE target.[MaxDurationMs]
                        END,
                        [LastUpdatedUtc] = @MetricObservedAtUtc
                WHEN NOT MATCHED THEN
                    INSERT ([BucketStartUtc], [Queue], [Outcome], [FailureReason],
                            [CompletedCount], [DurationCount], [TotalDurationMs], [MaxDurationMs], [LastUpdatedUtc])
                    VALUES (source.[BucketStartUtc], source.[Queue], source.[Outcome], source.[FailureReason],
                            source.[CompletedCount], source.[DurationCount], source.[TotalDurationMs],
                            source.[MaxDurationMs], @MetricObservedAtUtc);";
    }
}
