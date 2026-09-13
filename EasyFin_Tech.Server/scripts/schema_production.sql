CREATE TABLE [Users] (
    [Id] uniqueidentifier NOT NULL,
    [Email] nvarchar(450) NOT NULL,
    [Password] nvarchar(max) NOT NULL,
    CONSTRAINT [PK_Users] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [Clients] (
    [Id] uniqueidentifier NOT NULL,
    [UserId] uniqueidentifier NOT NULL,
    [Name] nvarchar(max) NOT NULL,
    [ContactPerson] nvarchar(max) NOT NULL,
    [Email] nvarchar(max) NOT NULL,
    [Phone] nvarchar(max) NOT NULL,
    [BusinessName] nvarchar(max) NOT NULL,
    [BusinessType] nvarchar(max) NOT NULL,
    [Address] nvarchar(max) NOT NULL,
    [TaxId] nvarchar(max) NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NULL,
    CONSTRAINT [PK_Clients] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Clients_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [FinancialYears] (
    [Id] uniqueidentifier NOT NULL,
    [ClientId] uniqueidentifier NOT NULL,
    [DisplayName] nvarchar(450) NOT NULL,
    [StartDate] datetime2 NOT NULL,
    [EndDate] datetime2 NOT NULL,
    [Status] int NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NULL,
    CONSTRAINT [PK_FinancialYears] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_FinancialYears_Clients_ClientId] FOREIGN KEY ([ClientId]) REFERENCES [Clients] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [FileRecords] (
    [Id] uniqueidentifier NOT NULL,
    [ClientId] uniqueidentifier NOT NULL,
    [FinancialYearId] uniqueidentifier NOT NULL,
    [OriginalFileName] nvarchar(max) NOT NULL,
    [StoredFileName] nvarchar(max) NOT NULL,
    [Extension] nvarchar(max) NOT NULL,
    [ContentType] nvarchar(max) NOT NULL,
    [SizeBytes] bigint NOT NULL,
    [UploadedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NULL,
    [ProcessingStatus] int NOT NULL,
    [ProcessingError] nvarchar(max) NULL,
    CONSTRAINT [PK_FileRecords] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_FileRecords_Clients_ClientId] FOREIGN KEY ([ClientId]) REFERENCES [Clients] ([Id]),
    CONSTRAINT [FK_FileRecords_FinancialYears_FinancialYearId] FOREIGN KEY ([FinancialYearId]) REFERENCES [FinancialYears] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [PdfProcessingResults] (
    [Id] uniqueidentifier NOT NULL,
    [FileRecordId] uniqueidentifier NOT NULL,
    [PdfType] int NOT NULL,
    [ExtractionMethod] int NOT NULL,
    [PageCount] int NOT NULL,
    [HasUsableText] bit NOT NULL,
    [ExtractedTextStoragePath] nvarchar(max) NOT NULL,
    [ProcessedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_PdfProcessingResults] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_PdfProcessingResults_FileRecords_FileRecordId] FOREIGN KEY ([FileRecordId]) REFERENCES [FileRecords] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [TransactionImportResults] (
    [Id] uniqueidentifier NOT NULL,
    [SourceFileId] uniqueidentifier NOT NULL,
    [ClientId] uniqueidentifier NOT NULL,
    [FinancialYearId] uniqueidentifier NOT NULL,
    [DetectedBank] int NOT NULL,
    [TotalDetected] int NOT NULL,
    [ProcessedCount] int NOT NULL,
    [RejectedCount] int NOT NULL,
    [WarningsCount] int NOT NULL,
    [Status] nvarchar(max) NOT NULL,
    [ErrorMessage] nvarchar(max) NULL,
    [StartedAt] datetime2 NOT NULL,
    [CompletedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_TransactionImportResults] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_TransactionImportResults_FileRecords_SourceFileId] FOREIGN KEY ([SourceFileId]) REFERENCES [FileRecords] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [Transactions] (
    [Id] uniqueidentifier NOT NULL,
    [TransactionDate] datetime2 NOT NULL,
    [Description] nvarchar(max) NOT NULL,
    [Debit] decimal(18,2) NULL,
    [Credit] decimal(18,2) NULL,
    [Amount] decimal(18,2) NOT NULL,
    [Balance] decimal(18,2) NULL,
    [Reference] nvarchar(max) NULL,
    [UTR] nvarchar(max) NULL,
    [TransactionType] nvarchar(max) NULL,
    [BankCode] int NOT NULL,
    [Account] nvarchar(max) NULL,
    [SourceFileId] uniqueidentifier NOT NULL,
    [ClientId] uniqueidentifier NOT NULL,
    [FinancialYearId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [ProcessingWarning] nvarchar(max) NULL,
    CONSTRAINT [PK_Transactions] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Transactions_Clients_ClientId] FOREIGN KEY ([ClientId]) REFERENCES [Clients] ([Id]),
    CONSTRAINT [FK_Transactions_FileRecords_SourceFileId] FOREIGN KEY ([SourceFileId]) REFERENCES [FileRecords] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_Transactions_FinancialYears_FinancialYearId] FOREIGN KEY ([FinancialYearId]) REFERENCES [FinancialYears] ([Id])
);
GO


CREATE INDEX [IX_Clients_UserId] ON [Clients] ([UserId]);
GO


CREATE INDEX [IX_FileRecords_ClientId] ON [FileRecords] ([ClientId]);
GO


CREATE INDEX [IX_FileRecords_FinancialYearId] ON [FileRecords] ([FinancialYearId]);
GO


CREATE INDEX [IX_FileRecords_ProcessingStatus] ON [FileRecords] ([ProcessingStatus]);
GO


CREATE UNIQUE INDEX [IX_FinancialYears_ClientId_DisplayName] ON [FinancialYears] ([ClientId], [DisplayName]);
GO


CREATE UNIQUE INDEX [IX_PdfProcessingResults_FileRecordId] ON [PdfProcessingResults] ([FileRecordId]);
GO


CREATE UNIQUE INDEX [IX_TransactionImportResults_SourceFileId] ON [TransactionImportResults] ([SourceFileId]);
GO


CREATE INDEX [IX_Transactions_BankCode] ON [Transactions] ([BankCode]);
GO


CREATE INDEX [IX_Transactions_ClientId] ON [Transactions] ([ClientId]);
GO


CREATE INDEX [IX_Transactions_FinancialYearId] ON [Transactions] ([FinancialYearId]);
GO


CREATE INDEX [IX_Transactions_SourceFileId] ON [Transactions] ([SourceFileId]);
GO


CREATE INDEX [IX_Transactions_TransactionDate] ON [Transactions] ([TransactionDate]);
GO


CREATE UNIQUE INDEX [IX_Users_Email] ON [Users] ([Email]);
GO


