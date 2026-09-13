-- ============================================================================
-- EasyFin Tech — Production Database Initialization & Schema Script
-- Safe & Idempotent (can be executed repeatedly without errors or data loss)
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'EasyFin_Tech')
BEGIN
    PRINT 'Creating database [EasyFin_Tech]...';
    CREATE DATABASE [EasyFin_Tech];
END
GO

USE [EasyFin_Tech];
GO

-- 1. Users Table
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'Users')
BEGIN
    PRINT 'Creating table [Users]...';
    CREATE TABLE [Users] (
        [Id] UNIQUEIDENTIFIER NOT NULL,
        [Email] NVARCHAR(450) NOT NULL,
        [Password] NVARCHAR(MAX) NOT NULL,
        CONSTRAINT [PK_Users] PRIMARY KEY ([Id])
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Users_Email' AND object_id = OBJECT_ID(N'Users'))
BEGIN
    CREATE UNIQUE INDEX [IX_Users_Email] ON [Users] ([Email]);
END
GO

-- 2. Clients Table
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'Clients')
BEGIN
    PRINT 'Creating table [Clients]...';
    CREATE TABLE [Clients] (
        [Id] UNIQUEIDENTIFIER NOT NULL,
        [UserId] UNIQUEIDENTIFIER NOT NULL,
        [Name] NVARCHAR(MAX) NOT NULL,
        [ContactPerson] NVARCHAR(MAX) NOT NULL,
        [Email] NVARCHAR(MAX) NOT NULL,
        [Phone] NVARCHAR(MAX) NOT NULL,
        [BusinessName] NVARCHAR(MAX) NOT NULL,
        [BusinessType] NVARCHAR(MAX) NOT NULL,
        [Address] NVARCHAR(MAX) NOT NULL,
        [TaxId] NVARCHAR(MAX) NOT NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [UpdatedAt] DATETIME2 NULL,
        CONSTRAINT [PK_Clients] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Clients_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Clients_UserId' AND object_id = OBJECT_ID(N'Clients'))
BEGIN
    CREATE INDEX [IX_Clients_UserId] ON [Clients] ([UserId]);
END
GO

-- 3. FinancialYears Table
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'FinancialYears')
BEGIN
    PRINT 'Creating table [FinancialYears]...';
    CREATE TABLE [FinancialYears] (
        [Id] UNIQUEIDENTIFIER NOT NULL,
        [ClientId] UNIQUEIDENTIFIER NOT NULL,
        [DisplayName] NVARCHAR(450) NOT NULL,
        [StartDate] DATETIME2 NOT NULL,
        [EndDate] DATETIME2 NOT NULL,
        [Status] INT NOT NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [UpdatedAt] DATETIME2 NULL,
        CONSTRAINT [PK_FinancialYears] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_FinancialYears_Clients_ClientId] FOREIGN KEY ([ClientId]) REFERENCES [Clients] ([Id]) ON DELETE CASCADE
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FinancialYears_ClientId_DisplayName' AND object_id = OBJECT_ID(N'FinancialYears'))
BEGIN
    CREATE UNIQUE INDEX [IX_FinancialYears_ClientId_DisplayName] ON [FinancialYears] ([ClientId], [DisplayName]);
END
GO

-- 4. FileRecords Table
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'FileRecords')
BEGIN
    PRINT 'Creating table [FileRecords]...';
    CREATE TABLE [FileRecords] (
        [Id] UNIQUEIDENTIFIER NOT NULL,
        [ClientId] UNIQUEIDENTIFIER NOT NULL,
        [FinancialYearId] UNIQUEIDENTIFIER NOT NULL,
        [OriginalFileName] NVARCHAR(MAX) NOT NULL,
        [StoredFileName] NVARCHAR(MAX) NOT NULL,
        [Extension] NVARCHAR(MAX) NOT NULL,
        [ContentType] NVARCHAR(MAX) NOT NULL,
        [SizeBytes] BIGINT NOT NULL,
        [UploadedAt] DATETIME2 NOT NULL,
        [UpdatedAt] DATETIME2 NULL,
        [ProcessingStatus] INT NOT NULL,
        [ProcessingError] NVARCHAR(MAX) NULL,
        CONSTRAINT [PK_FileRecords] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_FileRecords_Clients_ClientId] FOREIGN KEY ([ClientId]) REFERENCES [Clients] ([Id]),
        CONSTRAINT [FK_FileRecords_FinancialYears_FinancialYearId] FOREIGN KEY ([FinancialYearId]) REFERENCES [FinancialYears] ([Id]) ON DELETE CASCADE
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileRecords_ClientId' AND object_id = OBJECT_ID(N'FileRecords'))
BEGIN
    CREATE INDEX [IX_FileRecords_ClientId] ON [FileRecords] ([ClientId]);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileRecords_FinancialYearId' AND object_id = OBJECT_ID(N'FileRecords'))
BEGIN
    CREATE INDEX [IX_FileRecords_FinancialYearId] ON [FileRecords] ([FinancialYearId]);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileRecords_ProcessingStatus' AND object_id = OBJECT_ID(N'FileRecords'))
BEGIN
    CREATE INDEX [IX_FileRecords_ProcessingStatus] ON [FileRecords] ([ProcessingStatus]);
END
GO

-- 5. PdfProcessingResults Table
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'PdfProcessingResults')
BEGIN
    PRINT 'Creating table [PdfProcessingResults]...';
    CREATE TABLE [PdfProcessingResults] (
        [Id] UNIQUEIDENTIFIER NOT NULL,
        [FileRecordId] UNIQUEIDENTIFIER NOT NULL,
        [PdfType] INT NOT NULL,
        [ExtractionMethod] INT NOT NULL,
        [PageCount] INT NOT NULL,
        [HasUsableText] BIT NOT NULL,
        [ExtractedTextStoragePath] NVARCHAR(MAX) NOT NULL,
        [ProcessedAt] DATETIME2 NOT NULL,
        CONSTRAINT [PK_PdfProcessingResults] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_PdfProcessingResults_FileRecords_FileRecordId] FOREIGN KEY ([FileRecordId]) REFERENCES [FileRecords] ([Id]) ON DELETE CASCADE
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PdfProcessingResults_FileRecordId' AND object_id = OBJECT_ID(N'PdfProcessingResults'))
BEGIN
    CREATE UNIQUE INDEX [IX_PdfProcessingResults_FileRecordId] ON [PdfProcessingResults] ([FileRecordId]);
END
GO

-- 6. TransactionImportResults Table
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'TransactionImportResults')
BEGIN
    PRINT 'Creating table [TransactionImportResults]...';
    CREATE TABLE [TransactionImportResults] (
        [Id] UNIQUEIDENTIFIER NOT NULL,
        [SourceFileId] UNIQUEIDENTIFIER NOT NULL,
        [ClientId] UNIQUEIDENTIFIER NOT NULL,
        [FinancialYearId] UNIQUEIDENTIFIER NOT NULL,
        [DetectedBank] INT NOT NULL,
        [TotalDetected] INT NOT NULL,
        [ProcessedCount] INT NOT NULL,
        [RejectedCount] INT NOT NULL,
        [WarningsCount] INT NOT NULL,
        [Status] NVARCHAR(MAX) NOT NULL,
        [ErrorMessage] NVARCHAR(MAX) NULL,
        [StartedAt] DATETIME2 NOT NULL,
        [CompletedAt] DATETIME2 NOT NULL,
        CONSTRAINT [PK_TransactionImportResults] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_TransactionImportResults_FileRecords_SourceFileId] FOREIGN KEY ([SourceFileId]) REFERENCES [FileRecords] ([Id]) ON DELETE CASCADE
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TransactionImportResults_SourceFileId' AND object_id = OBJECT_ID(N'TransactionImportResults'))
BEGIN
    CREATE UNIQUE INDEX [IX_TransactionImportResults_SourceFileId] ON [TransactionImportResults] ([SourceFileId]);
END
GO

-- 7. Transactions Table
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'Transactions')
BEGIN
    PRINT 'Creating table [Transactions]...';
    CREATE TABLE [Transactions] (
        [Id] UNIQUEIDENTIFIER NOT NULL,
        [TransactionDate] DATETIME2 NOT NULL,
        [Description] NVARCHAR(MAX) NOT NULL,
        [Debit] DECIMAL(18,2) NULL,
        [Credit] DECIMAL(18,2) NULL,
        [Amount] DECIMAL(18,2) NOT NULL,
        [Balance] DECIMAL(18,2) NULL,
        [Reference] NVARCHAR(MAX) NULL,
        [UTR] NVARCHAR(MAX) NULL,
        [TransactionType] NVARCHAR(MAX) NULL,
        [BankCode] INT NOT NULL,
        [Account] NVARCHAR(MAX) NULL,
        [SourceFileId] UNIQUEIDENTIFIER NOT NULL,
        [ClientId] UNIQUEIDENTIFIER NOT NULL,
        [FinancialYearId] UNIQUEIDENTIFIER NOT NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [ProcessingWarning] NVARCHAR(MAX) NULL,
        CONSTRAINT [PK_Transactions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Transactions_Clients_ClientId] FOREIGN KEY ([ClientId]) REFERENCES [Clients] ([Id]),
        CONSTRAINT [FK_Transactions_FileRecords_SourceFileId] FOREIGN KEY ([SourceFileId]) REFERENCES [FileRecords] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_Transactions_FinancialYears_FinancialYearId] FOREIGN KEY ([FinancialYearId]) REFERENCES [FinancialYears] ([Id])
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Transactions_BankCode' AND object_id = OBJECT_ID(N'Transactions'))
BEGIN
    CREATE INDEX [IX_Transactions_BankCode] ON [Transactions] ([BankCode]);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Transactions_ClientId' AND object_id = OBJECT_ID(N'Transactions'))
BEGIN
    CREATE INDEX [IX_Transactions_ClientId] ON [Transactions] ([ClientId]);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Transactions_FinancialYearId' AND object_id = OBJECT_ID(N'Transactions'))
BEGIN
    CREATE INDEX [IX_Transactions_FinancialYearId] ON [Transactions] ([FinancialYearId]);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Transactions_SourceFileId' AND object_id = OBJECT_ID(N'Transactions'))
BEGIN
    CREATE INDEX [IX_Transactions_SourceFileId] ON [Transactions] ([SourceFileId]);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Transactions_TransactionDate' AND object_id = OBJECT_ID(N'Transactions'))
BEGIN
    CREATE INDEX [IX_Transactions_TransactionDate] ON [Transactions] ([TransactionDate]);
END
GO

PRINT 'EasyFin Tech production database schema initialization complete.';
GO
