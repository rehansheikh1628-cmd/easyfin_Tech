using System;
using System.Collections.Generic;

namespace Accufex.Server.DTOs;

public class DashboardSummaryDto
{
    public string Status { get; set; } = "Online / Connected";
    public string Database { get; set; } = "Accufex";
    public string Server { get; set; } = @"localhost\SQLEXPRESS";
    public bool CanConnect { get; set; } = true;
    public int TotalStatements { get; set; }
    public int CompletedStatements { get; set; }
    public int StatementsProcessed { get; set; } // Backward-compatible alias for CompletedStatements
    public int ProcessingStatements { get; set; }
    public int FailedStatements { get; set; }
    public int TotalTransactions { get; set; }
    public int ClientProfiles { get; set; } = 1;
    public List<DashboardRecentStatementDto> RecentStatements { get; set; } = [];
    public object? Tables { get; set; }
}

public class DashboardRecentStatementDto
{
    public Guid Id { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string? BankName { get; set; }
    public DateTime UploadedAt { get; set; }
    public string Status { get; set; } = "ReadyForProcessing";
    public int ProcessingStatus { get; set; }
    public int TransactionCount { get; set; }
    public long FileSizeBytes { get; set; }
    public string FileSizeFormatted { get; set; } = string.Empty;
}
