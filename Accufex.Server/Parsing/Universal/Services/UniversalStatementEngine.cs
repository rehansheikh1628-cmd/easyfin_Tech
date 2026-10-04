using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Accufex.Server.DTOs;
using Accufex.Server.Parsing.Models;
using Accufex.Server.Parsing.Universal.Interfaces;
using Accufex.Server.Parsing.Universal.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Accufex.Server.Parsing.Universal.Services;

/// <summary>
/// Primary coordinator service for the Universal Statement Engine (Phase 2).
/// Performs dynamic table row clustering, coordinate-aware column extraction,
/// multi-line narration aggregation, and sequential financial balance validation.
/// </summary>
public class UniversalStatementEngine : IUniversalStatementEngine
{
    private readonly IStatementStructureAnalyzer _structureAnalyzer;
    private readonly IUniversalConfidenceCalculator _confidenceCalculator;
    private readonly IUniversalRowSegmenter _rowSegmenter;
    private readonly IFinancialValidator _financialValidator;
    private readonly ILogger<UniversalStatementEngine> _logger;

    private static readonly Regex DateRegex = new(
        @"^(?:\d{1,2}[/-]\d{1,2}[/-]\d{2,4}|\d{1,2}\s+[A-Za-z]{3}\s+\d{2,4}|\d{1,2}-[A-Za-z]{3}-\d{2,4}|\d{1,2}\.[A-Za-z]{3}\.\d{2,4}|\d{4}-\d{2}-\d{2}|\d{1,2}\.\d{1,2}\.\d{2,4})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AmountRegex = new(
        @"^(?:(?:₹|INR|Rs\.?|\$)\s*)?-?[\d,]+(?:\.\d{1,2})?(?:\s*(?:Cr\.?|Dr\.?))?$|^\((?:(?:₹|INR|Rs\.?|\$)\s*)?[\d,]+(?:\.\d{1,2})?\)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public UniversalStatementEngine(
        IStatementStructureAnalyzer structureAnalyzer,
        IUniversalConfidenceCalculator confidenceCalculator,
        ILogger<UniversalStatementEngine> logger,
        IUniversalRowSegmenter? rowSegmenter = null,
        IFinancialValidator? financialValidator = null)
    {
        _structureAnalyzer = structureAnalyzer;
        _confidenceCalculator = confidenceCalculator;
        _logger = logger;
        _rowSegmenter = rowSegmenter ?? new UniversalRowSegmenter(NullLogger<UniversalRowSegmenter>.Instance);
        _financialValidator = financialValidator ?? new FinancialValidator(NullLogger<FinancialValidator>.Instance);
    }

    public Task<UniversalParseResult> ProcessStatementAsync(
        PdfExtractionResult extraction,
        BankDetectionResult detection,
        CancellationToken cancellationToken = default)
    {
        var result = new UniversalParseResult
        {
            EngineVersion = "Universal-v2-extraction",
            DetectedBankName = !string.IsNullOrWhiteSpace(detection?.BankName) && detection.BankName != "Unknown"
                ? detection.BankName
                : null
        };

        if (extraction == null || extraction.Pages == null || extraction.Pages.Count == 0 || !extraction.HasUsableText)
        {
            result.Success = false;
            result.NeedsReview = true;
            result.StatusMessage = "The statement contains no usable digital text. Scanned or rasterized PDFs cannot be parsed.";
            result.Confidence = new UniversalConfidenceScore
            {
                OverallScore = 0.0,
                Level = UniversalConfidenceLevel.Low,
                ReviewReasons = ["No digital text blocks detected in PDF."]
            };
            return Task.FromResult(result);
        }

        // 1. Discover Statement Structure & Column Boundaries
        var structure = _structureAnalyzer.AnalyzeStructure(extraction);
        result.Structure = structure;
        result.DetectedBankName ??= structure.PotentialBankName;
        result.StatementFormat = "Universal-Tabular-v2";

        // 2. Structural Sufficiency Gate
        if (!structure.HasSufficientColumnsForExtraction)
        {
            _logger.LogInformation("Universal Engine: Insufficient structural evidence to extract transactions automatically.");

            result.Confidence = _confidenceCalculator.CalculateConfidence(structure, [], null, null);
            result.Success = false;
            result.NeedsReview = true;
            result.StatusMessage = "This statement format requires review before conversion.";
            result.Warnings.AddRange(structure.StructuralWarnings);
            return Task.FromResult(result);
        }

        // 3. Scan for Document Opening / Closing Balance Metadata
        decimal? openingBalance = null;
        decimal? closingBalance = null;

        foreach (var page in extraction.Pages)
        {
            var pRows = page.CandidateRows ?? [];
            foreach (var r in pRows)
            {
                var text = r.RawLineText?.Trim() ?? string.Empty;
                if (text.Contains("Opening Balance", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("Brought Forward", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("B/F", StringComparison.OrdinalIgnoreCase))
                {
                    var amtWord = r.LineFragments.LastOrDefault(f => AmountRegex.IsMatch(f.Text.Trim()));
                    if (amtWord != null && TryParseAmount(amtWord.Text, out var obVal))
                    {
                        openingBalance ??= obVal;
                    }
                }
                else if (text.Contains("Closing Balance", StringComparison.OrdinalIgnoreCase) ||
                         text.Contains("Carried Forward", StringComparison.OrdinalIgnoreCase) ||
                         text.Contains("C/F", StringComparison.OrdinalIgnoreCase))
                {
                    var amtWord = r.LineFragments.LastOrDefault(f => AmountRegex.IsMatch(f.Text.Trim()));
                    if (amtWord != null && TryParseAmount(amtWord.Text, out var cbVal))
                    {
                        closingBalance ??= cbVal;
                    }
                }
            }
        }

        // 4. Phase 2A: Dynamic Row / Transaction Boundary Detection (Multi-line Aware)
        var segmentedRows = _rowSegmenter.SegmentDocumentRows(extraction.Pages, structure);

        // 5. Phase 2B: Coordinate-Aware Column Extraction
        var dateCol = structure.Columns.FirstOrDefault(c => c.ColumnType == StatementColumnType.Date);
        var valDateCol = structure.Columns.FirstOrDefault(c => c.ColumnType == StatementColumnType.ValueDate);
        var descCol = structure.Columns.FirstOrDefault(c => c.ColumnType == StatementColumnType.Description);
        var refCol = structure.Columns.FirstOrDefault(c => c.ColumnType == StatementColumnType.Reference);
        var debitCol = structure.Columns.FirstOrDefault(c => c.ColumnType == StatementColumnType.Debit);
        var creditCol = structure.Columns.FirstOrDefault(c => c.ColumnType == StatementColumnType.Credit);
        var amountCol = structure.Columns.FirstOrDefault(c => c.ColumnType == StatementColumnType.SignedAmount);
        var indicatorCol = structure.Columns.FirstOrDefault(c => c.ColumnType == StatementColumnType.Indicator);
        var balCol = structure.Columns.FirstOrDefault(c => c.ColumnType == StatementColumnType.Balance);

        var transactions = new List<ParsedTransaction>();
        var warnings = new List<string>();
        decimal? previousRowBalance = openingBalance;

        foreach (var candidateRow in segmentedRows)
        {
            string? dateText = null;
            string? valDateText = null;
            string? refText = null;
            string? debitText = null;
            string? creditText = null;
            string? amountText = null;
            string? indicatorText = null;
            string? balText = null;
            var descWords = new List<string>();

            // Group fragments according to their horizontal X-coordinate
            var frags = candidateRow.Fragments.OrderBy(f => f.Y).ThenBy(f => f.X).ToList();

            foreach (var f in frags)
            {
                var fragText = f.Text.Trim();
                if (string.IsNullOrWhiteSpace(fragText) || fragText == "-" || fragText == "--") continue;

                var matchedCol = FindMatchingColumn(f.X, structure.Columns);
                if (matchedCol == null)
                {
                    descWords.Add(fragText);
                    continue;
                }

                switch (matchedCol.ColumnType)
                {
                    case StatementColumnType.Date:
                        if (DateRegex.IsMatch(fragText)) dateText = fragText;
                        break;
                    case StatementColumnType.ValueDate:
                        if (DateRegex.IsMatch(fragText)) valDateText = fragText;
                        break;
                    case StatementColumnType.Reference:
                        refText = string.IsNullOrWhiteSpace(refText) ? fragText : $"{refText} {fragText}";
                        break;
                    case StatementColumnType.Debit:
                        if (AmountRegex.IsMatch(fragText)) debitText = fragText;
                        break;
                    case StatementColumnType.Credit:
                        if (AmountRegex.IsMatch(fragText)) creditText = fragText;
                        break;
                    case StatementColumnType.SignedAmount:
                        if (AmountRegex.IsMatch(fragText)) amountText = fragText;
                        break;
                    case StatementColumnType.Indicator:
                        indicatorText = fragText;
                        break;
                    case StatementColumnType.Balance:
                        if (AmountRegex.IsMatch(fragText)) balText = fragText;
                        break;
                    default:
                        descWords.Add(fragText);
                        break;
                }
            }

            // Fallback for primary date from segmented row
            dateText ??= candidateRow.PrimaryDateText;

            if (string.IsNullOrWhiteSpace(dateText) || !TryParseDate(dateText, out var parsedDate))
            {
                continue;
            }

            // Determine Debit and Credit values safely
            decimal? debit = null;
            decimal? credit = null;

            if (!string.IsNullOrWhiteSpace(debitText) && TryParseAmount(debitText, out var dVal) && dVal > 0)
            {
                debit = dVal;
            }

            if (!string.IsNullOrWhiteSpace(creditText) && TryParseAmount(creditText, out var cVal) && cVal > 0)
            {
                credit = cVal;
            }

            // Handle signed amount column if separate debit/credit columns are absent
            if (!debit.HasValue && !credit.HasValue && !string.IsNullOrWhiteSpace(amountText) && TryParseAmount(amountText, out var singleAmt))
            {
                var absAmt = Math.Abs(singleAmt);
                if (!string.IsNullOrWhiteSpace(indicatorText))
                {
                    var ind = indicatorText.ToLowerInvariant();
                    if (ind.Contains("dr")) debit = absAmt;
                    else if (ind.Contains("cr")) credit = absAmt;
                }
                else if (singleAmt < 0)
                {
                    debit = absAmt;
                }
                else if (balCol != null && !string.IsNullOrWhiteSpace(balText) && TryParseAmount(balText, out var currB) && previousRowBalance.HasValue)
                {
                    // Determine direction from running balance delta
                    if (currB < previousRowBalance.Value && Math.Abs((previousRowBalance.Value - currB) - absAmt) <= 0.05m)
                    {
                        debit = absAmt;
                    }
                    else if (currB > previousRowBalance.Value && Math.Abs((currB - previousRowBalance.Value) - absAmt) <= 0.05m)
                    {
                        credit = absAmt;
                    }
                }
            }

            // Parse Balance
            decimal? balance = null;
            if (!string.IsNullOrWhiteSpace(balText) && TryParseAmount(balText, out var bVal))
            {
                balance = bVal;
                previousRowBalance = balance;
            }

            // If neither debit nor credit could be ascertained, flag warning
            if (!debit.HasValue && !credit.HasValue)
            {
                warnings.Add($"Row {candidateRow.RowIndex} ({parsedDate:dd/MM/yyyy}): Debit/Credit direction could not be determined safely.");
            }

            var tx = new ParsedTransaction
            {
                TransactionDate = parsedDate,
                ValueDate = !string.IsNullOrWhiteSpace(valDateText) && TryParseDate(valDateText, out var vd) ? vd : null,
                Description = descWords.Count > 0 ? string.Join(" ", descWords) : "Transaction",
                Debit = debit,
                Credit = credit,
                Amount = debit ?? credit ?? 0m,
                Balance = balance,
                Reference = refText,
                TransactionType = credit.HasValue && credit.Value > 0 ? "Credit" : "Debit",
                BankCode = 99,
                BankName = result.DetectedBankName ?? "Unknown Bank",
                ParserVersion = result.EngineVersion,
                SourcePageNumber = candidateRow.PageNumber,
                SourceLineIndex = candidateRow.RowIndex,
                Confidence = 0.85
            };

            transactions.Add(tx);
        }

        result.Transactions = transactions;
        result.OpeningBalance = openingBalance;
        result.ClosingBalance = closingBalance ?? transactions.LastOrDefault()?.Balance;

        // 6. Phase 2C: Financial Validation (Sequential Balance Reconciliation)
        var validation = _financialValidator.Validate(transactions, openingBalance, result.ClosingBalance);
        result.FinancialValidation = validation;

        // 7. Calculate Objective Evidence-Based Confidence
        var confidence = _confidenceCalculator.CalculateConfidence(structure, transactions, openingBalance, result.ClosingBalance, validation);
        result.Confidence = confidence;

        // 8. Phase 2 Safety Policy:
        // Transactions are marked as reviewable unless confidence is High AND sequential balance is fully reconciled.
        if (confidence.Level == UniversalConfidenceLevel.High && validation.IsFullyReconciled && transactions.Count > 0)
        {
            result.Success = true;
            result.NeedsReview = false;
            result.StatusMessage = "Statement format analyzed successfully with verified balance continuity.";
        }
        else
        {
            result.Success = false;
            result.NeedsReview = true;
            result.StatusMessage = "This statement format requires review before conversion.";
            warnings.AddRange(confidence.ReviewReasons);
        }

        result.Warnings = warnings;

        _logger.LogInformation("Universal Engine Phase 2 complete. TxCount: {Count}, Confidence: {ConfLevel} ({Score:P0}), Reconciled: {Reconciled}/{Total}, NeedsReview: {NeedsReview}",
            transactions.Count, confidence.Level, confidence.OverallScore, validation.ReconciledRowsCount, validation.TotalRowsChecked, result.NeedsReview);

        return Task.FromResult(result);
    }

    private static DetectedColumnLayout? FindMatchingColumn(double x, List<DetectedColumnLayout> columns)
    {
        if (columns == null || columns.Count == 0) return null;

        var sorted = columns.OrderBy(c => c.LeftX).ToList();

        for (int i = 0; i < sorted.Count; i++)
        {
            var col = sorted[i];
            double nextLeft = i + 1 < sorted.Count ? sorted[i + 1].LeftX : double.MaxValue;

            if (x >= col.LeftX - 10.0 && x < nextLeft - 5.0)
            {
                return col;
            }
        }

        if (x >= sorted[^1].LeftX - 10.0)
        {
            return sorted[^1];
        }

        return null;
    }

    private static bool TryParseAmount(string text, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string clean = text.Trim()
            .Replace("₹", "")
            .Replace("INR", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Rs.", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Rs", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Cr.", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Dr.", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Cr", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Dr", "", StringComparison.OrdinalIgnoreCase)
            .Replace(",", "")
            .Trim();

        if (clean.StartsWith('(') && clean.EndsWith(')'))
        {
            clean = "-" + clean.Substring(1, clean.Length - 2).Trim();
        }

        // Avoid parsing phone numbers / 12-digit account numbers without decimals as currency amounts
        if (clean.Length > 10 && !clean.Contains('.'))
        {
            return false;
        }

        return decimal.TryParse(clean, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount);
    }

    private static bool TryParseDate(string text, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string clean = text.Trim();
        string[] formats =
        [
            "dd/MM/yyyy", "dd-MM-yyyy", "dd.MM.yyyy",
            "d/M/yyyy", "d-M-yyyy", "d.M.yyyy",
            "dd/MM/yy", "dd-MM-yy", "dd.MM.yy",
            "d/M/yy", "d-M-yy", "d.M.yy",
            "dd MMM yyyy", "dd-MMM-yyyy", "dd/MMM/yyyy",
            "d MMM yyyy", "d-MMM-yyyy", "d/MMM/yyyy",
            "dd MMMM yyyy", "d MMMM yyyy",
            "yyyy-MM-dd", "yyyy/MM/dd", "yyyy.MM.dd"
        ];

        return DateTime.TryParseExact(clean, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
