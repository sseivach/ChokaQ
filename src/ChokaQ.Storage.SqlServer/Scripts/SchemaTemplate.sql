-- ====================================================================================
-- ChokaQ: Database Master Schema
-- Defines the complete structure for Hot Data, Archives, Dead Letters, and Statistics.
-- ====================================================================================
-- The placeholder {SCHEMA} will be replaced with the configured schema name
-- (e.g., 'chokaq' or 'my_jobs') at runtime by the C# initializer.
-- ====================================================================================

-- 1. Queues Configuration
-- Stores runtime settings for each queue (pausing, timeouts).
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[{SCHEMA}].[Queues]') AND type in (N'U'))
BEGIN
    CREATE TABLE [{SCHEMA}].[Queues](
        [Name] [nvarchar](50) NOT NULL,
        [IsPaused] [bit] NOT NULL DEFAULT 0,
        [ZombieTimeoutSeconds] [int] NULL,
        [LastUpdatedUtc] [datetime2](7) NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_{SCHEMA}_Queues] PRIMARY KEY CLUSTERED ([Name] ASC)
    );

    -- Ensure 'default' queue always exists
    INSERT INTO [{SCHEMA}].[Queues] ([Name], [IsPaused]) VALUES ('default', 0);
END
GO

-- 2. StatsSummary (High-Performance Counters)
-- Avoids expensive COUNT(*) queries by pre-aggregating metrics.
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[{SCHEMA}].[StatsSummary]') AND type in (N'U'))
BEGIN
    CREATE TABLE [{SCHEMA}].[StatsSummary](
        [QueueName] [nvarchar](50) NOT NULL,
        [SucceededTotal] [bigint] NOT NULL DEFAULT 0,
        [FailedTotal] [bigint] NOT NULL DEFAULT 0,
        [LastUpdatedUtc] [datetime2](7) NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_{SCHEMA}_StatsSummary] PRIMARY KEY CLUSTERED ([QueueName] ASC)
    );
    
    -- Init stats for default queue
    IF NOT EXISTS (SELECT 1 FROM [{SCHEMA}].[StatsSummary] WHERE QueueName = 'default')
    BEGIN
        INSERT INTO [{SCHEMA}].[StatsSummary] (QueueName) VALUES ('default');
    END
END
GO

-- 3. Jobs (The Hot Storage)
-- Only active, pending, or retrying jobs live here. Minimal footprint for speed.
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[{SCHEMA}].[Jobs]') AND type in (N'U'))
BEGIN
    CREATE TABLE [{SCHEMA}].[Jobs](
        [Id] [nvarchar](50) NOT NULL,
        [Queue] [nvarchar](50) NOT NULL,
        [Type] [nvarchar](100) NOT NULL,
        [Payload] [nvarchar](max) NULL,
        [Priority] [int] NOT NULL DEFAULT 0,
        [Status] [int] NOT NULL, -- 0:Pending, 1:Fetched, 2:Processing, etc.
        
        -- Reliability & Idempotency
        [Tags] [nvarchar](max) NULL,          -- Searchable markers (CSV)
        [IdempotencyKey] [nvarchar](100) NULL, -- Prevents duplicate submissions
        
        -- Lifecycle Timestamps
        [CreatedAtUtc] [datetime2](7) NOT NULL,
        [ScheduledAtUtc] [datetime2](7) NULL,  -- For delayed jobs or retries
        [FetchedAtUtc] [datetime2](7) NULL,    -- When worker picked it up
        [HeartbeatUtc] [datetime2](7) NULL,    -- Zombie detection pulse
        
        -- Execution State
        [WorkerId] [nvarchar](100) NULL,
        [AttemptCount] [int] NOT NULL DEFAULT 0,
        
        CONSTRAINT [PK_{SCHEMA}_Jobs] PRIMARY KEY CLUSTERED ([Id] ASC)
    );

    -- Polling Index: The heart of the worker's "Fetch" query.
    -- Finds Pending jobs in a specific queue that are ready (ScheduledAt <= Now).
    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_Jobs_Poll' AND object_id = OBJECT_ID(N'[{SCHEMA}].[Jobs]'))
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_Jobs_Poll] 
        ON [{SCHEMA}].[Jobs] ([Queue], [Status], [ScheduledAtUtc], [Priority] DESC)
        INCLUDE ([WorkerId], [HeartbeatUtc]);
    END

    -- Dashboard Index: Fast sorting by creation date.
    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_Jobs_Dashboard' AND object_id = OBJECT_ID(N'[{SCHEMA}].[Jobs]'))
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_Jobs_Dashboard] 
        ON [{SCHEMA}].[Jobs] ([CreatedAtUtc] DESC);
    END
    
    -- Idempotency Index: Ensures uniqueness if key is provided.
    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_Jobs_Idempotency' AND object_id = OBJECT_ID(N'[{SCHEMA}].[Jobs]'))
    BEGIN
        CREATE UNIQUE NONCLUSTERED INDEX [IX_{SCHEMA}_Jobs_Idempotency]
        ON [{SCHEMA}].[Jobs] ([IdempotencyKey])
        WHERE [IdempotencyKey] IS NOT NULL;
    END
END
GO

-- 4. JobsSucceeded (The Archive)
-- Historical data. Optimized for range scans (History View).
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[{SCHEMA}].[JobsSucceeded]') AND type in (N'U'))
BEGIN
    CREATE TABLE [{SCHEMA}].[JobsSucceeded](
        [Id] [nvarchar](50) NOT NULL,
        [Queue] [nvarchar](50) NOT NULL,
        [Type] [nvarchar](100) NOT NULL,
        [Payload] [nvarchar](max) NULL,
        [Tags] [nvarchar](max) NULL,
        
        -- Metrics
        [FinishedAtUtc] [datetime2](7) NOT NULL,
        [DurationMs] [float] NULL,
        [CreatedBy] [nvarchar](100) NULL,
        
        CONSTRAINT [PK_{SCHEMA}_JobsSucceeded] PRIMARY KEY CLUSTERED ([Id] ASC)
    );

    -- History Timeline Index
    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsSucceeded_History' AND object_id = OBJECT_ID(N'[{SCHEMA}].[JobsSucceeded]'))
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsSucceeded_History] 
        ON [{SCHEMA}].[JobsSucceeded] ([Queue], [FinishedAtUtc] DESC);
    END

    -- Tag Search Index (Critical for Business Analytics)
    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsSucceeded_Tags' AND object_id = OBJECT_ID(N'[{SCHEMA}].[JobsSucceeded]'))
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsSucceeded_Tags] 
        ON [{SCHEMA}].[JobsSucceeded] ([Tags]) 
        WHERE [Tags] IS NOT NULL;
    END
END
GO

-- 5. JobsMorgue (The Dead Letter Queue)
-- Failed jobs awaiting manual intervention (Resurrection or Deletion).
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[{SCHEMA}].[JobsMorgue]') AND type in (N'U'))
BEGIN
    CREATE TABLE [{SCHEMA}].[JobsMorgue](
        [Id] [nvarchar](50) NOT NULL,
        [Queue] [nvarchar](50) NOT NULL,
        [Type] [nvarchar](100) NOT NULL,
        [Payload] [nvarchar](max) NULL,
        [Tags] [nvarchar](max) NULL,
        
        -- Forensic Data
        [ErrorDetails] [nvarchar](max) NULL,
        [AttemptCount] [int] NOT NULL,
        [FailedAtUtc] [datetime2](7) NOT NULL DEFAULT SYSUTCDATETIME(),
        
        CONSTRAINT [PK_{SCHEMA}_JobsMorgue] PRIMARY KEY CLUSTERED ([Id] ASC)
    );

    -- Morgue Management Index
    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsMorgue_Queue' AND object_id = OBJECT_ID(N'[{SCHEMA}].[JobsMorgue]'))
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsMorgue_Queue] 
        ON [{SCHEMA}].[JobsMorgue] ([Queue], [FailedAtUtc] DESC);
    END

    -- Tag Search Index
    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_JobsMorgue_Tags' AND object_id = OBJECT_ID(N'[{SCHEMA}].[JobsMorgue]'))
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_JobsMorgue_Tags] 
        ON [{SCHEMA}].[JobsMorgue] ([Tags]) 
        WHERE [Tags] IS NOT NULL;
    END
END
GO