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
/// Deterministic statement parser for Bank of Baroda (BOB-v1).
/// Production-grade implementation supporting:
/// 1. Baroda Connect / Finacle Corporate & Retail NetBanking Statements:
///    S.No | Date | Value Date | Description / Particulars | Cheque No | Withdrawal (Dr) | Deposit (Cr) | Balance (Cr/Dr)
/// 2. bob World Mobile App e-Statements / mPassbook:
///    Date | Particulars / Narration | Chq. / Ref. No. | Withdrawal | Deposit | Balance
/// Supports multi-page statement parsing, dynamic column boundary detection,
/// multi-line narration reconstruction across page boundaries, cheque/reference/UTR extraction,
/// culture-invariant signed Dr./Cr. amount parsing, and sequential running balance verification.
/// </summary>
public class BOBStatementParser : IBankStatementParser
{
    public int BankCode => (int)BankType.BOB;
    public string BankName => "Bank of Baroda";
    public string ParserVersion => "BOB-v1";

    private readonly ILogger<BOBStatementParser> _logger;

    private static readonly Regex DateRegex = new(
        @"^(?:\d{2}[/-]\d{2}[/-]\d{4}|\d{1,2}\s+[A-Za-z]{3}\s+\d{4}|\d{1,2}-[A-Za-z]{3}-\d{4}|\d{2}\.\d{2}\.\d{4})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AmountRegex = new(
        @"^-?[\d,]+(?:\.\d{1,2})?(?:\s*(?:Cr\.?|Dr\.?))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SpecificUtrRegex = new(
        @"\b(BARB[A-Z0-9]+)\b",
        RegexOptions.Compiled);

    private static readonly Regex GenericUtrRegex = new(
        @"\b(UPI/[A-Za-z0-9/_-]+|IMPS/[A-Za-z0-9/_-]+|NEFT/[A-Za-z0-9/_-]+|[A-Z0-9]{12,22})\b",
        RegexOptions.Compiled);

    private static readonly Regex ChequeRegex = new(@"^\d{6,12}$", RegexOptions.Compiled);
    private static readonly Regex SerialNumberRegex = new(@"^\d{1,5}\.?$", RegexOptions.Compiled);

    public BOBStatementParser(ILogger<BOBStatementParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(BankDetectionResult detection)
    {
        return detection != null &&
               detection.DetectedBank == BankType.BOB &&
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
        BobTxAccumulator? currentTx = null;

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

            double headerBottomY = layoutConfig.HeaderBottomY;
            double footerTopY = layoutConfig.FooterTopY;

            // Check for Opening Balance on Page 1 if not yet detected
            if (page.PageNumber == 1 && !openingBalance.HasValue)
            {
                openingBalance = ExtractOpeningBalance(lines, headerBottomY);
            }

            // 2. Process lines between headerBottomY and footerTopY
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
                    if (rawLineText.Contains("Grand Total", StringComparison.OrdinalIgnoreCase) ||
                        rawLineText.Contains("Closing Balance", StringComparison.OrdinalIgnoreCase))
                    {
                        var cb = ExtractClosingBalance(lineWords);
                        if (cb.HasValue) closingBalance = cb;
                    }
                    continue;
                }

                // Check if this line is an opening balance / brought forward anchor
                if (rawLineText.Contains("Opening Balance", StringComparison.OrdinalIgnoreCase) ||
                    rawLineText.Contains("Brought Forward", StringComparison.OrdinalIgnoreCase) ||
                    rawLineText.Contains("B/F", StringComparison.OrdinalIgnoreCase))
                {
                    var amtWord = lineWords.LastOrDefault(w => AmountRegex.IsMatch(w.Text.Trim()));
                    if (amtWord != null && TryParseAmount(amtWord.Text, out var obVal))
                    {
                        bool isDr = rawLineText.Contains("Dr", StringComparison.OrdinalIgnoreCase);
                        openingBalance ??= isDr ? -Math.Abs(obVal) : Math.Abs(obVal);
                    }
                    continue;
                }

                // Check if this line starts a new transaction
                bool startsNewTx = DoesLineStartTransaction(lineWords, layoutConfig);

                if (startsNewTx)
                {
                    FinalizeTx();

                    currentTx = new BobTxAccumulator
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

            // Finalize any open transaction at end of page
            FinalizeTx();
        }

        // 3. Running Balance Verification & Math Continuity
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

        _logger.LogInformation("Parsed {Count} Bank of Baroda transactions. Debits: {Debits:C2}, Credits: {Credits:C2}, Opening: {Open}, Closing: {Close}, Warnings: {WarnCount}",
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

    private enum BobLayoutType
    {
        BarodaConnectFinacle, // S.No | Date | Value Date | Description / Particulars | Cheque No | Withdrawal (Dr) | Deposit (Cr) | Balance (Cr/Dr)
        BobWorldEStatement    // Date | Particulars / Narration | Chq / Ref No | Withdrawal | Deposit | Balance
    }

    private class BobLayoutConfig
    {
        public BobLayoutType LayoutType { get; set; } = BobLayoutType.BarodaConnectFinacle;
        public double HeaderBottomY { get; set; } = 150.0;
        public double FooterTopY { get; set; } = 1000.0;

        // Baroda Connect / Finacle Layout X coordinates
        public double SnoMaxX { get; set; } = 55.0;
        public double DateMaxX { get; set; } = 115.0;
        public double ValueDateMaxX { get; set; } = 175.0;
        public double DescriptionMaxX { get; set; } = 320.0;
        public double ChequeMaxX { get; set; } = 370.0;
        public double WithdrawalMaxX { get; set; } = 445.0;
        public double DepositMaxX { get; set; } = 515.0;

        // bob World Layout X coordinates
        public double BwDateMaxX { get; set; } = 95.0;
        public double BwNarrationMaxX { get; set; } = 300.0;
        public double BwChqMaxX { get; set; } = 365.0;
        public double BwWithdrawalMaxX { get; set; } = 440.0;
        public double BwDepositMaxX { get; set; } = 515.0;
    }

    private static BobLayoutConfig DetectPageLayout(List<List<PdfTextBlock>> lines, double pageWidth)
    {
        var config = new BobLayoutConfig();

        int headerLineIndex = -1;
        bool isBobWorld = false;

        for (int i = 0; i < lines.Count; i++)
        {
            var lineText = string.Join(" ", lines[i].Select(w => w.Text));
            bool hasDate = lineText.Contains("Date", StringComparison.OrdinalIgnoreCase) ||
                           lineText.Contains("Txn Date", StringComparison.OrdinalIgnoreCase) ||
                           lineText.Contains("Tran Date", StringComparison.OrdinalIgnoreCase);
            bool hasWithdrawal = lineText.Contains("Withdrawal", StringComparison.OrdinalIgnoreCase) ||
                                 lineText.Contains("Debit", StringComparison.OrdinalIgnoreCase);
            bool hasDeposit = lineText.Contains("Deposit", StringComparison.OrdinalIgnoreCase) ||
                              lineText.Contains("Credit", StringComparison.OrdinalIgnoreCase);
            bool hasBalance = lineText.Contains("Balance", StringComparison.OrdinalIgnoreCase);

            if (hasDate && (hasWithdrawal || hasDeposit) && hasBalance)
            {
                headerLineIndex = i;

                bool hasSno = lineText.Contains("Serial No", StringComparison.OrdinalIgnoreCase) ||
                              lineText.Contains("Serial", StringComparison.OrdinalIgnoreCase) ||
                              lineText.Contains("S.No", StringComparison.OrdinalIgnoreCase) ||
                              lineText.Contains("S No", StringComparison.OrdinalIgnoreCase) ||
                              lineText.Contains("Sl.No", StringComparison.OrdinalIgnoreCase) ||
                              lineText.Contains("Sl No", StringComparison.OrdinalIgnoreCase);
                bool hasValueDate = lineText.Contains("Value Date", StringComparison.OrdinalIgnoreCase);

                if (!hasSno && !hasValueDate)
                {
                    isBobWorld = true;
                }
                break;
            }
        }

        if (headerLineIndex >= 0)
        {
            var headerLine = lines[headerLineIndex];
            config.HeaderBottomY = headerLine.Max(w => w.Y + (w.Height > 0 ? w.Height : 10.0));

            // Check if header wraps to next line
            if (headerLineIndex + 1 < lines.Count)
            {
                var nextText = string.Join(" ", lines[headerLineIndex + 1].Select(w => w.Text));
                if (nextText.Contains("(Dr)", StringComparison.OrdinalIgnoreCase) ||
                    nextText.Contains("(Cr)", StringComparison.OrdinalIgnoreCase) ||
                    nextText.Contains("(INR)", StringComparison.OrdinalIgnoreCase) ||
                    nextText.Contains("No.", StringComparison.OrdinalIgnoreCase) ||
                    nextText.Contains("Number", StringComparison.OrdinalIgnoreCase) ||
                    nextText.Contains("Date", StringComparison.OrdinalIgnoreCase))
                {
                    config.HeaderBottomY = Math.Max(config.HeaderBottomY, lines[headerLineIndex + 1].Max(w => w.Y + (w.Height > 0 ? w.Height : 10.0)));
                }
            }

            config.LayoutType = isBobWorld ? BobLayoutType.BobWorldEStatement : BobLayoutType.BarodaConnectFinacle;

            // Calibrate column positions from header tokens
            if (!isBobWorld)
            {
                var snoWord = headerLine.FirstOrDefault(w => w.Text.Contains("Serial", StringComparison.OrdinalIgnoreCase) || w.Text.Contains("S.No", StringComparison.OrdinalIgnoreCase) || w.Text.Contains("Sl", StringComparison.OrdinalIgnoreCase) || w.Text.Equals("S", StringComparison.OrdinalIgnoreCase));
                var dateWord = headerLine.FirstOrDefault(w => w.Text.Contains("Date", StringComparison.OrdinalIgnoreCase) && !w.Text.Contains("Value", StringComparison.OrdinalIgnoreCase));
                var valWord = headerLine.FirstOrDefault(w => w.Text.Contains("Value", StringComparison.OrdinalIgnoreCase));
                var descWord = headerLine.FirstOrDefault(w => w.Text.Contains("Description", StringComparison.OrdinalIgnoreCase) || w.Text.Contains("Particulars", StringComparison.OrdinalIgnoreCase) || w.Text.Contains("Narration", StringComparison.OrdinalIgnoreCase));
                var chqWord = headerLine.FirstOrDefault(w => w.Text.Contains("CHQ", StringComparison.OrdinalIgnoreCase) || w.Text.Contains("Cheque", StringComparison.OrdinalIgnoreCase) || w.Text.Contains("Chq", StringComparison.OrdinalIgnoreCase));
                var withWord = headerLine.FirstOrDefault(w => w.Text.Contains("Withdrawal", StringComparison.OrdinalIgnoreCase) || w.Text.Contains("Debit", StringComparison.OrdinalIgnoreCase));
                var depWord = headerLine.FirstOrDefault(w => w.Text.Contains("Deposit", StringComparison.OrdinalIgnoreCase) || w.Text.Contains("Credit", StringComparison.OrdinalIgnoreCase));
                var balWord = headerLine.FirstOrDefault(w => w.Text.Contains("Balance", StringComparison.OrdinalIgnoreCase));

                if (snoWord != null && dateWord != null && dateWord.X > snoWord.X) config.SnoMaxX = dateWord.X;
                if (dateWord != null && valWord != null && valWord.X > dateWord.X) config.DateMaxX = valWord.X;
                else if (dateWord != null && descWord != null && descWord.X > dateWord.X) config.DateMaxX = descWord.X;

                if (valWord != null && descWord != null && descWord.X > valWord.X) config.ValueDateMaxX = descWord.X;
                if (descWord != null && chqWord != null && chqWord.X > descWord.X) config.DescriptionMaxX = chqWord.X;
                else if (descWord != null && withWord != null && withWord.X > descWord.X) config.DescriptionMaxX = withWord.X;

                if (chqWord != null && withWord != null && withWord.X > chqWord.X) config.ChequeMaxX = (chqWord.X + withWord.X) / 2.0;
                if (withWord != null && depWord != null && depWord.X > withWord.X) config.WithdrawalMaxX = (withWord.X + depWord.X) / 2.0;
                if (depWord != null && balWord != null && balWord.X > depWord.X) config.DepositMaxX = (depWord.X + balWord.X) / 2.0;
            }
            else
            {
                var dateWord = headerLine.FirstOrDefault(w => w.Text.Contains("Date", StringComparison.OrdinalIgnoreCase));
                var narrWord = headerLine.FirstOrDefault(w => w.Text.Contains("Particulars", StringComparison.OrdinalIgnoreCase) || w.Text.Contains("Description", StringComparison.OrdinalIgnoreCase) || w.Text.Contains("Narration", StringComparison.OrdinalIgnoreCase));
                var chqWord = headerLine.FirstOrDefault(w => w.Text.Contains("Chq", StringComparison.OrdinalIgnoreCase) || w.Text.Contains("Cheque", StringComparison.OrdinalIgnoreCase) || w.Text.Contains("Ref", StringComparison.OrdinalIgnoreCase));
                var withWord = headerLine.FirstOrDefault(w => w.Text.Contains("Withdrawal", StringComparison.OrdinalIgnoreCase) || w.Text.Contains("Debit", StringComparison.OrdinalIgnoreCase));
                var depWord = headerLine.FirstOrDefault(w => w.Text.Contains("Deposit", StringComparison.OrdinalIgnoreCase) || w.Text.Contains("Credit", StringComparison.OrdinalIgnoreCase));
                var balWord = headerLine.FirstOrDefault(w => w.Text.Contains("Balance", StringComparison.OrdinalIgnoreCase));

                if (dateWord != null && narrWord != null && narrWord.X > dateWord.X) config.BwDateMaxX = narrWord.X;
                if (narrWord != null && chqWord != null && chqWord.X > narrWord.X) config.BwNarrationMaxX = chqWord.X;
                else if (narrWord != null && withWord != null && withWord.X > narrWord.X) config.BwNarrationMaxX = withWord.X;

                if (chqWord != null && withWord != null && withWord.X > chqWord.X) config.BwChqMaxX = (chqWord.X + withWord.X) / 2.0;
                if (withWord != null && depWord != null && depWord.X > withWord.X) config.BwWithdrawalMaxX = (withWord.X + depWord.X) / 2.0;
                if (depWord != null && balWord != null && balWord.X > depWord.X) config.BwDepositMaxX = (depWord.X + balWord.X) / 2.0;
            }
        }
        else
        {
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
                Regex.IsMatch(lineText, @"^Page\s*:\s*\d+", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(lineText, @"^Page\s+\d+\s+of\s+\d+", RegexOptions.IgnoreCase) ||
                lineText.Contains("This is a computer generated", StringComparison.OrdinalIgnoreCase) ||
                lineText.Contains("Download to read ad-free", StringComparison.OrdinalIgnoreCase) ||
                lineText.Contains("Toll Free", StringComparison.OrdinalIgnoreCase) ||
                lineText.Contains("www.bankofbaroda", StringComparison.OrdinalIgnoreCase))
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
                lineText.Contains("B/F", StringComparison.OrdinalIgnoreCase) ||
                lineText.Contains("Brought Forward", StringComparison.OrdinalIgnoreCase))
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

        if (lineText.Contains("Bank of Baroda", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("बैंक ऑफ़ बड़ौदा", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("बैंक ऑफ बड़ौदा", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("India's International Bank", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Statement of Account", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Account Statement", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Account Details", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("bob World", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Download to read ad-free", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("SCRIBD", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Account No", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("A/c No", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Customer Name", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Customer ID", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Cust ID", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Branch Address", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("IFSC Code", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("MICR Code", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Nomination", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Statement Period", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Page Total", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Grand Total", StringComparison.OrdinalIgnoreCase) ||
            lineText.StartsWith("Grand", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(lineText, @"^Page\s*:\s*\d+", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(lineText, @"^Page\s+\d+\s+of\s+\d+", RegexOptions.IgnoreCase) ||
            lineText.Contains("This is a computer generated", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Toll Free", StringComparison.OrdinalIgnoreCase) ||
            lineText.Contains("Helpline", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Table column header rows
        if ((lineText.Contains("Date", StringComparison.OrdinalIgnoreCase) || lineText.Contains("Txn Date", StringComparison.OrdinalIgnoreCase)) &&
            (lineText.Contains("Withdrawal", StringComparison.OrdinalIgnoreCase) || lineText.Contains("Debit", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static bool DoesLineStartTransaction(List<PdfTextBlock> lineWords, BobLayoutConfig config)
    {
        if (lineWords.Count == 0) return false;

        var firstWord = lineWords[0];

        // Format 1 (Finacle): Could start with S.No (1, 2, 3...) or directly with Date
        if (config.LayoutType == BobLayoutType.BarodaConnectFinacle)
        {
            if (SerialNumberRegex.IsMatch(firstWord.Text.Trim()) && lineWords.Count > 1)
            {
                var secondWord = lineWords[1];
                if (DateRegex.IsMatch(secondWord.Text.Trim()))
                {
                    bool hasAmount = lineWords.Any(w => w.X > secondWord.X && AmountRegex.IsMatch(w.Text.Trim()));
                    return hasAmount;
                }
            }

            if (DateRegex.IsMatch(firstWord.Text.Trim()))
            {
                bool hasAmount = lineWords.Any(w => w.X > firstWord.X && AmountRegex.IsMatch(w.Text.Trim()));
                return hasAmount;
            }
        }
        else
        {
            // bob World: Starts with Date in date area
            if (DateRegex.IsMatch(firstWord.Text.Trim()))
            {
                bool hasAmount = lineWords.Any(w => w.X > firstWord.X && AmountRegex.IsMatch(w.Text.Trim()));
                return hasAmount;
            }
        }

        return false;
    }

    private class BobTxAccumulator
    {
        public int PageNumber { get; set; }
        public BobLayoutType Layout { get; set; }
        public string? SNoText { get; set; }
        public string DateText { get; set; } = string.Empty;
        public string? ValueDateText { get; set; }
        public string? WithdrawalText { get; set; }
        public string? DepositText { get; set; }
        public string? BalanceText { get; set; }
        public string? ChequeNoText { get; set; }
        public List<string> NarrationWords { get; set; } = [];
        public List<PdfTextBlock> RawBlocks { get; set; } = [];
    }

    private static void AssignLineToTransaction(BobTxAccumulator tx, List<PdfTextBlock> lineWords, BobLayoutConfig config)
    {
        tx.RawBlocks.AddRange(lineWords);

        if (config.LayoutType == BobLayoutType.BarodaConnectFinacle)
        {
            // Baroda Connect: S.No (optional) -> Date -> Value Date -> Description -> Cheque No -> Withdrawal -> Deposit -> Balance
            foreach (var w in lineWords)
            {
                var text = w.Text.Trim();
                if (string.IsNullOrWhiteSpace(text) || text == "-" || text == "--") continue;

                if (w.X < config.SnoMaxX && string.IsNullOrEmpty(tx.SNoText) && SerialNumberRegex.IsMatch(text))
                {
                    tx.SNoText = text;
                }
                else if (w.X < config.DateMaxX && string.IsNullOrEmpty(tx.DateText) && DateRegex.IsMatch(text))
                {
                    tx.DateText = text;
                }
                else if (w.X >= config.DateMaxX && w.X < config.ValueDateMaxX && string.IsNullOrEmpty(tx.ValueDateText) && DateRegex.IsMatch(text))
                {
                    tx.ValueDateText = text;
                }
                else if (w.X >= config.DescriptionMaxX && w.X < config.ChequeMaxX && string.IsNullOrEmpty(tx.ChequeNoText) && ChequeRegex.IsMatch(text))
                {
                    tx.ChequeNoText = text;
                }
                else if (w.X >= config.ChequeMaxX && w.X < config.WithdrawalMaxX && string.IsNullOrEmpty(tx.WithdrawalText) && AmountRegex.IsMatch(text))
                {
                    tx.WithdrawalText = text;
                }
                else if (w.X >= config.WithdrawalMaxX && w.X < config.DepositMaxX && string.IsNullOrEmpty(tx.DepositText) && AmountRegex.IsMatch(text))
                {
                    tx.DepositText = text;
                }
                else if (w.X >= config.DepositMaxX && string.IsNullOrEmpty(tx.BalanceText) && AmountRegex.IsMatch(text))
                {
                    tx.BalanceText = text;
                }
                else if (w.X >= config.ValueDateMaxX && w.X < config.DescriptionMaxX)
                {
                    tx.NarrationWords.Add(text);
                }
                else
                {
                    if (AmountRegex.IsMatch(text) && w.X >= config.ChequeMaxX)
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
        else
        {
            // bob World: Date -> Particulars / Narration -> Cheque / Ref No -> Withdrawal -> Deposit -> Balance
            foreach (var w in lineWords)
            {
                var text = w.Text.Trim();
                if (string.IsNullOrWhiteSpace(text) || text == "-" || text == "--") continue;

                if (w.X < config.BwDateMaxX && string.IsNullOrEmpty(tx.DateText) && DateRegex.IsMatch(text))
                {
                    tx.DateText = text;
                }
                else if (w.X >= config.BwNarrationMaxX && w.X < config.BwChqMaxX && string.IsNullOrEmpty(tx.ChequeNoText) && ChequeRegex.IsMatch(text))
                {
                    tx.ChequeNoText = text;
                }
                else if (w.X >= config.BwChqMaxX && w.X < config.BwWithdrawalMaxX && string.IsNullOrEmpty(tx.WithdrawalText) && AmountRegex.IsMatch(text))
                {
                    tx.WithdrawalText = text;
                }
                else if (w.X >= config.BwWithdrawalMaxX && w.X < config.BwDepositMaxX && string.IsNullOrEmpty(tx.DepositText) && AmountRegex.IsMatch(text))
                {
                    tx.DepositText = text;
                }
                else if (w.X >= config.BwDepositMaxX && string.IsNullOrEmpty(tx.BalanceText) && AmountRegex.IsMatch(text))
                {
                    tx.BalanceText = text;
                }
                else if (w.X >= config.BwDateMaxX && w.X < config.BwNarrationMaxX)
                {
                    tx.NarrationWords.Add(text);
                }
                else
                {
                    if (AmountRegex.IsMatch(text) && w.X >= config.BwChqMaxX)
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

    private static void AppendContinuationLine(BobTxAccumulator tx, List<PdfTextBlock> lineWords, BobLayoutConfig config)
    {
        tx.RawBlocks.AddRange(lineWords);

        foreach (var w in lineWords)
        {
            var text = w.Text.Trim();
            if (string.IsNullOrWhiteSpace(text) || text == "-" || text == "--") continue;

            if (config.LayoutType == BobLayoutType.BarodaConnectFinacle)
            {
                if (w.X < config.WithdrawalMaxX)
                {
                    if (w.X >= config.DescriptionMaxX && w.X < config.ChequeMaxX && string.IsNullOrEmpty(tx.ChequeNoText) && ChequeRegex.IsMatch(text))
                    {
                        tx.ChequeNoText = text;
                    }
                    else
                    {
                        tx.NarrationWords.Add(text);
                    }
                }
            }
            else
            {
                if (w.X < config.BwWithdrawalMaxX)
                {
                    if (w.X >= config.BwNarrationMaxX && w.X < config.BwChqMaxX && string.IsNullOrEmpty(tx.ChequeNoText) && ChequeRegex.IsMatch(text))
                    {
                        tx.ChequeNoText = text;
                    }
                    else
                    {
                        tx.NarrationWords.Add(text);
                    }
                }
            }
        }
    }

    private static ParsedTransaction? BuildTransaction(BobTxAccumulator acc, int rowIndex, int bankCode, string bankName, string parserVersion)
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

        string description = string.Join(" ", acc.NarrationWords).Trim();
        if (string.IsNullOrWhiteSpace(description))
        {
            description = "Bank of Baroda Transaction";
        }

        string? reference = !string.IsNullOrWhiteSpace(acc.ChequeNoText) ? acc.ChequeNoText.Trim() : null;
        string? utr = null;

        var specificMatch = SpecificUtrRegex.Match(description);
        if (specificMatch.Success)
        {
            utr = specificMatch.Value;
            reference ??= utr;
        }
        else
        {
            var genericMatch = GenericUtrRegex.Match(description);
            if (genericMatch.Success)
            {
                utr = genericMatch.Value;
                reference ??= utr;
            }
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
