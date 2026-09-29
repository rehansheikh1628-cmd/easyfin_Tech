using System;
using System.Collections.Generic;

namespace Accufex.Server.Models;

public partial class TransactionImportResult
{
    public Guid Id { get; set; }

    public Guid SourceFileId { get; set; }

    public Guid ClientId { get; set; }

    public Guid FinancialYearId { get; set; }

    public int DetectedBank { get; set; }

    public int TotalDetected { get; set; }

    public int ProcessedCount { get; set; }

    public int RejectedCount { get; set; }

    public int WarningsCount { get; set; }

    public string Status { get; set; } = null!;

    public string? ErrorMessage { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime CompletedAt { get; set; }

    public virtual FileRecord SourceFile { get; set; } = null!;
}
