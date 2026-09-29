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
/// Deterministic statement parser for ICICI Bank statements format 2 (ICICI-v2).
/// Supports the Corporate / Detailed Statement format (e.g. Overdraft / Cash Credit / CAA accounts)
/// with 9 columns: Sr No, Tran ID, Value Date, Transaction Date, Cheque no/ RefNo, Transaction Remarks,
/// Withdrawl (Dr), Deposit (Cr), Balance.
/// Features top-anchor narration reconstruction, dynamic column calibration, NA financial value handling,
/// multi-page continuation, negative balance support, UPI/RTGS/NEFT UTR extraction, and running balance continuity.
/// </summary>
public class ICICIStatementParserV2 : IBankStatementParser
{
    public int BankCode => (int)BankType.ICICI;
    public string BankName => "ICICI Bank";
    public string ParserVersion => "ICICI-v2";

    private readonly ILogger<ICICIStatementParserV2> _logger;

    private static readonly Regex DateExactRegex = new(@"^\d{2}-[A-Za-z]{3}-\d{4}$", RegexOptions.Compiled);
    private static readonly Regex DatePartialRegex = new(@"\d{2}-[A-Za-z]{3}-\d{4}", RegexOptions.Compiled);
    private static readonly Regex UpiRrnRegex = new(@"(?:UPI/|/)(\d{10,12})", RegexOptions.Compiled);
    private static readonly Regex UpiIdRegex = new(@"/(?:ICI|UPI)([a-zA-Z0-9]{20,})", RegexOptions.Compiled);
    private static readonly Regex RtgsRegex = new(@"\b([A-Z]{4}[A-Z0-9]{14,20})\b", RegexOptions.Compiled);
    private static readonly Regex NeftRegex = new(@"(?:NEFT|RTGS)[-/]([A-Z0-9]{16,22})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public ICICIStatementParserV2(ILogger<ICICIStatementParserV2> logger)
    {
        _logger = logger;
    }

    public bool CanParse(BankDetectionResult detection)
    {
        return detection != null &&
               detection.DetectedBank == BankType.ICICI &&
               detection.IsSupported &&
               (detection.DetectedFormat == "ICICI-v2" ||
                detection.DetectionSignals.Any(s => s.Contains("ICICI-v2")));
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

        try
        {
            // 1. Calibrate Column Boundaries Dynamically
            var colBounds = CalibrateColumnBoundaries(extraction.Pages);

            // 2. Iterate through pages and extract transactions
            for (int pageIdx = 0; pageIdx < extraction.Pages.Count; pageIdx++)
            {
                var page = extraction.Pages[pageIdx];
                int pageNum = page.PageNumber;

                // Flatten text blocks / words for geometry-based partitioning
                var allBlocks = page.CandidateRows
                    .SelectMany(r => r.LineFragments)
                    .Where(b => !string.IsNullOrWhiteSpace(b.Text))
                    .ToList();

                if (allBlocks.Count == 0) continue;

                // Determine vertical active table bounds
                double tableTopY = 30.0;
                double tableBottomY = page.Height > 0 ? page.Height - 15.0 : 825.0;

                // Page 1 header exclusion
                if (pageNum == 1)
                {
                    // Locate header row or default to Y=355
                    var headerRow = page.CandidateRows.FirstOrDefault(r =>
                        r.RawLineText.Contains("Sr", StringComparison.OrdinalIgnoreCase) &&
                        r.RawLineText.Contains("Withdrawl", StringComparison.OrdinalIgnoreCase));

                    if (headerRow != null)
                    {
                        tableTopY = headerRow.Y + headerRow.Height + 12.0;
                    }
                    else
                    {
                        tableTopY = 355.0;
                    }
                }

                // Check for legend / footer cutoff on end-of-statement pages
                var legendRow = page.CandidateRows.FirstOrDefault(r =>
                    r.RawLineText.Contains("Legends Used", StringComparison.OrdinalIgnoreCase) ||
                    r.RawLineText.Contains("BBPS - Bharat Bill", StringComparison.OrdinalIgnoreCase) ||
                    r.RawLineText.Contains("You can download maximum of", StringComparison.OrdinalIgnoreCase));

                if (legendRow != null)
                {
                    tableBottomY = legendRow.Y - 5.0;
                }

                // Filter blocks within active table region
                var tableBlocks = allBlocks
                    .Where(b => b.Y >= tableTopY && b.Y <= tableBottomY)
                    .ToList();

                if (tableBlocks.Count == 0) continue;

                // 3. Find Transaction Anchor Rows
                // In ICICI-v2, each transaction begins with a serial number (1, 2, 3...) at X < colBounds.TranIdLeft
                var anchorBlocks = tableBlocks
                    .Where(b => b.X < colBounds.TranIdLeft && int.TryParse(b.Text, out _))
                    .OrderBy(b => b.Y)
                    .ToList();

                if (anchorBlocks.Count == 0) continue;

                // 4. Partition and reconstruct each transaction
                for (int i = 0; i < anchorBlocks.Count; i++)
                {
                    var currentAnchor = anchorBlocks[i];
                    int srNo = int.Parse(currentAnchor.Text);

                    double txTopY = currentAnchor.Y - 3.0;
                    double txBottomY = (i == anchorBlocks.Count - 1)
                        ? tableBottomY + 15.0
                        : anchorBlocks[i + 1].Y - 3.0;

                    var txBlocks = tableBlocks
                        .Where(b => b.Y >= txTopY && b.Y < txBottomY)
                        .OrderBy(b => b.Y)
                        .ThenBy(b => b.X)
                        .ToList();

                    var parsedTx = ExtractTransactionFromBlocks(txBlocks, srNo, colBounds, pageNum, i + 1);
                    if (parsedTx != null)
                    {
                        transactions.Add(parsedTx);
                    }
                }
            }

            // 5. Remove any duplicate serial numbers while preserving order
            transactions = transactions
                .GroupBy(t => t.SourceLineIndex) // SrNo is stored in SourceLineIndex
                .Select(g => g.First())
                .OrderBy(t => t.SourceLineIndex)
                .ToList();

            if (transactions.Count == 0)
            {
                return new BankParsingResult
                {
                    Success = false,
                    ErrorMessage = "No transactions could be parsed from the ICICI-v2 statement.",
                    BankCode = BankCode,
                    BankName = BankName,
                    ParserVersion = ParserVersion
                };
            }

            // 6. Balance Continuity & Opening/Closing Balance Derivation
            var firstTx = transactions.First();
            var lastTx = transactions.Last();

            // Implied opening balance before first transaction
            if (firstTx.Balance.HasValue)
            {
                openingBalance = firstTx.Balance.Value + (firstTx.Debit ?? 0m) - (firstTx.Credit ?? 0m);
            }
            closingBalance = lastTx.Balance;

            decimal runningBalance = openingBalance ?? 0m;
            int continuityMismatches = 0;

            for (int i = 0; i < transactions.Count; i++)
            {
                var tx = transactions[i];
                if (tx.Balance.HasValue)
                {
                    decimal expected = runningBalance - (tx.Debit ?? 0m) + (tx.Credit ?? 0m);
                    if (Math.Abs(expected - tx.Balance.Value) > 0.01m)
                    {
                        continuityMismatches++;
                        tx.NeedsReview = true;
                        tx.ReviewWarnings.Add($"Running balance mismatch at Sr No {tx.SourceLineIndex}: Expected {expected:F2}, but statement shows {tx.Balance.Value:F2}.");
                    }
                    runningBalance = tx.Balance.Value;
                }
            }

            if (continuityMismatches > 0)
            {
                warnings.Add($"Detected {continuityMismatches} running balance continuity mismatch(es) across statement.");
            }

            _logger.LogInformation("Successfully parsed {Count} ICICI-v2 transactions. Opening: {Opening:N2}, Closing: {Closing:N2}, Mismatches: {Mismatches}",
                transactions.Count, openingBalance ?? 0m, closingBalance ?? 0m, continuityMismatches);

            return new BankParsingResult
            {
                Success = true,
                BankCode = BankCode,
                BankName = BankName,
                ParserVersion = ParserVersion,
                AccountNumber = detection.AccountNumber,
                CustomerName = detection.CustomerName,
                StatementPeriodStart = detection.StatementFrom ?? (transactions.Count > 0 ? transactions.Min(t => t.TransactionDate) : null),
                StatementPeriodEnd = detection.StatementTo ?? (transactions.Count > 0 ? transactions.Max(t => t.TransactionDate) : null),
                TotalDetected = transactions.Count,
                ProcessedCount = transactions.Count,
                RejectedCount = 0,
                WarningsCount = warnings.Count + transactions.Sum(t => t.ReviewWarnings.Count),
                Transactions = transactions,
                Warnings = warnings
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse ICICI-v2 bank statement");
            return new BankParsingResult
            {
                Success = false,
                ErrorMessage = $"An error occurred while parsing the ICICI-v2 statement: {ex.Message}",
                BankCode = BankCode,
                BankName = BankName,
                ParserVersion = ParserVersion
            };
        }
    }

    private static ParsedTransaction? ExtractTransactionFromBlocks(
        List<PdfTextBlock> blocks,
        int srNo,
        ColumnBoundaries col,
        int pageNumber,
        int lineIndex)
    {
        if (blocks == null || blocks.Count == 0) return null;

        // 1. Tran ID (col.TranIdLeft <= X < col.ValDateLeft)
        var tranIdBlocks = blocks
            .Where(b => b.X >= col.TranIdLeft && b.X < col.ValDateLeft)
            .OrderBy(b => b.X)
            .ToList();
        string tranId = string.Join("", tranIdBlocks.Select(b => b.Text)).Trim();

        // 2. Value Date (col.ValDateLeft <= X < col.TxDateLeft)
        var valDateBlocks = blocks
            .Where(b => b.X >= col.ValDateLeft && b.X < col.TxDateLeft)
            .OrderBy(b => b.Y)
            .ThenBy(b => b.X)
            .ToList();
        string valDateRaw = string.Join("", valDateBlocks.Select(b => b.Text)).Trim();
        DateTime? valueDate = ParseDateString(valDateRaw);

        // 3. Transaction Date (col.TxDateLeft <= X < col.ChequeLeft)
        var txDateBlocks = blocks
            .Where(b => b.X >= col.TxDateLeft && b.X < col.ChequeLeft)
            .OrderBy(b => b.Y)
            .ThenBy(b => b.X)
            .ToList();
        string txDateRaw = string.Join("", txDateBlocks.Select(b => b.Text)).Trim();
        DateTime? txDate = ParseDateString(txDateRaw) ?? valueDate;

        if (!txDate.HasValue) return null;

        // 4. Cheque / Ref No (col.ChequeLeft <= X < col.RemarksLeft)
        var chequeBlocks = blocks
            .Where(b => b.X >= col.ChequeLeft && b.X < col.RemarksLeft)
            .Where(b => !string.Equals(b.Text.Trim(), "NA", StringComparison.OrdinalIgnoreCase))
            .OrderBy(b => b.X)
            .ToList();
        string chequeRef = string.Join(" ", chequeBlocks.Select(b => b.Text)).Trim();

        // 5. Narration / Transaction Remarks (col.RemarksLeft <= X < col.DrLeft)
        var narrBlocks = blocks
            .Where(b => b.X >= col.RemarksLeft && b.X < col.DrLeft)
            .OrderBy(b => b.Y)
            .ThenBy(b => b.X)
            .ToList();
        string description = CleanNarration(narrBlocks);

        // 6. Withdrawl (Dr) (col.DrLeft <= X < col.CrLeft)
        decimal? debit = null;
        var drBlocks = blocks
            .Where(b => b.X >= col.DrLeft && b.X < col.CrLeft)
            .Where(b => !string.Equals(b.Text.Trim(), "NA", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (drBlocks.Count > 0)
        {
            string drStr = string.Join("", drBlocks.Select(b => b.Text)).Replace(",", "");
            if (decimal.TryParse(drStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal d) && d > 0)
            {
                debit = d;
            }
        }

        // 7. Deposit (Cr) (col.CrLeft <= X < col.BalLeft)
        decimal? credit = null;
        var crBlocks = blocks
            .Where(b => b.X >= col.CrLeft && b.X < col.BalLeft)
            .Where(b => !string.Equals(b.Text.Trim(), "NA", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (crBlocks.Count > 0)
        {
            string crStr = string.Join("", crBlocks.Select(b => b.Text)).Replace(",", "");
            if (decimal.TryParse(crStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal c) && c > 0)
            {
                credit = c;
            }
        }

        // 8. Balance (X >= col.BalLeft)
        decimal? balance = null;
        var balBlocks = blocks
            .Where(b => b.X >= col.BalLeft)
            .ToList();
        if (balBlocks.Count > 0)
        {
            string balStr = string.Join("", balBlocks.Select(b => b.Text)).Replace(",", "");
            if (decimal.TryParse(balStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal b))
            {
                balance = b;
            }
        }

        decimal amount = debit ?? credit ?? 0m;
        string txType = debit.HasValue ? "Debit" : "Credit";

        // 9. Identifier, Cheque, and UTR Extraction
        string? reference = null;
        string? utr = null;

        if (!string.IsNullOrWhiteSpace(chequeRef))
        {
            reference = chequeRef;
        }

        var upiMatch = UpiRrnRegex.Match(description);
        if (upiMatch.Success)
        {
            reference ??= upiMatch.Groups[1].Value;
            utr = upiMatch.Groups[1].Value;
        }

        var upiIdMatch = UpiIdRegex.Match(description);
        if (upiIdMatch.Success)
        {
            reference ??= upiIdMatch.Groups[1].Value;
        }

        var rtgsMatch = RtgsRegex.Match(description);
        if (rtgsMatch.Success)
        {
            utr ??= rtgsMatch.Groups[1].Value;
            reference ??= rtgsMatch.Groups[1].Value;
        }

        var neftMatch = NeftRegex.Match(description);
        if (neftMatch.Success)
        {
            utr ??= neftMatch.Groups[1].Value;
            reference ??= neftMatch.Groups[1].Value;
        }

        // Fallback reference to Tran ID if nothing else available
        if (string.IsNullOrWhiteSpace(reference) && !string.IsNullOrWhiteSpace(tranId))
        {
            reference = tranId;
        }

        return new ParsedTransaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = txDate.Value,
            ValueDate = valueDate,
            Description = description,
            Debit = debit,
            Credit = credit,
            Amount = amount,
            Balance = balance,
            Reference = reference,
            Utr = utr,
            TransactionType = txType,
            BankCode = (int)BankType.ICICI,
            BankName = "ICICI Bank",
            ParserVersion = "ICICI-v2",
            SourcePageNumber = pageNumber,
            SourceLineIndex = srNo,
            Confidence = 1.0,
            NeedsReview = false,
            RawSourceText = string.Join(" ", blocks.Select(b => b.Text))
        };
    }

    private static string CleanNarration(List<PdfTextBlock> blocks)
    {
        if (blocks.Count == 0) return string.Empty;

        // Group into lines by Y
        var lineGroups = blocks
            .GroupBy(b => Math.Round(b.Y / 3.0) * 3.0)
            .OrderBy(g => g.Key);

        var lines = new List<string>();
        foreach (var group in lineGroups)
        {
            var lineText = string.Join(" ", group.OrderBy(b => b.X).Select(b => b.Text)).Trim();
            if (!string.IsNullOrEmpty(lineText))
            {
                lines.Add(lineText);
            }
        }

        var full = string.Join(" ", lines);
        return Regex.Replace(full, @"\s+", " ").Trim();
    }

    private static DateTime? ParseDateString(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Handle wrapped date fragments like "20-Feb-2026"
        var clean = Regex.Replace(text, @"\s+", "");

        if (DateTime.TryParseExact(clean, "dd-MMM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return d;
        }

        var match = DatePartialRegex.Match(clean);
        if (match.Success && DateTime.TryParseExact(match.Value, "dd-MMM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static ColumnBoundaries CalibrateColumnBoundaries(List<PdfPageResult> pages)
    {
        // Default calibration for ICICI 1.pdf geometry
        double tranIdLeft = 65.0;
        double valDateLeft = 115.0;
        double txDateLeft = 185.0;
        double chequeLeft = 255.0;
        double remarksLeft = 312.0;
        double drLeft = 395.0;
        double crLeft = 455.0;
        double balLeft = 505.0;

        // Try to dynamically locate header row on Page 1
        var p1 = pages.FirstOrDefault(p => p.PageNumber == 1);
        if (p1 != null)
        {
            var allBlocks = p1.CandidateRows.SelectMany(r => r.LineFragments).ToList();

            var tranIdWord = allBlocks.FirstOrDefault(b => b.Y >= 330 && b.Y <= 355 && (b.Text.Equals("Tran", StringComparison.OrdinalIgnoreCase) || b.Text.Equals("ID", StringComparison.OrdinalIgnoreCase)));
            var valDateWord = allBlocks.FirstOrDefault(b => b.Y >= 330 && b.Y <= 355 && b.Text.Equals("Value", StringComparison.OrdinalIgnoreCase));
            var txDateWord = allBlocks.FirstOrDefault(b => b.Y >= 330 && b.Y <= 355 && b.Text.Equals("Transaction", StringComparison.OrdinalIgnoreCase) && b.X > 160 && b.X < 250);
            var chqWord = allBlocks.FirstOrDefault(b => b.Y >= 330 && b.Y <= 355 && (b.Text.Equals("Cheque", StringComparison.OrdinalIgnoreCase) || b.Text.Equals("RefNo", StringComparison.OrdinalIgnoreCase)));
            var remarksWord = allBlocks.FirstOrDefault(b => b.Y >= 330 && b.Y <= 355 && b.Text.Equals("Remarks", StringComparison.OrdinalIgnoreCase));
            var drWord = allBlocks.FirstOrDefault(b => b.Y >= 330 && b.Y <= 355 && (b.Text.Equals("Withdrawl", StringComparison.OrdinalIgnoreCase) || b.Text.Equals("(Dr)", StringComparison.OrdinalIgnoreCase)));
            var crWord = allBlocks.FirstOrDefault(b => b.Y >= 330 && b.Y <= 355 && (b.Text.Equals("Deposit", StringComparison.OrdinalIgnoreCase) || b.Text.Equals("(Cr)", StringComparison.OrdinalIgnoreCase)));
            var balWord = allBlocks.FirstOrDefault(b => b.Y >= 330 && b.Y <= 355 && b.Text.Equals("Balance", StringComparison.OrdinalIgnoreCase));

            if (tranIdWord != null) tranIdLeft = tranIdWord.X - 12.0;
            if (valDateWord != null) valDateLeft = valDateWord.X - 15.0;
            if (txDateWord != null) txDateLeft = txDateWord.X - 15.0;
            if (chqWord != null) chequeLeft = chqWord.X - 10.0;
            if (remarksWord != null) remarksLeft = remarksWord.X - 18.0;
            if (drWord != null) drLeft = drWord.X - 15.0;
            if (crWord != null) crLeft = crWord.X - 10.0;
            if (balWord != null) balLeft = balWord.X - 10.0;
        }

        return new ColumnBoundaries
        {
            TranIdLeft = tranIdLeft,
            ValDateLeft = valDateLeft,
            TxDateLeft = txDateLeft,
            ChequeLeft = chequeLeft,
            RemarksLeft = remarksLeft,
            DrLeft = drLeft,
            CrLeft = crLeft,
            BalLeft = balLeft
        };
    }

    private class ColumnBoundaries
    {
        public double TranIdLeft { get; set; }
        public double ValDateLeft { get; set; }
        public double TxDateLeft { get; set; }
        public double ChequeLeft { get; set; }
        public double RemarksLeft { get; set; }
        public double DrLeft { get; set; }
        public double CrLeft { get; set; }
        public double BalLeft { get; set; }
    }
}
