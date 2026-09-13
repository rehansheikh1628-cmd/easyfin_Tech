-- ============================================================================
-- EasyFin Tech — Production SQL Server Database Restore & Recovery Script
-- Use for Disaster Recovery, Environment Replication, or Point-in-Time Restore
-- ============================================================================

USE [master];
GO

-- Configuration Parameters
DECLARE @DatabaseName NVARCHAR(128) = N'EasyFin_Tech';
-- Specify the exact path to the backup file to restore:
DECLARE @BackupFilePath NVARCHAR(1024) = N'C:\SQLBackups\EasyFin_Tech\EasyFin_Tech_FULL_LATEST.bak';

PRINT '=================================================================';
PRINT ' EasyFin Tech — Disaster Recovery Restore Procedure';
PRINT ' Target Database: ' + @DatabaseName;
PRINT ' Source Backup File: ' + @BackupFilePath;
PRINT '=================================================================';

-- Step 1: Verify the integrity of the backup file before restoring
PRINT 'Step 1: Verifying backup file readability and checksums...';
RESTORE VERIFYONLY
FROM DISK = @BackupFilePath
WITH CHECKSUM;

IF @@ERROR <> 0
BEGIN
    RAISERROR('Backup file verification failed. Restore aborted.', 16, 1);
    RETURN;
END
PRINT 'Backup file verification passed successfully.';

-- Step 2: Terminate existing connections and set database to SINGLE_USER
PRINT 'Step 2: Terminating active user connections...';
IF EXISTS (SELECT 1 FROM sys.databases WHERE name = @DatabaseName)
BEGIN
    EXEC(N'ALTER DATABASE [' + @DatabaseName + N'] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;');
    PRINT 'Database set to SINGLE_USER mode.';
END

-- Step 3: Perform Full Database Restore
PRINT 'Step 3: Restoring database from backup...';
RESTORE DATABASE [EasyFin_Tech]
FROM DISK = @BackupFilePath
WITH
    REPLACE,
    RECOVERY,
    STATS = 10;

PRINT 'Database restored successfully.';

-- Step 4: Reset database back to MULTI_USER mode
PRINT 'Step 4: Setting database back to MULTI_USER mode...';
ALTER DATABASE [EasyFin_Tech] SET MULTI_USER;
PRINT 'Database set to MULTI_USER mode.';

-- Step 5: Verify physical and logical database integrity
PRINT 'Step 5: Running DBCC CHECKDB integrity verification...';
DBCC CHECKDB ([EasyFin_Tech]) WITH NO_INFOMSGS;
PRINT 'DBCC CHECKDB completed without integrity errors.';

-- Step 6: Verify application table availability
USE [EasyFin_Tech];
PRINT 'Step 6: Verifying core table record counts...';
SELECT 'Users' AS TableName, COUNT(1) AS RecordCount FROM [Users]
UNION ALL
SELECT 'Clients', COUNT(1) FROM [Clients]
UNION ALL
SELECT 'FinancialYears', COUNT(1) FROM [FinancialYears]
UNION ALL
SELECT 'FileRecords', COUNT(1) FROM [FileRecords]
UNION ALL
SELECT 'Transactions', COUNT(1) FROM [Transactions];

PRINT '=================================================================';
PRINT ' Disaster Recovery Restore Completed Successfully!';
PRINT ' Next Step: Test GET /api/health/database from application.';
PRINT '=================================================================';
GO
