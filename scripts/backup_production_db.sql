-- ============================================================================
-- EasyFin Tech — Production SQL Server Database Backup Script
-- Supports Full, Differential, and Transaction Log Backups
-- Compatible with all SQL Server Editions (Enterprise, Standard, Express, Dev)
-- ============================================================================

USE [master];
GO

-- Configuration Parameters (adjust target folder according to your production environment)
DECLARE @DatabaseName NVARCHAR(128) = N'EasyFin_Tech';
DECLARE @BackupRootDir NVARCHAR(256) = N'C:\SQLBackups\EasyFin_Tech';
DECLARE @BackupType NVARCHAR(10) = N'FULL'; -- Options: 'FULL', 'DIFF', 'LOG'
DECLARE @RetentionDays INT = 30; -- Automated retention guideline

-- 1. Ensure Target Backup Directory Exists via xp_create_subdir
EXEC master.dbo.xp_create_subdir @BackupRootDir;

-- 2. Detect Backup Compression Support (Supported in Standard & Enterprise; not Express)
DECLARE @EngineEdition INT = CAST(SERVERPROPERTY('EngineEdition') AS INT);
DECLARE @CompressionClause NVARCHAR(50) = CASE WHEN @EngineEdition IN (2, 3) THEN N', COMPRESSION' ELSE N'' END;

-- 3. Format Timestamp: YYYYMMDD_HHMMSS
DECLARE @Timestamp NVARCHAR(20) = REPLACE(REPLACE(REPLACE(CONVERT(NVARCHAR(20), GETDATE(), 120), '-', ''), ':', ''), ' ', '_');
DECLARE @BackupFileName NVARCHAR(512);
DECLARE @BackupFilePath NVARCHAR(1024);
DECLARE @BackupDescription NVARCHAR(256);
DECLARE @BackupSql NVARCHAR(MAX);

IF @BackupType = N'FULL'
BEGIN
    SET @BackupFileName = @DatabaseName + N'_FULL_' + @Timestamp + N'.bak';
    SET @BackupFilePath = @BackupRootDir + N'\' + @BackupFileName;
    SET @BackupDescription = N'Full Backup of ' + @DatabaseName + N' at ' + CONVERT(NVARCHAR(30), GETDATE(), 120);

    PRINT 'Starting FULL database backup to: ' + @BackupFilePath;
    SET @BackupSql = N'BACKUP DATABASE [' + @DatabaseName + N'] TO DISK = @path WITH FORMAT, INIT, NAME = @desc, STATS = 10, CHECKSUM' + @CompressionClause;
    
    EXEC sp_executesql @BackupSql, N'@path NVARCHAR(1024), @desc NVARCHAR(256)', @path = @BackupFilePath, @desc = @BackupDescription;

    -- Verify the backup file integrity immediately
    PRINT 'Verifying FULL backup file integrity...';
    RESTORE VERIFYONLY
    FROM DISK = @BackupFilePath
    WITH CHECKSUM;

    PRINT 'FULL backup completed and verified successfully.';
END
ELSE IF @BackupType = N'DIFF'
BEGIN
    SET @BackupFileName = @DatabaseName + N'_DIFF_' + @Timestamp + N'.bak';
    SET @BackupFilePath = @BackupRootDir + N'\' + @BackupFileName;
    SET @BackupDescription = N'Differential Backup of ' + @DatabaseName + N' at ' + CONVERT(NVARCHAR(30), GETDATE(), 120);

    PRINT 'Starting DIFFERENTIAL database backup to: ' + @BackupFilePath;
    SET @BackupSql = N'BACKUP DATABASE [' + @DatabaseName + N'] TO DISK = @path WITH DIFFERENTIAL, FORMAT, INIT, NAME = @desc, STATS = 10, CHECKSUM' + @CompressionClause;

    EXEC sp_executesql @BackupSql, N'@path NVARCHAR(1024), @desc NVARCHAR(256)', @path = @BackupFilePath, @desc = @BackupDescription;

    PRINT 'Verifying DIFFERENTIAL backup file integrity...';
    RESTORE VERIFYONLY
    FROM DISK = @BackupFilePath
    WITH CHECKSUM;

    PRINT 'DIFFERENTIAL backup completed and verified successfully.';
END
ELSE IF @BackupType = N'LOG'
BEGIN
    SET @BackupFileName = @DatabaseName + N'_LOG_' + @Timestamp + N'.trn';
    SET @BackupFilePath = @BackupRootDir + N'\' + @BackupFileName;
    SET @BackupDescription = N'Transaction Log Backup of ' + @DatabaseName + N' at ' + CONVERT(NVARCHAR(30), GETDATE(), 120);

    PRINT 'Starting TRANSACTION LOG backup to: ' + @BackupFilePath;
    SET @BackupSql = N'BACKUP LOG [' + @DatabaseName + N'] TO DISK = @path WITH FORMAT, INIT, NAME = @desc, STATS = 10, CHECKSUM' + @CompressionClause;

    EXEC sp_executesql @BackupSql, N'@path NVARCHAR(1024), @desc NVARCHAR(256)', @path = @BackupFilePath, @desc = @BackupDescription;

    PRINT 'Verifying TRANSACTION LOG backup file integrity...';
    RESTORE VERIFYONLY
    FROM DISK = @BackupFilePath
    WITH CHECKSUM;

    PRINT 'TRANSACTION LOG backup completed and verified successfully.';
END
GO

-- ============================================================================
-- Retention Policy & Maintenance Guidelines:
-- 1. Full Backups: Run daily at 01:00 AM.
-- 2. Differential Backups: Run every 6 hours (07:00, 13:00, 19:00).
-- 3. Transaction Log Backups: Run every 15 minutes (if Full Recovery Model).
-- 4. Retention Cleanup: Retain 30 days of full backups on primary backup volume.
-- 5. Off-site Replication: Sync C:\SQLBackups to immutable cloud/NAS storage.
-- ============================================================================
