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
/// Deterministic statement parser for State Bank of India (SBI) statements (SBI-v1).
/// Generic production implementation supporting multi-page statement parsing,
/// staggered Post Date and Value Date anchor grouping, multi-line narration joining,
/// cheque/reference number detection, Indian currency formatting, and sequential
/// running balance continuity verification.
/// </summary>
public class SBIStatementParser : IBankStatementParser
{
    public int BankCode => (int)BankType.SBI;
    public string BankName => "State Bank of India";
    public string ParserVersion => "SBI-v1";

    private readonly ILogger<SBIStatementParser> _logger;

    private static readonly Regex DateRegex = new(@"^\d{2}-\d{2}-\d{4}$", RegexOptions.Compiled);
    private static readonly Regex AmountRegex = new(@"[\d,]+(?:\.\d{1,2})?", RegexOptions.Compiled);
    private static readonly Regex UtrRegex = new(@"\b([A-Z0-9]{12,22})\b", RegexOptions.Compiled);

    public SBIStatementParser(ILogger<SBIStatementParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(BankDetectionResult detection)
    {
        return detection != null &&
               detection.DetectedBank == BankType.SBI &&
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
        int rejectedRows = 0;
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

            // 1. Group words into visual lines using top-left Y coordinate (+- 4pt)
            var orderedBlocks = textBlocks
                .OrderBy(b => b.Y)
                .ThenBy(b => b.X)
                .ToList();

            var lines = new List<List<PdfTextBlock>>();
            foreach (var b in orderedBlocks)
            {
                var matchingLine = lines.FirstOrDefault(l => Math.Abs(l[0].Y - b.Y) <= 4.0);
                if (matchingLine != null)
                {
                    matchingLine.Add(b);
                }
                else
                {
                    lines.Add(new List<PdfTextBlock> { b });
                }
            }

            // 2. Locate table header line
            int headerLineIdx = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                var text = string.Join(" ", lines[i].Select(w => w.Text));
                if ((text.Contains("Post Date", StringComparison.OrdinalIgnoreCase) || text.Contains("Value Date", StringComparison.OrdinalIgnoreCase)) &&
                    text.Contains("Balance", StringComparison.OrdinalIgnoreCase))
                {
                    headerLineIdx = i;
                    break;
                }
                if (text.Contains("Post Date", StringComparison.OrdinalIgnoreCase) && i + 2 < lines.Count &&
                    (string.Join(" ", lines[i + 1].Select(w => w.Text)).Contains("Balance", StringComparison.OrdinalIgnoreCase) ||
                     string.Join(" ", lines[i + 2].Select(w => w.Text)).Contains("Balance", StringComparison.OrdinalIgnoreCase)))
                {
                    headerLineIdx = i;
                    break;
                }
            }

            double headerBottomY = 0;
            if (headerLineIdx >= 0)
            {
                int lastHeaderIdx = headerLineIdx;
                for (int i = headerLineIdx + 1; i < Math.Min(lines.Count, headerLineIdx + 4); i++)
                {
                    var lineText = string.Join(" ", lines[i].Select(w => w.Text));
                    if (lineText.Contains("BROUGHT FORWARD", StringComparison.OrdinalIgnoreCase) ||
                        lines[i].Any(w => w.X < 100 && DateRegex.IsMatch(w.Text)))
                    {
                        break;
                    }
                    if (lineText.Contains("Date", StringComparison.OrdinalIgnoreCase) ||
                        lineText.Contains("Post", StringComparison.OrdinalIgnoreCase) ||
                        lineText.Contains("Balance", StringComparison.OrdinalIgnoreCase) ||
                        lineText.Contains("Ref", StringComparison.OrdinalIgnoreCase) ||
                        lineText.Contains("Cheque", StringComparison.OrdinalIgnoreCase) ||
                        lineText.Contains("Description", StringComparison.OrdinalIgnoreCase) ||
                        lineText.Contains("Credit", StringComparison.OrdinalIgnoreCase) ||
                        lineText.Contains("Debit", StringComparison.OrdinalIgnoreCase))
                    {
                        lastHeaderIdx = i;
                    }
                    else
                    {
                        break;
                    }
                }
                headerBottomY = lines[lastHeaderIdx].Max(w => w.Y + w.Height);
            }

            // 3. Locate footer / end of table & extract Opening/Closing balances
            double footerTopY = page.Height > 0 ? page.Height : 2000.0;
            foreach (var l in lines)
            {
                var sorted = l.OrderBy(w => w.X).ToList();
                string text = string.Join(" ", sorted.Select(w => w.Text));
                double y = l.Min(w => w.Y);

                bool isClosing = text.Contains("CLOSING BALANCE", StringComparison.OrdinalIgnoreCase) ||
                                 (l.Any(w => w.Text.Equals("CLOSING", StringComparison.OrdinalIgnoreCase)) &&
                                  l.Any(w => w.Text.Equals("BALANCE", StringComparison.OrdinalIgnoreCase)));

                if (isClosing)
                {
                    var balWord = l.LastOrDefault(w => w.Text.EndsWith("CR", StringComparison.OrdinalIgnoreCase) || w.Text.EndsWith("DR", StringComparison.OrdinalIgnoreCase));
                    if (balWord != null && TryParseAmount(balWord.Text, out var cbVal))
                    {
                        closingBalance ??= cbVal;
                    }
                    footerTopY = Math.Min(footerTopY, y - 2);
                }
                else if (text.Contains("Statement Summary", StringComparison.OrdinalIgnoreCase) ||
                         text.Contains("In Case Your Account", StringComparison.OrdinalIgnoreCase) ||
                         text.Contains("END OF STATEMENT", StringComparison.OrdinalIgnoreCase))
                {
                    footerTopY = Math.Min(footerTopY, y - 2);
                }
                else if (text.Contains("BROUGHT FORWARD", StringComparison.OrdinalIgnoreCase) ||
                         (l.Any(w => w.Text.Equals("BROUGHT", StringComparison.OrdinalIgnoreCase)) &&
                          l.Any(w => w.Text.Equals("FORWARD", StringComparison.OrdinalIgnoreCase))))
                {
                    var balWord = l.LastOrDefault(w => w.Text.EndsWith("CR", StringComparison.OrdinalIgnoreCase) || w.Text.EndsWith("DR", StringComparison.OrdinalIgnoreCase));
                    if (balWord != null && TryParseAmount(balWord.Text, out var obVal))
                    {
                        openingBalance ??= obVal;
                    }
                }
            }

            // 4. Filter lines within table body
            var tableLines = lines
                .Where(l =>
                {
                    var sorted = l.OrderBy(w => w.X).ToList();
                    string text = string.Join(" ", sorted.Select(w => w.Text));
                    double y = l.Min(w => w.Y);

                    bool isClosing = text.Contains("CLOSING BALANCE", StringComparison.OrdinalIgnoreCase) ||
                                     (l.Any(w => w.Text.Equals("CLOSING", StringComparison.OrdinalIgnoreCase)) &&
                                      l.Any(w => w.Text.Equals("BALANCE", StringComparison.OrdinalIgnoreCase)));
                    bool isBf = text.Contains("BROUGHT FORWARD", StringComparison.OrdinalIgnoreCase) ||
                                (l.Any(w => w.Text.Equals("BROUGHT", StringComparison.OrdinalIgnoreCase)) &&
                                 l.Any(w => w.Text.Equals("FORWARD", StringComparison.OrdinalIgnoreCase)));

                    return y > headerBottomY && y < footerTopY &&
                           !isBf && !isClosing &&
                           !text.Contains("Page no.", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            // 5. Find transaction anchors:
            // An anchor is a line containing a date in X < 100
            var anchors = new List<(int LineIdx, DateTime PostDate, DateTime ValueDate, double Y)>();
            for (int i = 0; i < tableLines.Count; i++)
            {
                var l = tableLines[i];
                var dateWord = l.FirstOrDefault(w => w.X < 100 && DateRegex.IsMatch(w.Text));
                if (dateWord != null && DateTime.TryParseExact(dateWord.Text, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var pDate))
                {
                    double y = dateWord.Y;

                    // If this date line is within 12pt vertically of the previous anchor, it is the staggered Value Date
                    if (anchors.Count > 0 && Math.Abs(y - anchors.Last().Y) < 12.0)
                    {
                        var last = anchors.Last();
                        anchors[anchors.Count - 1] = (last.LineIdx, last.PostDate, pDate, last.Y);
                        continue;
                    }

                    // Look for value date on the same line if present
                    var vDateWord = l.FirstOrDefault(w => w.X >= 60 && w.X < 170 && w != dateWord && DateRegex.IsMatch(w.Text));
                    DateTime vDate = pDate;
                    if (vDateWord != null && DateTime.TryParseExact(vDateWord.Text, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var vd))
                    {
                        vDate = vd;
                    }

                    anchors.Add((i, pDate, vDate, y));
                }
            }

            // 6. Partition lines and extract each transaction
            for (int i = 0; i < anchors.Count; i++)
            {
                int startIdx = anchors[i].LineIdx;
                int endIdx = (i + 1 < anchors.Count) ? anchors[i + 1].LineIdx : tableLines.Count;

                var txLines = tableLines.Skip(startIdx).Take(endIdx - startIdx).ToList();

                var tx = new ParsedTransaction
                {
                    TransactionDate = anchors[i].PostDate,
                    ValueDate = anchors[i].ValueDate,
                    SourcePageNumber = page.PageNumber,
                    SourceLineIndex = rowIndexCounter++,
                    BankCode = BankCode,
                    BankName = BankName,
                    ParserVersion = ParserVersion
                };

                var descParts = new List<string>();
                string? chequeNo = null;
                decimal? debit = null;
                decimal? credit = null;
                decimal? balance = null;

                foreach (var line in txLines)
                {
                    foreach (var w in line)
                    {
                        double x = w.X;

                        // Balance column (handled via balTokens below)
                        if (x >= 490)
                        {
                            continue;
                        }
                        // Credit column (430 <= X < 500)
                        else if (x >= 430 && x < 490 && AmountRegex.IsMatch(w.Text))
                        {
                            if (!credit.HasValue && TryParseAmount(w.Text, out var cVal))
                            {
                                credit = cVal;
                            }
                        }
                        // Debit column (360 <= X < 430)
                        else if (x >= 360 && x < 430 && AmountRegex.IsMatch(w.Text))
                        {
                            if (!debit.HasValue && TryParseAmount(w.Text, out var dVal))
                            {
                                debit = dVal;
                            }
                        }
                        // Cheque / Ref column (270 <= X < 360)
                        else if (x >= 270 && x < 360)
                        {
                            if (string.IsNullOrWhiteSpace(chequeNo) && w.Text.Length >= 3 && char.IsDigit(w.Text[0]))
                            {
                                chequeNo = w.Text;
                            }
                            else
                            {
                                descParts.Add(w.Text);
                            }
                        }
                        // Narration description (130 <= X < 360)
                        else if (x >= 130 && x < 360)
                        {
                            descParts.Add(w.Text);
                        }
                        // Other words not in date columns
                        else if (x < 130 && !DateRegex.IsMatch(w.Text))
                        {
                            descParts.Add(w.Text);
                        }
                    }
                }

                // Balance extraction: gather tokens in balance column (X >= 490)
                var balTokens = txLines.SelectMany(l => l)
                    .Where(w => w.X >= 490)
                    .OrderBy(w => w.X)
                    .Select(w => w.Text)
                    .Distinct()
                    .ToList();

                if (balTokens.Count > 0)
                {
                    string joinedBal = string.Join("", balTokens)
                        .Replace("CR", "", StringComparison.OrdinalIgnoreCase)
                        .Replace("DR", "", StringComparison.OrdinalIgnoreCase)
                        .Replace(" ", "");

                    int lastComma = joinedBal.LastIndexOf(',');
                    int lastDot = joinedBal.LastIndexOf('.');
                    if (lastComma > lastDot && joinedBal.Length - lastComma == 3)
                    {
                        joinedBal = joinedBal.Substring(0, lastComma).Replace(",", "") + "." + joinedBal.Substring(lastComma + 1);
                    }
                    else
                    {
                        joinedBal = joinedBal.Replace(",", "");
                    }

                    if (decimal.TryParse(joinedBal, NumberStyles.Any, CultureInfo.InvariantCulture, out var bVal))
                    {
                        balance = bVal;
                    }
                }

                tx.Description = string.Join(" ", descParts).Trim();
                tx.Debit = debit;
                tx.Credit = credit;
                tx.Balance = balance;
                tx.Reference = chequeNo;

                // Extract potential UTR from narration if not already found in cheque column
                if (string.IsNullOrEmpty(tx.Reference))
                {
                    var utrMatch = UtrRegex.Match(tx.Description);
                    if (utrMatch.Success && utrMatch.Value.Length >= 12)
                    {
                        tx.Utr = utrMatch.Value;
                    }
                }
                else
                {
                    tx.Utr = chequeNo;
                }

                tx.RawSourceText = string.Join(" | ", txLines.Select(l => string.Join(" ", l.Select(w => w.Text))));
                totalRowsDetected++;

                if (tx.Debit.HasValue || tx.Credit.HasValue || tx.Balance.HasValue)
                {
                    transactions.Add(tx);
                }
                else
                {
                    rejectedRows++;
                }
            }
        }

        // 7. Sequential Running Balance Continuity & Reconciliation
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

            decimal expectedBal = tx.Debit.HasValue
                ? (runningBal - tx.Debit.Value)
                : (runningBal + (tx.Credit ?? 0m));

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

        if (balanceMismatchCount > 0)
        {
            warnings.Add($"Detected {balanceMismatchCount} running balance continuity mismatch(es) across {transactions.Count} transactions.");
        }

        if (closingBalance.HasValue && Math.Abs(runningBal - closingBalance.Value) > 0.01m)
        {
            warnings.Add($"Final calculated balance ₹{runningBal:N2} differs from closing balance ₹{closingBalance.Value:N2}.");
        }

        _logger.LogInformation("SBI-v1 parse complete: {TxCount} transactions extracted, {Debits} debits, {Credits} credits, {Mismatches} mismatches.",
            transactions.Count, transactions.Count(t => t.Debit.HasValue), transactions.Count(t => t.Credit.HasValue), balanceMismatchCount);

        return new BankParsingResult
        {
            Success = transactions.Count > 0,
            Transactions = transactions,
            TotalDetected = totalRowsDetected,
            ProcessedCount = transactions.Count,
            RejectedCount = rejectedRows,
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

    private static bool TryParseAmount(string text, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var clean = text.Replace("CR", "", StringComparison.OrdinalIgnoreCase)
                        .Replace("DR", "", StringComparison.OrdinalIgnoreCase)
                        .Replace(",", "")
                        .Trim();

        return decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out amount);
    }
}
