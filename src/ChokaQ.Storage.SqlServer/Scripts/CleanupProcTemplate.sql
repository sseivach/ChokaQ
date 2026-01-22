-- ==========================================================
-- ChokaQ Cleanup Procedure Template
-- ==========================================================
-- Efficiently removes old job history using Batch Deletion logic.
-- This prevents transaction log explosion and table locking issues.
-- Note: Uses "CREATE OR ALTER" (requires SQL Server 2016+).
-- ==========================================================

CREATE OR ALTER PROCEDURE [{SCHEMA}].[sp_CleanupJobs]
    @SucceededRetentionDays int = 7,  -- Keep successful jobs for 1 week
    @FailedRetentionDays int = 30,    -- Keep failed jobs for 1 month (for debugging)
    @BatchSize int = 1000             -- Rows to delete per transaction
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @RowsAffected int;
    
    -- Calculate cutoff dates
    DECLARE @SucceededCutoff datetime2(7) = DATEADD(day, -@SucceededRetentionDays, SYSUTCDATETIME());
    DECLARE @FailedCutoff datetime2(7) = DATEADD(day, -@FailedRetentionDays, SYSUTCDATETIME());

    -- ======================================================
    -- 1. Cleanup SUCCEEDED (2) and CANCELLED (4) Jobs
    -- ======================================================
    SET @RowsAffected = 1;
    
    WHILE @RowsAffected > 0
    BEGIN
        BEGIN TRANSACTION;
        
        -- Delete in small batches to avoid locking the table for too long
        DELETE TOP (@BatchSize) 
        FROM [{SCHEMA}].[Jobs]
        WHERE (Status = 2 OR Status = 4) -- Succeeded or Cancelled
          AND FinishedAtUtc < @SucceededCutoff;
        
        SET @RowsAffected = @@ROWCOUNT;
        
        COMMIT TRANSACTION;
        
    END

    -- ======================================================
    -- 2. Cleanup FAILED (3) Jobs
    -- ======================================================
    SET @RowsAffected = 1;
    
    WHILE @RowsAffected > 0
    BEGIN
        BEGIN TRANSACTION;
        
        DELETE TOP (@BatchSize) 
        FROM [{SCHEMA}].[Jobs]
        WHERE Status = 3 -- Failed
          AND FinishedAtUtc < @FailedCutoff;
        
        SET @RowsAffected = @@ROWCOUNT;
        
        COMMIT TRANSACTION;
    END

    RETURN 0;
END
GO