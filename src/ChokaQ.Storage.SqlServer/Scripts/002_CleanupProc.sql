-- ==========================================================
-- ChokaQ Cleanup Procedure
-- 
-- Efficiently removes old job history using Batch Deletion.
-- Prevents transaction log explosion and table locking.
-- ==========================================================

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[chokaq].[sp_CleanupJobs]') AND type in (N'P', N'PC'))
BEGIN
    EXEC('CREATE PROCEDURE [chokaq].[sp_CleanupJobs] AS BEGIN SET NOCOUNT ON; END')
END
GO

ALTER PROCEDURE [chokaq].[sp_CleanupJobs]
    @SucceededRetentionDays int = 7,  -- Keep success history for 1 week
    @FailedRetentionDays int = 30,    -- Keep failure history for 1 month (for debugging)
    @BatchSize int = 1000             -- Rows to delete per transaction
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @RowsAffected int;
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
        FROM [chokaq].[Jobs]
        WHERE (Status = 2 OR Status = 4) -- Succeeded or Cancelled
          AND FinishedAtUtc < @SucceededCutoff;
        
        SET @RowsAffected = @@ROWCOUNT;
        
        COMMIT TRANSACTION;
        
        -- Optional: Small sleep to let other queries breathe
        -- WAITFOR DELAY '00:00:00.050'; 
    END

    -- ======================================================
    -- 2. Cleanup FAILED (3) Jobs
    -- ======================================================
    SET @RowsAffected = 1;
    
    WHILE @RowsAffected > 0
    BEGIN
        BEGIN TRANSACTION;
        
        DELETE TOP (@BatchSize) 
        FROM [chokaq].[Jobs]
        WHERE Status = 3 -- Failed
          AND FinishedAtUtc < @FailedCutoff;
        
        SET @RowsAffected = @@ROWCOUNT;
        
        COMMIT TRANSACTION;
    END

    -- Return 0 to indicate success
    RETURN 0;
END
GO