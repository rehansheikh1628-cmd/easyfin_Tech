USE [master];
GO

PRINT '--- 1. Restoring EasyFin_Tech_RecoveryTest from Backup ---';
RESTORE DATABASE [EasyFin_Tech_RecoveryTest]
FROM DISK = 'C:\SQLBackups\EasyFin_Tech\EasyFin_Tech_FULL_20260912_230241.bak'
WITH
    MOVE 'EasyFin_Tech' TO 'C:\Program Files\Microsoft SQL Server\MSSQL17.SQLEXPRESS\MSSQL\DATA\EasyFin_Tech_RecoveryTest.mdf',
    MOVE 'EasyFin_Tech_log' TO 'C:\Program Files\Microsoft SQL Server\MSSQL17.SQLEXPRESS\MSSQL\DATA\EasyFin_Tech_RecoveryTest_log.ldf',
    REPLACE,
    RECOVERY;
GO

PRINT '--- 2. Verifying Tables and Records in Restored Database ---';
USE [EasyFin_Tech_RecoveryTest];
GO

SELECT 'Users' AS TableName, COUNT(1) AS RecordCount FROM [Users]
UNION ALL
SELECT 'Clients', COUNT(1) FROM [Clients]
UNION ALL
SELECT 'FinancialYears', COUNT(1) FROM [FinancialYears]
UNION ALL
SELECT 'FileRecords', COUNT(1) FROM [FileRecords]
UNION ALL
SELECT 'Transactions', COUNT(1) FROM [Transactions];
GO

PRINT '--- 3. Running DBCC CHECKDB on Restored Database ---';
DBCC CHECKDB ([EasyFin_Tech_RecoveryTest]) WITH NO_INFOMSGS;
GO

PRINT '--- 4. Cleaning Up Recovery Test Database ---';
USE [master];
GO
ALTER DATABASE [EasyFin_Tech_RecoveryTest] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE [EasyFin_Tech_RecoveryTest];
GO
PRINT '--- Recovery Test Completed Successfully! ---';
