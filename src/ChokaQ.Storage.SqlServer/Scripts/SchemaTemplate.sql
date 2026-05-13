-- ============================================================================
-- PROJECT: ChokaQ — THREE PILLARS ARCHITECTURE
-- DESCRIPTION: High-performance schema for SQL Server.
-- OPTIMIZATIONS: PAGE Compression, Filtered Indexes, FillFactor 80.
-- ============================================================================
-- TABLES:
--   JobsHot      → Active jobs (Hot Data)
--   JobsArchive  → Succeeded jobs (History)
--   JobsDLQ      → Failed jobs (Dead Letter Queue)
--   StatsSummary → Lifetime counters
--   MetricBuckets → Rolling throughput/failure aggregates
--   Queues       → Queue configuration
--   SchemaMigrations → Applied ChokaQ SQL schema versions
-- ============================================================================

-- ============================================================================
-- 0. SCHEMA INITIALIZATION
-- ============================================================================
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = '{SCHEMA}')
BEGIN
    EXEC('CREATE SCHEMA [{SCHEMA}]');
END
GO

-- 0a. Schema Migration Ledger
-- Production systems need to know which schema shape is actually installed.
-- Idempotent CREATE TABLE scripts are useful, but without a ledger operators cannot
-- answer "which ChokaQ schema version is this database on?" during incidents or upgrades.
-- Each future migration should insert exactly one row here after it successfully applies.
IF OBJECT_ID(N'[{SCHEMA}].[SchemaMigrations]', N'U') IS NULL
BEGIN
    CREATE TABLE [{SCHEMA}].[SchemaMigrations](
        [Version]      [int]           NOT NULL,
        [Name]         [nvarchar](200) NOT NULL,
        [Description]  [nvarchar](1000) NULL,
        [AppliedAtUtc] [datetime2](7)  NOT NULL,

        CONSTRAINT [PK_{SCHEMA}_SchemaMigrations] PRIMARY KEY CLUSTERED ([Version] ASC)
    ) WITH (DATA_COMPRESSION = PAGE);
END
GO

IF NOT EXISTS (SELECT 1 FROM [{SCHEMA}].[SchemaMigrations] WHERE [Version] = 1)
BEGIN
    INSERT INTO [{SCHEMA}].[SchemaMigrations] ([Version], [Name], [Description], [AppliedAtUtc])
    VALUES (
        1,
        N'Initial Three Pillars schema',
        N'Creates JobsHot, JobsArchive, JobsDLQ, StatsSummary, Queues, indexes, and baseline operational metadata.',
        SYSUTCDATETIME()
    );
END
GO


-- ============================================================================
-- 1. HOT DATA TABLE (Active Jobs)
-- Optimized for high-frequency INSERT/UPDATE/DELETE cycles.
-- ============================================================================
IF OBJECT_ID(N'[{SCHEMA}].[JobsHot]', N'U') IS NULL 
BEGIN
    CREATE TABLE [{SCHEMA}].[JobsHot](
        [Id]             [varchar](50)    NOT NULL,
        [Queue]          [varchar](255)   NOT NULL,
        [Type]           [varchar](255)   NOT NULL,
        [Payload]        [nvarchar](max)  NULL,                                  -- NVARCHAR for Unicode safety
        [Tags]           [varchar](1000)  NULL,
        [IdempotencyKey] [varchar](255)   NULL,
        
        [Priority]       [int]            NOT NULL DEFAULT 10,
        [Status]         [int]            NOT NULL,                              -- 0:Pending, 1:Fetched, 2:Processing
        [AttemptCount]   [int]            NOT NULL DEFAULT 0,
        
        [WorkerId]       [varchar](100)   NULL,
        [HeartbeatUtc]   [datetime2](7)   NULL,
        
        [ScheduledAtUtc] [datetime2](7)   NULL,
        [CreatedAtUtc]   [datetime2](7)   NOT NULL,
        [StartedAtUtc]   [datetime2](7)   NULL,
        [LastUpdatedUtc] [datetime2](7)   NOT NULL,
        [CreatedBy]      [varchar](100)   NULL,
        [LastModifiedBy] [varchar](100)   NULL,

        CONSTRAINT [PK_{SCHEMA}_JobsHot] PRIMARY KEY CLUSTERED ([Id] ASC)
        WITH (DATA_COMPRESSION = PAGE, FILLFACTOR = 80)
    );
END
GO

-- 1a. Ultimate Fetch Index (The Engine)
-- Narrow, filtered, and aligned with the fetch ORDER BY.
-- CreatedAtUtc is part of the key because the fetch query sorts by
-- ISNULL(ScheduledAtUtc, CreatedAtUtc). Without it, SQL Server may need
-- extra lookups/sorts exactly on the hottest competing-consumer path.
IF EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsHot_Fetch' AND object_id = OBJECT_ID('[{SCHEMA}].[JobsHot]'))
AND NOT EXISTS (
    SELECT 1
    FROM sys.indexes i
    INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
    INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE i.name = 'IX_{SCHEMA}_JobsHot_Fetch'
      AND i.object_id = OBJECT_ID('[{SCHEMA}].[JobsHot]')
      AND c.name = 'CreatedAtUtc'
      AND ic.key_ordinal > 0
)
BEGIN
    DROP INDEX [IX_{SCHEMA}_JobsHot_Fetch] ON [{SCHEMA}].[JobsHot];
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsHot_Fetch' AND object_id = OBJECT_ID('[{SCHEMA}].[JobsHot]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsHot_Fetch] 
    ON [{SCHEMA}].[JobsHot] ([Queue], [Priority] DESC, [ScheduledAtUtc], [CreatedAtUtc]) 
    INCLUDE ([Id], [Type])
    WHERE [Status] = 0
    WITH (DATA_COMPRESSION = PAGE, FILLFACTOR = 80);
END
GO

-- 1b. Search Index for active jobs (Tags)
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsHot_Tags' AND object_id = OBJECT_ID('[{SCHEMA}].[JobsHot]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsHot_Tags] 
    ON [{SCHEMA}].[JobsHot] ([Tags]) 
    WHERE [Tags] IS NOT NULL
    WITH (DATA_COMPRESSION = PAGE, FILLFACTOR = 80);
END
GO

-- 1c. Hot-Pillar Idempotency Guard
-- This index prevents duplicate active work with the same business key. It is intentionally
-- scoped to JobsHot: once a job is archived or moved to DLQ, a later enqueue with the same key
-- is treated as a new logical attempt unless the optional result-idempotency middleware says otherwise.
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsHot_Idempotency' AND object_id = OBJECT_ID('[{SCHEMA}].[JobsHot]'))
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsHot_Idempotency] 
    ON [{SCHEMA}].[JobsHot] ([IdempotencyKey]) 
    WHERE [IdempotencyKey] IS NOT NULL
    WITH (DATA_COMPRESSION = PAGE, FILLFACTOR = 80);
END
GO

-- 1d. Queue Statistics Index (for GetQueuesAsync aggregation)
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsHot_QueueStats' AND object_id = OBJECT_ID('[{SCHEMA}].[JobsHot]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsHot_QueueStats] 
    ON [{SCHEMA}].[JobsHot] ([Queue], [Status])
    INCLUDE ([Priority])
    WITH (DATA_COMPRESSION = PAGE, FILLFACTOR = 80);
END
GO

-- 1e. Pending Lag Index (Dashboard saturation SLI)
-- Queue lag is polled often by The Deck, so it needs a narrow filtered index over only
-- eligible hot-path rows. This lets SQL Server answer Avg/Max Pending lag without walking
-- Archive/DLQ history or the full JobsHot table.
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsHot_PendingLag' AND object_id = OBJECT_ID('[{SCHEMA}].[JobsHot]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsHot_PendingLag]
    ON [{SCHEMA}].[JobsHot] ([Queue], [ScheduledAtUtc], [CreatedAtUtc])
    WHERE [Status] = 0
    WITH (DATA_COMPRESSION = PAGE, FILLFACTOR = 80);
END
GO

-- 1f. Active Jobs Dashboard Index
-- The Deck often asks for recent active jobs, optionally filtered by Status.
-- We keep this index narrow and intentionally do not INCLUDE Payload: payloads can be large
-- JSON documents, and covering SELECT * would turn a dashboard index into a write-heavy clone
-- of JobsHot. SQL Server can seek the small recent/status set, then perform bounded lookups.
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsHot_StatusCreated' AND object_id = OBJECT_ID('[{SCHEMA}].[JobsHot]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsHot_StatusCreated]
    ON [{SCHEMA}].[JobsHot] ([Status], [CreatedAtUtc] DESC)
    INCLUDE ([Id], [Queue], [Type], [Priority], [ScheduledAtUtc], [WorkerId])
    WITH (DATA_COMPRESSION = PAGE, FILLFACTOR = 80);
END
GO

-- 1g. Abandoned Fetch Recovery Index
-- Fetched rows have not run user code yet, so ZombieRescueService returns old ones to Pending.
-- This filtered index keeps that periodic sweep focused on Fetched rows instead of scanning
-- the whole Hot table when the system has many Pending or Processing jobs.
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsHot_FetchedRecovery' AND object_id = OBJECT_ID('[{SCHEMA}].[JobsHot]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsHot_FetchedRecovery]
    ON [{SCHEMA}].[JobsHot] ([LastUpdatedUtc])
    INCLUDE ([Id], [Queue], [WorkerId])
    WHERE [Status] = 1
    WITH (DATA_COMPRESSION = PAGE, FILLFACTOR = 80);
END
GO

-- 1h. Processing Heartbeat Recovery Index
-- Zombie detection is heartbeat-driven. A filtered Processing-only index gives the rescue
-- sweep a small access path for expired heartbeats while leaving Pending fetch performance alone.
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsHot_ProcessingHeartbeat' AND object_id = OBJECT_ID('[{SCHEMA}].[JobsHot]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsHot_ProcessingHeartbeat]
    ON [{SCHEMA}].[JobsHot] ([HeartbeatUtc], [LastUpdatedUtc])
    INCLUDE ([Id], [Queue], [Type], [WorkerId])
    WHERE [Status] = 2
    WITH (DATA_COMPRESSION = PAGE, FILLFACTOR = 80);
END
GO


-- ============================================================================
-- 2. ARCHIVE TABLE (Success History)
-- Optimized for long-term storage and infrequent audit reads.
-- ============================================================================
IF OBJECT_ID(N'[{SCHEMA}].[JobsArchive]', N'U') IS NULL 
BEGIN
    CREATE TABLE [{SCHEMA}].[JobsArchive](
        [Id]             [varchar](50)    NOT NULL,
        [Queue]          [varchar](255)   NOT NULL,
        [Type]           [varchar](255)   NOT NULL,
        [Payload]        [nvarchar](max)  NULL,
        [Tags]           [varchar](1000)  NULL,
        [AttemptCount]   [int]            NOT NULL DEFAULT 1,
        [WorkerId]       [varchar](100)   NULL,
        [CreatedBy]      [varchar](100)   NULL,
        [LastModifiedBy] [varchar](100)   NULL,
        [CreatedAtUtc]   [datetime2](7)   NOT NULL,
        [StartedAtUtc]   [datetime2](7)   NULL,
        [FinishedAtUtc]  [datetime2](7)   NOT NULL,
        [DurationMs]     [float]          NULL,

        CONSTRAINT [PK_{SCHEMA}_JobsArchive] PRIMARY KEY CLUSTERED ([Id] ASC)
        WITH (DATA_COMPRESSION = PAGE)
    );
END
GO

-- 2a. Archive Date Index (Dashboard trends)
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsArchive_Date' AND object_id = OBJECT_ID('[{SCHEMA}].[JobsArchive]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsArchive_Date] 
    ON [{SCHEMA}].[JobsArchive] ([FinishedAtUtc] DESC)
    INCLUDE ([Queue], [Type], [DurationMs])
    WITH (DATA_COMPRESSION = PAGE);
END
GO

-- 2b. Archive Tags Search
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsArchive_Tags' AND object_id = OBJECT_ID('[{SCHEMA}].[JobsArchive]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsArchive_Tags] 
    ON [{SCHEMA}].[JobsArchive] ([Tags]) 
    WHERE [Tags] IS NOT NULL
    WITH (DATA_COMPRESSION = PAGE);
END
GO

-- 2c. Archive Queue Filter (for "show history of queue X")
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsArchive_Queue' AND object_id = OBJECT_ID('[{SCHEMA}].[JobsArchive]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsArchive_Queue] 
    ON [{SCHEMA}].[JobsArchive] ([Queue], [FinishedAtUtc] DESC)
    WITH (DATA_COMPRESSION = PAGE);
END
GO


-- ============================================================================
-- 3. DEAD LETTER QUEUE (Failed Jobs)
-- Stores jobs that exhausted retries, were cancelled, or became zombies.
-- Supports manual review, resurrection, and operational analytics.
-- ============================================================================
IF OBJECT_ID(N'[{SCHEMA}].[JobsDLQ]', N'U') IS NULL 
BEGIN
    CREATE TABLE [{SCHEMA}].[JobsDLQ](
        [Id]             [varchar](50)    NOT NULL,
        [Queue]          [varchar](255)   NOT NULL,
        [Type]           [varchar](255)   NOT NULL,
        [Payload]        [nvarchar](max)  NULL,
        [Tags]           [varchar](1000)  NULL,
        
        [FailureReason]  [int]            NOT NULL,                              -- 0:MaxRetries, 1:Cancelled, 2:Zombie, 3:CircuitBreaker, 4:Rejected, 5:Throttled, 6:FatalError, 7:Timeout, 8:Transient
        [ErrorDetails]   [nvarchar](max)  NULL,
        [AttemptCount]   [int]            NOT NULL,
        
        [WorkerId]       [varchar](100)   NULL,
        [CreatedBy]      [varchar](100)   NULL,
        [LastModifiedBy] [varchar](100)   NULL,
        [CreatedAtUtc]   [datetime2](7)   NOT NULL,
        [FailedAtUtc]    [datetime2](7)   NOT NULL,

        CONSTRAINT [PK_{SCHEMA}_JobsDLQ] PRIMARY KEY CLUSTERED ([Id] ASC)
        WITH (DATA_COMPRESSION = PAGE)
    );
END
GO

-- 3a. DLQ Date Index (Dashboard: recent failures)
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsDLQ_Date' AND object_id = OBJECT_ID('[{SCHEMA}].[JobsDLQ]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsDLQ_Date] 
    ON [{SCHEMA}].[JobsDLQ] ([FailedAtUtc] DESC)
    INCLUDE ([Queue], [Type], [FailureReason], [AttemptCount])
    WITH (DATA_COMPRESSION = PAGE);
END
GO

-- 3b. DLQ Tags Search (Support investigations)
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsDLQ_Tags' AND object_id = OBJECT_ID('[{SCHEMA}].[JobsDLQ]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsDLQ_Tags] 
    ON [{SCHEMA}].[JobsDLQ] ([Tags]) 
    WHERE [Tags] IS NOT NULL
    WITH (DATA_COMPRESSION = PAGE);
END
GO

-- 3c. DLQ Queue Filter
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsDLQ_Queue' AND object_id = OBJECT_ID('[{SCHEMA}].[JobsDLQ]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsDLQ_Queue] 
    ON [{SCHEMA}].[JobsDLQ] ([Queue], [FailedAtUtc] DESC)
    WITH (DATA_COMPRESSION = PAGE);
END
GO

-- 3d. DLQ Failure Reason Filter (Analytics: show all zombies, all cancelled, etc.)
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsDLQ_Reason' AND object_id = OBJECT_ID('[{SCHEMA}].[JobsDLQ]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsDLQ_Reason] 
    ON [{SCHEMA}].[JobsDLQ] ([FailureReason], [FailedAtUtc] DESC)
    INCLUDE ([Queue], [Type])
    WITH (DATA_COMPRESSION = PAGE);
END
GO

-- 3e. DLQ Job Type Filter
-- Filtered bulk operations frequently target one failed job family, for example
-- "requeue every email_v1 throttling failure". A Type-first index makes those
-- operator workflows seekable instead of relying on a recent-failure scan.
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsDLQ_Type' AND object_id = OBJECT_ID('[{SCHEMA}].[JobsDLQ]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsDLQ_Type]
    ON [{SCHEMA}].[JobsDLQ] ([Type], [FailedAtUtc] DESC)
    INCLUDE ([Queue], [FailureReason], [CreatedAtUtc])
    WITH (DATA_COMPRESSION = PAGE);
END
GO

-- 3f. DLQ CreatedAt Filter
-- DLQ history filters use CreatedAtUtc to answer "when was this work originally
-- submitted?", which is different from FailedAtUtc. Keeping this access path separate
-- avoids turning date-range incident review into a full DLQ scan.
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsDLQ_CreatedAt' AND object_id = OBJECT_ID('[{SCHEMA}].[JobsDLQ]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsDLQ_CreatedAt]
    ON [{SCHEMA}].[JobsDLQ] ([CreatedAtUtc] DESC)
    INCLUDE ([Queue], [Type], [FailureReason], [FailedAtUtc])
    WITH (DATA_COMPRESSION = PAGE);
END
GO


-- ============================================================================
-- 4. STATISTICS TABLE (Fast Monitoring)
-- Provides instant O(1) dashboard metrics without scanning job tables.
-- ============================================================================
IF OBJECT_ID(N'[{SCHEMA}].[StatsSummary]', N'U') IS NULL 
BEGIN
    CREATE TABLE [{SCHEMA}].[StatsSummary](
        [Queue]           [varchar](255)   NOT NULL,
        [SucceededTotal]  [bigint]         NOT NULL DEFAULT 0,
        [FailedTotal]     [bigint]         NOT NULL DEFAULT 0,
        [RetriedTotal]    [bigint]         NOT NULL DEFAULT 0,
        [LastActivityUtc] [datetime2](7)   NULL,
        
        CONSTRAINT [PK_{SCHEMA}_StatsSummary] PRIMARY KEY CLUSTERED ([Queue] ASC)
    ) WITH (DATA_COMPRESSION = PAGE);
END
GO

-- 4a. Seed Default Statistics
IF NOT EXISTS (SELECT 1 FROM [{SCHEMA}].[StatsSummary] WHERE [Queue] = 'default')
BEGIN
    INSERT INTO [{SCHEMA}].[StatsSummary] ([Queue], [SucceededTotal], [FailedTotal], [RetriedTotal], [LastActivityUtc])
    VALUES ('default', 0, 0, 0, SYSUTCDATETIME());
END
GO


-- ============================================================================
-- 5. METRIC BUCKETS (Rolling Observability)
-- Records immutable completion outcomes in small time buckets. Earlier dashboard
-- versions counted recent Archive/DLQ rows directly, which was simple but made
-- lifecycle tables do double duty as metrics storage. Buckets keep throughput
-- and failure-rate reads bounded even when history retention grows.
-- ============================================================================
IF OBJECT_ID(N'[{SCHEMA}].[MetricBuckets]', N'U') IS NULL
BEGIN
    CREATE TABLE [{SCHEMA}].[MetricBuckets](
        [BucketStartUtc] [datetime2](0)   NOT NULL,                              -- UTC second bucket; small enough for 1m windows without per-job event rows.
        [Queue]          [varchar](255)   NOT NULL,
        [Outcome]        [tinyint]        NOT NULL,                              -- 0 = Succeeded, 1 = Failed/DLQ
        [FailureReason]  [int]            NOT NULL,                              -- -1 for success, otherwise FailureReason enum value.
        [CompletedCount] [bigint]         NOT NULL DEFAULT 0,
        [DurationCount]  [bigint]         NOT NULL DEFAULT 0,
        [TotalDurationMs] [float]         NOT NULL DEFAULT 0,
        [MaxDurationMs]  [float]          NULL,
        [LastUpdatedUtc] [datetime2](7)   NOT NULL DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_{SCHEMA}_MetricBuckets]
            PRIMARY KEY CLUSTERED ([BucketStartUtc] ASC, [Queue] ASC, [Outcome] ASC, [FailureReason] ASC),
        CONSTRAINT [CK_{SCHEMA}_MetricBuckets_Outcome]
            CHECK ([Outcome] IN (0, 1)),
        CONSTRAINT [CK_{SCHEMA}_MetricBuckets_Counts]
            CHECK ([CompletedCount] >= 0 AND [DurationCount] >= 0 AND [TotalDurationMs] >= 0)
    ) WITH (DATA_COMPRESSION = PAGE);
END
GO

-- 5a. Recent Metric Bucket Window
-- The Deck reads the last few minutes on every refresh. Keeping BucketStartUtc as
-- the leading key makes that query a tiny range scan instead of a history-table scan.
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_MetricBuckets_Recent' AND object_id = OBJECT_ID('[{SCHEMA}].[MetricBuckets]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_MetricBuckets_Recent]
    ON [{SCHEMA}].[MetricBuckets] ([BucketStartUtc] DESC)
    INCLUDE ([Queue], [Outcome], [FailureReason], [CompletedCount], [DurationCount], [TotalDurationMs], [MaxDurationMs])
    WITH (DATA_COMPRESSION = PAGE);
END
GO


-- ============================================================================
-- 6. QUEUES CONFIGURATION (Control Plane)
-- Runtime circuit breakers and visibility settings.
-- ============================================================================
IF OBJECT_ID(N'[{SCHEMA}].[Queues]', N'U') IS NULL 
BEGIN
    CREATE TABLE [{SCHEMA}].[Queues](
        [Name]                 [varchar](255)   NOT NULL,
        [IsPaused]             [bit]            NOT NULL DEFAULT 0,
        [IsActive]             [bit]            NOT NULL DEFAULT 1,
        [ZombieTimeoutSeconds] [int]            NULL,
        [MaxWorkers]           [int]            NULL, -- Bulkhead concurrency limit
        [LastUpdatedUtc]       [datetime2](7)   NOT NULL,
        
        CONSTRAINT [PK_{SCHEMA}_Queues] PRIMARY KEY CLUSTERED ([Name] ASC)
    ) WITH (DATA_COMPRESSION = PAGE);
END
GO

-- 5a. Seed Default Configuration
IF NOT EXISTS (SELECT 1 FROM [{SCHEMA}].[Queues] WHERE [Name] = 'default')
BEGIN
    INSERT INTO [{SCHEMA}].[Queues] ([Name], [IsPaused], [IsActive], [ZombieTimeoutSeconds], [MaxWorkers], [LastUpdatedUtc])
    VALUES ('default', 0, 1, NULL, NULL, SYSUTCDATETIME());
END
GO

-- ============================================================================
-- 7. MIGRATION LEDGER UPDATES
-- ============================================================================
-- Version 2 records the first explicit query-plan hardening pass. The indexes above are
-- all idempotent, so re-running startup provisioning can safely repair missing indexes
-- and still insert this ledger row exactly once.
IF NOT EXISTS (SELECT 1 FROM [{SCHEMA}].[SchemaMigrations] WHERE [Version] = 2)
BEGIN
    INSERT INTO [{SCHEMA}].[SchemaMigrations] ([Version], [Name], [Description], [AppliedAtUtc])
    VALUES (
        2,
        N'Hot path index hardening',
        N'Adds fetch sort coverage, active-dashboard, recovery, DLQ type, and DLQ created-date indexes.',
        SYSUTCDATETIME()
    );
END
GO

-- Version 3 adds the rolling observability bucket table. It upgrades throughput
-- and failure-rate reads from bounded Archive/DLQ lookbacks to materialized
-- completion-outcome aggregates.
IF NOT EXISTS (SELECT 1 FROM [{SCHEMA}].[SchemaMigrations] WHERE [Version] = 3)
BEGIN
    INSERT INTO [{SCHEMA}].[SchemaMigrations] ([Version], [Name], [Description], [AppliedAtUtc])
    VALUES (
        3,
        N'Rolling metric buckets',
        N'Adds MetricBuckets for materialized throughput and failure-rate windows.',
        SYSUTCDATETIME()
    );
END
GO
