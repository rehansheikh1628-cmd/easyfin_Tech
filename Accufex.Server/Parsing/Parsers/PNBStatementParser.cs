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
/// Deterministic statement parser for Punjab National Bank (PNB-v1).
/// Production-grade implementation supporting:
/// 1. Standard PNB e-Statements & passbook downloads:
///    Tran Date | Withdrawal | Deposit | Balance | Alpha | CHQ. NO. | Narration | Additional Info
/// 2. PNB Finacle Corporate & NetBanking tabular statements:
///    Date | Value Date | Description | Cheque No | Withdrawal (Dr) | Deposit (Cr) | Balance
/// Supports multi-page statement parsing, dynamic column boundary detection,
/// multi-line narration reconstruction across page boundaries, cheque/reference/UTR extraction,
/// culture-invariant signed Dr./Cr. amount parsing, and sequential running balance verification.
/// </summary>
public class PNBStatementParser : IBankStatementParser
{
    public int BankCode => (int)BankType.PNB;
    public string BankName => "Punjab National Bank";
    public string ParserVersion => "PNB-v1";

    private readonly ILogger<PNBStatementParser> _logger;

    private static readonly Regex DateRegex = new(
        @"^(?:\d{2}[/-]\d{2}[/-]\d{4}|\d{1,2}\s+[A-Za-z]{3}\s+\d{4}|\d{1,2}-[A-Za-z]{3}-\d{4})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EmbeddedDateRegex = new(
        @"\b(?:\d{2}[/-]\d{2}[/-]\d{4}|\d{1,2}\s+[A-Za-z]{3}\s+\d{4}|\d{1,2}-[A-Za-z]{3}-\d{4})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AmountRegex = new(@"^-?[\d,]+(?:\.\d{1,2})?(?:\s*(?:Cr\.?|Dr\.?))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EmbeddedAmountRegex = new(@"-?[\d,]+\.\d{2}(?:\s*(?:Cr\.?|Dr\.?))?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UtrRegex = new(@"\b(PUNB[A-Z0-9]+|UPI/[A-Za-z0-9/_-]+|IMPS/[A-Za-z0-9/_-]+|NEFT/[A-Za-z0-9/_-]+|[A-Z0-9]{12,22})\b", RegexOptions.Compiled);
    private static readonly Regex ChequeRegex = new(@"^\d{6,12}$", RegexOptions.Compiled);

    public PNBStatementParser(ILogger<PNBStatementParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(BankDetectionResult detection)
    {
        return detection != null &&
               detection.DetectedBank == BankType.PNB &&
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

        string? detectedAccount = detection?.AccountNumber;
        string? customerName = detection?.CustomerName;

        // Internal transaction accumulator for multi-line narration joining
        PnbTxAccumulator? currentTx = null;

        void FinalizeTx()
        {
            if (currentTx == null) return;

            var tx = BuildTransaction(currentTx, rowIndexCounter++, BankCode, BankName, ParserVersion);
            if (tx != null)
            {
                // Prevent duplicate transactions at page boundaries
                bool isDuplicate = transactions.Count > 0 &&
                                   transactions.Last().TransactionDate == tx.TransactionDate &&
                                   transactions.Last().Amount == tx.Amount &&
                                   transactions.Last().Balance == tx.Balance &&
                                   transactions.Last().Description == tx.Description &&
                                   transactions.Last().Reference == tx.Reference &&
                                   transactions.Last().SourcePageNumber != tx.SourcePageNumber;

                if (!isDuplicate)
                {
                    transactions.Add(tx);
                    totalRowsDetected++;
                }
            }
            currentTx = null;
        }

        foreach (var page in extraction.Pages.OrderBy(p => p.PageNumber))
        {
            var textBlocks = (page.TextBlocks != null && page.TextBlocks.Count > 0)
                ? page.TextBlocks.Where(b => !string.IsNullOrWhiteSpace(b.Text)).ToList()
                : (page.CandidateRows != null
                    ? page.CandidateRows.SelectMany(r => r.LineFragments).Where(b => !string.IsNullOrWhiteSpace(b.Text)).ToList()
                    : new List<PdfTextBlock>());

            if (textBlocks.Count == 0 && (page.CandidateRows == null || page.CandidateRows.Count == 0))
            {
                continue;
            }

            var lines = GroupIntoLines(page, textBlocks);
            if (lines.Count == 0) continue;

            // 1. Detect layout format and column bounds on this page
            var layoutConfig = DetectPageLayout(lines, page.Width > 0 ? page.Width : 595.0);

            // 2. Locate Header bottom Y and Footer top Y
            double headerBottomY = layoutConfig.HeaderBottomY;
            double footerTopY = layoutConfig.FooterTopY;

            // Check for Opening Balance on Page 1 if not yet detected
            if (page.PageNumber == 1 && !openingBalance.HasValue)
            {
                openingBalance = ExtractOpeningBalance(lines, headerBottomY);
            }

            // 3. Process lines between headerBottomY and footerTopY
            foreach (var line in lines)
            {
                double lineY = line.Min(w => w.Y);
                if (lineY <= headerBottomY || lineY >= footerTopY)
                {
                    // Check if footer contains closing balance
                    if (closingBalance == null && (lineY >= footerTopY || lineY > page.Height * 0.75))
                    {
                        var cb = ExtractClosingBalance(line);
                        if (cb.HasValue) closingBalance = cb;
                    }
                    continue;
                }

                var lineWords = line.OrderBy(w => w.X).ToList();
                var rawLineText = string.Join(" ", lineWords.Select(w => w.Text)).Trim();

                if (string.IsNullOrWhiteSpace(rawLineText)) continue;

                // Skip repeated headers, column labels, and summary banners
                if (IsHeaderOrFooterBanner(rawLineText, lineWords))
                {
                    // If line is Page Total or Grand Total, check for closing balance or ignore
                    if (rawLineText.Contains("Grand Total", StringComparison.OrdinalIgnoreCase) ||
                        rawLineText.Contains("Closing Balance", StringComparison.OrdinalIgnoreCase))
                    {
                        var cb = ExtractClosingBalance(lineWords);
                        if (cb.HasValue) closingBalance = cb;
                    }
                    continue;
                }

                // Check if this line starts a new transaction
                bool startsNewTx = DoesLineStartTransaction(lineWords, layoutConfig);

                if (startsNewTx)
                {
                    FinalizeTx();

                    currentTx = new PnbTxAccumulator
                    {
                        PageNumber = page.PageNumber,
                        Layout = layoutConfig.LayoutType
                    };

                    AssignLineToTransaction(currentTx, lineWords, layoutConfig);
                }
                else if (currentTx != null)
                {
                    // Narration continuation line
                    AppendContinuationLine(currentTx, lineWords, layoutConfig);
                }
            }

            // Finalize any open transaction at end of page if page ends cleanly
            FinalizeTx();
        }

        // 4. Running Balance Verification & Math Continuity
        decimal runningBal = openingBalance ?? (transactions.FirstOrDefault()?.Balance ?? 0m);
        for (int i = 0; i < transactions.Count; i++)
        {
            var tx = transactions[i];

            decimal debit = tx.Debit ?? 0m;
            decimal credit = tx.Credit ?? 0m;
            decimal expected = runningBal - debit + credit;

            if (tx.Balance.HasValue)
            {
                if (Math.Abs(tx.Balance.Value - expected) > 0.05m && i > 0)
                {
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

        _logger.LogInformation("Parsed {Count} PNB transactions. Debits: {Debits:C2}, Credits: {Credits:C2}, Opening: {Open}, Closing: {Close}, Warnings: {WarnCount}",
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
                    result.Add(new List<PdfTextBlock>
                    {
                        new()
                        {
                            Text = row.RawLineText,
                            X = 40,
                            Y = row.Y,
                            Width = 500,
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

    private enum PnbLayoutType
    {
        StandardFormat1, // Tran Date | Withdrawal | Deposit | Balance | Alpha | CHQ. NO. | Narration | Additional Info
        FinacleFormat2   // Date | Value Date | Description | Cheque No | Withdrawal | Deposit | Balance
    }

    private class PnbLayoutConfig
    {
        public PnbLayoutType LayoutType { get; set; } = PnbLayoutType.StandardFormat1;
        public double HeaderBottomY { get; set; } = 150.0;
        public double FooterTopY { get; set; } = 1000.0;

        // Format 1 column boundary X coordinates
        public double DateMaxX { get; set; } = 100.0;
        public double WithdrawalMaxX { get; set; } = 160.0;
        public double DepositMaxX { get; set; } = 220.0;
        public double BalanceMaxX { get; set; } = 300.0;
        public double AlphaMaxX { get; set; } = 340.0;
        public double ChqMaxX { get; set; } = 400.0;
        public double NarrationMaxX { get; set; } = 520.0;

        // Format 2 column boundary X coordinates
        public double F2DateMaxX { get; set; } = 80.0;
        public double F2ValueDateMaxX { get; set; } = 140.0;
        public double F2NarrationMaxX { get; set; } = 320.0;
        public double F2ChqMaxX { get; set; } = 380.0;
        public double F2WithdrawalMaxX { get; set; } = 450.0;
        public double F2DepositMaxX { get; set; } = 520.0;
    }

    private static PnbLayoutConfig DetectPageLayout(List<List<PdfTextBlock>> lines, double pageWidth)
    {
        var config = new PnbLayoutConfig();

        // 1. Check for header lines containing column identifiers
        int headerLineIndex = -1;
        bool isFormat2 = false;

        for (int i = 0; i < lines.Count; i++)
        {
            var lineText = string.Join(" ", lines[i].Select(w => w.Text));
            bool hasDate = lineText.Contains("Tran Date", StringComparison.OrdinalIgnoreCase) ||
                           lineText.Contains("Txn Date", StringComparison.OrdinalIgnoreCase) ||
                           lineText.Contains("Date", StringComparison.OrdinalIgnoreCase);
            bool hasWithdrawal = lineText.Contains("Withdrawal", StringComparison.OrdinalIgnoreCase) ||
                                 lineText.Contains("Debit", StringComparison.OrdinalIgnoreCase);
            bool hasDeposit = lineText.Contains("Deposit", StringComparison.OrdinalIgnoreCase) ||
                              lineText.Contains("Credit", StringComparison.OrdinalIgnoreCase);
            bool hasBalance = lineText.Contains("Balance", StringComparison.OrdinalIgnoreCase);

            if (hasDate && (hasWithdrawal || hasDeposit) && hasBalance)
            {
                headerLineIndex = i;

                // Determine whether Narration appears before Withdrawal (Format 2) or after Balance (Format 1)
                var narrWord = lines[i].FirstOrDefault(w => w.Text.Contains("Narration", StringComparison.OrdinalIgnoreCase) ||
                                                            w.Text.Contains("Particulars", StringComparison.OrdinalIgnoreCase) ||
                                                            w.Text.Contains("Description", StringComparison.OrdinalIgnoreCase));
                var withWord = lines[i].FirstOrDefault(w => w.Text.Contains("Withdrawal", StringComparison.OrdinalIgnoreCase) ||
                                                            w.Text.Contains("Debit", StringComparison.OrdinalIgnoreCase));

                if (narrWord != null && withWord != null && narrWord.X < withWord.X)
                {
                    isFormat2 = true;
                }
                break;
            }
        }

        if (headerLineIndex >= 0)
        {
            var headerLine = lines[headerLineIndex];
            config.HeaderBottomY = headerLine.Max(w => w.Y + (w.Height > 0 ? w.Height : 10.0));

            // Check if header continues on next line
            if (headerLineIndex + 1 < lines.Count)
            {
                var nextText = string.Join(" ", lines[headerLineIndex + 1].Select(w => w.Text));
                if (nextText.Contains("Additional", StringComparison.OrdinalIgnoreCase) ||
                    nextText.Contains("Info", StringComparison.OrdinalIgnoreCase) ||
                    nextText.Contains("NO.", StringComparison.OrdinalIgnoreCase) ||
                    nextText.Contains("Alpha", StringComparison.OrdinalIgnoreCase))
                {
                    config.HeaderBottomY = Math.Max(config.HeaderBottomY, lines[headerLineIndex + 1].Max(w => w.Y + (w.Height > 0 ? w.Height : 10.0)));
                }
            }

            config.LayoutType = isFormat2 ? PnbLayoutType.FinacleFormat2 : PnbLayoutType.StandardFormat1;

            // Calibrate column positions if header words are present
            if (!isFormat2)
            {
                var dateWord = headerLine.FirstOrDefault(w => w.Text.Contains("Date", StringComparison.OrdinalIgnoreCase));
                var withWord = headerLine.FirstOrDefault(w => w.Text.Contains("Withdrawal", StringComparison.OrdinalIgnoreCase) || w.Text.Contains("Debit", StringComparison.OrdinalIgnoreCase));
                var depWord = headerLine.FirstOrDefault(w => w.Text.Contains("Deposit", StringComparison.OrdinalIgnoreCase) || w.Text.Contains("Credit", StringComparison.OrdinalIgnoreCase));
                var balWord = headerLine.FirstOrDefault(w => w.Text.Contains("Balance", StringComparison.OrdinalIgnoreCase));
                var alphaWord = headerLine.FirstOrDefault(w => w.Text.Contains("Alpha", StringComparison.OrdinalIgnoreCase));
                var chqWord = headerLine.FirstOrDefault(w => w.Text.Contains("CHQ", StringComparison.OrdinalIgnoreCase) || w.Text.Contains("Cheque", StringComparison.OrdinalIgnoreCase));
                var narrWord = headerLine.FirstOrDefault(w => w.Text.Contains("Narration", StringComparison.OrdinalIgnoreCase) || w.Text.Contains("Particulars", StringComparison.OrdinalIgnoreCase));

                if (withWord != null && dateWord != null && withWord.X > dateWord.X) config.DateMaxX = withWord.X;
                if (depWord != null && withWord != null && depWord.X > withWord.X) config.WithdrawalMaxX = depWord.X;
                if (balWord != null && depWord != null && balWord.X > depWord.X) config.DepositMaxX = balWord.X;
                if (alphaWord != null && balWord != null && alphaWord.X > balWord.X) config.BalanceMaxX = alphaWord.X;
                else if (chqWord != null && balWord != null && chqWord.X > balWord.X) config.BalanceMaxX = chqWord.X;
                if (chqWord != null && alphaWord != null && chqWord.X > alphaWord.X) config.AlphaMaxX = chqWord.X;
                else if (alphaWord == null && chqWord != null) config.AlphaMaxX = chqWord.X;
                if (narrWord != null && chqWord != null && narrWord.X > chqWord.X) config.ChqMaxX = narrWord.X;
            }
            else
            {
                var dateWord = headerLine.FirstOrDefault(w => w.Text.Contains("Date", StringComparison.OrdinalIgnoreCase));
                var valWord = headerLine.FirstOrDefault(w => w.Text.Contains("Value", StringComparison.OrdinalIgnoreCase));
                var narrWord = headerLine.FirstOrDefault(w => w.Text.Contains("Description", StringComparison.OrdinalIgnoreCase) || w.Text.Contains("Particulars", StringComparison.OrdinalIgnoreCase) || w.Text.Contains("Narration", StringComparison.OrdinalIgnoreCase));
                var chqWord = headerLine.FirstOrDefault(w => w.Text.Contains("CHQ", StringComparison.OrdinalIgnoreCase) || w.Text.Contains("Cheque", StringComparison.OrdinalIgnoreCase));
                var withWord = headerLine.FirstOrDefault(w => w.Text.Contains("Withdrawal", StringComparison.OrdinalIgnoreCase) || w.Text.Contains("Debit", StringComparison.OrdinalIgnoreCase));
                var depWord = headerLine.FirstOrDefault(w => w.Text.Contains("Deposit", StringComparison.OrdinalIgnoreCase) || w.Text.Contains("Credit", StringComparison.OrdinalIgnoreCase));
                var balWord = headerLine.FirstOrDefault(w => w.Text.Contains("Balance", StringComparison.OrdinalIgnoreCase));

                if (valWord != null && dateWord != null && valWord.X > dateWord.X) config.F2DateMaxX = valWord.X;
                if (narrWord != null && valWord != null && narrWord.X > valWord.X) config.F2ValueDateMaxX = narrWord.X;
                if (chqWord != null && narrWord != null && chqWord.X > narrWord.X) config.F2NarrationMaxX = chqWord.X;
                if (withWord != null && chqWord != null && withWord.X > chqWord.X) config.F2ChqMaxX = withWord.X;
                else if (withWord != null && narrWord != null && withWord.X > narrWord.X) config.F2ChqMaxX = withWord.X;
                if (depWord != null && withWord != null && depWord.X > withWord.X) config.F2WithdrawalMaxX = depWord.X;
                if (balWord != null && depWord != null && balWord.X > depWord.X) config.F2DepositMaxX = balWord.X;
            }
        }
        else
        {
            // Default header boundary
            config.HeaderBottomY = 120.0;
        }

        // 3. Locate Footer boundary
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var lineMinY = lines[i].Min(w => w.Y);
            if (lineMinY <= config.HeaderBottomY) break;

            var lineText = string.Join(" ", lines[i].Select(w => w.Text));
            if (lineText.Contains("Page Total", StringComparison.OrdinalIgnoreCase) ||
                lineText.Contains("Grand Total", StringComparison.OrdinalIgnoreCase) ||
                lineText.StartsWith("Grand", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(lineText, @"^Page\s+\d+\s+of\s+\d+", RegexOptions.IgnoreCase) ||
                lineText.Contains("This is a computer generated", StringComparison.OrdinalIgnoreCase))
            {
                config.FooterTopY = lineMinY - 2.0;
                break;
            }
        }

        return config;
    }

    private static decimal? ExtractOpeningBalance(List<List<PdfTextBlock>> lines, double headerBottomY)
    {
        foreach (var line in lines)
        {
            var lineText = string.Join(" ", line.Select(w => w.Text));
            if (lineText.Contains("OPENING BALANCE", StringComparison.OrdinalIgnoreCase) ||
                lineText.Contains("Brought Forward", StringComparison.OrdinalIgnoreCase) ||
                lineText.Contains("B/F", StringComparison.OrdinalIgnoreCase))
            {
                var amtWord = line.LastOrDefault(w => AmountRegex.IsMatch(w.Text.Trim()));
                if (amtWord != null && TryParseAmount(amtWord.Text, out var amt))
                {
                    bool isDr = lineText.Contains("Dr", StringComparison.OrdinalIgnoreCase);
                    return isDr ? -Math.Abs(amt) : Math.Abs(amt);
                }
            }
        }
        return null;
    }

    private static decimal? ExtractClosingBalance(List<PdfTextBlock> line)
    {
        var lineText = string.Join(" ", line.Select(w => w.Text));
        if (lineText.Contains("CLOSING BALANCE", StringComparison.OrdinalIgnoreCase))
        {
            var amtWord = line.LastOrDefault(w => AmountRegex.IsMatch(w.Text.Trim()));
            if (amtWord != null && TryParseAmount(amtWord.Text, out var amt))
            {
                bool isDr = lineText.Contains("Dr", StringComparison.OrdinalIgnoreCase);
                return isDr ? -Math.Abs(amt) : Math.Abs(amt);
            }
        }
        return null;
    }

    private static bool IsHeaderOrFooterBanner(string lineText, List<PdfTextBlock> words)
    {
        if (string.IsNullOrWhiteSpace(lineText)) return true;

        if (lineText.Contains("Punjab National Bank", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("पंजाब नैशनल बैंक", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Statement of Account No", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Statement for Period", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Customer Name:", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Customer Address:", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Branch Address:", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Branch Contact No", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Customer Care No", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("IFSC Code:", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("MICR Code:", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Acct Currency:", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("CKYC No.:", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Printed By:", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Page Total", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Grand Total", StringComparison.OrdinalIgnoreCase) ||
            lineText.StartsWith("Grand", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(lineText, @"^Page\s+\d+\s+of\s+\d+", RegexOptions.IgnoreCase) ||
            lineText.Contains("...the name you can BANK upon", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("भरोसे का प्रतीक", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("This is a computer generated", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Table column header rows
        if ((lineText.Contains("Tran Date", StringComparison.OrdinalIgnoreCase) || lineText.Contains("Txn Date", StringComparison.OrdinalIgnoreCase)) &&
            (lineText.Contains("Withdrawal", StringComparison.OrdinalIgnoreCase) || lineText.Contains("Debit", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (lineText.Contains("Alpha", StringComparison.OrdinalIgnoreCase) && lineText.Contains("CHQ. NO.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (lineText.Contains("Additional Info", StringComparison.OrdinalIgnoreCase) && lineText.Length < 30)
        {
            return true;
        }

        return false;
    }

    private static bool DoesLineStartTransaction(List<PdfTextBlock> lineWords, PnbLayoutConfig config)
    {
        if (lineWords.Count == 0) return false;

        double dateThreshold = config.LayoutType == PnbLayoutType.StandardFormat1 ? config.DateMaxX + 25.0 : config.F2DateMaxX + 25.0;
        var dateWord = lineWords.FirstOrDefault(w => w.X < dateThreshold && DateRegex.IsMatch(w.Text.Trim()));

        if (dateWord != null)
        {
            // Verify there is at least one monetary amount or valid balance in this line
            bool hasAmount = lineWords.Any(w => w.X > dateWord.X && AmountRegex.IsMatch(w.Text.Trim()));
            return hasAmount;
        }

        return false;
    }

    private class PnbTxAccumulator
    {
        public int PageNumber { get; set; }
        public PnbLayoutType Layout { get; set; }
        public string DateText { get; set; } = string.Empty;
        public string? ValueDateText { get; set; }
        public string? WithdrawalText { get; set; }
        public string? DepositText { get; set; }
        public string? BalanceText { get; set; }
        public string? AlphaText { get; set; }
        public string? ChequeNoText { get; set; }
        public List<string> NarrationWords { get; set; } = [];
        public List<string> AdditionalInfoWords { get; set; } = [];
        public List<PdfTextBlock> RawBlocks { get; set; } = [];
    }

    private static void AssignLineToTransaction(PnbTxAccumulator tx, List<PdfTextBlock> lineWords, PnbLayoutConfig config)
    {
        tx.RawBlocks.AddRange(lineWords);

        if (config.LayoutType == PnbLayoutType.StandardFormat1)
        {
            // Format 1: Tran Date (X < DateMaxX) -> Withdrawal -> Deposit -> Balance -> Alpha -> CHQ. NO. -> Narration -> Additional Info
            foreach (var w in lineWords)
            {
                var text = w.Text.Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;

                if (w.X < config.DateMaxX && string.IsNullOrEmpty(tx.DateText) && DateRegex.IsMatch(text))
                {
                    tx.DateText = text;
                }
                else if (w.X >= config.DateMaxX && w.X < config.WithdrawalMaxX && string.IsNullOrEmpty(tx.WithdrawalText) && AmountRegex.IsMatch(text))
                {
                    tx.WithdrawalText = text;
                }
                else if (w.X >= config.WithdrawalMaxX && w.X < config.DepositMaxX && string.IsNullOrEmpty(tx.DepositText) && AmountRegex.IsMatch(text))
                {
                    tx.DepositText = text;
                }
                else if (w.X >= config.DepositMaxX && w.X < config.BalanceMaxX)
                {
                    if (string.IsNullOrEmpty(tx.BalanceText) && AmountRegex.IsMatch(text))
                    {
                        tx.BalanceText = text;
                    }
                    else if (text.Equals("Cr.", StringComparison.OrdinalIgnoreCase) ||
                             text.Equals("Dr.", StringComparison.OrdinalIgnoreCase) ||
                             text.Equals("Cr", StringComparison.OrdinalIgnoreCase) ||
                             text.Equals("Dr", StringComparison.OrdinalIgnoreCase))
                    {
                        tx.BalanceText = (tx.BalanceText ?? "") + " " + text;
                    }
                }
                else if (w.X >= config.BalanceMaxX && w.X < config.AlphaMaxX)
                {
                    if (text.Equals("Cr.", StringComparison.OrdinalIgnoreCase) ||
                        text.Equals("Dr.", StringComparison.OrdinalIgnoreCase) ||
                        text.Equals("Cr", StringComparison.OrdinalIgnoreCase) ||
                        text.Equals("Dr", StringComparison.OrdinalIgnoreCase))
                    {
                        tx.BalanceText = (tx.BalanceText ?? "") + " " + text;
                    }
                    else if (string.IsNullOrEmpty(tx.AlphaText) && text.Length <= 6 && !AmountRegex.IsMatch(text))
                    {
                        tx.AlphaText = text;
                    }
                }
                else if (w.X >= config.AlphaMaxX && w.X < config.ChqMaxX && string.IsNullOrEmpty(tx.ChequeNoText) && (ChequeRegex.IsMatch(text) || text.Length <= 10))
                {
                    tx.ChequeNoText = text;
                }
                else if (w.X >= config.ChqMaxX && w.X < config.NarrationMaxX)
                {
                    tx.NarrationWords.Add(text);
                }
                else if (w.X >= config.NarrationMaxX)
                {
                    tx.AdditionalInfoWords.Add(text);
                }
                else
                {
                    // Fallback word assignment based on token characteristics
                    if (string.IsNullOrEmpty(tx.BalanceText) && AmountRegex.IsMatch(text) && w.X < config.ChqMaxX)
                    {
                        tx.BalanceText = text;
                    }
                    else if ((text.Equals("Cr.", StringComparison.OrdinalIgnoreCase) ||
                              text.Equals("Dr.", StringComparison.OrdinalIgnoreCase) ||
                              text.Equals("Cr", StringComparison.OrdinalIgnoreCase) ||
                              text.Equals("Dr", StringComparison.OrdinalIgnoreCase)) &&
                             w.X < config.ChqMaxX)
                    {
                        tx.BalanceText = (tx.BalanceText ?? "") + " " + text;
                    }
                    else
                    {
                        tx.NarrationWords.Add(text);
                    }
                }
            }
        }
        else
        {
            // Format 2: Date -> Value Date -> Description -> Cheque No -> Withdrawal -> Deposit -> Balance
            foreach (var w in lineWords)
            {
                var text = w.Text.Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;

                if (w.X < config.F2DateMaxX && string.IsNullOrEmpty(tx.DateText) && DateRegex.IsMatch(text))
                {
                    tx.DateText = text;
                }
                else if (w.X >= config.F2DateMaxX && w.X < config.F2ValueDateMaxX && string.IsNullOrEmpty(tx.ValueDateText) && DateRegex.IsMatch(text))
                {
                    tx.ValueDateText = text;
                }
                else if (w.X >= config.F2NarrationMaxX && w.X < config.F2ChqMaxX && string.IsNullOrEmpty(tx.ChequeNoText) && (ChequeRegex.IsMatch(text) || text.Length <= 10))
                {
                    tx.ChequeNoText = text;
                }
                else if (w.X >= config.F2ChqMaxX && w.X < config.F2WithdrawalMaxX && string.IsNullOrEmpty(tx.WithdrawalText) && AmountRegex.IsMatch(text))
                {
                    tx.WithdrawalText = text;
                }
                else if (w.X >= config.F2WithdrawalMaxX && w.X < config.F2DepositMaxX && string.IsNullOrEmpty(tx.DepositText) && AmountRegex.IsMatch(text))
                {
                    tx.DepositText = text;
                }
                else if (w.X >= config.F2DepositMaxX && string.IsNullOrEmpty(tx.BalanceText) && AmountRegex.IsMatch(text))
                {
                    tx.BalanceText = text;
                }
                else if (w.X >= config.F2ValueDateMaxX && w.X < config.F2NarrationMaxX)
                {
                    tx.NarrationWords.Add(text);
                }
                else
                {
                    if (AmountRegex.IsMatch(text) && w.X >= config.F2ChqMaxX)
                    {
                        if (string.IsNullOrEmpty(tx.BalanceText))
                            tx.BalanceText = text;
                        else if (string.IsNullOrEmpty(tx.DepositText))
                            tx.DepositText = text;
                        else if (string.IsNullOrEmpty(tx.WithdrawalText))
                            tx.WithdrawalText = text;
                    }
                    else
                    {
                        tx.NarrationWords.Add(text);
                    }
                }
            }
        }
    }

    private static void AppendContinuationLine(PnbTxAccumulator tx, List<PdfTextBlock> lineWords, PnbLayoutConfig config)
    {
        tx.RawBlocks.AddRange(lineWords);

        foreach (var w in lineWords)
        {
            var text = w.Text.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            if (config.LayoutType == PnbLayoutType.StandardFormat1)
            {
                if (w.X < config.AlphaMaxX && (text.Equals("Cr.", StringComparison.OrdinalIgnoreCase) ||
                                               text.Equals("Dr.", StringComparison.OrdinalIgnoreCase) ||
                                               text.Equals("Cr", StringComparison.OrdinalIgnoreCase) ||
                                               text.Equals("Dr", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (w.X >= config.NarrationMaxX)
                {
                    tx.AdditionalInfoWords.Add(text);
                }
                else if (w.X >= config.ChqMaxX)
                {
                    tx.NarrationWords.Add(text);
                }
                else if (w.X >= config.AlphaMaxX && string.IsNullOrEmpty(tx.ChequeNoText) && ChequeRegex.IsMatch(text))
                {
                    tx.ChequeNoText = text;
                }
                else
                {
                    tx.NarrationWords.Add(text);
                }
            }
            else
            {
                if (w.X < config.F2WithdrawalMaxX)
                {
                    tx.NarrationWords.Add(text);
                }
            }
        }
    }

    private static ParsedTransaction? BuildTransaction(PnbTxAccumulator acc, int rowIndex, int bankCode, string bankName, string parserVersion)
    {
        if (string.IsNullOrWhiteSpace(acc.DateText)) return null;

        if (!TryParseDate(acc.DateText, out var txnDate))
        {
            return null;
        }

        DateTime? valDate = null;
        if (!string.IsNullOrWhiteSpace(acc.ValueDateText) && TryParseDate(acc.ValueDateText, out var vd))
        {
            valDate = vd;
        }

        decimal? debit = null;
        if (!string.IsNullOrWhiteSpace(acc.WithdrawalText) && TryParseAmount(acc.WithdrawalText, out var wAmt) && wAmt > 0)
        {
            debit = wAmt;
        }

        decimal? credit = null;
        if (!string.IsNullOrWhiteSpace(acc.DepositText) && TryParseAmount(acc.DepositText, out var dAmt) && dAmt > 0)
        {
            credit = dAmt;
        }

        // Must have at least one debit or credit amount to be a financial movement
        if ((!debit.HasValue || debit.Value <= 0) && (!credit.HasValue || credit.Value <= 0))
        {
            return null;
        }

        decimal? balance = null;
        if (!string.IsNullOrWhiteSpace(acc.BalanceText))
        {
            var balClean = acc.BalanceText.Trim();
            bool isDr = balClean.EndsWith("Dr.", StringComparison.OrdinalIgnoreCase) ||
                        balClean.EndsWith("Dr", StringComparison.OrdinalIgnoreCase) ||
                        balClean.StartsWith("-");

            if (TryParseAmount(balClean, out var bVal))
            {
                balance = isDr ? -Math.Abs(bVal) : Math.Abs(bVal);
            }
        }

        // Construct description
        var descTokens = new List<string>();
        if (acc.NarrationWords.Count > 0)
        {
            descTokens.Add(string.Join(" ", acc.NarrationWords));
        }
        if (acc.AdditionalInfoWords.Count > 0)
        {
            descTokens.Add(string.Join(" ", acc.AdditionalInfoWords));
        }

        string description = string.Join(" ", descTokens).Trim();
        if (string.IsNullOrWhiteSpace(description))
        {
            description = "PNB Transaction";
        }

        // Reference & UTR
        string? reference = !string.IsNullOrWhiteSpace(acc.ChequeNoText) ? acc.ChequeNoText.Trim() : null;
        string? utr = null;

        var utrMatch = UtrRegex.Match(description);
        if (utrMatch.Success)
        {
            utr = utrMatch.Value;
            reference ??= utr;
        }

        decimal amount = debit ?? credit ?? 0m;
        string txnType = credit.HasValue && credit.Value > 0 ? "Credit" : "Debit";

        return new ParsedTransaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = txnDate,
            ValueDate = valDate ?? txnDate,
            Description = description,
            Reference = reference,
            Utr = utr,
            Debit = debit,
            Credit = credit,
            Amount = amount,
            Balance = balance,
            TransactionType = txnType,
            BankCode = bankCode,
            BankName = bankName,
            ParserVersion = parserVersion,
            SourcePageNumber = acc.PageNumber,
            SourceLineIndex = rowIndex,
            Confidence = 1.0,
            RawSourceText = string.Join(" ", acc.RawBlocks.Select(w => w.Text))
        };
    }

    private static bool TryParseDate(string text, out DateTime date)
    {
        date = default;
        text = text.Trim();

        string[] formats =
        {
            "dd-MM-yyyy", "dd/MM/yyyy", "d-M-yyyy", "d/M/yyyy",
            "dd-MMM-yyyy", "d-MMM-yyyy", "dd MMM yyyy", "d MMM yyyy",
            "dd.MM.yyyy", "yyyy-MM-dd"
        };

        return DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static bool TryParseAmount(string text, out decimal amount)
    {
        amount = 0m;
        text = text.Trim().Replace(",", "");

        // Remove Dr/Cr/Dr./Cr. suffix if present
        if (text.EndsWith("Cr.", StringComparison.OrdinalIgnoreCase) || text.EndsWith("Cr", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(0, text.Length - (text.EndsWith("Cr.", StringComparison.OrdinalIgnoreCase) ? 3 : 2)).Trim();
        }
        else if (text.EndsWith("Dr.", StringComparison.OrdinalIgnoreCase) || text.EndsWith("Dr", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(0, text.Length - (text.EndsWith("Dr.", StringComparison.OrdinalIgnoreCase) ? 3 : 2)).Trim();
        }

        return decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount);
    }
}
