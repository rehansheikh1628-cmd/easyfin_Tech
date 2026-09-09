using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using EasyFin_Tech.Server.DTOs;
using EasyFin_Tech.Server.Parsing.Interfaces;
using EasyFin_Tech.Server.Parsing.Models;
using Microsoft.Extensions.Logging;

namespace EasyFin_Tech.Server.Parsing.Parsers;

/// <summary>
/// Deterministic statement parser for Central Bank of India statements (CENTRAL-v1).
/// Generic production implementation supporting multi-line transaction narration,
/// cross-subpage boundary narration reconstruction, strict dd/MM/yy date parsing,
/// Indian currency number formatting, Dr balance semantics (negative decimals for CC/OD accounts),
/// rate change exclusion, and running balance reconciliation.
/// </summary>
public class CentralBankStatementParser : IBankStatementParser
{
    public int BankCode => (int)BankType.CentralBank;
    public string BankName => "Central Bank of India";
    public string ParserVersion => "CENTRAL-v1";

    private readonly ILogger<CentralBankStatementParser> _logger;

    private static readonly Regex DateTokenRegex = new(@"^\d{2}/\d{2}/\d{2}$", RegexOptions.Compiled);
    private static readonly Regex Date4TokenRegex = new(@"^\d{2}/\d{2}/\d{4}$", RegexOptions.Compiled);

    public CentralBankStatementParser(ILogger<CentralBankStatementParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(BankDetectionResult detection)
    {
        return detection != null && detection.DetectedBank == BankType.CentralBank && detection.IsSupported;
    }

    public BankParsingResult Parse(PdfExtractionResult extraction, BankDetectionResult detection)
    {
        if (extraction == null || extraction.Pages == null || extraction.Pages.Count == 0)
        {
            return new BankParsingResult
            {
                Success = false,
                ErrorMessage = "Extraction result contains no pages to parse.",
                BankCode = BankCode,
                BankName = BankName,
                ParserVersion = ParserVersion
            };
        }

        // Calibrate column geometry from headers or use robust calibrated defaults
        var columnBounds = CalibrateColumnIntervals(extraction);

        var transactions = new List<ParsedTransaction>();
        var globalWarnings = new List<string>();
        int totalRowsDetected = 0;
        int rejectedRows = 0;
        int warningsCount = 0;

        decimal? previousBalance = null;

        // Flatten rows preserving natural reading order across all pages
        var allRows = extraction.Pages
            .OrderBy(p => p.PageNumber)
            .SelectMany(p => p.CandidateRows.OrderBy(r => r.Y))
            .ToList();

        TxAccumulator? currentTx = null;

        void FinalizeCurrentTx()
        {
            if (currentTx == null) return;

            var tx = currentTx;
            currentTx = null;

            // Must have at least a Debit, Credit, or Balance to be a genuine financial transaction
            if (string.IsNullOrWhiteSpace(tx.DebitStr) &&
                string.IsNullOrWhiteSpace(tx.CreditStr) &&
                string.IsNullOrWhiteSpace(tx.BalanceStr))
            {
                rejectedRows++;
                return;
            }

            var parsedTx = BuildParsedTransaction(tx, ref previousBalance, ref warningsCount);
            if (parsedTx != null)
            {
                totalRowsDetected++;
                transactions.Add(parsedTx);
            }
            else
            {
                rejectedRows++;
            }
        }

        foreach (var row in allRows)
        {
            var rowText = row.RawLineText?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(rowText)) continue;

            // 1. Skip non-transaction statement headers, metadata, and summaries
            if (IsNonTransactionMetadata(rowText))
            {
                continue;
            }

            // 2. Identify start of a transaction:
            // A transaction line starts with two date tokens: Value Date and Post Date (X < 75 and X in [75..125])
            var frags = row.LineFragments;
            bool startsWithTwoDates = frags.Count >= 2 &&
                (DateTokenRegex.IsMatch(frags[0].Text) || Date4TokenRegex.IsMatch(frags[0].Text)) &&
                (DateTokenRegex.IsMatch(frags[1].Text) || Date4TokenRegex.IsMatch(frags[1].Text)) &&
                frags[0].X < columnBounds.PostDateMinX &&
                frags[1].X >= columnBounds.PostDateMinX && frags[1].X < columnBounds.DetailsMinX;

            if (startsWithTwoDates)
            {
                // Check if this row is a Base Rate Change note (e.g. BS RT CHG or CA RT CHG) without amounts
                bool isRateChange = rowText.Contains("BS RT CHG", StringComparison.OrdinalIgnoreCase) ||
                                    rowText.Contains("CA RT CHG", StringComparison.OrdinalIgnoreCase);

                if (isRateChange)
                {
                    // Rate change row is informational metadata, not a financial transaction
                    continue;
                }

                // Finalize the previous transaction if one is pending
                FinalizeCurrentTx();

                // Start new transaction
                currentTx = new TxAccumulator
                {
                    PageNumber = row.PageNumber,
                    RowIndex = row.RowIndex,
                    RawLineText = rowText,
                    ValueDateStr = frags[0].Text,
                    PostDateStr = frags[1].Text
                };

                // Assign tokens across the columns
                foreach (var frag in frags.Skip(2))
                {
                    AssignTokenToColumn(currentTx, frag, columnBounds);
                }

                // If this dated line has no debit, no credit, and no balance, it is a rate change parameter row
                if (string.IsNullOrWhiteSpace(currentTx.DebitStr) &&
                    string.IsNullOrWhiteSpace(currentTx.CreditStr) &&
                    string.IsNullOrWhiteSpace(currentTx.BalanceStr))
                {
                    currentTx = null;
                }
            }
            else if (currentTx != null)
            {
                // This is a continuation line!
                // Append narration tokens and catch continuation other fields
                foreach (var frag in frags)
                {
                    double x = frag.X;
                    if (x >= columnBounds.DetailsMinX && x < columnBounds.ChqMinX)
                    {
                        currentTx.NarrationTokens.Add(frag.Text);
                    }
                    else if (x >= columnBounds.ChqMinX && x < columnBounds.DebitMinX && string.IsNullOrEmpty(currentTx.ChequeNo))
                    {
                        currentTx.ChequeNo = frag.Text;
                    }
                    else if (x >= columnBounds.DebitMinX && x < columnBounds.CreditMinX && string.IsNullOrEmpty(currentTx.DebitStr))
                    {
                        currentTx.DebitStr = frag.Text;
                    }
                    else if (x >= columnBounds.CreditMinX && x < columnBounds.BalanceMinX && string.IsNullOrEmpty(currentTx.CreditStr))
                    {
                        currentTx.CreditStr = frag.Text;
                    }
                    else if (x >= columnBounds.BalanceMinX && string.IsNullOrEmpty(currentTx.BalanceStr))
                    {
                        currentTx.BalanceStr = frag.Text;
                    }
                }
            }
        }

        // Finalize the last transaction
        FinalizeCurrentTx();

        bool success = transactions.Count > 0;
        string? errorMessage = success ? null : "No valid transactions could be extracted from the Central Bank of India statement.";
        int processedCount = transactions.Count;
        double avgConfidence = transactions.Count > 0 ? Math.Round(transactions.Average(t => t.Confidence), 2) : 0.0;

        _logger.LogInformation("Central Bank statement parsing completed. Processed: {Count}, Warnings: {Warnings}, Rejected: {Rejected}",
            processedCount, warningsCount, rejectedRows);

        return new BankParsingResult
        {
            Success = success,
            ErrorMessage = errorMessage,
            BankCode = BankCode,
            BankName = BankName,
            ParserVersion = ParserVersion,
            AccountNumber = detection?.AccountNumber,
            CustomerName = detection?.CustomerName,
            StatementPeriodStart = detection?.StatementFrom,
            StatementPeriodEnd = detection?.StatementTo,
            TotalDetected = totalRowsDetected,
            ProcessedCount = processedCount,
            RejectedCount = rejectedRows,
            WarningsCount = warningsCount,
            OverallConfidence = avgConfidence,
            Transactions = transactions,
            Warnings = globalWarnings
        };
    }

    private static void AssignTokenToColumn(TxAccumulator tx, PdfTextBlock frag, CentralBankColumnBounds bounds)
    {
        double x = frag.X;

        if (x >= bounds.DetailsMinX && x < bounds.ChqMinX)
        {
            tx.NarrationTokens.Add(frag.Text);
        }
        else if (x >= bounds.ChqMinX && x < bounds.DebitMinX)
        {
            tx.ChequeNo = frag.Text;
        }
        else if (x >= bounds.DebitMinX && x < bounds.CreditMinX)
        {
            tx.DebitStr = frag.Text;
        }
        else if (x >= bounds.CreditMinX && x < bounds.BalanceMinX)
        {
            tx.CreditStr = frag.Text;
        }
        else if (x >= bounds.BalanceMinX)
        {
            tx.BalanceStr = frag.Text;
        }
    }

    private ParsedTransaction? BuildParsedTransaction(
        TxAccumulator tx,
        ref decimal? previousBalance,
        ref int warningsCount)
    {
        // 1. Parse Transaction Date (Post Date takes priority, fallback to Value Date)
        DateTime txDate;
        if (!TryParseCentralBankDate(tx.PostDateStr, out txDate))
        {
            if (!TryParseCentralBankDate(tx.ValueDateStr, out txDate))
            {
                return null;
            }
        }

        DateTime? valDate = null;
        if (TryParseCentralBankDate(tx.ValueDateStr, out var parsedValDate))
        {
            valDate = parsedValDate;
        }

        // 2. Parse Debit, Credit, and Balance
        decimal? debit = ParseAmount(tx.DebitStr);
        decimal? credit = ParseAmount(tx.CreditStr);
        decimal? balance = ParseBalance(tx.BalanceStr);

        // A valid transaction must have a debit or credit, or an explicit balance
        if (!debit.HasValue && !credit.HasValue && !balance.HasValue)
        {
            return null;
        }

        decimal amount;
        string txType;

        if (debit.HasValue && debit.Value > 0)
        {
            amount = debit.Value;
            txType = "Debit";
        }
        else if (credit.HasValue && credit.Value > 0)
        {
            amount = credit.Value;
            txType = "Credit";
        }
        else if (debit.HasValue)
        {
            // Zero debit (e.g. cheque return 0.00)
            amount = 0m;
            txType = "Debit";
        }
        else
        {
            amount = credit ?? 0m;
            txType = "Credit";
        }

        // 3. Clean Narration
        string narration = string.Join(" ", tx.NarrationTokens).Trim();
        narration = Regex.Replace(narration, @"\s+", " ");

        // 4. Running Balance Validation
        var warnings = new List<string>();
        if (previousBalance.HasValue && balance.HasValue)
        {
            // Mathematical formula for Cash Credit / overdraft account with negative Dr balances:
            // Expected Balance = Previous Balance - Debit + Credit
            decimal expectedBalance = previousBalance.Value - (debit ?? 0m) + (credit ?? 0m);
            decimal discrepancy = Math.Abs(expectedBalance - balance.Value);

            if (discrepancy > 0.01m)
            {
                warnings.Add($"Running balance mismatch. Expected: {expectedBalance:F2}, Reported: {balance.Value:F2} (Diff: {discrepancy:F2}).");
                warningsCount++;
            }
        }

        if (balance.HasValue)
        {
            previousBalance = balance.Value;
        }

        return new ParsedTransaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = txDate,
            ValueDate = valDate,
            Description = narration,
            Debit = debit,
            Credit = credit,
            Amount = amount,
            Balance = balance,
            Reference = string.IsNullOrWhiteSpace(tx.ChequeNo) ? null : tx.ChequeNo.Trim(),
            TransactionType = txType,
            BankCode = BankCode,
            BankName = BankName,
            ParserVersion = ParserVersion,
            SourcePageNumber = tx.PageNumber,
            SourceLineIndex = tx.RowIndex,
            Confidence = 1.0,
            NeedsReview = warnings.Count > 0,
            ReviewWarnings = warnings,
            RawSourceText = tx.RawLineText
        };
    }

    private static bool TryParseCentralBankDate(string? dateStr, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(dateStr)) return false;

        string trimmed = dateStr.Trim();
        string[] formats = ["dd/MM/yy", "dd/MM/yyyy", "d/M/yy", "d/M/yyyy"];

        return DateTime.TryParseExact(
            trimmed,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    private static decimal? ParseAmount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Clean Indian number formatting with commas and whitespace
        string cleaned = text.Replace(",", "").Trim();
        if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var amt))
        {
            return amt;
        }

        return null;
    }

    private static decimal? ParseBalance(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        string trimmed = text.Trim();
        bool isDr = trimmed.EndsWith("Dr", StringComparison.OrdinalIgnoreCase);

        string cleaned = trimmed
            .Replace("Dr", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Cr", "", StringComparison.OrdinalIgnoreCase)
            .Replace(",", "")
            .Trim();

        if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var amt))
        {
            // In Cash Credit (overdrawn) accounts, Dr suffix denotes a negative balance
            return isDr ? -amt : amt;
        }

        return null;
    }

    private static bool IsNonTransactionMetadata(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;

        if (text.StartsWith("STATEMENT OF ACCOUNT", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Branch Code", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Account No.", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Cleared Balance", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Limit :", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Statement From", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Page No.", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Value Post Details", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Date Date", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("____", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Statement Summary", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("In Case Your Account", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("****Toll Free", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("*** END OF", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("BROUGHT FORWARD", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("CARRIED FORWARD", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("CLOSING BALANCE", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static CentralBankColumnBounds CalibrateColumnIntervals(PdfExtractionResult extraction)
    {
        // Locate column header line across pages: "Value Post Details Chq.No. Debit Credit Balance"
        foreach (var page in extraction.Pages)
        {
            foreach (var row in page.CandidateRows)
            {
                var frags = row.LineFragments;
                var valFrag = frags.FirstOrDefault(f => f.Text.Equals("Value", StringComparison.OrdinalIgnoreCase));
                var postFrag = frags.FirstOrDefault(f => f.Text.Equals("Post", StringComparison.OrdinalIgnoreCase));
                var detailsFrag = frags.FirstOrDefault(f => f.Text.StartsWith("Detail", StringComparison.OrdinalIgnoreCase));
                var chqFrag = frags.FirstOrDefault(f => f.Text.StartsWith("Chq", StringComparison.OrdinalIgnoreCase));
                var debitFrag = frags.FirstOrDefault(f => f.Text.Equals("Debit", StringComparison.OrdinalIgnoreCase));
                var creditFrag = frags.FirstOrDefault(f => f.Text.Equals("Credit", StringComparison.OrdinalIgnoreCase));
                var balFrag = frags.FirstOrDefault(f => f.Text.StartsWith("Balance", StringComparison.OrdinalIgnoreCase));

                if (postFrag != null && detailsFrag != null && debitFrag != null && creditFrag != null && balFrag != null)
                {
                    double postMin = postFrag.X - 8.0;
                    double detailsMin = detailsFrag.X - 25.0;
                    double chqMin = chqFrag != null ? chqFrag.X - 3.0 : 248.0;
                    double debitMin = debitFrag.X - 20.0;
                    double creditMin = creditFrag.X - 20.0;
                    double balMin = balFrag.X - 20.0;

                    return new CentralBankColumnBounds(
                        PostDateMinX: postMin,
                        DetailsMinX: detailsMin,
                        ChqMinX: chqMin,
                        DebitMinX: debitMin,
                        CreditMinX: creditMin,
                        BalanceMinX: balMin
                    );
                }
            }
        }

        // Calibrated defaults derived from source PDF coordinates
        return new CentralBankColumnBounds(
            PostDateMinX: 75.0,
            DetailsMinX: 110.0,
            ChqMinX: 248.0,
            DebitMinX: 285.0,
            CreditMinX: 370.0,
            BalanceMinX: 460.0
        );
    }

    private sealed record CentralBankColumnBounds(
        double PostDateMinX,
        double DetailsMinX,
        double ChqMinX,
        double DebitMinX,
        double CreditMinX,
        double BalanceMinX);

    private sealed class TxAccumulator
    {
        public int PageNumber { get; set; }
        public int RowIndex { get; set; }
        public string RawLineText { get; set; } = string.Empty;
        public string ValueDateStr { get; set; } = string.Empty;
        public string PostDateStr { get; set; } = string.Empty;
        public string ChequeNo { get; set; } = string.Empty;
        public string DebitStr { get; set; } = string.Empty;
        public string CreditStr { get; set; } = string.Empty;
        public string BalanceStr { get; set; } = string.Empty;
        public List<string> NarrationTokens { get; set; } = [];
    }
}
