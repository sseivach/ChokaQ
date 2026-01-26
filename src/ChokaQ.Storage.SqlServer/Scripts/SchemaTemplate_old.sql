-- ==========================================================
-- ChokaQ Database Schema Template
-- ==========================================================
-- This script is used for auto-provisioning the database schema.
-- The placeholder {SCHEMA} will be replaced with the configured schema name
-- (e.g., 'chokaq' or 'my_jobs') at runtime by the C# initializer.
-- ==========================================================

-- 1. Schema Setup
-- Isolate library tables from the user's business tables.
-- Logic: If the schema doesn't exist, create it.
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = '{SCHEMA}')
BEGIN
    EXEC('CREATE SCHEMA [{SCHEMA}]')
END
GO

-- 2. Main Jobs Table
-- Stores all background jobs, their state, payload, and metadata.
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[{SCHEMA}].[Jobs]') AND type in (N'U'))
BEGIN
    CREATE TABLE [{SCHEMA}].[Jobs](
        -- Identity
        [Id] [nvarchar](50) NOT NULL,
        [Queue] [nvarchar](50) NOT NULL DEFAULT 'default',
        [Type] [nvarchar](255) NOT NULL,    -- Full .NET type name
        [Payload] [nvarchar](MAX) NOT NULL, -- JSON serialized arguments
        
        -- State Machine
        -- 0=Pending, 1=Processing, 2=Succeeded, 3=Failed, 4=Cancelled
        [Status] [int] NOT NULL, 
        [AttemptCount] [int] NOT NULL DEFAULT 0,
        
        -- Execution Metadata
        [Priority] [int] NOT NULL DEFAULT 10,  -- 0=Low, 10=Normal, 20=High, 90=Critical, 100=IDDQD
        [CreatedBy] [nvarchar](100) NULL,      -- User ID / Service Name initiator
        [Tags] [nvarchar](1000) NULL,          -- Searchable tags (e.g. "Order:123,Tenant:ABC")
        [ParentJobId] [nvarchar](50) NULL,     -- For future Workflow/Saga support
        [IdempotencyKey] [nvarchar](200) NULL, -- Unique key to prevent duplicate job creation
        
        -- Concurrency & Diagnostics
        [WorkerId] [nvarchar](100) NULL,       -- Lock owner (ID of the worker processing this job)
        [ErrorDetails] [nvarchar](MAX) NULL,   -- Exception stack trace if failed

        -- Timestamps
        [CreatedAtUtc] [datetime2](7) NOT NULL,
        [ScheduledAtUtc] [datetime2](7) NULL,  -- Delay execution until this time
        [StartedAtUtc] [datetime2](7) NULL,    -- Actual processing start time
        [FinishedAtUtc] [datetime2](7) NULL,   -- Completion time
        [LastUpdatedUtc] [datetime2](7) NOT NULL,
        
        CONSTRAINT [PK_{SCHEMA}_Jobs] PRIMARY KEY CLUSTERED ([Id] ASC)
    )
END
GO

-- 3. Idempotency Index
-- Ensures unique IdempotencyKey (if not null) to prevent duplicate jobs.
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_Jobs_Idempotency' AND object_id = OBJECT_ID(N'[{SCHEMA}].[Jobs]'))
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [IX_{SCHEMA}_Jobs_Idempotency] ON [{SCHEMA}].[Jobs]
    (
        [IdempotencyKey] ASC
    )
    WHERE [IdempotencyKey] IS NOT NULL
END
GO

-- 4. Worker Fetch Index (PERFORMANCE CRITICAL)
-- Optimized for the "Poller" query.
-- Logic: Give me Pending jobs (Status=0), ordered by Priority (Desc) then Schedule (Asc).
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_Jobs_Fetch' AND object_id = OBJECT_ID(N'[{SCHEMA}].[Jobs]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_Jobs_Fetch] ON [{SCHEMA}].[Jobs]
    (
        [Status] ASC,     
        [Priority] DESC,  
        [ScheduledAtUtc] ASC 
    )
    INCLUDE ([Queue], [Type], [AttemptCount], [CreatedAtUtc])
    WHERE ([Status] = 0) -- Partial Index: significantly smaller and faster
END
GO

-- 5. Dashboard Index
-- Optimized for displaying the latest jobs in the UI/Dashboard.
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_Jobs_Dashboard' AND object_id = OBJECT_ID(N'[{SCHEMA}].[Jobs]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_Jobs_Dashboard] ON [{SCHEMA}].[Jobs]
    (
        [CreatedAtUtc] DESC
    )
END
GO

-- 6. Queues Table (Traffic Control & Configuration)
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[{SCHEMA}].[Queues]') AND type in (N'U'))
BEGIN
    CREATE TABLE [{SCHEMA}].[Queues](
        [Name] [nvarchar](50) NOT NULL,
        [IsPaused] [bit] NOT NULL DEFAULT 0,
        [ZombieTimeoutSeconds] [int] NULL,
        [LastUpdatedUtc] [datetime2](7) NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_{SCHEMA}_Queues] PRIMARY KEY CLUSTERED ([Name] ASC)
    )

    -- Ensure 'default' queue exists
    INSERT INTO [{SCHEMA}].[Queues] ([Name], [IsPaused]) VALUES ('default', 0)
END
GO

-- 7. Stats Index (Critical for Dashboard Performance)
-- Allows calculating counts and timelines without scanning payloads.
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_{SCHEMA}_Jobs_Stats' AND object_id = OBJECT_ID(N'[{SCHEMA}].[Jobs]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{SCHEMA}_Jobs_Stats] ON [{SCHEMA}].[Jobs]
    (
        [Queue] ASC,
        [Status] ASC
    )
    INCLUDE ([StartedAtUtc], [FinishedAtUtc])
END
GO