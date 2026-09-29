using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Accufex.Server.DTOs;
using Accufex.Server.Parsing.Interfaces;
using Accufex.Server.Parsing.Models;
using Microsoft.Extensions.Logging;

namespace Accufex.Server.Parsing.Parsers;

/// <summary>
/// Deterministic statement parser for HDFC Bank digital PDF statements.
/// Reconstructs multi-line narrations, handles cross-page boundary transactions,
/// filters non-transaction header/footer elements textually and structurally,
/// parses strict dates & canonical amounts, and performs mathematical balance verification.
/// </summary>
public class HdfcStatementParser : IBankStatementParser
{
    public int BankCode => (int)BankType.Hdfc;
    public string BankName => "HDFC Bank";
    public string ParserVersion => "HDFC-v1";

    private readonly ILogger<HdfcStatementParser> _logger;

    private static readonly Regex DateTokenRegex = new(@"^\d{2}/\d{2}/\d{2,4}$", RegexOptions.Compiled);

    public HdfcStatementParser(ILogger<HdfcStatementParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(BankDetectionResult detection)
    {
        return detection != null && detection.DetectedBank == BankType.Hdfc && detection.IsSupported;
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
        var globalWarnings = new List<string>();
        int totalRowsDetected = 0;
        int rejectedRows = 0;
        int warningsCount = 0;

        // Track running balance across the entire document
        decimal? previousBalance = null;

        for (int pageIndex = 0; pageIndex < extraction.Pages.Count; pageIndex++)
        {
            var page = extraction.Pages[pageIndex];
            var pageNumber = page.PageNumber;

            // Use CandidateRows sorted by top Y coordinate, or build visual lines from text blocks
            var lines = GetSortedVisualLines(page);

            bool reachedStatementSummary = false;
            bool reachedPageFooter = false;
            bool inTransactionZone = false;

            foreach (var line in lines)
            {
                var lineText = line.Text.Trim();
                if (string.IsNullOrWhiteSpace(lineText)) continue;

                // 2. Filter Statement Summary & Closing sections (do not treat summary as transactions)
                if (IsStatementSummary(lineText))
                {
                    reachedStatementSummary = true;
                    if (pendingTx != null)
                    {
                        ValidateAndFinalizeTransaction(pendingTx, ref previousBalance, ref warningsCount);
                        transactions.Add(pendingTx);
                        pendingTx = null;
                    }
                    continue;
                }

                if (reachedStatementSummary)
                {
                    // Ignore remaining lines after statement summary
                    continue;
                }

                // 3. Filter Page Footers: once footer starts after transaction table, ignore remaining lines on this page
                if (reachedPageFooter)
                {
                    continue;
                }

                if (line.Y >= Math.Max(columnBounds.TableTopY + 50.0, page.Height * 0.75) && IsPageFooterLine(lineText, page.RepeatedFooters))
                {
                    reachedPageFooter = true;
                    continue;
                }

                // 4. Filter Table Column Header Lines & Statement Period (demarcates transaction zone)
                if (IsTableHeaderLine(lineText))
                {
                    inTransactionZone = true;
                    continue;
                }

                if (IsStatementPeriodLine(lineText))
                {
                    inTransactionZone = true;
                    continue;
                }

                // 5. Transaction Boundary Detection: Check if the line starts with a transaction date in Date column
                var firstToken = line.Fragments.OrderBy(f => f.X).FirstOrDefault();
                bool isTransactionStart = firstToken != null &&
                                          firstToken.X < columnBounds.DateMaxX &&
                                          DateTokenRegex.IsMatch(firstToken.Text.Trim()) &&
                                          ParseDate(firstToken.Text.Trim()) != DateTime.MinValue;

                if (isTransactionStart)
                {
                    inTransactionZone = true;
                }
                else
                {
                    // 6. Filter Customer Info & Account Metadata Headers (only non-transaction lines)
                    if (IsCustomerHeaderLine(lineText, page.RepeatedHeaders, detection.CustomerName, detection.AccountNumber))
                    {
                        continue;
                    }

                    if (!inTransactionZone)
                    {
                        // Check if this line is a cross-page continuation for pendingTx:
                        // Candidate occurs before next transaction date, is not metadata, not footer, not table header,
                        // and falls within the calibrated narration (or reference) column geometry.
                        if (pendingTx != null && IsContinuationFragmentLine(line, columnBounds))
                        {
                            inTransactionZone = true;
                        }
                        else
                        {
                            // Text precedes the transaction zone on this page (e.g. customer name/address lines)
                            // and must not be treated as transactions or narration continuations.
                            continue;
                        }
                    }
                }

                if (isTransactionStart && firstToken != null)
                {
                    totalRowsDetected++;

                    // Finalize the previous pending transaction before starting a new one
                    if (pendingTx != null)
                    {
                        ValidateAndFinalizeTransaction(pendingTx, ref previousBalance, ref warningsCount);
                        transactions.Add(pendingTx);
                        pendingTx = null;
                    }

                    // Parse date
                    var dateStr = firstToken.Text.Trim();
                    DateTime txDate = ParseDate(dateStr);

                    pendingTx = new ParsedTransaction
                    {
                        Id = Guid.NewGuid(),
                        TransactionDate = txDate,
                        BankCode = BankCode,
                        BankName = BankName,
                        ParserVersion = ParserVersion,
                        SourcePageNumber = pageNumber,
                        SourceLineIndex = line.LineIndex,
                        RawSourceText = lineText,
                        Confidence = 0.95
                    };

                    // Slice line fragments into respective column intervals
                    ExtractColumnsFromLine(line.Fragments, columnBounds, pendingTx);
                }
                else if (pendingTx != null)
                {
                    // 7. Multi-line continuation: use column geometry to assign fragments to appropriate fields
                    
                    // A. Narration fragments: [NarrationMinX - 10, RefMinX)
                    var narrationFragments = line.Fragments
                        .Where(f => f.X >= columnBounds.NarrationMinX - 10.0 && f.X < columnBounds.RefMinX)
                        .OrderBy(f => f.X)
                        .ToList();

                    if (narrationFragments.Count > 0)
                    {
                        var continuationText = string.Join(" ", narrationFragments.Select(f => f.Text.Trim()));
                        if (!string.IsNullOrWhiteSpace(continuationText))
                        {
                            if (!string.IsNullOrWhiteSpace(pendingTx.Description))
                            {
                                pendingTx.Description += " " + continuationText;
                            }
                            else
                            {
                                pendingTx.Description = continuationText;
                            }

                            pendingTx.RawSourceText += "\n" + lineText;
                        }
                    }

                    // B. Reference fragments: [RefMinX, ValueDtMinX)
                    var refFragments = line.Fragments
                        .Where(f => f.X >= columnBounds.RefMinX && f.X < columnBounds.ValueDtMinX)
                        .OrderBy(f => f.X)
                        .Select(f => f.Text.Trim())
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .ToList();

                    if (refFragments.Count > 0)
                    {
                        var refContinuation = string.Join(" ", refFragments);
                        if (string.IsNullOrWhiteSpace(pendingTx.Reference))
                        {
                            pendingTx.Reference = refContinuation;
                        }
                        else if (!pendingTx.Reference.Contains(refContinuation))
                        {
                            pendingTx.Reference += " " + refContinuation;
                        }

                        if (narrationFragments.Count == 0)
                        {
                            pendingTx.RawSourceText += "\n" + lineText;
                        }
                    }

                    // C. Value Date fragments: [ValueDtMinX, WithdrawalMinX)
                    if (!pendingTx.ValueDate.HasValue)
                    {
                        var valFragments = line.Fragments
                            .Where(f => f.X >= columnBounds.ValueDtMinX && f.X < columnBounds.WithdrawalMinX)
                            .OrderBy(f => f.X)
                            .Select(f => f.Text.Trim())
                            .Where(t => !string.IsNullOrWhiteSpace(t) && DateTokenRegex.IsMatch(t))
                            .ToList();

                        if (valFragments.Count > 0)
                        {
                            pendingTx.ValueDate = ParseDate(valFragments[0]);
                        }
                    }

                    // D. Ambiguity check: unattached amount line
                    if (narrationFragments.Count == 0 && refFragments.Count == 0 &&
                        line.Fragments.Any(f => f.X >= columnBounds.WithdrawalMinX && IsNumeric(f.Text)))
                    {
                        pendingTx.ReviewWarnings.Add($"Unattached amount line detected on page {pageNumber}: '{lineText}'");
                        pendingTx.NeedsReview = true;
                        warningsCount++;
                    }
                }
            }
        }

        // Finalize the last pending transaction in the document
        if (pendingTx != null)
        {
            ValidateAndFinalizeTransaction(pendingTx, ref previousBalance, ref warningsCount);
            transactions.Add(pendingTx);
            pendingTx = null;
        }

        // Calculate overall confidence and counts
        int processedCount = transactions.Count;
        double avgConfidence = transactions.Count > 0
            ? Math.Round(transactions.Average(t => t.Confidence), 2)
            : 0.0;

        _logger.LogInformation("HDFC Parser completed. Extracted {Count} transactions across {Pages} pages. Avg confidence: {Conf:P0}",
            transactions.Count, extraction.PageCount, avgConfidence);

        return new BankParsingResult
        {
            Success = true,
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

    private static void ExtractColumnsFromLine(
        List<PdfTextBlock> fragments,
        HdfcColumnBounds bounds,
        ParsedTransaction tx)
    {
        // Narration fragments: [NarrationMinX, NarrationMaxX]
        var narrTokens = fragments
            .Where(f => f.X >= bounds.NarrationMinX && f.X < bounds.RefMinX)
            .OrderBy(f => f.X)
            .Select(f => f.Text.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t));
        tx.Description = string.Join(" ", narrTokens);

        // Reference number: [RefMinX, ValueDtMinX]
        var refTokens = fragments
            .Where(f => f.X >= bounds.RefMinX && f.X < bounds.ValueDtMinX)
            .OrderBy(f => f.X)
            .Select(f => f.Text.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t));
        var refStr = string.Join(" ", refTokens);
        if (!string.IsNullOrWhiteSpace(refStr))
        {
            tx.Reference = refStr;
        }

        // Value Date: [ValueDtMinX, WithdrawalMinX]
        var valTokens = fragments
            .Where(f => f.X >= bounds.ValueDtMinX && f.X < bounds.WithdrawalMinX)
            .OrderBy(f => f.X)
            .Select(f => f.Text.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t));
        var valStr = string.Join(" ", valTokens);
        if (!string.IsNullOrWhiteSpace(valStr) && DateTokenRegex.IsMatch(valStr))
        {
            tx.ValueDate = ParseDate(valStr);
        }

        // Withdrawal Amount (Debit): [WithdrawalMinX, DepositMinX]
        var withTokens = fragments
            .Where(f => f.X >= bounds.WithdrawalMinX && f.X < bounds.DepositMinX)
            .OrderBy(f => f.X)
            .Select(f => f.Text.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t));
        var withStr = string.Join(" ", withTokens);
        if (!string.IsNullOrWhiteSpace(withStr))
        {
            var val = ParseAmount(withStr, preserveSign: true);
            if (val.HasValue)
            {
                if (val.Value < 0)
                {
                    tx.Credit = Math.Abs(val.Value);
                    tx.ReviewWarnings.Add("Negative withdrawal interpreted as credit reversal.");
                }
                else
                {
                    tx.Debit = val.Value;
                }
            }
        }

        // Deposit Amount (Credit): [DepositMinX, BalanceMinX]
        var depTokens = fragments
            .Where(f => f.X >= bounds.DepositMinX && f.X < bounds.BalanceMinX)
            .OrderBy(f => f.X)
            .Select(f => f.Text.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t));
        var depStr = string.Join(" ", depTokens);
        if (!string.IsNullOrWhiteSpace(depStr))
        {
            var val = ParseAmount(depStr, preserveSign: true);
            if (val.HasValue)
            {
                if (val.Value < 0)
                {
                    tx.Debit = Math.Abs(val.Value);
                    tx.ReviewWarnings.Add("Negative deposit interpreted as debit reversal.");
                }
                else
                {
                    tx.Credit = val.Value;
                }
            }
        }

        // Closing Balance: [BalanceMinX, Page.Width]
        var balTokens = fragments
            .Where(f => f.X >= bounds.BalanceMinX)
            .OrderBy(f => f.X)
            .Select(f => f.Text.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t));
        var balStr = string.Join(" ", balTokens);
        if (!string.IsNullOrWhiteSpace(balStr))
        {
            tx.Balance = ParseAmount(balStr, preserveSign: true);
        }

        // Set Canonical Amount and TransactionType
        if (tx.Debit.HasValue && tx.Debit.Value > 0)
        {
            tx.Amount = tx.Debit.Value;
            tx.TransactionType = "Debit";
        }
        else if (tx.Credit.HasValue && tx.Credit.Value > 0)
        {
            tx.Amount = tx.Credit.Value;
            tx.TransactionType = "Credit";
        }
        else
        {
            tx.Amount = 0m;
            tx.TransactionType = "Unknown";
        }
    }

    private static void ValidateAndFinalizeTransaction(
        ParsedTransaction tx,
        ref decimal? previousBalance,
        ref int warningsCount)
    {
        // 1. Amount validity check
        if (!tx.Debit.HasValue && !tx.Credit.HasValue)
        {
            tx.NeedsReview = true;
            tx.ReviewWarnings.Add("Transaction has neither a debit nor credit amount.");
            tx.Confidence -= 0.3;
            warningsCount++;
        }

        if (tx.Debit.HasValue && tx.Credit.HasValue && tx.Debit.Value > 0 && tx.Credit.Value > 0)
        {
            tx.NeedsReview = true;
            tx.ReviewWarnings.Add("Transaction has both debit and credit amounts populated.");
            tx.Confidence -= 0.2;
            warningsCount++;
        }

        // 2. Mathematical running balance reconciliation check
        if (previousBalance.HasValue && tx.Balance.HasValue)
        {
            decimal expectedBalance = previousBalance.Value - (tx.Debit ?? 0m) + (tx.Credit ?? 0m);
            decimal actualBalance = tx.Balance.Value;

            if (Math.Abs(expectedBalance - actualBalance) <= 0.01m)
            {
                // Mathematical proof of correct boundary, debit/credit assignment, and balance!
                tx.Confidence = 1.0;
            }
            else
            {
                tx.NeedsReview = true;
                tx.ReviewWarnings.Add($"Running balance mismatch. Expected {expectedBalance:F2}, actual {actualBalance:F2}.");
                tx.Confidence = Math.Max(0.5, tx.Confidence - 0.25);
                warningsCount++;
            }
        }
        else if (tx.Balance.HasValue)
        {
            // First transaction with balance
            tx.Confidence = 0.98;
        }

        if (tx.Balance.HasValue)
        {
            previousBalance = tx.Balance.Value;
        }

        // Clean up description
        tx.Description = tx.Description.Trim();
    }

    private static DateTime ParseDate(string dateStr)
    {
        if (DateTime.TryParseExact(dateStr, "dd/MM/yy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1))
        {
            return d1;
        }
        if (DateTime.TryParseExact(dateStr, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2))
        {
            return d2;
        }
        return DateTime.MinValue;
    }

    private static decimal? ParseAmount(string raw, bool preserveSign = false)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var clean = raw.Replace(",", "").Trim();
        bool isDr = clean.EndsWith("Dr", StringComparison.OrdinalIgnoreCase);
        bool isCr = clean.EndsWith("Cr", StringComparison.OrdinalIgnoreCase);
        if (isDr || isCr)
        {
            clean = clean[..^2].Trim();
        }

        if (decimal.TryParse(clean, NumberStyles.Number | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var val))
        {
            if (isDr)
            {
                return -Math.Abs(val);
            }
            if (isCr)
            {
                return Math.Abs(val);
            }
            return preserveSign ? val : Math.Abs(val);
        }
        return null;
    }

    private static bool IsNumeric(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var clean = text.Replace(",", "").Trim();
        return decimal.TryParse(clean, NumberStyles.Number, CultureInfo.InvariantCulture, out _);
    }

    private static bool IsStatementPeriodLine(string text)
    {
        return text.Contains("Statement From", StringComparison.OrdinalIgnoreCase) ||
               Regex.IsMatch(text, @"(?:^|\s)From\s+\d{2}/\d{2}/\d{2,4}\s+To\s+\d{2}/\d{2}/\d{2,4}", RegexOptions.IgnoreCase);
    }

    private static bool IsTableHeaderLine(string text)
    {
        // Standard full header line
        if (text.Contains("Date", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("Narration", StringComparison.OrdinalIgnoreCase) &&
            (text.Contains("Withdrawal", StringComparison.OrdinalIgnoreCase) || text.Contains("Deposit", StringComparison.OrdinalIgnoreCase)) &&
            text.Contains("Balance", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Split or partial header containing typical HDFC column titles without transaction amounts
        var normalized = Regex.Replace(text, @"[./\s]+", " ").Trim();
        var keywords = new[] { "Date", "Narration", "Chq Ref No", "Chq", "Ref No", "Value Dt", "Withdrawal Amt", "Withdrawal", "Deposit Amt", "Deposit", "Closing Balance", "Balance" };
        int matchedKeywords = 0;
        foreach (var kw in keywords)
        {
            if (normalized.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                matchedKeywords++;
            }
        }

        if (matchedKeywords >= 2 && !Regex.IsMatch(text, @"\b\d{1,3}(?:,\d{2,3})*\.\d{2}\b"))
        {
            return true;
        }

        return false;
    }

    private static bool IsCustomerHeaderLine(string text, List<string>? repeatedHeaders, string? customerName = null, string? accountNumber = null)
    {
        if (repeatedHeaders != null && repeatedHeaders.Any(h => text.Contains(h, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(customerName) && text.Contains(customerName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(accountNumber) && text.Contains(accountNumber, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Punctuation artifacts from multi-column header layout (e.g. ": :", ": : -", ":", ".:")
        var stripped = text.Replace(":", "").Replace("-", "").Replace(".", "").Trim();
        if (string.IsNullOrEmpty(stripped))
        {
            return true;
        }

        // Structural metadata headers
        return text.Contains("HDFC BANK", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Page No", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Account Branch", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Branch :", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Branch Code", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Address :", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Address", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Customer Name", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Cust Name", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("City :", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("City", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("State :", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("State", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Statement From", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Statement of account", StringComparison.OrdinalIgnoreCase) ||
               Regex.IsMatch(text, @"(?:^|\s)From\s+\d{2}/\d{2}/\d{2,4}\s+To\s+\d{2}/\d{2}/\d{2,4}", RegexOptions.IgnoreCase) ||
               text.Contains("JOINT HOLDERS", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("RTGS/NEFT IFSC", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Cust ID", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Account No", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Nomination", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Account Status", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("A/C Open Date", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Preferred Customer", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Product Code", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Account Type", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("OD Limit", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Currency", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Phone", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("MICR", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Email", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsContinuationFragmentLine(VisualLineModel line, HdfcColumnBounds bounds)
    {
        // 1. Must occur in the transaction-table region (at or below TableTopY)
        if (line.Y < bounds.TableTopY)
        {
            return false;
        }

        // 2. Must not start with a transaction date in Date column
        var firstToken = line.Fragments.OrderBy(f => f.X).FirstOrDefault();
        if (firstToken != null && firstToken.X < bounds.DateMaxX && DateTokenRegex.IsMatch(firstToken.Text.Trim()) && ParseDate(firstToken.Text.Trim()) != DateTime.MinValue)
        {
            return false;
        }

        // 3. Candidate must contain fragments in the calibrated Narration column [NarrationMinX - 10, RefMinX)
        // or Reference column [RefMinX, ValueDtMinX)
        bool hasNarration = line.Fragments.Any(f => f.X >= bounds.NarrationMinX - 10.0 && f.X < bounds.RefMinX && !string.IsNullOrWhiteSpace(f.Text));
        bool hasRef = line.Fragments.Any(f => f.X >= bounds.RefMinX && f.X < bounds.ValueDtMinX && !string.IsNullOrWhiteSpace(f.Text));

        if (!hasNarration && !hasRef)
        {
            return false;
        }

        // 4. Must not be a table header line, footer line, or statement summary
        if (IsTableHeaderLine(line.Text) || IsPageFooterLine(line.Text, null) || IsStatementSummary(line.Text))
        {
            return false;
        }

        return true;
    }

    private static bool IsPageFooterLine(string text, List<string>? repeatedFooters)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return false;

        // Never treat a line starting with a transaction date as a page footer
        if (DateTokenRegex.IsMatch(trimmed)) return false;

        var normalizedText = Regex.Replace(trimmed, @"\s+", " ");

        // Match against genuine repeated footers using whole-line or exact/near-exact phrase matching
        if (repeatedFooters != null && repeatedFooters.Count > 0)
        {
            foreach (var footer in repeatedFooters)
            {
                if (string.IsNullOrWhiteSpace(footer) || footer.Length < 15)
                {
                    // Ignore generic short fragments (e.g. "PHONE", "ONE", "M PHONE")
                    continue;
                }

                var normalizedFooter = Regex.Replace(footer.Trim(), @"\s+", " ");

                // 1. Exact normalized line match
                if (string.Equals(normalizedText, normalizedFooter, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // 2. Prefix match for substantial multi-token signatures (>= 20 chars)
                if (normalizedFooter.Length >= 20 &&
                    (normalizedText.StartsWith(normalizedFooter, StringComparison.OrdinalIgnoreCase) ||
                     (normalizedText.Length >= 20 && normalizedFooter.StartsWith(normalizedText, StringComparison.OrdinalIgnoreCase))))
                {
                    return true;
                }
            }
        }

        return normalizedText.Contains("HDFC BANK LIMITED", StringComparison.OrdinalIgnoreCase) ||
               normalizedText.Contains("Closing balance includes funds", StringComparison.OrdinalIgnoreCase) ||
               normalizedText.Contains("earmarked for hold", StringComparison.OrdinalIgnoreCase) ||
               normalizedText.Contains("uncleared funds", StringComparison.OrdinalIgnoreCase) ||
               normalizedText.Contains("Contents of this statement", StringComparison.OrdinalIgnoreCase) ||
               normalizedText.Contains("considered correct", StringComparison.OrdinalIgnoreCase) ||
               normalizedText.Contains("no error is reported", StringComparison.OrdinalIgnoreCase) ||
               normalizedText.Contains("within 30 days", StringComparison.OrdinalIgnoreCase) ||
               normalizedText.Contains("requesting this statement", StringComparison.OrdinalIgnoreCase) ||
               normalizedText.Contains("Registered Office", StringComparison.OrdinalIgnoreCase) ||
               normalizedText.Contains("Senapati Bapat Marg", StringComparison.OrdinalIgnoreCase) ||
               normalizedText.Contains("Lower Parel", StringComparison.OrdinalIgnoreCase) ||
               normalizedText.Contains("State account branch GSTN", StringComparison.OrdinalIgnoreCase) ||
               normalizedText.Contains("HDFC Bank GSTIN", StringComparison.OrdinalIgnoreCase) ||
               normalizedText.Contains("goods-and-service-tax", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStatementSummary(string text)
    {
        return text.Contains("STATEMENT SUMMARY", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Opening Balance Dr Count", StringComparison.OrdinalIgnoreCase);
    }

    private static HdfcColumnBounds CalibrateColumnIntervals(PdfExtractionResult extraction)
    {
        // Default calibrated column boundaries for standard HDFC format
        var bounds = new HdfcColumnBounds
        {
            DateMaxX = 55.0,
            NarrationMinX = 65.0,
            RefMinX = 270.0,
            ValueDtMinX = 355.0,
            WithdrawalMinX = 395.0,
            DepositMinX = 475.0,
            BalanceMinX = 555.0,
            TableTopY = 190.0
        };

        // Locate the header row across pages to dynamically calibrate from actual coordinates
        var headerPage = extraction.Pages.FirstOrDefault(p => p.CandidateRows.Any(r => IsTableHeaderLine(r.RawLineText)));
        if (headerPage != null)
        {
            var headerRow = headerPage.CandidateRows.FirstOrDefault(r => IsTableHeaderLine(r.RawLineText));
            if (headerRow != null)
            {
                bounds.TableTopY = Math.Max(80.0, headerRow.Y - 15.0);

                if (headerRow.LineFragments.Count >= 5)
                {
                    var frags = headerRow.LineFragments;
                    var dateFrag = frags.FirstOrDefault(f => f.Text.Equals("Date", StringComparison.OrdinalIgnoreCase));
                    var narrFrag = frags.FirstOrDefault(f => f.Text.Equals("Narration", StringComparison.OrdinalIgnoreCase));
                    var refFrag = frags.FirstOrDefault(f => f.Text.Contains("Chq", StringComparison.OrdinalIgnoreCase) || f.Text.Contains("Ref", StringComparison.OrdinalIgnoreCase));
                    var valFrag = frags.FirstOrDefault(f => f.Text.Contains("Value", StringComparison.OrdinalIgnoreCase));
                    var withFrag = frags.FirstOrDefault(f => f.Text.Contains("Withdrawal", StringComparison.OrdinalIgnoreCase));
                    var depFrag = frags.FirstOrDefault(f => f.Text.Contains("Deposit", StringComparison.OrdinalIgnoreCase));
                    var balFrag = frags.FirstOrDefault(f => f.Text.Contains("Closing", StringComparison.OrdinalIgnoreCase) || f.Text.Contains("Balance", StringComparison.OrdinalIgnoreCase));

                    if (dateFrag != null)
                    {
                        bounds.DateMaxX = Math.Max(58.0, Math.Round(dateFrag.X + dateFrag.Width + 5.0, 1));
                        bounds.NarrationMinX = 58.0;
                    }
                    if (refFrag != null)
                    {
                        bounds.RefMinX = Math.Round(refFrag.X - 15.0, 1);
                    }
                    if (valFrag != null)
                    {
                        bounds.ValueDtMinX = Math.Round(valFrag.X - 8.0, 1);
                    }
                    if (withFrag != null)
                    {
                        bounds.WithdrawalMinX = Math.Round(withFrag.X - 12.0, 1);
                    }
                    if (depFrag != null)
                    {
                        bounds.DepositMinX = Math.Round(depFrag.X - 12.0, 1);
                    }
                    if (balFrag != null)
                    {
                        bounds.BalanceMinX = Math.Round(balFrag.X - 12.0, 1);
                    }
                }
            }
        }
        else
        {
            // If no explicit table header row, find the first line containing a valid transaction date
            var firstDateRow = extraction.Pages
                .SelectMany(p => p.CandidateRows)
                .FirstOrDefault(r =>
                {
                    var ft = r.LineFragments.OrderBy(f => f.X).FirstOrDefault();
                    return ft != null && ft.X < 60.0 && DateTokenRegex.IsMatch(ft.Text.Trim()) && ParseDate(ft.Text.Trim()) != DateTime.MinValue;
                });

            if (firstDateRow != null)
            {
                bounds.TableTopY = Math.Max(80.0, firstDateRow.Y - 25.0);
            }
        }

        return bounds;
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

    private sealed class VisualLineModel
    {
        public int LineIndex { get; set; }
        public double Y { get; set; }
        public string Text { get; set; } = string.Empty;
        public List<PdfTextBlock> Fragments { get; set; } = [];
    }

    private sealed class HdfcColumnBounds
    {
        public double DateMaxX { get; set; }
        public double NarrationMinX { get; set; }
        public double RefMinX { get; set; }
        public double ValueDtMinX { get; set; }
        public double WithdrawalMinX { get; set; }
        public double DepositMinX { get; set; }
        public double BalanceMinX { get; set; }
        public double TableTopY { get; set; }
    }
}
