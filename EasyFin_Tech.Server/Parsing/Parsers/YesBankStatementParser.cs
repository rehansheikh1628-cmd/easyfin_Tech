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
/// Deterministic statement parser for YES BANK digital PDF statements (YES-v1).
/// Supports dynamic column calibration, multi-line narration reconstruction with pre-anchor
/// and post-anchor geometry, cross-page boundary transactions, pre-table and post-table filtering,
/// strict invariant date & amount parsing, and sweep-account batch validation.
/// </summary>
public class YesBankStatementParser : IBankStatementParser
{
    public int BankCode => (int)BankType.YesBank;
    public string BankName => "YES BANK";
    public string ParserVersion => "YES-v1";

    private readonly ILogger<YesBankStatementParser> _logger;

    private static readonly Regex DateTokenRegex = new(@"^\d{2}-[A-Za-z]{3}-\d{4}$", RegexOptions.Compiled);
    private static readonly Regex DateTokenFallbackRegex = new(@"^\d{2}/\d{2}/\d{2,4}$", RegexOptions.Compiled);

    public YesBankStatementParser(ILogger<YesBankStatementParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(BankDetectionResult detection)
    {
        return detection != null && detection.DetectedBank == BankType.YesBank && detection.IsSupported;
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

        // 1. Calibrate column geometry from the first page containing the table header
        var columnBounds = CalibrateColumnIntervals(extraction);

        var transactions = new List<ParsedTransaction>();
        ParsedTransaction? pendingTx = null;
        var pendingPrefixWords = new List<string>();
        string? pendingPrefixRef = null;
        var globalWarnings = new List<string>();
        int totalRowsDetected = 0;
        int rejectedRows = 0;
        int warningsCount = 0;

        decimal? previousBalance = null;
        double lastLineY = 0.0;
        int lastLinePage = 0;

        for (int pageIndex = 0; pageIndex < extraction.Pages.Count; pageIndex++)
        {
            var page = extraction.Pages[pageIndex];
            var pageNumber = page.PageNumber;

            var lines = GetSortedVisualLines(page);

            bool reachedSummary = false;
            bool inTransactionZone = false;

            for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                var line = lines[lineIndex];
                var lineText = line.Text.Trim();
                if (string.IsNullOrWhiteSpace(lineText)) continue;

                // Check for summary footer line (marks end of table on that page)
                if (IsSummaryFooterLine(lineText))
                {
                    reachedSummary = true;
                    inTransactionZone = false;
                    continue;
                }

                if (reachedSummary)
                {
                    continue;
                }

                // Check for legal disclaimer / terms / glossary
                if (IsDisclaimerOrFooter(lineText))
                {
                    inTransactionZone = false;
                    continue;
                }

                // Check for table header line
                if (IsTableHeaderLine(lineText))
                {
                    inTransactionZone = true;
                    continue;
                }

                if (!inTransactionZone)
                {
                    // Pre-table customer metadata or repeated page headers before table start
                    continue;
                }

                // Inside transaction zone: determine if this line starts a new transaction
                var dateFragment = FindTransactionDateFragment(line, columnBounds);
                bool isTxStart = dateFragment != null;

                if (isTxStart)
                {
                    totalRowsDetected++;

                    // Finalize any previously pending transaction
                    if (pendingTx != null)
                    {
                        ValidateAndFinalizeTransaction(pendingTx, previousBalance, globalWarnings);
                        previousBalance = pendingTx.Balance;
                        transactions.Add(pendingTx);
                        if (pendingTx.NeedsReview) warningsCount++;
                        pendingTx = null;
                    }

                    // Extract all column fields for the new transaction
                    var newTx = ExtractTransactionRow(line, dateFragment!, columnBounds, pageNumber, lineIndex + 1);

                    // Prepend any pre-anchor description fragments that were collected just above this anchor
                    if (pendingPrefixWords.Count > 0)
                    {
                        var prefix = string.Join(" ", pendingPrefixWords).Trim();
                        newTx.Description = string.IsNullOrWhiteSpace(newTx.Description)
                            ? prefix
                            : $"{prefix} {newTx.Description}".Trim();
                        pendingPrefixWords.Clear();
                    }

                    if (string.IsNullOrEmpty(newTx.Reference) && !string.IsNullOrEmpty(pendingPrefixRef))
                    {
                        newTx.Reference = pendingPrefixRef;
                        pendingPrefixRef = null;
                    }

                    pendingTx = newTx;
                    lastLineY = line.Y;
                    lastLinePage = pageNumber;
                }
                else
                {
                    // Continuation line without a date token in the Date column
                    // In top-down coordinates (increasing Y):
                    // Look ahead for the next TxStart on this page
                    VisualLineModel? nextTxLine = null;
                    for (int nextIdx = lineIndex + 1; nextIdx < lines.Count; nextIdx++)
                    {
                        var nl = lines[nextIdx];
                        var nlt = nl.Text.Trim();
                        if (IsSummaryFooterLine(nlt) || IsDisclaimerOrFooter(nlt)) break;

                        var nextDateFrag = FindTransactionDateFragment(nl, columnBounds);
                        if (nextDateFrag != null)
                        {
                            nextTxLine = nl;
                            break;
                        }
                    }

                    // Collect fragments from description column
                    var descFragments = line.Fragments
                        .Where(f => f.X >= columnBounds.DescMinX && f.X < columnBounds.WithdrawalMinX)
                        .OrderBy(f => f.X)
                        .Select(f => f.Text.Trim())
                        .Where(t => !string.IsNullOrEmpty(t))
                        .ToList();

                    // Also check for wrapped reference fragments if reference column is present
                    var refFragments = line.Fragments
                        .Where(f => f.X >= columnBounds.RefMinX && f.X < columnBounds.DescMinX)
                        .OrderBy(f => f.X)
                        .Select(f => f.Text.Trim())
                        .Where(t => !string.IsNullOrEmpty(t))
                        .ToList();

                    bool hasTransferIndicator = line.Text.Contains("NEFT", StringComparison.OrdinalIgnoreCase) ||
                                                line.Text.Contains("YESBN", StringComparison.OrdinalIgnoreCase) ||
                                                line.Text.Contains("RTGS", StringComparison.OrdinalIgnoreCase) ||
                                                line.Text.Contains("IMPS", StringComparison.OrdinalIgnoreCase) ||
                                                line.Text.Contains("UPI", StringComparison.OrdinalIgnoreCase);

                    bool isPrefixOfNextTx = false;

                    if (pageNumber > lastLinePage)
                    {
                        // Page break continuation: line is immediately after the rediscovered table header on the new page
                        // Unless it has a transfer reference for the upcoming transaction, it continues the active transaction from the previous page
                        if (pendingTx != null && !hasTransferIndicator)
                        {
                            isPrefixOfNextTx = false;
                        }
                        else if (nextTxLine != null)
                        {
                            isPrefixOfNextTx = true;
                        }
                    }
                    else
                    {
                        double yDistFromPrev = line.Y - lastLineY;
                        double yDistToNext = nextTxLine != null ? nextTxLine.Y - line.Y : double.MaxValue;

                        if (hasTransferIndicator && nextTxLine != null && yDistToNext <= 14.0)
                        {
                            isPrefixOfNextTx = true;
                        }
                        else if (pendingTx != null && yDistFromPrev <= 12.0)
                        {
                            isPrefixOfNextTx = false;
                        }
                        else if (nextTxLine != null && yDistToNext <= 7.0 &&
                                 !nextTxLine.Text.Contains("SWEEP", StringComparison.OrdinalIgnoreCase) &&
                                 !nextTxLine.Text.Contains("INT. ON", StringComparison.OrdinalIgnoreCase))
                        {
                            isPrefixOfNextTx = true;
                        }
                        else if (pendingTx != null)
                        {
                            isPrefixOfNextTx = false;
                        }
                        else
                        {
                            isPrefixOfNextTx = true;
                        }
                    }

                    if (isPrefixOfNextTx)
                    {
                        pendingPrefixWords.AddRange(descFragments);
                        if (refFragments.Count > 0 && pendingPrefixRef == null)
                        {
                            pendingPrefixRef = string.Join("", refFragments).Trim();
                        }
                    }
                    else if (pendingTx != null)
                    {
                        if (descFragments.Count > 0)
                        {
                            var continuation = string.Join(" ", descFragments);
                            pendingTx.Description = $"{pendingTx.Description} {continuation}".Trim();
                        }

                        if (refFragments.Count > 0 && string.IsNullOrEmpty(pendingTx.Reference))
                        {
                            pendingTx.Reference = string.Join("", refFragments).Trim();
                        }

                        lastLineY = line.Y;
                        lastLinePage = pageNumber;
                    }
                    else
                    {
                        pendingPrefixWords.AddRange(descFragments);
                    }
                }
            }
        }

        // Finalize last transaction if pending
        if (pendingTx != null)
        {
            ValidateAndFinalizeTransaction(pendingTx, previousBalance, globalWarnings);
            transactions.Add(pendingTx);
            if (pendingTx.NeedsReview) warningsCount++;
        }

        bool success = transactions.Count > 0;
        string? errorMessage = success ? null : "Failed to extract any valid YES BANK transactions from the document.";

        int processedCount = transactions.Count;
        double avgConfidence = transactions.Count > 0
            ? Math.Round(transactions.Average(t => t.Confidence), 2)
            : 0.0;

        _logger.LogInformation(
            "YES BANK statement parsed. Detected: {TotalRows}, Parsed: {ParsedCount}, Warnings: {WarningsCount}, Success: {Success}",
            totalRowsDetected, transactions.Count, warningsCount, success);

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

    private record YesColumnBounds(
        double DateMaxX,
        double ValueDateMinX,
        double ValueDateMaxX,
        double RefMinX,
        double RefMaxX,
        double DescMinX,
        double WithdrawalMinX,
        double DepositMinX,
        double BalanceMinX);

    private YesColumnBounds CalibrateColumnIntervals(PdfExtractionResult extraction)
    {
        foreach (var page in extraction.Pages)
        {
            var lines = GetSortedVisualLines(page);
            foreach (var line in lines)
            {
                if (IsTableHeaderLine(line.Text))
                {
                    var frags = line.Fragments;

                    var txDateFrag = frags.FirstOrDefault(f => f.Text.Contains("Transaction", StringComparison.OrdinalIgnoreCase) ||
                                                               f.Text.Equals("Date", StringComparison.OrdinalIgnoreCase));
                    var valDateFrag = frags.FirstOrDefault(f => f.Text.Contains("Value", StringComparison.OrdinalIgnoreCase));
                    var chqRefFrag = frags.FirstOrDefault(f => f.Text.Contains("Cheque", StringComparison.OrdinalIgnoreCase) ||
                                                               f.Text.Contains("Reference", StringComparison.OrdinalIgnoreCase) ||
                                                               f.Text.Contains("TranID", StringComparison.OrdinalIgnoreCase));
                    var descFrag = frags.FirstOrDefault(f => f.Text.Contains("Description", StringComparison.OrdinalIgnoreCase) ||
                                                             f.Text.Contains("Remarks", StringComparison.OrdinalIgnoreCase));
                    var withFrag = frags.FirstOrDefault(f => f.Text.Contains("Withdrawal", StringComparison.OrdinalIgnoreCase) ||
                                                             f.Text.Contains("Withdrawl", StringComparison.OrdinalIgnoreCase));
                    var depFrag = frags.FirstOrDefault(f => f.Text.Contains("Deposit", StringComparison.OrdinalIgnoreCase));
                    var balFrag = frags.FirstOrDefault(f => f.Text.Contains("Balance", StringComparison.OrdinalIgnoreCase) ||
                                                            f.Text.Contains("Running", StringComparison.OrdinalIgnoreCase));

                    double valDateMin = valDateFrag != null ? Math.Round(valDateFrag.X - 10.0, 1) : 95.0;
                    double refMin = chqRefFrag != null ? Math.Round(chqRefFrag.X - 10.0, 1) : 170.0;

                    // In YES BANK statements, the "Description" header is typically centered in a wide column (~290 to ~515).
                    // The reference column ends and description begins halfway between ref header and desc header.
                    double descMin = 295.0;
                    if (chqRefFrag != null && descFrag != null)
                    {
                        descMin = Math.Round((chqRefFrag.X + descFrag.X) / 2.0, 1);
                    }
                    else if (chqRefFrag != null)
                    {
                        descMin = Math.Round(chqRefFrag.X + 105.0, 1);
                    }
                    else if (descFrag != null)
                    {
                        descMin = Math.Round(descFrag.X - 80.0, 1);
                    }

                    double withMin = withFrag != null ? Math.Round(withFrag.X - 12.0, 1) : 515.0;
                    double depMin = depFrag != null ? Math.Round(depFrag.X - 12.0, 1) : 590.0;
                    double balMin = balFrag != null ? Math.Round(balFrag.X - 12.0, 1) : 660.0;

                    double dateMax = Math.Max(80.0, valDateMin - 5.0);
                    double valDateMax = Math.Max(valDateMin + 30.0, refMin - 5.0);
                    double refMax = Math.Max(refMin + 40.0, descMin - 5.0);

                    return new YesColumnBounds(
                        DateMaxX: dateMax,
                        ValueDateMinX: valDateMin,
                        ValueDateMaxX: valDateMax,
                        RefMinX: refMin,
                        RefMaxX: refMax,
                        DescMinX: descMin,
                        WithdrawalMinX: withMin,
                        DepositMinX: depMin,
                        BalanceMinX: balMin);
                }
            }
        }

        return new YesColumnBounds(
            DateMaxX: 95.0,
            ValueDateMinX: 95.0,
            ValueDateMaxX: 165.0,
            RefMinX: 165.0,
            RefMaxX: 295.0,
            DescMinX: 295.0,
            WithdrawalMinX: 515.0,
            DepositMinX: 590.0,
            BalanceMinX: 660.0);
    }

    #endregion

    #region Row Extraction & Parsing

    private ParsedTransaction ExtractTransactionRow(
        VisualLineModel line,
        PdfTextBlock dateFrag,
        YesColumnBounds bounds,
        int pageNumber,
        int rowNumber)
    {
        var frags = line.Fragments;

        // 1. Transaction Date
        DateTime txDate = ParseDate(dateFrag.Text.Trim()) ?? DateTime.MinValue;

        // 2. Value Date
        DateTime? valDate = null;
        var valDateFrags = frags.Where(f => f.X >= bounds.ValueDateMinX && f.X < bounds.ValueDateMaxX).ToList();
        if (valDateFrags.Count > 0)
        {
            var valStr = string.Join("", valDateFrags.Select(f => f.Text.Trim()));
            valDate = ParseDate(valStr);
        }

        // 3. Cheque / Reference
        string? reference = null;
        var refFrags = frags.Where(f => f.X >= bounds.RefMinX && f.X < bounds.DescMinX).ToList();
        if (refFrags.Count > 0)
        {
            reference = string.Join("", refFrags.Select(f => f.Text.Trim()));
            if (string.Equals(reference, "NA", StringComparison.OrdinalIgnoreCase))
            {
                reference = null;
            }
        }

        // 4. Description on the anchor line
        var descFrags = frags.Where(f => f.X >= bounds.DescMinX && f.X < bounds.WithdrawalMinX).OrderBy(f => f.X).ToList();
        string description = string.Join(" ", descFrags.Select(f => f.Text.Trim())).Trim();

        // 5. Withdrawal (Debit)
        decimal? debit = null;
        var withFrags = frags.Where(f => f.X >= bounds.WithdrawalMinX && f.X < bounds.DepositMinX).ToList();
        if (withFrags.Count > 0)
        {
            var withStr = string.Join("", withFrags.Select(f => f.Text.Trim()));
            debit = ParseAmount(withStr);
        }

        // 6. Deposit (Credit)
        decimal? credit = null;
        var depFrags = frags.Where(f => f.X >= bounds.DepositMinX && f.X < bounds.BalanceMinX).ToList();
        if (depFrags.Count > 0)
        {
            var depStr = string.Join("", depFrags.Select(f => f.Text.Trim()));
            credit = ParseAmount(depStr);
        }

        // 7. Balance
        decimal? balance = null;
        var balFrags = frags.Where(f => f.X >= bounds.BalanceMinX).ToList();
        if (balFrags.Count > 0)
        {
            var balStr = string.Join("", balFrags.Select(f => f.Text.Trim()));
            balance = ParseAmount(balStr);
        }

        decimal amount = debit ?? credit ?? 0m;
        string txType = debit.HasValue ? "DEBIT" : "CREDIT";

        return new ParsedTransaction
        {
            TransactionDate = txDate,
            ValueDate = valDate,
            Reference = reference,
            Description = description,
            Debit = debit,
            Credit = credit,
            Amount = amount,
            Balance = balance,
            TransactionType = txType,
            BankCode = BankCode,
            BankName = BankName,
            SourcePageNumber = pageNumber,
            SourceLineIndex = rowNumber,
            ParserVersion = ParserVersion,
            Confidence = 1.0,
            NeedsReview = false,
            ReviewWarnings = [],
            RawSourceText = line.Text
        };
    }

    private PdfTextBlock? FindTransactionDateFragment(VisualLineModel line, YesColumnBounds bounds)
    {
        foreach (var frag in line.Fragments)
        {
            if (frag.X < bounds.DateMaxX)
            {
                var text = frag.Text.Trim();
                if (DateTokenRegex.IsMatch(text) || DateTokenFallbackRegex.IsMatch(text))
                {
                    return frag;
                }
            }
        }

        return null;
    }

    private void ValidateAndFinalizeTransaction(
        ParsedTransaction tx,
        decimal? previousBalance,
        List<string> globalWarnings)
    {
        if (tx.TransactionDate == DateTime.MinValue)
        {
            tx.NeedsReview = true;
            tx.ReviewWarnings.Add("Missing or unparseable transaction date.");
            tx.Confidence = Math.Min(tx.Confidence, 0.5);
        }

        if (!tx.Debit.HasValue && !tx.Credit.HasValue)
        {
            tx.NeedsReview = true;
            tx.ReviewWarnings.Add("Transaction contains neither a valid debit nor credit amount.");
            tx.Confidence = Math.Min(tx.Confidence, 0.5);
        }

        if (tx.Debit.HasValue && tx.Credit.HasValue)
        {
            tx.NeedsReview = true;
            tx.ReviewWarnings.Add("Both debit and credit amounts are populated simultaneously.");
            tx.Confidence = Math.Min(tx.Confidence, 0.6);
        }

        if (previousBalance.HasValue && tx.Balance.HasValue)
        {
            decimal expectedBalance = previousBalance.Value - (tx.Debit ?? 0m) + (tx.Credit ?? 0m);
            decimal discrepancy = Math.Abs(tx.Balance.Value - expectedBalance);

            if (discrepancy > 0.01m)
            {
                bool isSweepEntry = tx.Description.Contains("SWEEP-IN", StringComparison.OrdinalIgnoreCase) ||
                                    tx.Description.Contains("INT. ON SWCR", StringComparison.OrdinalIgnoreCase) ||
                                    tx.Description.Contains("TDS RECOVERED", StringComparison.OrdinalIgnoreCase);

                if (!isSweepEntry || tx.Balance.Value != 0m)
                {
                    tx.ReviewWarnings.Add($"Running balance mismatch. Expected: {expectedBalance:N2}, Actual: {tx.Balance.Value:N2} (Diff: {discrepancy:N2})");
                }
            }
        }
    }

    #endregion

    #region Text Helpers & Filtering

    private static bool IsTableHeaderLine(string text)
    {
        bool hasTxDate = text.Contains("Transaction Date", StringComparison.OrdinalIgnoreCase) ||
                         (text.Contains("Date", StringComparison.OrdinalIgnoreCase) && text.Contains("Value", StringComparison.OrdinalIgnoreCase));
        bool hasBalance = text.Contains("Balance", StringComparison.OrdinalIgnoreCase);
        bool hasWithdrawal = text.Contains("Withdrawal", StringComparison.OrdinalIgnoreCase) || text.Contains("Withdrawl", StringComparison.OrdinalIgnoreCase);

        return hasTxDate && (hasBalance || hasWithdrawal);
    }

    private static bool IsSummaryFooterLine(string text)
    {
        return text.Contains("Opening Balance:", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Total Withdrawals:", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Total Deposits:", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Closing Balance:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDisclaimerOrFooter(string text)
    {
        return text.Contains("Mandatory disclaimer", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Under Goods and Services Tax", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Please contact a YES BANK", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Transaction codes in your account statement", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Please check the entries in the statement", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Reward points accrued", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("To redeem your Rewardz", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Benefits of Nomination", StringComparison.OrdinalIgnoreCase) ||
               Regex.IsMatch(text, @"^Page\s+\d+\s+of\s+\d+$", RegexOptions.IgnoreCase);
    }

    private static DateTime? ParseDate(string text)
    {
        if (DateTime.TryParseExact(text, "dd-MMM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1))
        {
            return d1;
        }

        if (DateTime.TryParseExact(text, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2))
        {
            return d2;
        }

        if (DateTime.TryParseExact(text, "dd/MM/yy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d3))
        {
            return d3;
        }

        return null;
    }

    private static decimal? ParseAmount(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var cleaned = text.Replace(",", "").Trim();
        if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
        {
            return amount;
        }

        return null;
    }

    private sealed class VisualLineModel
    {
        public int LineIndex { get; set; }
        public double Y { get; set; }
        public string Text { get; set; } = string.Empty;
        public List<PdfTextBlock> Fragments { get; set; } = [];
    }

    private static List<VisualLineModel> GetSortedVisualLines(PdfPageResult page)
    {
        if (page.CandidateRows != null && page.CandidateRows.Count > 0)
        {
            return page.CandidateRows
                .OrderBy(r => r.Y)
                .Select((r, idx) => new VisualLineModel
                {
                    LineIndex = idx + 1,
                    Y = r.Y,
                    Text = r.RawLineText,
                    Fragments = r.LineFragments
                })
                .ToList();
        }

        // Fallback: group TextBlocks by proximity in Y
        var blocks = page.TextBlocks.OrderBy(b => b.Y).ThenBy(b => b.X).ToList();
        var lines = new List<VisualLineModel>();

        foreach (var block in blocks)
        {
            var match = lines.FirstOrDefault(l => Math.Abs(l.Y - block.Y) <= 3.5);
            if (match != null)
            {
                match.Fragments.Add(block);
            }
            else
            {
                lines.Add(new VisualLineModel
                {
                    LineIndex = lines.Count + 1,
                    Y = block.Y,
                    Fragments = [block]
                });
            }
        }

        foreach (var l in lines)
        {
            l.Fragments = l.Fragments.OrderBy(f => f.X).ToList();
            l.Text = string.Join(" ", l.Fragments.Select(f => f.Text));
        }

        return lines.OrderBy(l => l.Y).ToList();
    }

    #endregion
}
