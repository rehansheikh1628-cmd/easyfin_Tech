using System;
using System.Collections.Generic;

namespace Accufex.Server.Parsing.Universal.Models;

/// <summary>
/// Persisted review state for an unapproved unknown-bank statement.
/// Contains complete structural evidence, detected columns, candidate transactions,
/// and mathematical reconciliation metrics.
/// </summary>
public class UniversalReviewSession
{
    public Guid FileRecordId { get; set; }

    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Review status: "ReviewRequired", "Approved", "Converted", "Rejected"
    /// </summary>
    public string Status { get; set; } = "ReviewRequired";

    public string? DetectedBank { get; set; } = "Unknown Bank";

    public StatementStructureAnalysis? Structure { get; set; }

    public List<DetectedColumnLayout> Columns { get; set; } = [];

    public UniversalConfidenceScore Confidence { get; set; } = new();

    public FinancialValidationResult FinancialValidation { get; set; } = new();

    public List<UniversalCandidateTransactionDto> CandidateTransactions { get; set; } = [];

    public List<string> Warnings { get; set; } = [];

    public List<int> RowsRequiringAttention { get; set; } = [];

    public bool IsApprovalRequired { get; set; } = true;

    public bool IsConversionAllowed { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
