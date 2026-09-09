using System;

namespace EasyFin_Tech.Server.Validation.Models;

/// <summary>
/// Immutable snapshot of a transaction exactly as originally parsed from the bank statement document.
/// Created at initial statement parse/import and NEVER overwritten or mutated.
/// </summary>
public class OriginalTransactionSnapshot
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

    public Guid SourceFileId { get; set; }

    public Guid ClientId { get; set; }

    public Guid FinancialYearId { get; set; }

    public int SourcePageNumber { get; set; }

    public int SourceLineIndex { get; set; }

    public string ParserVersion { get; set; } = string.Empty;

    public string? ProcessingWarning { get; set; }
}
