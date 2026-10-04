using System;
using System.Collections.Generic;
using Accufex.Server.Parsing.Universal.Models;

namespace Accufex.Server.DTOs;

public class UniversalReviewDto
{
    public Guid FileId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string Status { get; set; } = "ReviewRequired";

    public string? DetectedBank { get; set; }

    public UniversalConfidenceScore Confidence { get; set; } = new();

    public FinancialValidationResult FinancialValidation { get; set; } = new();

    public List<DetectedColumnLayout> Columns { get; set; } = [];

    public List<UniversalCandidateTransactionDto> Transactions { get; set; } = [];

    public List<string> Warnings { get; set; } = [];

    public List<int> RowsRequiringAttention { get; set; } = [];

    public bool IsApprovalRequired { get; set; } = true;

    public bool IsConversionAllowed { get; set; }

    public int TotalTransactions => Transactions.Count;

    public int AttentionCount => RowsRequiringAttention.Count;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class CorrectUniversalTransactionRequest
{
    public DateTime? TransactionDate { get; set; }

    public DateTime? ValueDate { get; set; }

    public string Description { get; set; } = string.Empty;

    public string? Reference { get; set; }

    public decimal? Debit { get; set; }

    public decimal? Credit { get; set; }

    public decimal? Balance { get; set; }

    public string? Reason { get; set; }
}

public class UpdateUniversalColumnsRequest
{
    public List<DetectedColumnLayout> Columns { get; set; } = [];
}

public class ApproveUniversalReviewResponse
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    public Guid FileId { get; set; }

    public int ConvertedCount { get; set; }

    public string? LearnedFingerprintHash { get; set; }
}
