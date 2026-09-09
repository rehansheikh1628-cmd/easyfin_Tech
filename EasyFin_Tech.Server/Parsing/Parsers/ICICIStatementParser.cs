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
/// Deterministic statement parser for ICICI Bank statements (ICICI-v1).
/// Generic production implementation supporting multi-line transaction narration via midpoint partitioning,
/// multi-page continuation, dynamic column calibration, Indian currency formatting,
/// cheque/reference/UTR identification, and running balance continuity validation.
/// </summary>
public class ICICIStatementParser : IBankStatementParser
{
    public int BankCode => (int)BankType.ICICI;
    public string BankName => "ICICI Bank";
    public string ParserVersion => "ICICI-v1";

    private readonly ILogger<ICICIStatementParser> _logger;

    private static readonly Regex DateRegex = new(@"^\d{2}[-/]\d{2}[-/]\d{2,4}$", RegexOptions.Compiled);
    private static readonly Regex NeftRegex = new(@"(?:NEFT|RTGS)[-/]([A-Z0-9]{16,22})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ImpsRegex = new(@"(?:MMT/)?IMPS/(\d{12})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NfsRegex = new(@"NFS/(?:CASH WDL/)?(\d{12})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UpiRefRegex = new(@"/(\d{12})/", RegexOptions.Compiled);
    private static readonly Regex UpiIdRegex = new(@"/(?:ICI|UPI)([a-zA-Z0-9]{20,})", RegexOptions.Compiled);

    public ICICIStatementParser(ILogger<ICICIStatementParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(BankDetectionResult detection)
    {
        return detection != null && 
               detection.DetectedBank == BankType.ICICI && 
               detection.IsSupported && 
               detection.DetectedFormat != "ICICI-v2";
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

        // Process each page independently to respect page bounds and headers/footers
        foreach (var page in extraction.Pages.OrderBy(p => p.PageNumber))
        {
            if (page.CandidateRows == null || page.CandidateRows.Count == 0)
            {
                continue;
            }

            // 1. Locate table header row on this page
            var headerRow = page.CandidateRows.FirstOrDefault(r =>
            {
                var text = r.RawLineText ?? string.Empty;
                return text.Contains("DATE", StringComparison.OrdinalIgnoreCase) &&
                       text.Contains("PARTICULARS", StringComparison.OrdinalIgnoreCase) &&
                       text.Contains("DEPOSITS", StringComparison.OrdinalIgnoreCase) &&
                       text.Contains("WITHDRAWALS", StringComparison.OrdinalIgnoreCase) &&
                       text.Contains("BALANCE", StringComparison.OrdinalIgnoreCase);
            });

            if (headerRow == null)
            {
                // Not a transaction page (e.g. summary page, advertisement page, legend page)
                continue;
            }

            double tableHeaderBottomY = headerRow.Y + headerRow.Height;

            // 2. Locate footer / total row
            var totalRow = page.CandidateRows.FirstOrDefault(r =>
                r.Y > tableHeaderBottomY &&
                (r.RawLineText?.StartsWith("Total:", StringComparison.OrdinalIgnoreCase) == true ||
                 r.RawLineText?.StartsWith("Total :", StringComparison.OrdinalIgnoreCase) == true));

            double tableFooterTopY = totalRow != null ? totalRow.Y : page.Height;

            // 3. Calibrate column intervals dynamically from header tokens if available
            var columnBounds = CalibrateColumns(headerRow, page.Width);

            // 4. Collect candidate fragments within the table body
            var tableFrags = page.CandidateRows
                .Where(r => r.Y >= tableHeaderBottomY - 2 && r.Y <= tableFooterTopY + 2)
                .SelectMany(r => r.LineFragments)
                .ToList();

            // 5. Identify transaction anchors by Date fragment in DATE column
            var dateFrags = tableFrags
                .Where(f => f.X >= columnBounds.DateMinX && f.X <= columnBounds.DateMaxX && DateRegex.IsMatch(f.Text))
                .OrderBy(f => f.Y)
                .ToList();

            var anchors = new List<(PdfTextBlock DateFrag, decimal? Dep, decimal? Wdl, decimal Bal, bool IsBf)>();

            foreach (var df in dateFrags)
            {
                double yCenter = df.Y + df.Height / 2.0;

                // Find fragments roughly on the same baseline (+- 6pt)
                var rowFrags = tableFrags
                    .Where(f => Math.Abs((f.Y + f.Height / 2.0) - yCenter) < 6.0)
                    .ToList();

                // Check for Brought Forward (Opening Balance)
                bool isBf = rowFrags.Any(f => (f.Text.Equals("B/F", StringComparison.OrdinalIgnoreCase) ||
                                               f.Text.Equals("BROUGHT", StringComparison.OrdinalIgnoreCase)) &&
                                              f.X >= columnBounds.ParticularsMinX && f.X <= columnBounds.DepositsMinX);

                // Balance fragment (in BALANCE column)
                var balFrag = rowFrags.FirstOrDefault(f => f.X >= columnBounds.BalanceMinX &&
                                                           (f.X + f.Width) <= columnBounds.BalanceMaxX &&
                                                           TryParseAmount(f.Text, out _));
                if (balFrag == null) continue;
                TryParseAmount(balFrag.Text, out var bal);

                if (isBf)
                {
                    anchors.Add((df, null, null, bal, true));
                    continue;
                }

                decimal? dep = null;
                decimal? wdl = null;

                // Deposit fragment (in DEPOSITS column)
                var depFrag = rowFrags.FirstOrDefault(f => f.X >= columnBounds.DepositsMinX &&
                                                           (f.X + f.Width) <= columnBounds.DepositsMaxX &&
                                                           TryParseAmount(f.Text, out _));
                if (depFrag != null && TryParseAmount(depFrag.Text, out var depVal))
                {
                    dep = depVal;
                }

                // Withdrawal fragment (in WITHDRAWALS column)
                var wdlFrag = rowFrags.FirstOrDefault(f => f.X >= columnBounds.WithdrawalsMinX &&
                                                           (f.X + f.Width) <= columnBounds.WithdrawalsMaxX &&
                                                           TryParseAmount(f.Text, out _));
                if (wdlFrag != null && TryParseAmount(wdlFrag.Text, out var wdlVal))
                {
                    wdl = wdlVal;
                }

                if (dep.HasValue || wdl.HasValue)
                {
                    anchors.Add((df, dep, wdl, bal, false));
                }
            }

            // 6. Partition narration lines between adjacent anchors using vertical midpoints
            for (int i = 0; i < anchors.Count; i++)
            {
                var anchor = anchors[i];
                double currY = anchor.DateFrag.Y + anchor.DateFrag.Height / 2.0;

                if (anchor.IsBf)
                {
                    openingBalance ??= anchor.Bal;
                    continue;
                }

                double topBound = (i == 0)
                    ? tableHeaderBottomY
                    : (currY + (anchors[i - 1].DateFrag.Y + anchors[i - 1].DateFrag.Height / 2.0)) / 2.0;

                double bottomBound = (i == anchors.Count - 1)
                    ? tableFooterTopY
                    : (currY + (anchors[i + 1].DateFrag.Y + anchors[i + 1].DateFrag.Height / 2.0)) / 2.0;

                // Gather narration text in PARTICULARS column within [topBound, bottomBound]
                var narrFrags = tableFrags
                    .Where(f =>
                    {
                        double fy = f.Y + f.Height / 2.0;
                        return fy >= topBound && fy < bottomBound &&
                               f.X >= columnBounds.ModeMinX && f.X <= columnBounds.ParticularsMaxX;
                    })
                    .OrderBy(f => f.Y)
                    .ThenBy(f => f.X)
                    .ToList();

                var narrLines = narrFrags
                    .GroupBy(f => Math.Round(f.Y / 3.0) * 3.0)
                    .OrderBy(g => g.Key)
                    .Select(g => string.Join(" ", g.OrderBy(f => f.X).Select(f => f.Text)))
                    .ToList();

                var narration = string.Join(" ", narrLines).Trim();

                // Fallback date parsing supporting common ICICI date representations
                DateTime txDate = ParseDate(anchor.DateFrag.Text);

                ExtractIdentifiers(narration, out var refNo, out var utr);

                decimal amount = anchor.Wdl ?? anchor.Dep ?? 0m;
                string txType = anchor.Wdl.HasValue ? "Debit" : "Credit";

                var parsedTx = new ParsedTransaction
                {
                    Id = Guid.NewGuid(),
                    SourcePageNumber = page.PageNumber,
                    SourceLineIndex = rowIndexCounter++,
                    TransactionDate = txDate,
                    ValueDate = txDate,
                    Description = narration,
                    Debit = anchor.Wdl,
                    Credit = anchor.Dep,
                    Amount = amount,
                    Balance = anchor.Bal,
                    Reference = refNo,
                    Utr = utr,
                    TransactionType = txType,
                    BankCode = BankCode,
                    BankName = BankName,
                    ParserVersion = ParserVersion,
                    RawSourceText = $"{anchor.DateFrag.Text} {narration} {(anchor.Dep.HasValue ? anchor.Dep.Value.ToString("F2") : anchor.Wdl?.ToString("F2"))} {anchor.Bal:F2}"
                };

                totalRowsDetected++;
                transactions.Add(parsedTx);
            }
        }

        closingBalance = transactions.LastOrDefault()?.Balance;

        // 7. Validate Running Balance Continuity
        decimal runningBal = openingBalance ?? (transactions.FirstOrDefault()?.Balance ?? 0m);
        int balanceMismatchCount = 0;

        for (int i = 0; i < transactions.Count; i++)
        {
            var tx = transactions[i];
            decimal expectedBal = tx.Debit.HasValue
                ? (runningBal - tx.Debit.Value)
                : (runningBal + (tx.Credit ?? 0m));

            if (tx.Balance.HasValue && Math.Abs(expectedBal - tx.Balance.Value) > 0.001m)
            {
                balanceMismatchCount++;
                tx.NeedsReview = true;
                tx.ReviewWarnings.Add($"Running balance mismatch: Expected ₹{expectedBal:N2} based on previous balance ₹{runningBal:N2}, but recorded ₹{tx.Balance.Value:N2}.");
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

        _logger.LogInformation("ICICI-v1 parse complete: {TxCount} transactions extracted, {Debits} debits, {Credits} credits, {Mismatches} mismatches.",
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

    private static DateTime ParseDate(string dateStr)
    {
        string[] formats = ["dd-MM-yyyy", "dd/MM/yyyy", "dd-MM-yy", "dd/MM/yy", "yyyy-MM-dd"];
        if (DateTime.TryParseExact(dateStr.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return dt;
        }

        if (DateTime.TryParse(dateStr.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var fallback))
        {
            return fallback;
        }

        return DateTime.UtcNow.Date;
    }

    private static bool TryParseAmount(string text, out decimal amount)
    {
        amount = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var clean = text.Replace(",", "").Trim();
        return decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out amount);
    }

    private static void ExtractIdentifiers(string narration, out string? reference, out string? utr)
    {
        reference = null;
        utr = null;

        if (string.IsNullOrWhiteSpace(narration)) return;

        var neftMatch = NeftRegex.Match(narration);
        if (neftMatch.Success)
        {
            utr = neftMatch.Groups[1].Value;
            reference = utr;
            return;
        }

        var impsMatch = ImpsRegex.Match(narration);
        if (impsMatch.Success)
        {
            reference = impsMatch.Groups[1].Value;
            utr = reference;
            return;
        }

        var nfsMatch = NfsRegex.Match(narration);
        if (nfsMatch.Success)
        {
            reference = nfsMatch.Groups[1].Value;
            return;
        }

        var upiRefMatch = UpiRefRegex.Match(narration);
        if (upiRefMatch.Success)
        {
            reference = upiRefMatch.Groups[1].Value;
            utr = reference;
            return;
        }

        var upiIdMatch = UpiIdRegex.Match(narration);
        if (upiIdMatch.Success)
        {
            reference = upiIdMatch.Groups[1].Value;
        }
    }

    private static ColumnBounds CalibrateColumns(PdfCandidateRow headerRow, double pageWidth)
    {
        var frags = headerRow.LineFragments;

        var dateFrag = frags.FirstOrDefault(f => f.Text.Equals("DATE", StringComparison.OrdinalIgnoreCase));
        var modeFrag = frags.FirstOrDefault(f => f.Text.Equals("MODE", StringComparison.OrdinalIgnoreCase));
        var partFrag = frags.FirstOrDefault(f => f.Text.Equals("PARTICULARS", StringComparison.OrdinalIgnoreCase));
        var depFrag = frags.FirstOrDefault(f => f.Text.Equals("DEPOSITS", StringComparison.OrdinalIgnoreCase));
        var wdlFrag = frags.FirstOrDefault(f => f.Text.Equals("WITHDRAWALS", StringComparison.OrdinalIgnoreCase));
        var balFrag = frags.FirstOrDefault(f => f.Text.Equals("BALANCE", StringComparison.OrdinalIgnoreCase));

        double dateMinX = dateFrag != null ? Math.Max(10.0, dateFrag.X - 10.0) : 20.0;
        double dateMaxX = modeFrag != null ? modeFrag.X - 5.0 : (partFrag != null ? partFrag.X - 60.0 : 70.0);

        double modeMinX = modeFrag != null ? modeFrag.X - 5.0 : 68.0;

        double partMinX = partFrag != null ? partFrag.X - 10.0 : 120.0;
        double partMaxX = depFrag != null ? depFrag.X - 5.0 : 340.0;

        double depMinX = depFrag != null ? depFrag.X - 15.0 : 335.0;
        double depMaxX = wdlFrag != null ? wdlFrag.X - 5.0 : 400.0;

        double wdlMinX = wdlFrag != null ? wdlFrag.X - 10.0 : 395.0;
        double wdlMaxX = balFrag != null ? balFrag.X - 10.0 : 470.0;

        double balMinX = balFrag != null ? balFrag.X - 10.0 : 470.0;
        double balMaxX = Math.Max(pageWidth - 10.0, 580.0);

        return new ColumnBounds
        {
            DateMinX = dateMinX,
            DateMaxX = dateMaxX,
            ModeMinX = modeMinX,
            ParticularsMinX = partMinX,
            ParticularsMaxX = partMaxX,
            DepositsMinX = depMinX,
            DepositsMaxX = depMaxX,
            WithdrawalsMinX = wdlMinX,
            WithdrawalsMaxX = wdlMaxX,
            BalanceMinX = balMinX,
            BalanceMaxX = balMaxX
        };
    }

    private class ColumnBounds
    {
        public double DateMinX { get; set; }
        public double DateMaxX { get; set; }
        public double ModeMinX { get; set; }
        public double ParticularsMinX { get; set; }
        public double ParticularsMaxX { get; set; }
        public double DepositsMinX { get; set; }
        public double DepositsMaxX { get; set; }
        public double WithdrawalsMinX { get; set; }
        public double WithdrawalsMaxX { get; set; }
        public double BalanceMinX { get; set; }
        public double BalanceMaxX { get; set; }
    }
}
