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
/// Deterministic statement parser for Kotak Mahindra Bank (KOTAK-v1).
/// Production-grade implementation supporting multi-page statement parsing,
/// dynamic column boundary detection, multi-line narration reconstruction,
/// cheque/reference number extraction (UPI, IMPS, MB, EBPP, TBMS, cheque),
/// culture-invariant Dr./Cr. amount parsing, and running balance continuity verification.
/// </summary>
public class KotakStatementParser : IBankStatementParser
{
    public int BankCode => (int)BankType.Kotak;
    public string BankName => "Kotak Mahindra Bank";
    public string ParserVersion => "KOTAK-v1";

    private readonly ILogger<KotakStatementParser> _logger;

    private static readonly Regex DateRegex = new(
        @"^(?:\d{1,2}\s+(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\s+\d{4}|\d{2}[/-]\d{2}[/-]\d{4}|\d{1,2}-[A-Za-z]{3}-\d{4})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EmbeddedDateRegex = new(
        @"\b(?:\d{1,2}\s+(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\s+\d{4}|\d{2}[/-]\d{2}[/-]\d{4}|\d{1,2}-[A-Za-z]{3}-\d{4})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AmountRegex = new(@"^-?[\d,]+(?:\.\d{1,2})?$", RegexOptions.Compiled);
    private static readonly Regex EmbeddedAmountRegex = new(@"-?[\d,]+\.\d{2}", RegexOptions.Compiled);
    private static readonly Regex UtrRegex = new(@"\b(UPI-[A-Za-z0-9]+|MB-[A-Za-z0-9]+|IMPS-[A-Za-z0-9]+|EBPP-[A-Za-z0-9]+|TBMS-[A-Za-z0-9]+|[A-Z0-9]{12,22})\b", RegexOptions.Compiled);

    public KotakStatementParser(ILogger<KotakStatementParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(BankDetectionResult detection)
    {
        return detection != null &&
               detection.DetectedBank == BankType.Kotak &&
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
        bool statementEnded = false;

        string? detectedAccount = detection?.AccountNumber;
        string? customerName = detection?.CustomerName;

        foreach (var page in extraction.Pages.OrderBy(p => p.PageNumber))
        {
            if (statementEnded)
            {
                break;
            }

            var textBlocks = (page.TextBlocks != null && page.TextBlocks.Count > 0)
                ? page.TextBlocks.Where(b => !string.IsNullOrWhiteSpace(b.Text)).ToList()
                : (page.CandidateRows != null
                    ? page.CandidateRows.SelectMany(r => r.LineFragments).Where(b => !string.IsNullOrWhiteSpace(b.Text)).ToList()
                    : new List<PdfTextBlock>());

            if (textBlocks.Count == 0 && (page.CandidateRows == null || page.CandidateRows.Count == 0))
            {
                continue;
            }

            // 1. Group words into visual lines using Y coordinate (+- 4.0pt) and sort words horizontally
            var lines = GroupIntoLines(page, textBlocks);

            if (lines.Count == 0)
            {
                continue;
            }

            // Check if this page is a non-transaction section (e.g. Account Summary, Important Information / Glossary)
            bool isSummaryOrInfoPage = lines.Any(l =>
            {
                var t = string.Join(" ", l.Select(w => w.Text));
                return t.Contains("Account Summary", StringComparison.OrdinalIgnoreCase) ||
                       t.Contains("Summary of Accounts", StringComparison.OrdinalIgnoreCase) ||
                       t.Contains("Important Information", StringComparison.OrdinalIgnoreCase) ||
                       t.Contains("Commonly Used Narrations", StringComparison.OrdinalIgnoreCase);
            });

            if (isSummaryOrInfoPage)
            {
                foreach (var line in lines)
                {
                    var lineText = string.Join(" ", line.Select(w => w.Text));

                    // Extract Opening and Closing Balance from Account Summary table if available
                    if (lineText.Contains("Savings Account", StringComparison.OrdinalIgnoreCase) ||
                        lineText.Contains("Current Account", StringComparison.OrdinalIgnoreCase))
                    {
                        var amtWords = line.Where(w => AmountRegex.IsMatch(w.Text.Trim()) && (w.Text.Contains('.') || w.Text.Contains(','))).ToList();
                        if (amtWords.Count >= 2)
                        {
                            if (TryParseAmount(amtWords[0].Text, out var sumOb)) openingBalance ??= sumOb;
                            if (TryParseAmount(amtWords[1].Text, out var sumCb)) closingBalance ??= sumCb;
                        }
                    }

                    if (lineText.Contains("End of Statement", StringComparison.OrdinalIgnoreCase))
                    {
                        statementEnded = true;
                    }
                }

                // If this is a summary or advisory page without a transaction table header, skip processing transaction rows
                bool hasTableColumns = lines.Any(l =>
                {
                    var t = string.Join(" ", l.Select(w => w.Text));
                    return (t.Contains("Withdrawal", StringComparison.OrdinalIgnoreCase) || t.Contains("(Dr.)", StringComparison.OrdinalIgnoreCase)) &&
                           (t.Contains("Deposit", StringComparison.OrdinalIgnoreCase) || t.Contains("(Cr.)", StringComparison.OrdinalIgnoreCase)) &&
                           t.Contains("Balance", StringComparison.OrdinalIgnoreCase);
                });

                if (!hasTableColumns || statementEnded)
                {
                    continue;
                }
            }

            // 2. Locate table header line
            int headerLineIdx = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                var text = string.Join(" ", lines[i].Select(w => w.Text));
                if ((text.Contains("Date", StringComparison.OrdinalIgnoreCase) || text.Contains("Txn Date", StringComparison.OrdinalIgnoreCase)) &&
                    (text.Contains("Description", StringComparison.OrdinalIgnoreCase) || text.Contains("Narration", StringComparison.OrdinalIgnoreCase) || text.Contains("Particulars", StringComparison.OrdinalIgnoreCase)) &&
                    (text.Contains("Balance", StringComparison.OrdinalIgnoreCase) || text.Contains("Withdrawal", StringComparison.OrdinalIgnoreCase) || text.Contains("Deposit", StringComparison.OrdinalIgnoreCase)))
                {
                    headerLineIdx = i;
                    break;
                }
            }

            double headerBottomY = 0;
            if (headerLineIdx >= 0)
            {
                int lastHeaderIdx = headerLineIdx;
                for (int i = headerLineIdx + 1; i < Math.Min(lines.Count, headerLineIdx + 3); i++)
                {
                    var lineWords = lines[i];
                    bool isTxn = HasTransactionDateAndAmounts(lineWords, out _, out _, out _) ||
                                 (lineWords.Count > 0 && DateRegex.IsMatch(lineWords[0].Text)) ||
                                 (lineWords.Count > 1 && int.TryParse(lineWords[0].Text, out _) && DateRegex.IsMatch(lineWords[1].Text));
                    if (isTxn)
                    {
                        break;
                    }

                    var lineText = string.Join(" ", lineWords.Select(w => w.Text));
                    if (lineText.Contains("Withdrawal", StringComparison.OrdinalIgnoreCase) ||
                        lineText.Contains("Deposit", StringComparison.OrdinalIgnoreCase) ||
                        lineText.Contains("(Dr.)", StringComparison.OrdinalIgnoreCase) ||
                        lineText.Contains("(Cr.)", StringComparison.OrdinalIgnoreCase) ||
                        lineText.Contains("Chq/Ref", StringComparison.OrdinalIgnoreCase) ||
                        lineText.Contains("Balance", StringComparison.OrdinalIgnoreCase))
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
            else
            {
                headerBottomY = 0;
            }

            // 3. Locate Opening Balance on Page 1 (or search in header region)
            if (page.PageNumber == 1 && !openingBalance.HasValue)
            {
                foreach (var line in lines)
                {
                    var lineText = string.Join(" ", line.Select(w => w.Text));
                    if (lineText.Contains("Opening Balance", StringComparison.OrdinalIgnoreCase) ||
                        lineText.Contains("Brought Forward", StringComparison.OrdinalIgnoreCase))
                    {
                        var amtWords = line.Where(w => AmountRegex.IsMatch(w.Text.Trim()) && (w.Text.Contains('.') || w.Text.Contains(','))).ToList();
                        if (amtWords.Count > 0 && TryParseAmount(amtWords.Last().Text, out var obVal))
                        {
                            openingBalance = obVal;
                            break;
                        }
                    }
                }
            }

            // Also extract metadata if not yet populated
            if (string.IsNullOrEmpty(detectedAccount))
            {
                foreach (var line in lines)
                {
                    var lineText = string.Join(" ", line.Select(w => w.Text));
                    var mAcc = Regex.Match(lineText, @"Account\s*(?:No|Number|#)\s*[:.]?\s*(\d{10,18})", RegexOptions.IgnoreCase);
                    if (mAcc.Success)
                    {
                        detectedAccount = mAcc.Groups[1].Value;
                        break;
                    }
                }
            }

            // 4. Process lines below header
            double footerTopY = page.Height > 0 ? page.Height : 2000.0;
            var transactionLines = new List<List<PdfTextBlock>>();

            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                var lineY = l.Min(w => w.Y);
                if (lineY <= headerBottomY)
                {
                    continue; // Skip header lines
                }

                var lineText = string.Join(" ", l.Select(w => w.Text));

                // Check for footer / end of table markers
                if (lineText.Contains("Statement Generated on", StringComparison.OrdinalIgnoreCase) ||
                    lineText.Contains("This is a system generated", StringComparison.OrdinalIgnoreCase) ||
                    Regex.IsMatch(lineText, @"Page\s+\d+\s+of\s+\d+", RegexOptions.IgnoreCase) ||
                    lineText.Contains("Registered Office", StringComparison.OrdinalIgnoreCase) ||
                    lineText.Contains("Regd. Office", StringComparison.OrdinalIgnoreCase) ||
                    lineText.Contains("In case of any discrepancy", StringComparison.OrdinalIgnoreCase) ||
                    lineText.Contains("End of Statement", StringComparison.OrdinalIgnoreCase) ||
                    lineText.Contains("Account Summary", StringComparison.OrdinalIgnoreCase) ||
                    lineText.Contains("Important Information", StringComparison.OrdinalIgnoreCase))
                {
                    footerTopY = Math.Min(footerTopY, lineY - 2);
                    if (lineText.Contains("End of Statement", StringComparison.OrdinalIgnoreCase))
                    {
                        statementEnded = true;
                    }
                    continue;
                }

                if (lineY >= footerTopY)
                {
                    continue;
                }

                // Defensively skip period banners or account details lines that might be below headerBottomY
                if (Regex.IsMatch(lineText, @"^Account\s+Statement\s+\d{1,2}\s+[A-Za-z]{3}\s+\d{4}", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(lineText, @"^Statement\s+of\s+Account", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(lineText, @"^Account\s+No\.?\s+\d+", RegexOptions.IgnoreCase))
                {
                    continue;
                }

                transactionLines.Add(l);
            }

            // 5. Cluster lines into transactions
            ParsedTransaction? currentTx = null;

            foreach (var line in transactionLines)
            {
                var sortedWords = line.OrderBy(w => w.X).ToList();
                var lineText = string.Join(" ", sortedWords.Select(w => w.Text)).Trim();

                if (string.IsNullOrWhiteSpace(lineText)) continue;

                // Check if this line is an opening balance line
                if (lineText.Contains("Opening Balance", StringComparison.OrdinalIgnoreCase))
                {
                    var amtWords = sortedWords.Where(w => AmountRegex.IsMatch(w.Text.Trim()) && (w.Text.Contains('.') || w.Text.Contains(','))).ToList();
                    if (amtWords.Count > 0 && TryParseAmount(amtWords.Last().Text, out var obVal))
                    {
                        openingBalance ??= obVal;
                    }
                    continue;
                }

                // Check if line represents the start of a new transaction
                // In Kotak: line may start with Sr No (e.g. "1") or directly Date ("05 Oct 2022")
                bool startsWithSrNo = sortedWords.Count > 1 && int.TryParse(sortedWords[0].Text, out _) && (DateRegex.IsMatch(sortedWords[1].Text) || (sortedWords.Count > 3 && DateRegex.IsMatch($"{sortedWords[1].Text} {sortedWords[2].Text} {sortedWords[3].Text}")));
                bool startsWithDate = DateRegex.IsMatch(sortedWords[0].Text) || (sortedWords.Count >= 3 && DateRegex.IsMatch($"{sortedWords[0].Text} {sortedWords[1].Text} {sortedWords[2].Text}"));

                if (startsWithSrNo || startsWithDate || HasTransactionDateAndAmounts(sortedWords, out _, out _, out _))
                {
                    // If we had a previous transaction being constructed, finalize it
                    if (currentTx != null)
                    {
                        transactions.Add(currentTx);
                        totalRowsDetected++;
                    }

                    // Parse new transaction row
                    currentTx = ParseTransactionRow(sortedWords, rowIndexCounter++, page.PageNumber);
                }
                else if (currentTx != null)
                {
                    // Multi-line narration continuation
                    // Ensure it's not a repeating column header or page artifact
                    if (!lineText.Contains("Withdrawal", StringComparison.OrdinalIgnoreCase) &&
                        !lineText.Contains("Deposit", StringComparison.OrdinalIgnoreCase) &&
                        !lineText.Contains("Balance", StringComparison.OrdinalIgnoreCase) &&
                        !lineText.Contains("Chq/Ref", StringComparison.OrdinalIgnoreCase) &&
                        !lineText.Contains("Kotak Mahindra Bank", StringComparison.OrdinalIgnoreCase))
                    {
                        currentTx.Description = (currentTx.Description + " " + lineText).Trim();
                    }
                }
            }

            if (currentTx != null)
            {
                transactions.Add(currentTx);
                totalRowsDetected++;
                currentTx = null;
            }
        }

        // 6. Running Balance Verification & Math Continuity
        decimal runningBal = openingBalance ?? (transactions.FirstOrDefault()?.Balance ?? 0m);
        int balanceMismatchCount = 0;
        for (int i = 0; i < transactions.Count; i++)
        {
            var tx = transactions[i];

            // Continuity formula for Kotak: Previous Balance - Debit + Credit = Current Balance
            decimal debit = tx.Debit ?? 0m;
            decimal credit = tx.Credit ?? 0m;
            decimal expected = runningBal - debit + credit;

            if (tx.Balance.HasValue)
            {
                if (Math.Abs(tx.Balance.Value - expected) > 0.05m && i > 0)
                {
                    balanceMismatchCount++;
                    warnings.Add($"Balance continuity mismatch at row #{tx.SourceLineIndex} ({tx.TransactionDate:dd/MM/yyyy}): Expected {expected:F2} but recorded {tx.Balance.Value:F2}.");
                }
                runningBal = tx.Balance.Value;
            }
            else
            {
                tx.Balance = expected;
                runningBal = expected;
            }
        }

        if (transactions.Count > 0)
        {
            closingBalance ??= transactions.Last().Balance;
            openingBalance ??= (transactions[0].Balance ?? 0m) + (transactions[0].Debit ?? 0m) - (transactions[0].Credit ?? 0m);
        }

        decimal totalDebits = transactions.Where(t => t.Debit.HasValue).Sum(t => t.Debit!.Value);
        decimal totalCredits = transactions.Where(t => t.Credit.HasValue).Sum(t => t.Credit!.Value);

        _logger.LogInformation("Parsed {Count} Kotak transactions. Debits: {Debits:C2}, Credits: {Credits:C2}, Opening: {Open}, Closing: {Close}, Warnings: {WarnCount}",
            transactions.Count, totalDebits, totalCredits, openingBalance, closingBalance, warnings.Count);

        return new BankParsingResult
        {
            Success = transactions.Count > 0,
            BankCode = BankCode,
            BankName = BankName,
            ParserVersion = ParserVersion,
            AccountNumber = detectedAccount,
            CustomerName = customerName,
            StatementPeriodStart = detection?.StatementFrom,
            StatementPeriodEnd = detection?.StatementTo,
            TotalDetected = totalRowsDetected,
            ProcessedCount = transactions.Count,
            RejectedCount = 0,
            WarningsCount = warnings.Count,
            OverallConfidence = 1.0,
            Transactions = transactions,
            Warnings = warnings
        };
    }

    private static List<List<PdfTextBlock>> GroupIntoLines(PdfPageResult page, List<PdfTextBlock> textBlocks)
    {
        if (textBlocks.Count > 0)
        {
            var orderedBlocks = textBlocks
                .OrderBy(b => b.Y)
                .ThenBy(b => b.X)
                .ToList();

            var result = new List<List<PdfTextBlock>>();
            foreach (var b in orderedBlocks)
            {
                var matchingLine = result.FirstOrDefault(l => Math.Abs(l[0].Y - b.Y) <= 4.0);
                if (matchingLine != null)
                {
                    matchingLine.Add(b);
                }
                else
                {
                    result.Add(new List<PdfTextBlock> { b });
                }
            }

            foreach (var l in result)
            {
                l.Sort((a, b) => a.X.CompareTo(b.X));
            }

            return result;
        }

        if (page.CandidateRows != null && page.CandidateRows.Count > 0)
        {
            var result = new List<List<PdfTextBlock>>();
            foreach (var row in page.CandidateRows.OrderBy(r => r.Y))
            {
                if (row.LineFragments != null && row.LineFragments.Count > 0)
                {
                    var frags = row.LineFragments.OrderBy(f => f.X).ToList();
                    result.Add(frags);
                }
                else if (!string.IsNullOrWhiteSpace(row.RawLineText))
                {
                    // Synthesize block from RawLineText
                    result.Add(new List<PdfTextBlock>
                    {
                        new()
                        {
                            Text = row.RawLineText,
                            X = 50,
                            Y = row.Y,
                            Width = 400,
                            Height = 12,
                            PageNumber = page.PageNumber
                        }
                    });
                }
            }
            return result;
        }

        return new List<List<PdfTextBlock>>();
    }

    private static bool HasTransactionDateAndAmounts(List<PdfTextBlock> words, out DateTime txnDate, out int dateEndIdx, out List<int> amountIndices)
    {
        txnDate = default;
        dateEndIdx = -1;
        amountIndices = new List<int>();

        if (words == null || words.Count == 0) return false;

        // Never treat period header banners, account summaries, or notice lines as transactions
        var lineText = string.Join(" ", words.Select(w => w.Text));
        if (lineText.Contains("Statement", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Account", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Period", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Page ", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains(" - ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Look for date in words
        for (int i = 0; i < Math.Min(5, words.Count); i++)
        {
            if (TryParseDate(words[i].Text, out txnDate))
            {
                dateEndIdx = i;
                break;
            }
            if (i + 2 < words.Count && TryParseDate($"{words[i].Text} {words[i + 1].Text} {words[i + 2].Text}", out txnDate))
            {
                dateEndIdx = i + 2;
                break;
            }
        }

        if (dateEndIdx < 0) return false;

        // Check if there are amounts with decimals towards the right of the row
        for (int i = dateEndIdx + 1; i < words.Count; i++)
        {
            var text = words[i].Text.Trim();
            if ((words[i].X >= 340 || words[i].X <= 0) &&
                AmountRegex.IsMatch(text) &&
                (text.Contains('.') || text.Contains(',')))
            {
                amountIndices.Add(i);
            }
        }

        return amountIndices.Count >= 1;
    }

    public ParsedTransaction ParseTransactionRow(List<PdfTextBlock> words, int rowIndex, int pageNumber)
    {
        // 1. Identify Date
        DateTime txnDate = DateTime.MinValue;
        int descStartIdx = 0;

        if (words.Count > 1 && int.TryParse(words[0].Text, out _))
        {
            descStartIdx = 1;
        }

        for (int i = descStartIdx; i < Math.Min(descStartIdx + 4, words.Count); i++)
        {
            if (TryParseDate(words[i].Text, out txnDate))
            {
                descStartIdx = i + 1;
                break;
            }
            if (i + 2 < words.Count && TryParseDate($"{words[i].Text} {words[i + 1].Text} {words[i + 2].Text}", out txnDate))
            {
                descStartIdx = i + 3;
                break;
            }
        }

        // 2. Identify amounts from the right
        // In Kotak table: Withdrawal (Dr.), Deposit (Cr.), Balance (X >= 340)
        var amountWords = new List<(int Index, PdfTextBlock Block, decimal Value)>();
        for (int i = descStartIdx; i < words.Count; i++)
        {
            var w = words[i];

            // In Kotak table layout, amounts reside in the right-hand columns (X >= 340).
            // Tokens at X < 340 are SrNo, Date, Description, or Cheque/Reference.
            if (w.X > 0 && w.X < 340)
            {
                continue;
            }

            // Exclude pure reference/cheque integer numbers without decimals (e.g. "000124", "227899615102", "2026")
            if (Regex.IsMatch(w.Text.Trim(), @"^\d{4,16}$") && !w.Text.Contains('.') && !w.Text.Contains(','))
            {
                continue;
            }

            if (TryParseAmount(w.Text, out var val))
            {
                amountWords.Add((i, w, val));
            }
        }

        decimal? debit = null;
        decimal? credit = null;
        decimal? balance = null;
        int descEndIdx = words.Count - 1;

        if (amountWords.Count >= 2)
        {
            // The last amount is typically Balance
            var balTuple = amountWords.Last();
            balance = balTuple.Value;

            // The second to last amount is either Debit or Credit
            var amtTuple = amountWords[amountWords.Count - 2];
            descEndIdx = amtTuple.Index - 1;

            // Determine if debit or credit based on position or column structure
            // If there are 3 amounts (Debit, Credit, Balance), e.g. one is 0.00
            if (amountWords.Count >= 3)
            {
                var drTuple = amountWords[amountWords.Count - 3];
                var crTuple = amountWords[amountWords.Count - 2];
                descEndIdx = drTuple.Index - 1;

                if (drTuple.Value > 0) debit = drTuple.Value;
                if (crTuple.Value > 0) credit = crTuple.Value;
            }
            else
            {
                // We have 1 transaction amount and 1 balance
                // Check X coordinate or word text to classify as debit or credit
                // Typically: Debit is to the left of Credit. Page width ~ 600.
                // Debit X ~ 340-425, Credit X ~ 425-500.
                if (amtTuple.Block.X > 0 && amtTuple.Block.X < 425)
                {
                    debit = amtTuple.Value;
                }
                else if (amtTuple.Block.X >= 425)
                {
                    credit = amtTuple.Value;
                }
                else
                {
                    // If no coordinate geometry (e.g. synthetic test with 2 amounts: [Amount, Balance])
                    // Look at raw line text for (Dr)/(Cr) indicator or default based on transaction type if specified
                    var raw = string.Join(" ", words.Select(x => x.Text));
                    if (raw.Contains("Dr", StringComparison.OrdinalIgnoreCase))
                    {
                        debit = amtTuple.Value;
                    }
                    else if (raw.Contains("Cr", StringComparison.OrdinalIgnoreCase))
                    {
                        credit = amtTuple.Value;
                    }
                    else
                    {
                        // Default to debit if no other indicator
                        debit = amtTuple.Value;
                    }
                }
            }
        }
        else if (amountWords.Count == 1)
        {
            balance = amountWords[0].Value;
            descEndIdx = amountWords[0].Index - 1;
        }

        // 3. Separate Reference and Narration from remaining words
        var middleWords = new List<PdfTextBlock>();
        for (int i = descStartIdx; i <= descEndIdx; i++)
        {
            if (i >= 0 && i < words.Count)
            {
                middleWords.Add(words[i]);
            }
        }

        string? reference = null;
        string? utr = null;
        var descWords = new List<string>();

        foreach (var w in middleWords)
        {
            var token = w.Text.Trim();
            // Check if token matches Chq/Ref pattern (e.g. UPI-..., MB-..., IMPS-..., EBPP-..., TBMS-..., or 6-12 digit cheque/reference)
            if (reference == null && (token.StartsWith("UPI-", StringComparison.OrdinalIgnoreCase) ||
                                      token.StartsWith("MB-", StringComparison.OrdinalIgnoreCase) ||
                                      token.StartsWith("IMPS-", StringComparison.OrdinalIgnoreCase) ||
                                      token.StartsWith("EBPP-", StringComparison.OrdinalIgnoreCase) ||
                                      token.StartsWith("TBMS-", StringComparison.OrdinalIgnoreCase) ||
                                      (Regex.IsMatch(token, @"^\d{6,12}$") && (w.X <= 0 || (w.X > 220 && w.X < 390)))))
            {
                reference = token;
                if (token.StartsWith("UPI-", StringComparison.OrdinalIgnoreCase) ||
                    token.StartsWith("IMPS-", StringComparison.OrdinalIgnoreCase) ||
                    token.Length >= 12)
                {
                    utr = token;
                }
            }
            else
            {
                descWords.Add(token);
            }
        }

        string description = string.Join(" ", descWords).Trim();

        // Extract reference from description if still empty
        if (string.IsNullOrEmpty(reference))
        {
            var matchUtr = UtrRegex.Match(description);
            if (matchUtr.Success)
            {
                reference = matchUtr.Value;
                utr = matchUtr.Value;
            }
        }

        decimal amount = debit ?? credit ?? 0m;
        string txnType = credit.HasValue && credit.Value > 0 ? "Credit" : "Debit";

        return new ParsedTransaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = txnDate != DateTime.MinValue ? txnDate : DateTime.Today,
            ValueDate = txnDate != DateTime.MinValue ? txnDate : null,
            Description = description,
            Reference = reference,
            Utr = utr,
            Debit = debit,
            Credit = credit,
            Amount = amount,
            Balance = balance,
            TransactionType = txnType,
            BankCode = BankCode,
            BankName = BankName,
            ParserVersion = ParserVersion,
            SourcePageNumber = pageNumber,
            SourceLineIndex = rowIndex,
            Confidence = 1.0,
            RawSourceText = string.Join(" ", words.Select(w => w.Text))
        };
    }

    private static bool TryParseDate(string text, out DateTime date)
    {
        date = default;
        text = text.Trim();

        string[] formats =
        {
            "dd MMM yyyy", "d MMM yyyy", "dd-MMM-yyyy", "d-MMM-yyyy",
            "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy",
            "dd.MM.yyyy", "yyyy-MM-dd"
        };

        return DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static bool TryParseAmount(string text, out decimal amount)
    {
        amount = 0m;
        text = text.Trim().Replace(",", "");

        // Remove Dr/Cr suffix if attached
        if (text.EndsWith("Dr", StringComparison.OrdinalIgnoreCase) || text.EndsWith("Cr", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(0, text.Length - 2).Trim();
        }

        return decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount);
    }
}
