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
/// Deterministic statement parser for Axis Bank digital PDF statements (AXIS-v1).
/// Generic production implementation supporting dynamic column calibration, multi-line Particulars
/// reconstruction, multi-line other fields (Cheque Number, Branch SOL), pre-table and post-table
/// filtering, strict invariant date and amount parsing, and running balance validation.
/// </summary>
public class AxisStatementParser : IBankStatementParser
{
    public int BankCode => (int)BankType.Axis;
    public string BankName => "Axis Bank";
    public string ParserVersion => "AXIS-v1";

    private readonly ILogger<AxisStatementParser> _logger;

    private static readonly Regex DateTokenRegex = new(@"^\d{2}/\d{2}/\d{4}$", RegexOptions.Compiled);
    private static readonly Regex DrCrRegex = new(@"^(DR|CR)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AmountRegex = new(@"^-?[\d,]+\.\d{2}$", RegexOptions.Compiled);

    public AxisStatementParser(ILogger<AxisStatementParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(BankDetectionResult detection)
    {
        return detection != null && detection.DetectedBank == BankType.Axis && detection.IsSupported;
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

        // 1. Calibrate column geometry dynamically from detected table headers
        var columnBounds = CalibrateColumnIntervals(extraction);

        var transactions = new List<ParsedTransaction>();
        var globalWarnings = new List<string>();
        int totalRowsDetected = 0;
        int rejectedRows = 0;
        int warningsCount = 0;

        decimal? previousBalance = null;

        for (int pageIndex = 0; pageIndex < extraction.Pages.Count; pageIndex++)
        {
            var page = extraction.Pages[pageIndex];
            var pageNumber = page.PageNumber;
            var candidateRows = page.CandidateRows;

            if (candidateRows == null || candidateRows.Count == 0) continue;

            // Locate table header line on this page
            var headerIdx = candidateRows.FindIndex(r => IsTableHeaderLine(r.RawLineText));
            if (headerIdx < 0)
            {
                // Page does not contain an Axis table header (e.g. disclaimer/legend only page)
                continue;
            }

            // Skip header line and any secondary sub-header lines (e.g. "(dd/mm/yyyy)", "Date (dd/mm/yyyy)", "Number")
            int tableStartIdx = headerIdx + 1;
            while (tableStartIdx < candidateRows.Count && IsTableSubHeaderLine(candidateRows[tableStartIdx].RawLineText))
            {
                tableStartIdx++;
            }

            var tableRows = candidateRows.Skip(tableStartIdx).ToList();

            // Locate table termination / footer boundary on this page
            var endIdx = tableRows.FindIndex(r => IsTableEndOrFooter(r.RawLineText));
            if (endIdx >= 0)
            {
                tableRows = tableRows.Take(endIdx).ToList();
            }

            // Group candidate rows into individual transaction clusters using state machine
            var currentBlock = new List<PdfCandidateRow>();
            bool currentHasDate = false;
            bool currentHasAmount = false;
            double lastLineY = 0.0;

            void FlushBlock()
            {
                if (currentBlock.Count == 0) return;

                totalRowsDetected++;
                var parsedTx = ExtractTransactionFromBlock(currentBlock, columnBounds, pageNumber, previousBalance, globalWarnings);

                if (parsedTx != null)
                {
                    if (parsedTx.Balance.HasValue)
                    {
                        previousBalance = parsedTx.Balance.Value;
                    }

                    if (parsedTx.NeedsReview)
                    {
                        warningsCount++;
                    }

                    transactions.Add(parsedTx);
                }
                else
                {
                    rejectedRows++;
                }

                currentBlock.Clear();
                currentHasDate = false;
                currentHasAmount = false;
            }

            foreach (var row in tableRows)
            {
                var rowText = row.RawLineText?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(rowText)) continue;

                // Check for inline footer / disclaimer / legend lines
                if (IsTableEndOrFooter(rowText))
                {
                    break;
                }

                bool lineHasDate = row.LineFragments.Any(f =>
                    f.X >= columnBounds.TxDateMinX && f.X < columnBounds.TxDateMaxX &&
                    DateTokenRegex.IsMatch(f.Text.Trim()));

                bool lineHasAmount = row.LineFragments.Any(f =>
                    f.X >= columnBounds.AmountMinX && f.X < columnBounds.AmountMaxX &&
                    AmountRegex.IsMatch(f.Text.Trim())) &&
                    row.LineFragments.Any(f =>
                    f.X >= columnBounds.DrCrMinX && f.X < columnBounds.DrCrMaxX &&
                    DrCrRegex.IsMatch(f.Text.Trim()));

                // Determine if this row triggers the start of a NEW transaction:
                bool isNewTx = false;
                if (currentBlock.Count > 0)
                {
                    if (lineHasDate && currentHasDate)
                    {
                        isNewTx = true;
                    }
                    else if (lineHasAmount && currentHasAmount)
                    {
                        isNewTx = true;
                    }
                    else if (row.Y - lastLineY > 20.0)
                    {
                        isNewTx = true;
                    }
                }

                if (isNewTx)
                {
                    FlushBlock();
                }

                currentBlock.Add(row);
                if (lineHasDate) currentHasDate = true;
                if (lineHasAmount) currentHasAmount = true;
                lastLineY = row.Y;
            }

            FlushBlock();
        }

        bool success = transactions.Count > 0;
        string? errorMessage = success ? null : "No valid transactions could be extracted from the Axis Bank statement.";
        int processedCount = transactions.Count;
        double avgConfidence = transactions.Count > 0 ? Math.Round(transactions.Average(t => t.Confidence), 2) : 0.0;

        _logger.LogInformation("Axis-v1 statement parsing completed. Processed: {Count}, Warnings: {Warnings}, Rejected: {Rejected}",
            processedCount, warningsCount, rejectedRows);

        return new BankParsingResult
        {
            Success = success,
            ErrorMessage = errorMessage,
            BankCode = BankCode,
            BankName = BankName,
            ParserVersion = ParserVersion,
            AccountNumber = detection.AccountNumber,
            CustomerName = detection.CustomerName,
            StatementPeriodStart = detection.StatementFrom,
            StatementPeriodEnd = detection.StatementTo,
            TotalDetected = totalRowsDetected,
            ProcessedCount = processedCount,
            RejectedCount = rejectedRows,
            WarningsCount = warningsCount,
            OverallConfidence = avgConfidence,
            Transactions = transactions,
            Warnings = globalWarnings,
            Errors = []
        };
    }

    #region Column Calibration

    public record AxisColumnBounds(
        double SnoMaxX,
        double TxDateMinX,
        double TxDateMaxX,
        double ValDateMinX,
        double ValDateMaxX,
        double ParticularsMinX,
        double ParticularsMaxX,
        double AmountMinX,
        double AmountMaxX,
        double DrCrMinX,
        double DrCrMaxX,
        double BalanceMinX,
        double BalanceMaxX,
        double ChequeMinX,
        double ChequeMaxX,
        double BranchSolMinX);

    private AxisColumnBounds CalibrateColumnIntervals(PdfExtractionResult extraction)
    {
        foreach (var page in extraction.Pages)
        {
            if (page.CandidateRows == null) continue;

            foreach (var row in page.CandidateRows)
            {
                if (IsTableHeaderLine(row.RawLineText))
                {
                    var frags = row.LineFragments;

                    var snoFrag = frags.FirstOrDefault(f => f.Text.Contains("S.NO", StringComparison.OrdinalIgnoreCase) ||
                                                           f.Text.Contains("S.No", StringComparison.OrdinalIgnoreCase));
                    var txDateFrag = frags.FirstOrDefault(f => f.Text.Contains("Transaction", StringComparison.OrdinalIgnoreCase));
                    var valDateFrag = frags.FirstOrDefault(f => f.Text.Contains("Value", StringComparison.OrdinalIgnoreCase));
                    var descFrag = frags.FirstOrDefault(f => f.Text.Contains("Particulars", StringComparison.OrdinalIgnoreCase) ||
                                                            f.Text.Contains("Description", StringComparison.OrdinalIgnoreCase) ||
                                                            f.Text.Contains("Narration", StringComparison.OrdinalIgnoreCase));
                    var amtFrag = frags.FirstOrDefault(f => f.Text.Contains("Amount", StringComparison.OrdinalIgnoreCase));
                    var drcrFrag = frags.FirstOrDefault(f => f.Text.Contains("Debit/Credit", StringComparison.OrdinalIgnoreCase) ||
                                                            f.Text.Contains("DR/CR", StringComparison.OrdinalIgnoreCase));
                    var balFrag = frags.FirstOrDefault(f => f.Text.Contains("Balance", StringComparison.OrdinalIgnoreCase));
                    var chqFrag = frags.FirstOrDefault(f => f.Text.Contains("Cheque", StringComparison.OrdinalIgnoreCase));
                    var branchFrag = frags.FirstOrDefault(f => f.Text.Contains("Branch", StringComparison.OrdinalIgnoreCase) ||
                                                              f.Text.Contains("SOL", StringComparison.OrdinalIgnoreCase));

                    // Dynamically calculate boundaries from midpoints of header tokens
                    double snoMax = snoFrag != null && txDateFrag != null
                        ? Math.Round((snoFrag.X + snoFrag.Width + txDateFrag.X) / 2.0, 1)
                        : (snoFrag != null ? Math.Round(snoFrag.X + snoFrag.Width + 5.0, 1) : 35.0);
                    double txDateMin = snoMax;

                    double valDateMin = txDateFrag != null && valDateFrag != null
                        ? Math.Round((txDateFrag.X + txDateFrag.Width + valDateFrag.X) / 2.0, 1)
                        : (valDateFrag != null ? Math.Round(valDateFrag.X - 5.0, 1) : 85.0);
                    double txDateMax = valDateMin;

                    double particularsMin = valDateFrag != null && descFrag != null
                        ? Math.Round((valDateFrag.X + valDateFrag.Width + descFrag.X) / 2.0, 1)
                        : (descFrag != null ? Math.Round(descFrag.X - 8.0, 1) : 135.0);
                    double valDateMax = particularsMin;

                    double amountMin = descFrag != null && amtFrag != null
                        ? Math.Round((descFrag.X + descFrag.Width + amtFrag.X) / 2.0, 1)
                        : (amtFrag != null ? Math.Round(amtFrag.X - 10.0, 1) : 260.0);
                    double particularsMax = amountMin;

                    double drcrMin = amtFrag != null && drcrFrag != null
                        ? Math.Round((amtFrag.X + amtFrag.Width + drcrFrag.X) / 2.0, 1)
                        : (drcrFrag != null ? Math.Round(drcrFrag.X - 8.0, 1) : 312.0);
                    double amountMax = drcrMin;

                    double balanceMin = drcrFrag != null && balFrag != null
                        ? Math.Round((drcrFrag.X + drcrFrag.Width + balFrag.X) / 2.0, 1)
                        : (balFrag != null ? Math.Round(balFrag.X - 10.0, 1) : 365.0);
                    double drcrMax = balanceMin;

                    double chequeMin = balFrag != null && chqFrag != null
                        ? Math.Round((balFrag.X + balFrag.Width + chqFrag.X) / 2.0, 1)
                        : (chqFrag != null ? Math.Round(chqFrag.X - 10.0, 1) : 445.0);
                    double balanceMax = chequeMin;

                    double branchSolMin = branchFrag != null
                        ? (chqFrag != null ? Math.Round((chqFrag.X + chqFrag.Width + branchFrag.X) / 2.0, 1) : Math.Round(branchFrag.X - 10.0, 1))
                        : 500.0;
                    double chequeMax = branchSolMin;

                    return new AxisColumnBounds(
                        SnoMaxX: snoMax,
                        TxDateMinX: txDateMin,
                        TxDateMaxX: txDateMax,
                        ValDateMinX: valDateMin,
                        ValDateMaxX: valDateMax,
                        ParticularsMinX: particularsMin,
                        ParticularsMaxX: particularsMax,
                        AmountMinX: amountMin,
                        AmountMaxX: amountMax,
                        DrCrMinX: drcrMin,
                        DrCrMaxX: drcrMax,
                        BalanceMinX: balanceMin,
                        BalanceMaxX: balanceMax,
                        ChequeMinX: chequeMin,
                        ChequeMaxX: chequeMax,
                        BranchSolMinX: branchSolMin);
                }
            }
        }

        // Generic fallback boundaries calibrated for standard Axis A4 layout
        return new AxisColumnBounds(
            SnoMaxX: 35.0,
            TxDateMinX: 35.0,
            TxDateMaxX: 85.0,
            ValDateMinX: 85.0,
            ValDateMaxX: 135.0,
            ParticularsMinX: 135.0,
            ParticularsMaxX: 260.0,
            AmountMinX: 260.0,
            AmountMaxX: 312.0,
            DrCrMinX: 312.0,
            DrCrMaxX: 365.0,
            BalanceMinX: 365.0,
            BalanceMaxX: 445.0,
            ChequeMinX: 445.0,
            ChequeMaxX: 500.0,
            BranchSolMinX: 500.0);
    }

    #endregion

    #region Transaction Extraction & Validation

    private ParsedTransaction? ExtractTransactionFromBlock(
        List<PdfCandidateRow> blockRows,
        AxisColumnBounds columnBounds,
        int pageNumber,
        decimal? previousBalance,
        List<string> globalWarnings)
    {
        if (blockRows.Count == 0) return null;

        var allFrags = blockRows.SelectMany(r => r.LineFragments).ToList();

        // 1. Extract S.No (integer in S.No column)
        var snoFrag = allFrags.FirstOrDefault(f => f.X < columnBounds.SnoMaxX && int.TryParse(f.Text.Trim(), out _));
        int sourceLineNumber = snoFrag != null && int.TryParse(snoFrag.Text.Trim(), out var sno) ? sno : blockRows[0].RowIndex;

        // 2. Extract Transaction Date (strict invariant format dd/MM/yyyy)
        var txDateFrag = allFrags.FirstOrDefault(f =>
            f.X >= columnBounds.TxDateMinX && f.X < columnBounds.TxDateMaxX &&
            DateTokenRegex.IsMatch(f.Text.Trim()));

        DateTime txDate = DateTime.MinValue;
        bool hasValidTxDate = txDateFrag != null &&
                              DateTime.TryParseExact(txDateFrag.Text.Trim(), "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out txDate);

        // 3. Extract Value Date (strict invariant format dd/MM/yyyy)
        var valDateFrag = allFrags.FirstOrDefault(f =>
            f.X >= columnBounds.ValDateMinX && f.X < columnBounds.ValDateMaxX &&
            DateTokenRegex.IsMatch(f.Text.Trim()));

        DateTime valDate = txDate;
        if (valDateFrag != null && DateTime.TryParseExact(valDateFrag.Text.Trim(), "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedValDate))
        {
            valDate = parsedValDate;
        }

        // 4. Reconstruct multi-line Particulars (description) in visual top-to-bottom, left-to-right order
        var descTokens = blockRows
            .SelectMany(r => r.LineFragments
                .Where(f => f.X >= columnBounds.ParticularsMinX && f.X < columnBounds.ParticularsMaxX)
                .OrderBy(f => f.X)
                .Select(f => f.Text.Trim()))
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        string description = string.Join(" ", descTokens);

        // 5. Extract Amount
        var amtFrag = allFrags.FirstOrDefault(f =>
            f.X >= columnBounds.AmountMinX && f.X < columnBounds.AmountMaxX &&
            AmountRegex.IsMatch(f.Text.Trim()));

        decimal amount = 0m;
        bool hasValidAmount = amtFrag != null &&
                              decimal.TryParse(amtFrag.Text.Trim().Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out amount);

        // 6. Extract DR/CR Indicator
        var drcrFrag = allFrags.FirstOrDefault(f =>
            f.X >= columnBounds.DrCrMinX && f.X < columnBounds.DrCrMaxX &&
            DrCrRegex.IsMatch(f.Text.Trim()));

        string drcr = drcrFrag?.Text.Trim().ToUpperInvariant() ?? "";

        // 7. Extract Running Balance
        var balFrag = allFrags.FirstOrDefault(f =>
            f.X >= columnBounds.BalanceMinX && f.X < columnBounds.BalanceMaxX &&
            AmountRegex.IsMatch(f.Text.Trim()));

        decimal balance = 0m;
        bool hasValidBalance = balFrag != null &&
                               decimal.TryParse(balFrag.Text.Trim().Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out balance);

        // 8. Extract Cheque Number (if present in Cheque column)
        var chqTokens = blockRows
            .SelectMany(r => r.LineFragments
                .Where(f => f.X >= columnBounds.ChequeMinX && f.X < columnBounds.ChequeMaxX)
                .OrderBy(f => f.X)
                .Select(f => f.Text.Trim()))
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        string? chequeNo = chqTokens.Count > 0 ? string.Join(" ", chqTokens) : null;

        // 9. Extract Branch / SOL (if present in Branch column)
        var branchTokens = blockRows
            .SelectMany(r => r.LineFragments
                .Where(f => f.X >= columnBounds.BranchSolMinX)
                .OrderBy(f => f.X)
                .Select(f => f.Text.Trim()))
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        string? branchSol = branchTokens.Count > 0 ? string.Join(" ", branchTokens) : null;

        // 10. Map Amount + DR/CR to normalized Debit / Credit fields
        decimal? debit = null;
        decimal? credit = null;
        string transactionType = "UNKNOWN";

        if (drcr == "DR")
        {
            debit = amount;
            transactionType = "DEBIT";
        }
        else if (drcr == "CR")
        {
            credit = amount;
            transactionType = "CREDIT";
        }

        // 11. Validation and NeedsReview flagging
        var warnings = new List<string>();
        bool needsReview = false;
        double confidence = 0.95;

        if (!hasValidTxDate)
        {
            needsReview = true;
            confidence -= 0.30;
            warnings.Add("Invalid or missing transaction date.");
        }

        if (!hasValidAmount || amount <= 0m)
        {
            needsReview = true;
            confidence -= 0.30;
            warnings.Add("Invalid, missing, or zero transaction amount.");
        }

        if (string.IsNullOrEmpty(drcr) || transactionType == "UNKNOWN")
        {
            needsReview = true;
            confidence -= 0.25;
            warnings.Add("Missing or unrecognized Debit/Credit (DR/CR) indicator.");
        }

        if (!hasValidBalance)
        {
            needsReview = true;
            confidence -= 0.20;
            warnings.Add("Invalid or missing running balance.");
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            needsReview = true;
            confidence -= 0.15;
            warnings.Add("Empty transaction particulars / narration.");
        }

        // Running balance verification: Previous Balance - Debit + Credit = Current Balance
        if (previousBalance.HasValue && hasValidBalance && hasValidAmount && (debit.HasValue || credit.HasValue))
        {
            decimal expectedBal = debit.HasValue
                ? previousBalance.Value - debit.Value
                : previousBalance.Value + (credit ?? 0m);

            if (Math.Abs(expectedBal - balance) > 0.05m)
            {
                needsReview = true;
                confidence -= 0.10;
                warnings.Add($"Running balance mismatch: calculated {expectedBal:N2} vs extracted {balance:N2}.");
            }
        }

        confidence = Math.Max(0.10, Math.Min(1.0, confidence));

        // Format combined reference if cheque number or SOL exists
        string? reference = chequeNo;
        if (!string.IsNullOrWhiteSpace(branchSol) && string.IsNullOrWhiteSpace(reference))
        {
            // If cheque is empty, we can record branch SOL in metadata or keep reference clean
        }

        return new ParsedTransaction
        {
            TransactionDate = txDate,
            ValueDate = valDate,
            Description = description,
            Reference = reference,
            Debit = debit,
            Credit = credit,
            Amount = amount,
            Balance = hasValidBalance ? balance : null,
            TransactionType = transactionType,
            BankCode = BankCode,
            BankName = BankName,
            ParserVersion = ParserVersion,
            SourcePageNumber = pageNumber,
            SourceLineIndex = sourceLineNumber,
            Confidence = confidence,
            NeedsReview = needsReview,
            ReviewWarnings = warnings,
            RawSourceText = string.Join(" ", blockRows.Select(r => r.RawLineText))
        };
    }

    #endregion

    #region Structural Filtering Predicates

    private static bool IsTableHeaderLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        bool hasSno = text.Contains("S.NO", StringComparison.OrdinalIgnoreCase) || text.Contains("S.No", StringComparison.OrdinalIgnoreCase);
        bool hasParticulars = text.Contains("Particulars", StringComparison.OrdinalIgnoreCase);
        bool hasTxDate = text.Contains("Transaction Date", StringComparison.OrdinalIgnoreCase) || text.Contains("Transaction", StringComparison.OrdinalIgnoreCase);
        bool hasAmount = text.Contains("Amount(INR)", StringComparison.OrdinalIgnoreCase) || text.Contains("Amount", StringComparison.OrdinalIgnoreCase);
        bool hasDrCr = text.Contains("Debit/Credit", StringComparison.OrdinalIgnoreCase) || text.Contains("DR/CR", StringComparison.OrdinalIgnoreCase);
        bool hasBalance = text.Contains("Balance(INR)", StringComparison.OrdinalIgnoreCase) || text.Contains("Balance", StringComparison.OrdinalIgnoreCase);

        return hasParticulars && (hasTxDate || hasSno || hasAmount || hasDrCr || hasBalance);
    }

    private static bool IsTableSubHeaderLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        return text.Contains("(dd/mm/yyyy)", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("Date (dd/mm/yyyy) Number", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("Date", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("Number", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTableEndOrFooter(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        return text.Contains("TRANSACTION TOTAL", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Closing Balance:", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Cheque Return Details", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Unless the constituent notifies the bank immediately", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("REGISTERED OFFICE AXIS BANK LTD", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Legend :", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("End of Report", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("In case of any clarification regarding this transaction", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("With effect from 1st August 2016", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Deposit Insurance and Credit Guarantee Corporation", StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
