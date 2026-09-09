using System;
using System.Collections.Generic;

namespace EasyFin_Tech.Server.DTOs;

public class OriginalTransactionSnapshotDto
{
    public Guid TransactionId { get; set; }
    public DateTime TransactionDate { get; set; }
    public DateTime? ValueDate { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal? Debit { get; set; }
    public decimal? Credit { get; set; }
    public decimal Amount { get; set; }
    public decimal? Balance { get; set; }
    public string? Reference { get; set; }
    public string? Utr { get; set; }
    public string? TransactionType { get; set; }
    public int BankCode { get; set; }
    public string? Account { get; set; }
    public int SourcePageNumber { get; set; }
    public int SourceLineIndex { get; set; }
    public string ParserVersion { get; set; } = string.Empty;
    public string? ProcessingWarning { get; set; }
}

public class FieldChangeDto
{
    public string FieldName { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}

public class CorrectionHistoryEntryDto
{
    public DateTime TimestampUtc { get; set; }
    public Guid UserId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public List<FieldChangeDto> Changes { get; set; } = [];
}

public class TransactionReviewDto
{
    public Guid Id { get; set; }
    public DateTime TransactionDate { get; set; }
    public DateTime? ValueDate { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal? Debit { get; set; }
    public decimal? Credit { get; set; }
    public decimal Amount { get; set; }
    public decimal? Balance { get; set; }
    public string? Reference { get; set; }
    public string? Utr { get; set; }
    public string? TransactionType { get; set; }
    public int BankCode { get; set; }
    public string BankName { get; set; } = string.Empty;
    public string? Account { get; set; }
    public Guid SourceFileId { get; set; }
    public int SourcePageNumber { get; set; }
    public int SourceLineIndex { get; set; }
    public string ParserVersion { get; set; } = string.Empty;
    public string? ParserWarning { get; set; }

    // Phase 5 Dynamic Validation Properties (never written to ProcessingWarning)
    public string ValidationStatus { get; set; } = "VALID"; // VALID, REVIEW, INVALID, CORRECTED
    public string ValidationSeverity { get; set; } = "INFO"; // INFO, WARNING, ERROR
    public List<string> ValidationErrors { get; set; } = [];
    public List<string> ValidationWarnings { get; set; } = [];
    public string BalanceStatus { get; set; } = "NOT_CHECKABLE"; // BALANCED, MISMATCH, NOT_CHECKABLE
    public bool IsDuplicate { get; set; }
    public bool IsReadyForExport { get; set; }

    // Phase 5 Audit Properties
    public bool IsCorrected { get; set; }
    public int CorrectionCount { get; set; }
    public OriginalTransactionSnapshotDto? OriginalValues { get; set; }
    public List<CorrectionHistoryEntryDto> History { get; set; } = [];
}

public class CorrectTransactionRequest
{
    public DateTime TransactionDate { get; set; }
    public DateTime? ValueDate { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal? Debit { get; set; }
    public decimal? Credit { get; set; }
    public decimal Amount { get; set; }
    public decimal? Balance { get; set; }
    public string? Reference { get; set; }
    public string? TransactionType { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class StatementValidationSummaryDto
{
    public Guid StatementFileId { get; set; }
    public int DetectedBank { get; set; }
    public string BankName { get; set; } = string.Empty;
    public string ParserVersion { get; set; } = string.Empty;
    public int TotalTransactions { get; set; }
    public int ValidCount { get; set; }
    public int ReviewCount { get; set; }
    public int InvalidCount { get; set; }
    public int CorrectedCount { get; set; }
    public int DebitCount { get; set; }
    public int CreditCount { get; set; }
    public decimal TotalDebits { get; set; }
    public decimal TotalCredits { get; set; }
    public decimal NetMovement { get; set; }
    public int BalanceCheckableCount { get; set; }
    public int BalanceMatchedCount { get; set; }
    public int BalanceMismatchedCount { get; set; }
    public int DuplicateCount { get; set; }
    public bool IsReadyForExport { get; set; }
}

public class TransactionPagedResultDto
{
    public List<TransactionReviewDto> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
    public StatementValidationSummaryDto? Summary { get; set; }
}

public class StatementTransactionsResponse
{
    public bool Success { get; set; } = true;
    public int BankCode { get; set; }
    public string BankName { get; set; } = string.Empty;
    public string ParserVersion { get; set; } = string.Empty;
    public int TotalDetected { get; set; }
    public int ProcessedCount { get; set; }
    public int TotalCount { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;

    public List<TransactionReviewDto> Items { get; set; } = [];
    public List<TransactionReviewDto> Transactions => Items;

    public StatementValidationSummaryDto? Summary { get; set; }
}
