-- ==========================================================
-- ChokaQ Database Schema
--
-- INSTRUCTIONS FOR PRODUCTION:
-- 1. Connect to your TARGET database (e.g. MyProductionDb).
-- 2. Execute this script.
--
-- NOTE: This script is idempotent. It is safe to run multiple times.
-- ==========================================================

-- 1. Schema Setup
-- We use a dedicated schema to avoid conflicts with host application tables.
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'chokaq')
BEGIN
    EXEC('CREATE SCHEMA [chokaq]')
END
GO

-- 2. Main Jobs Table
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[chokaq].[Jobs]') AND type in (N'U'))
BEGIN
    CREATE TABLE [chokaq].[Jobs](
        -- Identity
        [Id] [nvarchar](50) NOT NULL,
        [Queue] [nvarchar](50) NOT NULL DEFAULT 'default',
        [Type] [nvarchar](255) NOT NULL,
        [Payload] [nvarchar](MAX) NOT NULL,
        
        -- State Machine
        [Status] [int] NOT NULL, 
        [AttemptCount] [int] NOT NULL DEFAULT 0,
        
        -- Execution Metadata
        [Priority] [int] NOT NULL DEFAULT 10, 
        [CreatedBy] [nvarchar](100) NULL,
        [Tags] [nvarchar](1000) NULL,
        [ParentJobId] [nvarchar](50) NULL,
        [IdempotencyKey] [nvarchar](200) NULL,
        
        -- Concurrency & Diagnostics
        [WorkerId] [nvarchar](100) NULL,
        [ErrorDetails] [nvarchar](MAX) NULL,

        -- Timestamps
        [CreatedAtUtc] [datetime2](7) NOT NULL,
        [ScheduledAtUtc] [datetime2](7) NULL,
        [StartedAtUtc] [datetime2](7) NULL,
        [FinishedAtUtc] [datetime2](7) NULL,
        [LastUpdatedUtc] [datetime2](7) NOT NULL,
        
        CONSTRAINT [PK_Jobs] PRIMARY KEY CLUSTERED ([Id] ASC)
    )
END
GO

-- 3. Idempotency Index
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Jobs_Idempotency' AND object_id = OBJECT_ID('chokaq.Jobs'))
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [IX_Jobs_Idempotency] ON [chokaq].[Jobs]
    (
        [IdempotencyKey] ASC
    )
    WHERE [IdempotencyKey] IS NOT NULL
END
GO

-- 4. Worker Fetch Index (Performance Critical)
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Jobs_Fetch' AND object_id = OBJECT_ID('chokaq.Jobs'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Jobs_Fetch] ON [chokaq].[Jobs]
    (
        [Status] ASC,     
        [Priority] DESC,  
        [ScheduledAtUtc] ASC 
    )
    INCLUDE ([Queue], [Type], [AttemptCount], [CreatedAtUtc])
    WHERE ([Status] = 0)
END
GO

-- 5. Dashboard Index
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Jobs_Dashboard' AND object_id = OBJECT_ID('chokaq.Jobs'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Jobs_Dashboard] ON [chokaq].[Jobs]
    (
        [CreatedAtUtc] DESC
    )
END
GO