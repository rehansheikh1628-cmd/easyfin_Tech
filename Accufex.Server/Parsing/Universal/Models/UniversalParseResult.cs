using System;
using System.Collections.Generic;
using Accufex.Server.Parsing.Models;

namespace Accufex.Server.Parsing.Universal.Models;

/// <summary>
/// Comprehensive result contract returned by the Universal Statement Engine.
/// Reuses canonical ACCUFEX ParsedTransaction models and bridges to standard BankParsingResult.
/// </summary>
public class UniversalParseResult
{
    public bool Success { get; set; }

    public string EngineVersion { get; set; } = "Universal-v1-prototype";

    public string? DetectedBankName { get; set; }

    public string? StatementFormat { get; set; }

    public StatementStructureAnalysis? Structure { get; set; }

    public UniversalConfidenceScore Confidence { get; set; } = new();

    public FinancialValidationResult? FinancialValidation { get; set; }

    public List<ParsedTransaction> Transactions { get; set; } = [];

    public decimal? OpeningBalance { get; set; }

    public decimal? ClosingBalance { get; set; }

    public bool NeedsReview { get; set; } = true;

    public string StatusMessage { get; set; } = string.Empty;

    public List<string> Warnings { get; set; } = [];

    public List<string> Errors { get; set; } = [];

    /// <summary>
    /// Bridges the universal parse result into the canonical ACCUFEX BankParsingResult contract.
    /// Strictly guarantees financial safety: if human review is required, success is set to false.
    /// </summary>
    public BankParsingResult ToBankParsingResult()
    {
        bool isSafeToExport = Success && !NeedsReview && Confidence.Level == UniversalConfidenceLevel.High;

        return new BankParsingResult
        {
            Success = isSafeToExport,
            ErrorMessage = isSafeToExport ? null : (!string.IsNullOrWhiteSpace(StatusMessage) ? StatusMessage : "This statement format requires review before conversion."),
            BankCode = 99, // 99 designates Universal Engine processing
            BankName = !string.IsNullOrWhiteSpace(DetectedBankName) ? DetectedBankName : "Universal Statement Format",
            ParserVersion = EngineVersion,
            TotalDetected = Transactions.Count,
            ProcessedCount = Transactions.Count,
            RejectedCount = 0,
            WarningsCount = Warnings.Count,
            OverallConfidence = Confidence.OverallScore,
            Transactions = Transactions,
            Warnings = Warnings,
            Errors = Errors
        };
    }
}
