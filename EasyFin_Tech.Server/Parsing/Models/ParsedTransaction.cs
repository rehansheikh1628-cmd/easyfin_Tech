using System;
using System.Collections.Generic;

namespace EasyFin_Tech.Server.Parsing.Models;

/// <summary>
/// Normalized bank transaction model representing a single validated financial movement.
/// Directly aligns with the canonical database schema and retains extraction traceability.
/// </summary>
public class ParsedTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime TransactionDate { get; set; }

    public DateTime? ValueDate { get; set; }

    public string Description { get; set; } = string.Empty;

    public decimal? Debit { get; set; }

    public decimal? Credit { get; set; }

    public decimal Amount { get; set; }

    public decimal? Balance { get; set; }

    public string? Reference { get; set; }

    public string? Utr { get; set; }

    public string TransactionType { get; set; } = string.Empty; // "Debit" or "Credit"

    public int BankCode { get; set; }

    public string BankName { get; set; } = string.Empty;

    public string ParserVersion { get; set; } = string.Empty;

    public int SourcePageNumber { get; set; }

    public int SourceLineIndex { get; set; }

    public double Confidence { get; set; } = 1.0;

    public bool NeedsReview { get; set; }

    public List<string> ReviewWarnings { get; set; } = [];

    public string RawSourceText { get; set; } = string.Empty;
}
