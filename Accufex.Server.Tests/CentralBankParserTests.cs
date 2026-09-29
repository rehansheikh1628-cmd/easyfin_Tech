using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Accufex.Server.DTOs;
using Accufex.Server.Parsing;
using Accufex.Server.Parsing.Interfaces;
using Accufex.Server.Parsing.Models;
using Accufex.Server.Parsing.Parsers;
using Microsoft.Extensions.Logging;
using Moq;
using UglyToad.PdfPig;
using Xunit;
using Xunit.Abstractions;

namespace Accufex.Server.Tests;

public class CentralBankParserTests
{
    private readonly ITestOutputHelper _output;
    private readonly Mock<ILogger<BankDetector>> _mockDetectorLogger;
    private readonly Mock<ILogger<CentralBankStatementParser>> _mockParserLogger;
    private readonly BankDetector _detector;
    private readonly CentralBankStatementParser _parser;
    private readonly BankParserRegistry _registry;

    public CentralBankParserTests(ITestOutputHelper output)
    {
        _output = output;
        _mockDetectorLogger = new Mock<ILogger<BankDetector>>();
        _mockParserLogger = new Mock<ILogger<CentralBankStatementParser>>();

        _detector = new BankDetector(_mockDetectorLogger.Object);
        _parser = new CentralBankStatementParser(_mockParserLogger.Object);
        _registry = new BankParserRegistry([_parser]);
    }

    private static PdfExtractionResult BuildSyntheticCentralBankExtraction(Action<List<PdfPageResult>> configurePages)
    {
        var pages = new List<PdfPageResult>();
        configurePages(pages);

        return new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            OriginalFileName = "CENTRAL_BANK_OF_INDIA.pdf",
            PageCount = pages.Count,
            ExtractionStatus = "DigitalTextExtracted",
            HasUsableText = true,
            PdfType = "DigitalWithText",
            Pages = pages
        };
    }

    private static PdfPageResult CreatePage(int pageNumber, List<PdfCandidateRow> rows)
    {
        return new PdfPageResult
        {
            PageNumber = pageNumber,
            Width = 595.0,
            Height = 842.0,
            HasUsableText = true,
            CandidateRows = rows,
            RawText = string.Join("\n", rows.Select(r => r.RawLineText))
        };
    }

    private static PdfCandidateRow CreateRow(int pageNumber, double y, List<(string Text, double X)> tokens)
    {
        var frags = tokens.Select((t, i) => new PdfTextBlock
        {
            Text = t.Text,
            X = t.X,
            Y = y,
            Width = t.Text.Length * 5.0,
            Height = 10.0,
            PageNumber = pageNumber,
            ReadingOrderIndex = i + 1
        }).ToList();

        return new PdfCandidateRow
        {
            PageNumber = pageNumber,
            Y = y,
            Height = 12.0,
            RawLineText = string.Join(" ", tokens.Select(t => t.Text)),
            LineFragments = frags
        };
    }

    #region Bank Detection Tests

    [Fact]
    public void CentralBank_Detector_IdentifiesBrandingAndColumns()
    {
        var extraction = BuildSyntheticCentralBankExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 40.0, [
                    ("CENTRAL", 40.0), ("BANK", 95.0), ("OF", 130.0), ("INDIA", 150.0)
                ]),
                CreateRow(1, 55.0, [
                    ("STATEMENT", 40.0), ("OF", 110.0), ("ACCOUNT", 130.0)
                ]),
                CreateRow(1, 70.0, [
                    ("FRIEND_FRIENDS", 40.0)
                ]),
                CreateRow(1, 85.0, [
                    ("Branch", 40.0), ("Code", 85.0), (":", 115.0), ("280624", 125.0),
                    ("IFSC", 200.0), ("Code", 230.0), (":", 260.0), ("CBIN0280624", 270.0)
                ]),
                CreateRow(1, 100.0, [
                    ("Account", 40.0), ("No.", 85.0), (":", 105.0), ("5092006804", 115.0)
                ]),
                CreateRow(1, 115.0, [
                    ("Statement", 40.0), ("From", 90.0), ("01/04/2025", 120.0), ("to", 170.0), ("31/03/2026", 185.0),
                    ("Page", 370.0), ("No.", 390.0), (":", 405.0), ("1", 420.0)
                ]),
                CreateRow(1, 130.0, [
                    ("Value", 45.0), ("Post", 90.0), ("Details", 140.0), ("Chq.No.", 250.0),
                    ("Debit", 325.0), ("Credit", 420.0), ("Balance", 490.0)
                ]),
                CreateRow(1, 145.0, [
                    ("Date", 45.0), ("Date", 90.0)
                ])
            };
            pages.Add(CreatePage(1, rows));
        });

        var result = _detector.DetectBank(extraction);

        Assert.NotNull(result);
        Assert.Equal(BankType.CentralBank, result.DetectedBank);
        Assert.Equal("Central Bank of India", result.BankName);
        Assert.True(result.IsSupported);
        Assert.True(result.Confidence >= 0.90);
        Assert.Equal("5092006804", result.AccountNumber);
        Assert.Equal("FRIEND_FRIENDS", result.CustomerName);
        Assert.Equal(new DateTime(2025, 4, 1), result.StatementFrom);
        Assert.Equal(new DateTime(2026, 3, 31), result.StatementTo);
    }

    [Fact]
    public void CentralBank_Detector_DoesNotMisclassifyExistingBanks()
    {
        // 1. Synthetic HDFC
        var hdfcExtraction = new PdfExtractionResult
        {
            Pages = [
                new PdfPageResult
                {
                    PageNumber = 1,
                    RawText = "HDFC BANK LIMITED\nIFSC: HDFC0000060\nDate Narration Chq/Ref.No Value Dt Withdrawal Amt. Deposit Amt. Closing Balance"
                }
            ]
        };
        var hdfcResult = _detector.DetectBank(hdfcExtraction);
        Assert.NotEqual(BankType.CentralBank, hdfcResult.DetectedBank);

        // 2. Synthetic YES BANK
        var yesExtraction = new PdfExtractionResult
        {
            Pages = [
                new PdfPageResult
                {
                    PageNumber = 1,
                    RawText = "YES BANK LIMITED\nIFSC: YESB0000001\nTxn Date Value Date Description Chq / Ref No. Debit Credit Balance"
                }
            ]
        };
        var yesResult = _detector.DetectBank(yesExtraction);
        Assert.NotEqual(BankType.CentralBank, yesResult.DetectedBank);

        // 3. Synthetic Axis Bank
        var axisExtraction = new PdfExtractionResult
        {
            Pages = [
                new PdfPageResult
                {
                    PageNumber = 1,
                    RawText = "AXIS BANK LTD\nIFSC: UTIB0000001\nAccount Statement Report\nS.NO Transaction Date Value Date Particulars Amount(INR) Debit/Credit Balance(INR)"
                }
            ]
        };
        var axisResult = _detector.DetectBank(axisExtraction);
        Assert.NotEqual(BankType.CentralBank, axisResult.DetectedBank);
    }

    [Fact]
    public void CentralBank_Parser_ResolvesVersion_CENTRALv1()
    {
        Assert.Equal("CENTRAL-v1", _parser.ParserVersion);
        Assert.Equal("Central Bank of India", _parser.BankName);
        Assert.Equal((int)BankType.CentralBank, _parser.BankCode);

        var detection = new BankDetectionResult
        {
            DetectedBank = BankType.CentralBank,
            BankName = "Central Bank of India",
            IsSupported = true,
            Confidence = 0.95
        };

        Assert.True(_parser.CanParse(detection));
        var resolved = _registry.ResolveParser(detection);
        Assert.NotNull(resolved);
        Assert.Equal("CENTRAL-v1", resolved.ParserVersion);
    }

    #endregion

    #region Synthetic Parsing Logic Tests

    [Fact]
    public void CentralBank_Parser_ExtractsSingleAndMultiLineTransactions()
    {
        var extraction = BuildSyntheticCentralBankExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 30.0, [
                    ("Value", 45.0), ("Post", 90.0), ("Details", 140.0), ("Chq.No.", 250.0),
                    ("Debit", 325.0), ("Credit", 420.0), ("Balance", 490.0)
                ]),
                CreateRow(1, 45.0, [
                    ("BROUGHT", 122.0), ("FORWARD", 152.0), (":", 182.0), ("7,29,360.87Dr", 486.0)
                ]),
                // Tx 1: Single line debit
                CreateRow(1, 60.0, [
                    ("01/04/25", 40.0), ("01/04/25", 85.0), ("PROCESSING", 130.0), ("CHGS", 175.0),
                    ("2,500.00", 320.0), ("7,31,860.87Dr", 486.0)
                ]),
                // Tx 2: Multi-line narration debit
                CreateRow(1, 75.0, [
                    ("04/04/25", 40.0), ("04/04/25", 85.0), ("TO", 130.0), ("TRF.", 145.0), ("NeSL", 165.0),
                    ("147.50", 330.0), ("7,32,008.37Dr", 486.0)
                ]),
                CreateRow(1, 87.0, [
                    ("Anniversary", 130.0), ("Charges", 170.0), ("TRF", 205.0), ("TO", 225.0), ("60317035722", 235.0)
                ]),
                // Rate change row: should be ignored!
                CreateRow(1, 100.0, [
                    ("01/05/25", 40.0), ("01/05/25", 85.0), ("BS", 130.0), ("RT", 145.0), ("CHG", 160.0), ("10.300", 180.0)
                ]),
                // Tx 3: Single line credit with cheque
                CreateRow(1, 115.0, [
                    ("25/04/25", 40.0), ("25/04/25", 85.0), ("BY", 130.0), ("CLG.", 145.0),
                    ("000278", 250.0), ("4,16,992.00", 405.0), ("3,15,016.37Dr", 486.0)
                ]),
                // CARRIED FORWARD and Statement Summary: should be ignored!
                CreateRow(1, 130.0, [
                    ("CARRIED", 122.0), ("FORWARD", 152.0), (":", 182.0), ("3,15,016.37Dr", 486.0)
                ]),
                CreateRow(1, 145.0, [
                    ("Statement", 40.0), ("Summary", 75.0), ("Dr.", 115.0), ("Count", 130.0), ("02", 150.0),
                    ("Cr.", 165.0), ("Count", 180.0), ("01", 200.0)
                ])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = new BankDetectionResult { DetectedBank = BankType.CentralBank, IsSupported = true };
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Equal(3, result.Transactions.Count);

        // Tx 1
        var tx1 = result.Transactions[0];
        Assert.Equal(new DateTime(2025, 4, 1), tx1.TransactionDate);
        Assert.Equal("PROCESSING CHGS", tx1.Description);
        Assert.Equal(2500.00m, tx1.Debit);
        Assert.Null(tx1.Credit);
        Assert.Equal(-731860.87m, tx1.Balance);
        Assert.Equal("Debit", tx1.TransactionType);

        // Tx 2 (multi-line narration)
        var tx2 = result.Transactions[1];
        Assert.Equal(new DateTime(2025, 4, 4), tx2.TransactionDate);
        Assert.Contains("NeSL Anniversary Charges TRF TO 60317035722", tx2.Description);
        Assert.Equal(147.50m, tx2.Debit);
        Assert.Equal(-732008.37m, tx2.Balance);

        // Tx 3 (credit + cheque)
        var tx3 = result.Transactions[2];
        Assert.Equal(new DateTime(2025, 4, 25), tx3.TransactionDate);
        Assert.Equal(416992.00m, tx3.Credit);
        Assert.Null(tx3.Debit);
        Assert.Equal("000278", tx3.Reference);
        Assert.Equal("Credit", tx3.TransactionType);
        Assert.Equal(-315016.37m, tx3.Balance);
    }

    [Fact]
    public void CentralBank_Parser_HandlesZeroDebitChequeReturn()
    {
        var extraction = BuildSyntheticCentralBankExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 30.0, [
                    ("07/01/26", 40.0), ("07/01/26", 85.0), ("OUT-CHQ", 130.0), ("RETURN", 170.0),
                    ("Funds", 195.0), ("insufficient", 215.0),
                    ("000089", 250.0), ("0.00", 335.0), ("7,92,809.15Dr", 486.0)
                ])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = new BankDetectionResult { DetectedBank = BankType.CentralBank, IsSupported = true };
        var result = _parser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Single(result.Transactions);

        var tx = result.Transactions[0];
        Assert.Equal(0.00m, tx.Debit);
        Assert.Equal(0.00m, tx.Amount);
        Assert.Equal("Debit", tx.TransactionType);
        Assert.Equal("000089", tx.Reference);
        Assert.Contains("OUT-CHQ RETURN Funds insufficient", tx.Description);
        Assert.Equal(-792809.15m, tx.Balance);
    }

    #endregion

    #region Real PDF Fixture Integration Tests

    [Fact]
    public void RealPdf_CentralBank_ExtractsExact132Transactions()
    {
        string fixturePath = @"E:\Bank Statements\CENTRAL BANK OF INDIA-6804.pdf";
        if (!File.Exists(fixturePath))
        {
            _output.WriteLine($"[SKIPPED] Real Central Bank fixture not found at: {fixturePath}");
            return;
        }

        using var doc = PdfDocument.Open(fixturePath);
        Assert.Equal(4, doc.NumberOfPages);

        // Convert PdfPig document into PdfExtractionResult using standard PdfExtractionService geometry
        var extractionPages = new List<PdfPageResult>(doc.NumberOfPages);
        for (int p = 1; p <= doc.NumberOfPages; p++)
        {
            var pdfPage = doc.GetPage(p);
            var words = pdfPage.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();

            var lines = GroupWordsIntoLines(words, pdfPage.Height);
            var candidateRows = new List<PdfCandidateRow>(lines.Count);
            int rIdx = 1;

            foreach (var line in lines)
            {
                var frags = line.Words.Select((w, fIdx) => new PdfTextBlock
                {
                    Text = w.Text,
                    X = Math.Round(w.BoundingBox.Left, 2),
                    Y = Math.Round(pdfPage.Height - w.BoundingBox.Top, 2),
                    Width = Math.Round(w.BoundingBox.Width, 2),
                    Height = Math.Round(w.BoundingBox.Height, 2),
                    PageNumber = p,
                    ReadingOrderIndex = fIdx + 1
                }).ToList();

                var lineTop = frags.Min(f => f.Y);
                var lineBottom = frags.Max(f => f.Y + f.Height);

                candidateRows.Add(new PdfCandidateRow
                {
                    RowIndex = rIdx++,
                    PageNumber = p,
                    Y = Math.Round(lineTop, 2),
                    Height = Math.Round(lineBottom - lineTop, 2),
                    RawLineText = string.Join(" ", frags.Select(f => f.Text)),
                    LineFragments = frags
                });
            }

            extractionPages.Add(new PdfPageResult
            {
                PageNumber = p,
                Width = pdfPage.Width,
                Height = pdfPage.Height,
                HasUsableText = words.Count > 0,
                WordCount = words.Count,
                CandidateRows = candidateRows,
                RawText = string.Join("\n", candidateRows.Select(r => r.RawLineText))
            });
        }

        var extraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            OriginalFileName = Path.GetFileName(fixturePath),
            PageCount = doc.NumberOfPages,
            ExtractionStatus = "DigitalTextExtracted",
            HasUsableText = true,
            PdfType = "DigitalWithText",
            Pages = extractionPages
        };

        // 1. Verify Bank Detection
        var detection = _detector.DetectBank(extraction);
        Assert.NotNull(detection);
        Assert.Equal(BankType.CentralBank, detection.DetectedBank);
        Assert.Equal("Central Bank of India", detection.BankName);
        Assert.True(detection.IsSupported);
        Assert.Equal("3552886804", detection.AccountNumber);
        Assert.Equal("BHARAT TRADERS", detection.CustomerName);
        Assert.Equal(new DateTime(2025, 4, 1), detection.StatementFrom);
        Assert.Equal(new DateTime(2026, 3, 31), detection.StatementTo);

        // 2. Resolve Parser
        var resolvedParser = _registry.ResolveParser(detection);
        Assert.NotNull(resolvedParser);
        Assert.Equal("CENTRAL-v1", resolvedParser.ParserVersion);

        // 3. Parse Statement
        var parseResult = resolvedParser.Parse(extraction, detection);
        Assert.NotNull(parseResult);
        Assert.True(parseResult.Success);
        Assert.Null(parseResult.ErrorMessage);

        var txs = parseResult.Transactions;

        // 4. Verify Exact Transaction Counts:
        // Expected ground truth from 8 Statement Summaries: 101 Debits, 31 Credits = 132 Total
        int debitCount = txs.Count(t => t.TransactionType == "Debit");
        int creditCount = txs.Count(t => t.TransactionType == "Credit");

        _output.WriteLine($"[RESULT] Total Extracted: {txs.Count} (Debits: {debitCount}, Credits: {creditCount})");

        Assert.Equal(132, txs.Count);
        Assert.Equal(101, debitCount);
        Assert.Equal(31, creditCount);

        // 5. Verify Debit and Credit Sums down to the exact paisa
        decimal totalDebit = txs.Where(t => t.Debit.HasValue).Sum(t => t.Debit!.Value);
        decimal totalCredit = txs.Where(t => t.Credit.HasValue).Sum(t => t.Credit!.Value);

        _output.WriteLine($"[RESULT] Total Debit Sum: {totalDebit:F2} (Expected: 2734771.01)");
        _output.WriteLine($"[RESULT] Total Credit Sum: {totalCredit:F2} (Expected: 2675592.11)");

        Assert.Equal(2734771.01m, totalDebit);
        Assert.Equal(2675592.11m, totalCredit);

        // 6. Verify First Transaction
        var firstTx = txs[0];
        Assert.Equal(new DateTime(2025, 4, 1), firstTx.TransactionDate);
        Assert.Equal(new DateTime(2025, 4, 1), firstTx.ValueDate);
        Assert.Equal("PROCESSING CHGS", firstTx.Description);
        Assert.Equal(2500.00m, firstTx.Debit);
        Assert.Null(firstTx.Credit);
        Assert.Equal(2500.00m, firstTx.Amount);
        Assert.Equal(-731860.87m, firstTx.Balance);
        Assert.Equal("Debit", firstTx.TransactionType);

        // 7. Verify Last Transaction
        var lastTx = txs[^1];
        Assert.Equal(new DateTime(2026, 3, 31), lastTx.TransactionDate);
        Assert.Equal(new DateTime(2026, 3, 31), lastTx.ValueDate);
        Assert.Equal("GST", lastTx.Description);
        Assert.Equal(18.00m, lastTx.Debit);
        Assert.Null(lastTx.Credit);
        Assert.Equal(18.00m, lastTx.Amount);
        Assert.Equal(-788539.77m, lastTx.Balance);
        Assert.Equal("Debit", lastTx.TransactionType);

        // 8. Verify Zero Balance Mismatches Across All 132 Transactions
        decimal openingBalance = -729360.87m; // 7,29,360.87 Dr
        decimal runningBalance = openingBalance;
        int balanceErrors = 0;

        for (int i = 0; i < txs.Count; i++)
        {
            var tx = txs[i];
            decimal dVal = tx.Debit ?? 0m;
            decimal cVal = tx.Credit ?? 0m;

            runningBalance = runningBalance - dVal + cVal;

            if (tx.Balance.HasValue)
            {
                if (Math.Abs(runningBalance - tx.Balance.Value) > 0.01m)
                {
                    balanceErrors++;
                    _output.WriteLine($"[BALANCE ERROR @ Tx {i + 1}] Expected: {runningBalance:F2}, Actual: {tx.Balance.Value:F2}");
                }
            }
        }

        Assert.Equal(0, balanceErrors);
        Assert.Equal(-788539.77m, runningBalance);

        // 9. Verify Cross-Page Narration Joining
        // Tx #16 on subpage 1 starts with "TO REMIT" and continues after "BROUGHT FORWARD" with "NEFTYAMBUJA CEMENTS"
        var splitTx = txs.FirstOrDefault(t => t.Reference == "004999");
        Assert.NotNull(splitTx);
        Assert.Contains("TO REMIT", splitTx.Description);
        Assert.Contains("NEFTYAMBUJA CEMENTS", splitTx.Description);
        Assert.Equal(100000.00m, splitTx.Debit);
        Assert.Equal(-966988.17m, splitTx.Balance);

        // 10. Verify Zero Duplicate Transactions
        var distinctDescriptionsAndBalances = txs
            .Select(t => $"{t.TransactionDate:yyyy-MM-dd}_{t.Description}_{t.Amount}_{t.Balance}")
            .Distinct()
            .Count();
        Assert.Equal(132, distinctDescriptionsAndBalances);
    }

    #endregion

    #region Helper Methods for Line Grouping

    private static List<VisualLine> GroupWordsIntoLines(List<UglyToad.PdfPig.Content.Word> words, double pageHeight)
    {
        if (words.Count == 0) return [];

        var projectedWords = words.Select(w => new
        {
            Word = w,
            Left = w.BoundingBox.Left,
            Top = pageHeight - w.BoundingBox.Top,
            Bottom = pageHeight - w.BoundingBox.Bottom,
            Height = Math.Max(w.BoundingBox.Height, 6.0),
            CenterY = pageHeight - (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2.0
        })
        .OrderBy(w => w.CenterY)
        .ThenBy(w => w.Left)
        .ToList();

        var lines = new List<VisualLine>();

        foreach (var pw in projectedWords)
        {
            VisualLine? matchingLine = null;
            foreach (var line in lines)
            {
                var verticalTolerance = Math.Max(line.AverageHeight, pw.Height) * 0.45;
                if (Math.Abs(pw.CenterY - line.CenterY) <= verticalTolerance)
                {
                    matchingLine = line;
                    break;
                }
            }

            if (matchingLine != null)
            {
                matchingLine.Add(pw.Word, pw.Left, pw.Top, pw.Bottom, pw.Height);
            }
            else
            {
                var newLine = new VisualLine();
                newLine.Add(pw.Word, pw.Left, pw.Top, pw.Bottom, pw.Height);
                lines.Add(newLine);
            }
        }

        return lines
            .OrderBy(l => l.Top)
            .Select(l =>
            {
                l.SortWordsLeftToRight();
                return l;
            })
            .ToList();
    }

    private sealed class VisualLine
    {
        public List<UglyToad.PdfPig.Content.Word> Words { get; } = [];
        public double Top { get; private set; } = double.MaxValue;
        public double Bottom { get; private set; } = double.MinValue;
        private double _heightSum;

        public double CenterY => (Top + Bottom) / 2.0;
        public double AverageHeight => Words.Count > 0 ? _heightSum / Words.Count : 10.0;

        public void Add(UglyToad.PdfPig.Content.Word word, double left, double top, double bottom, double height)
        {
            Words.Add(word);
            if (top < Top) Top = top;
            if (bottom > Bottom) Bottom = bottom;
            _heightSum += height;
        }

        public void SortWordsLeftToRight()
        {
            Words.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));
        }
    }

    #endregion
}
