-- ============================================================================
-- PROJECT: ChokaQ — THREE PILLARS ARCHITECTURE
-- DESCRIPTION: High-performance schema for SQL Server.
-- OPTIMIZATIONS: PAGE Compression, Filtered Indexes, FillFactor 80.
-- ============================================================================
-- TABLES:
--   JobsHot      → Active jobs (Hot Data)
--   JobsArchive  → Succeeded jobs (History)
--   JobsDLQ      → Failed jobs (Dead Letter Queue)
--   StatsSummary → Pre-aggregated counters
--   Queues       → Queue configuration
-- ============================================================================

-- ============================================================================
-- 0. SCHEMA INITIALIZATION
-- ============================================================================
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = '{SCHEMA}')
BEGIN
    EXEC('CREATE SCHEMA [{SCHEMA}]');
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
        [Payload]        [nvarchar](max)  NULL,                                 -- NVARCHAR for Unicode safety
        [Tags]           [varchar](1000)  NULL,
        [IdempotencyKey] [varchar](255)   NULL,
        
        [Priority]       [int]            NOT NULL DEFAULT 10,
        [Status]         [int]            NOT NULL,                             -- 0:Pending, 1:Fetched, 2:Processing
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
-- Zero Key-Lookups, minimal size (filtered), prioritized sorting.
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsHot_Fetch' AND object_id = OBJECT_ID('[{SCHEMA}].[JobsHot]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsHot_Fetch] 
    ON [{SCHEMA}].[JobsHot] ([Queue], [Priority] DESC, [ScheduledAtUtc]) 
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

-- 1c. Strict Idempotency Guard
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
        
        [FailureReason]  [int]            NOT NULL,                             -- 0:MaxRetries, 1:Cancelled, 2:Zombie, 3:CircuitBreaker, 4:Rejected
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
-- 5. QUEUES CONFIGURATION (Control Plane)
-- Runtime circuit breakers and visibility settings.
-- ============================================================================
IF OBJECT_ID(N'[{SCHEMA}].[Queues]', N'U') IS NULL 
BEGIN
    CREATE TABLE [{SCHEMA}].[Queues](
        [Name]                 [varchar](255)   NOT NULL,
        [IsPaused]             [bit]            NOT NULL DEFAULT 0,
        [IsActive]             [bit]            NOT NULL DEFAULT 1,
        [ZombieTimeoutSeconds] [int]            NULL,
        [LastUpdatedUtc]       [datetime2](7)   NOT NULL,
        
        CONSTRAINT [PK_{SCHEMA}_Queues] PRIMARY KEY CLUSTERED ([Name] ASC)
    ) WITH (DATA_COMPRESSION = PAGE);
END
GO

-- 5a. Seed Default Configuration
IF NOT EXISTS (SELECT 1 FROM [{SCHEMA}].[Queues] WHERE [Name] = 'default')
BEGIN
    INSERT INTO [{SCHEMA}].[Queues] ([Name], [IsPaused], [IsActive], [ZombieTimeoutSeconds], [LastUpdatedUtc])
    VALUES ('default', 0, 1, NULL, SYSUTCDATETIME());
END
GO
