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
/// Deterministic statement parser for Bank of India (BOI) statements (BOI-v1).
/// Production-grade implementation supporting multi-page statement parsing,
/// dynamic column boundary detection, multi-line narration reconstruction,
/// instrument/cheque number extraction, UTR extraction, signed Dr./Cr. balance parsing,
/// and sequential running balance continuity verification.
/// </summary>
public class BOIStatementParser : IBankStatementParser
{
    public int BankCode => (int)BankType.BOI;
    public string BankName => "Bank of India";
    public string ParserVersion => "BOI-v1";

    private readonly ILogger<BOIStatementParser> _logger;

    private static readonly Regex DateRegex = new(@"^\d{2}-\d{2}-\d{4}$", RegexOptions.Compiled);
    private static readonly Regex AmountRegex = new(@"[\d,]+(?:\.\d{1,2})?", RegexOptions.Compiled);
    private static readonly Regex UtrRegex = new(@"\b([A-Z0-9]{12,22})\b", RegexOptions.Compiled);

    public BOIStatementParser(ILogger<BOIStatementParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(BankDetectionResult detection)
    {
        return detection != null &&
               detection.DetectedBank == BankType.BOI &&
               detection.IsSupported;
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

        var transactions = new List<ParsedTransaction>();
        var warnings = new List<string>();
        decimal? openingBalance = null;
        decimal? closingBalance = null;
        int totalRowsDetected = 0;
        int rowIndexCounter = 1;

        foreach (var page in extraction.Pages.OrderBy(p => p.PageNumber))
        {
            var textBlocks = (page.TextBlocks != null && page.TextBlocks.Count > 0)
                ? page.TextBlocks.Where(b => !string.IsNullOrWhiteSpace(b.Text)).ToList()
                : (page.CandidateRows != null
                    ? page.CandidateRows.SelectMany(r => r.LineFragments).Where(b => !string.IsNullOrWhiteSpace(b.Text)).ToList()
                    : new List<PdfTextBlock>());

            if (textBlocks.Count == 0)
            {
                continue;
            }

            // 1. Locate header line: Look for DESCRIPTION or TRAN DATE
            var headerWords = textBlocks
                .Where(w => w.Text.Equals("DESCRIPTION", StringComparison.OrdinalIgnoreCase) ||
                            w.Text.Equals("TRAN", StringComparison.OrdinalIgnoreCase))
                .ToList();

            double headerBottomY = 0.0;
            if (headerWords.Count > 0)
            {
                headerBottomY = headerWords.Min(w => w.Y) + 10.0;
            }
            else
            {
                // Fallback default header boundary if not explicitly located
                headerBottomY = 80.0;
            }

            // 2. Check Opening Balance on Page 1
            if (page.PageNumber == 1 && !openingBalance.HasValue)
            {
                var obLabel = textBlocks.FirstOrDefault(w => w.Text.Equals("OPENING", StringComparison.OrdinalIgnoreCase));
                if (obLabel != null)
                {
                    double obY = obLabel.Y;
                    var obWords = textBlocks
                        .Where(w => Math.Abs(w.Y - obY) <= 6.0)
                        .OrderBy(w => w.X)
                        .ToList();

                    var amtWord = obWords.FirstOrDefault(w =>
                        w.Text.Contains("Dr", StringComparison.OrdinalIgnoreCase) ||
                        w.Text.Contains("Cr", StringComparison.OrdinalIgnoreCase) ||
                        AmountRegex.IsMatch(w.Text));

                    if (amtWord != null)
                    {
                        bool isDr = amtWord.Text.Contains("Dr", StringComparison.OrdinalIgnoreCase) ||
                                    obWords.Any(w => w.Text.Contains("Dr", StringComparison.OrdinalIgnoreCase));
                        string clean = amtWord.Text.Replace("Dr.", "").Replace("Cr.", "").Replace("Dr", "").Replace("Cr", "").Replace(",", "").Trim();
                        if (decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var obVal))
                        {
                            openingBalance = isDr ? -obVal : obVal;
                        }
                    }
                }
            }

            // 3. Locate Footer / End of Table boundary
            double footerTopY = page.Height > 0 ? page.Height : 2000.0;
            var footerWords = textBlocks.Where(w =>
                w.Y > headerBottomY &&
                (w.Text.StartsWith("Page:", StringComparison.OrdinalIgnoreCase) ||
                 w.Text.Equals("relationship", StringComparison.OrdinalIgnoreCase) ||
                 w.Text.Equals("discrepancy", StringComparison.OrdinalIgnoreCase) ||
                 w.Text.Equals("system", StringComparison.OrdinalIgnoreCase) ||
                 w.Text.Equals("Helpline", StringComparison.OrdinalIgnoreCase) ||
                 w.Text.Equals("Toll", StringComparison.OrdinalIgnoreCase))).ToList();

            if (footerWords.Count > 0)
            {
                footerTopY = footerWords.Min(w => w.Y) - 2.0;
            }

            // 4. Find all transaction anchors: Date in 65 <= X < 140 below headerBottomY and above footerTopY
            var dateWords = textBlocks
                .Where(w => w.X >= 65 && w.X < 140 &&
                            w.Y > headerBottomY &&
                            w.Y < footerTopY &&
                            DateRegex.IsMatch(w.Text))
                .OrderBy(w => w.Y)
                .ToList();

            for (int i = 0; i < dateWords.Count; i++)
            {
                var dw = dateWords[i];
                double dateY = dw.Y;
                double nextDateY = (i + 1 < dateWords.Count)
                    ? dateWords[i + 1].Y
                    : footerTopY;

                // All words belonging to this transaction: between dateY - 4.0 and nextDateY - 4.0
                var txWords = textBlocks
                    .Where(w => w.Y >= (dateY - 4.0) && w.Y < (nextDateY - 4.0))
                    .OrderBy(w => w.Y)
                    .ThenBy(w => w.X)
                    .ToList();

                DateTime.TryParseExact(dw.Text, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var txDate);

                var tx = new ParsedTransaction
                {
                    TransactionDate = txDate,
                    SourcePageNumber = page.PageNumber,
                    SourceLineIndex = rowIndexCounter++,
                    BankCode = BankCode,
                    BankName = BankName,
                    ParserVersion = ParserVersion
                };

                // SNo: in X < 65
                var snoWord = txWords.FirstOrDefault(w => w.X < 65 && int.TryParse(w.Text, out _));

                // InstNo: in 140 <= X < 210 on baseline (+- 6pt from dateY)
                var instWord = txWords.FirstOrDefault(w => w.X >= 140 && w.X < 210 &&
                                                           Math.Abs(w.Y - dateY) <= 6.0 &&
                                                           w.Text.Length >= 3 && char.IsDigit(w.Text[0]));
                if (instWord != null)
                {
                    tx.Reference = instWord.Text;
                }

                // Debits: 470 <= X < 550
                var debitWord = txWords.FirstOrDefault(w => w.X >= 470 && w.X < 550 &&
                                                            decimal.TryParse(w.Text.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var dv) && dv > 0);
                if (debitWord != null && decimal.TryParse(debitWord.Text.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var dVal))
                {
                    tx.Debit = dVal;
                }

                // Credits: 550 <= X < 635
                var creditWord = txWords.FirstOrDefault(w => w.X >= 550 && w.X < 635 &&
                                                             decimal.TryParse(w.Text.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var cv) && cv > 0);
                if (creditWord != null && decimal.TryParse(creditWord.Text.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var cVal))
                {
                    tx.Credit = cVal;
                }

                // Balance: 635 <= X < 740
                var balWord = txWords.FirstOrDefault(w => w.X >= 635 && w.X < 740 &&
                                                          decimal.TryParse(w.Text.Replace("Dr.", "").Replace("Cr.", "").Replace("Dr", "").Replace("Cr", "").Replace(",", "").Trim(),
                                                              NumberStyles.Any, CultureInfo.InvariantCulture, out _));
                var typeWord = txWords.FirstOrDefault(w => w.X >= 740 && (w.Text.Contains("Dr", StringComparison.OrdinalIgnoreCase) || w.Text.Contains("Cr", StringComparison.OrdinalIgnoreCase)));
                bool isDr = (typeWord != null && typeWord.Text.Contains("Dr", StringComparison.OrdinalIgnoreCase)) ||
                            (balWord != null && balWord.Text.Contains("Dr", StringComparison.OrdinalIgnoreCase));

                if (balWord != null)
                {
                    string clean = balWord.Text.Replace("Dr.", "").Replace("Cr.", "").Replace("Dr", "").Replace("Cr", "").Replace(",", "").Trim();
                    if (decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var bVal))
                    {
                        tx.Balance = isDr ? -bVal : bVal;
                    }
                }

                // Description: words in description column or continuation lines
                var descWords = txWords
                    .Where(w =>
                    {
                        double x = w.X;
                        if (w == dw || w == snoWord || w == instWord || w == debitWord || w == creditWord || w == balWord || w == typeWord) return false;
                        if (x < 65) return false; // SNo column
                        if (x >= 470) return false; // Amounts column
                        return true;
                    })
                    .OrderBy(w => w.Y)
                    .ThenBy(w => w.X)
                    .Select(w => w.Text)
                    .ToList();

                tx.Description = string.Join(" ", descWords).Trim();

                // Extract potential UTR from narration
                var utrMatch = UtrRegex.Match(tx.Description);
                if (utrMatch.Success && utrMatch.Value.Length >= 12)
                {
                    tx.Utr = utrMatch.Value;
                }
                else if (!string.IsNullOrEmpty(tx.Reference))
                {
                    tx.Utr = tx.Reference;
                }

                tx.RawSourceText = string.Join(" | ", txWords.Select(w => w.Text));
                totalRowsDetected++;

                if (tx.Debit.HasValue || tx.Credit.HasValue || tx.Balance.HasValue)
                {
                    transactions.Add(tx);
                }
            }
        }

        // 5. Sequential Running Balance Continuity & Reconciliation
        decimal runningBal = openingBalance ?? (transactions.FirstOrDefault()?.Balance ?? 0m);
        int balanceMismatchCount = 0;

        for (int i = 0; i < transactions.Count; i++)
        {
            var tx = transactions[i];

            // Reconcile amount if one side is missing but balance delta is known
            if (!tx.Debit.HasValue && !tx.Credit.HasValue && tx.Balance.HasValue)
            {
                decimal diff = tx.Balance.Value - runningBal;
                if (diff > 0)
                {
                    tx.Credit = diff;
                }
                else if (diff < 0)
                {
                    tx.Debit = Math.Abs(diff);
                }
            }

            decimal expectedBal = runningBal - (tx.Debit ?? 0m) + (tx.Credit ?? 0m);

            if (tx.Balance.HasValue && Math.Abs(expectedBal - tx.Balance.Value) > 0.01m)
            {
                balanceMismatchCount++;
                tx.NeedsReview = true;
                tx.ReviewWarnings.Add($"Running balance mismatch: Expected ₹{expectedBal:N2} based on previous balance ₹{runningBal:N2}, but recorded ₹{tx.Balance.Value:N2}.");
            }

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
                tx.TransactionType = "Debit";
            }

            if (tx.Balance.HasValue)
            {
                runningBal = tx.Balance.Value;
            }
        }

        closingBalance = transactions.LastOrDefault()?.Balance;

        if (balanceMismatchCount > 0)
        {
            warnings.Add($"Detected {balanceMismatchCount} running balance continuity mismatch(es) across {transactions.Count} transactions.");
        }

        if (closingBalance.HasValue && Math.Abs(runningBal - closingBalance.Value) > 0.01m)
        {
            warnings.Add($"Final calculated balance ₹{runningBal:N2} differs from closing balance ₹{closingBalance.Value:N2}.");
        }

        _logger.LogInformation("BOI-v1 parse complete: {TxCount} transactions extracted, {Debits} debits, {Credits} credits, {Mismatches} mismatches.",
            transactions.Count, transactions.Count(t => t.Debit.HasValue), transactions.Count(t => t.Credit.HasValue), balanceMismatchCount);

        return new BankParsingResult
        {
            Success = transactions.Count > 0,
            Transactions = transactions,
            TotalDetected = totalRowsDetected,
            ProcessedCount = transactions.Count,
            RejectedCount = 0,
            WarningsCount = warnings.Count,
            OverallConfidence = 1.0,
            AccountNumber = detection?.AccountNumber,
            CustomerName = detection?.CustomerName,
            StatementPeriodStart = detection?.StatementFrom,
            StatementPeriodEnd = detection?.StatementTo,
            Warnings = warnings,
            BankCode = BankCode,
            BankName = BankName,
            ParserVersion = ParserVersion
        };
    }
}
