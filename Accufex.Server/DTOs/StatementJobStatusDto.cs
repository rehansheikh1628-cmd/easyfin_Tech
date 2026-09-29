using System;

namespace Accufex.Server.DTOs;

public class StatementJobStatusDto
{
    public Guid JobId { get; set; }

    public Guid FileId { get; set; }

    public string FileName { get; set; } = null!;

    public string Status { get; set; } = null!; // "Queued", "Processing", "Completed", "Failed", "Cancelled", "RequiresPassword"

    public int ProcessingStatus { get; set; }

    public int StatusCode => ProcessingStatus;

    public int Progress { get; set; }

    public int ProgressPercent => Progress;

    public string? Stage { get; set; }

    public string? ErrorMessage { get; set; }

    public bool RequiresPassword { get; set; }

    public bool IsTerminal => Status is "Completed" or "Failed" or "Cancelled";

    public string? DetectedBankName { get; set; }

    public int? TransactionCount { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}

public class UnlockJobRequest
{
    public string Password { get; set; } = null!;
}
