-- ============================================================================
-- PROJECT: ChokaQ
-- DESCRIPTION: High-performance schema for SQL Server.
-- OPTIMIZATIONS: PAGE Compression, Filtered Indexes, FillFactor 80, UTF-8 Ready.
-- ============================================================================

-- ============================================================================
-- 0. SCHEMA INITIALIZATION
-- ============================================================================
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = '{SCHEMA}')
BEGIN
    EXEC('CREATE SCHEMA [{SCHEMA}]');                                           -- Encapsulate broker objects
END
GO


-- ============================================================================
-- 1. HOT DATA TABLE (Active Jobs)
-- Optimized for high-frequency INSERT/UPDATE/DELETE cycles.
-- ============================================================================
IF OBJECT_ID(N'[{SCHEMA}].[Jobs]', N'U') IS NULL 
BEGIN
    CREATE TABLE [{SCHEMA}].[Jobs](
        [Id]             [varchar](50)    NOT NULL,                             -- Primary Identifier (GUID or Custom)
        [Queue]          [varchar](255)   NOT NULL,                             -- Logical queue name (Namespace)
        [Type]           [varchar](255)   NOT NULL,                             -- FullName of the Job handler class
        [Payload]        [varchar](max)   NULL,                                 -- JSON data (ASCII/UTF-8 optimized)
        [Tags]           [varchar](1000)  NULL,                                 -- Searchable metadata (Key=Value) Capped at 1000 to stay well under the 1700-byte limit, accounting for clustered key overhead.
        [IdempotencyKey] [varchar](255)   NULL,                                 -- Deduplication token
        
        [Priority]       [int]            NOT NULL DEFAULT 10,                  -- Execution priority (Lower = Faster)
        [Status]         [int]            NOT NULL,                             -- 0:Pending, 1:Fetched, 2:Processing
        [AttemptCount]   [int]            NOT NULL DEFAULT 0,                   -- Retry tracking
        
        [WorkerId]       [varchar](100)   NULL,                                 -- Instance processing the job
        [HeartbeatUtc]   [datetime2](7)   NULL,                                 -- Last pulse from active worker
        
        [ScheduledAtUtc] [datetime2](7)   NULL,                                 -- Delay execution until this time
        [CreatedAtUtc]   [datetime2](7)   NOT NULL,                             -- Creation timestamp
        [LastUpdatedUtc] [datetime2](7)   NOT NULL,                             -- Record version timestamp
        [CreatedBy]      [varchar](100)   NULL,                                 -- Identity of the producer

        CONSTRAINT [PK_{SCHEMA}_Jobs] PRIMARY KEY CLUSTERED ([Id] ASC)
        WITH (DATA_COMPRESSION = PAGE, FILLFACTOR = 80)                         -- Reserv space for updates/splits
    );
END
GO

-- 1a. Ultimate Fetch Index (The Engine)
-- Why: Zero Key-Lookups, minimal size (filtered), prioritized sorting.
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_Jobs_Fetch_Ultimate' AND object_id = OBJECT_ID('[{SCHEMA}].[Jobs]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_Jobs_Fetch_Ultimate] 
    ON [{SCHEMA}].[Jobs] ([Queue], [Priority] DESC, [ScheduledAtUtc]) 
    INCLUDE ([Id], [Type])                                                      -- Data needed for worker to start
    WHERE [Status] = 0                                                          -- Only index what needs to be done
    WITH (DATA_COMPRESSION = PAGE, FILLFACTOR = 80);
END
GO

-- 1b. Search Index for active jobs
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_Jobs_Tags' AND object_id = OBJECT_ID('[{SCHEMA}].[Jobs]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_Jobs_Tags] 
    ON [{SCHEMA}].[Jobs] ([Tags]) 
    WHERE [Tags] IS NOT NULL
    WITH (DATA_COMPRESSION = PAGE, FILLFACTOR = 80);
END
GO

-- 1c. Strict Idempotency Guard
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_Jobs_Idempotency' AND object_id = OBJECT_ID('[{SCHEMA}].[Jobs]'))
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [IX_{SCHEMA}_Jobs_Idempotency] 
    ON [{SCHEMA}].[Jobs] ([IdempotencyKey]) 
    WHERE [IdempotencyKey] IS NOT NULL
    WITH (DATA_COMPRESSION = PAGE, FILLFACTOR = 80);
END
GO


-- ============================================================================
-- 2. ARCHIVE TABLE (Success History)
-- Optimized for long-term storage and infrequent audit reads.
-- ============================================================================
IF OBJECT_ID(N'[{SCHEMA}].[JobsSucceeded]', N'U') IS NULL 
BEGIN
    CREATE TABLE [{SCHEMA}].[JobsSucceeded](
        [Id]             [varchar](50)    NOT NULL,                             -- Same ID as Jobs table
        [Queue]          [varchar](255)   NOT NULL,                             
        [Type]           [varchar](255)   NOT NULL,
        [Payload]        [varchar](max)   NULL,                                 -- Full data snapshot
        [Tags]           [varchar](1000)  NULL,                                 -- Searchable metadata (Key=Value) Capped at 1000 to stay well under the 1700-byte limit, accounting for clustered key overhead.
        [WorkerId]       [varchar](100)   NULL,
        [CreatedBy]      [varchar](100)   NULL,
        [FinishedAtUtc]  [datetime2](7)   NOT NULL,                             -- Completion time
        [DurationMs]     [float]          NULL,                                 -- Telemetry: Execution time

        CONSTRAINT [PK_{SCHEMA}_JobsSucceeded] PRIMARY KEY CLUSTERED ([Id] ASC)
        WITH (DATA_COMPRESSION = PAGE)                                          -- Default FF=100 (Read-heavy)
    );
END
GO

-- 2a. Archive Date Index (Dashboard trends)
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsSucceeded_Date' AND object_id = OBJECT_ID('[{SCHEMA}].[JobsSucceeded]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsSucceeded_Date] 
    ON [{SCHEMA}].[JobsSucceeded] ([FinishedAtUtc] DESC)
    WITH (DATA_COMPRESSION = PAGE);
END
GO

-- 2b. Archive Tags Search
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsSucceeded_Tags' AND object_id = OBJECT_ID('[{SCHEMA}].[JobsSucceeded]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsSucceeded_Tags] 
    ON [{SCHEMA}].[JobsSucceeded] ([Tags]) 
    WHERE [Tags] IS NOT NULL
    WITH (DATA_COMPRESSION = PAGE);
END
GO


-- ============================================================================
-- 3. MORGUE TABLE (Dead Letter Queue)
-- Stores jobs that failed all retry attempts.
-- ============================================================================
IF OBJECT_ID(N'[{SCHEMA}].[JobsMorgue]', N'U') IS NULL 
BEGIN
    CREATE TABLE [{SCHEMA}].[JobsMorgue](
        [Id]             [varchar](50)    NOT NULL,
        [Queue]          [varchar](255)   NOT NULL,
        [Type]           [varchar](255)   NOT NULL,
        [Payload]        [varchar](max)   NULL,
        [Tags]           [varchar](1000)  NULL,                                 -- Searchable metadata (Key=Value) Capped at 1000 to stay well under the 1700-byte limit, accounting for clustered key overhead.
        [ErrorDetails]   [varchar](max)   NULL,                                 -- Exception stack trace
        [AttemptCount]   [int]            NOT NULL,
        [WorkerId]       [varchar](100)   NULL,
        [CreatedBy]      [varchar](100)   NULL,
        [FailedAtUtc]    [datetime2](7)   NOT NULL,                             -- Time of death

        CONSTRAINT [PK_{SCHEMA}_JobsMorgue] PRIMARY KEY CLUSTERED ([Id] ASC)
        WITH (DATA_COMPRESSION = PAGE)
    );
END
GO

-- 3a. Morgue Date Index
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsMorgue_Date' AND object_id = OBJECT_ID('[{SCHEMA}].[JobsMorgue]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsMorgue_Date] 
    ON [{SCHEMA}].[JobsMorgue] ([FailedAtUtc] DESC)
    WITH (DATA_COMPRESSION = PAGE);
END
GO

-- 3b. Morgue Search (Crucial for support)
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsMorgue_Tags' AND object_id = OBJECT_ID('[{SCHEMA}].[JobsMorgue]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsMorgue_Tags] 
    ON [{SCHEMA}].[JobsMorgue] ([Tags]) 
    WHERE [Tags] IS NOT NULL
    WITH (DATA_COMPRESSION = PAGE);
END
GO


-- ============================================================================
-- 4. STATISTICS TABLE (Fast Monitoring)
-- Why: Provides instant O(1) dashboard metrics without scanning job tables.
-- ============================================================================
IF OBJECT_ID(N'[{SCHEMA}].[StatsSummary]', N'U') IS NULL 
BEGIN
    CREATE TABLE [{SCHEMA}].[StatsSummary](
        [Queue]           [varchar](255)   NOT NULL,                             -- PK (Queue Name)
        [SucceededTotal]  [bigint]         NOT NULL DEFAULT 0,                   -- Global counter
        [FailedTotal]     [bigint]         NOT NULL DEFAULT 0,                   -- Global counter
        [RetriedTotal]    [bigint]         NOT NULL DEFAULT 0,                   -- Transient failure counter
        [LastActivityUtc] [datetime2](7)   NULL,                                 -- Heartbeat for the queue
        
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
-- Purpose: Runtime circuit breakers and visibility settings.
-- ============================================================================
IF OBJECT_ID(N'[{SCHEMA}].[Queues]', N'U') IS NULL 
BEGIN
    CREATE TABLE [{SCHEMA}].[Queues](
        [Name]           [varchar](255)   NOT NULL,                             -- PK
        [IsPaused]       [bit]            NOT NULL DEFAULT 0,                   -- 1: Stop worker fetching
        [IsActive]       [bit]            NOT NULL DEFAULT 1,                   -- 0: Hide from dashboard
        [LastUpdatedUtc] [datetime2](7)   NOT NULL,                             -- Config change audit
        
        CONSTRAINT [PK_{SCHEMA}_Queues] PRIMARY KEY CLUSTERED ([Name] ASC)
    ) WITH (DATA_COMPRESSION = PAGE);
END
GO

-- 5a. Seed Default Configuration
IF NOT EXISTS (SELECT 1 FROM [{SCHEMA}].[Queues] WHERE [Name] = 'default')
BEGIN
    INSERT INTO [{SCHEMA}].[Queues] ([Name], [IsPaused], [IsActive], [LastUpdatedUtc])
    VALUES ('default', 0, 1, SYSUTCDATETIME());
END
GO