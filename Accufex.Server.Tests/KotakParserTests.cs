using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Accufex.Server.DTOs;
using Accufex.Server.Export.Services;
using Accufex.Server.Models;
using Accufex.Server.Parsing;
using Accufex.Server.Parsing.Interfaces;
using Accufex.Server.Parsing.Models;
using Accufex.Server.Parsing.Parsers;
using Accufex.Server.Validation.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;
using Xunit;
using Xunit.Abstractions;

namespace Accufex.Server.Tests;

public class KotakParserTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;
    private readonly BankDetector _detector = new(NullLogger<BankDetector>.Instance);
    private readonly KotakStatementParser _kotakParser = new(NullLogger<KotakStatementParser>.Instance);
    private readonly BankParserRegistry _registry;
    private readonly ExcelExportService _exportService = new(NullLogger<ExcelExportService>.Instance);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string FixturePdfPath = @"E:\Bank Statements\KOTAK-811.pdf";
    private const string FixtureCsvPath = @"D:\Bank Statements\EXCEL\2022.csv";

    public KotakParserTests(CustomWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
        _registry = new BankParserRegistry([_kotakParser]);
    }

    private static PdfExtractionResult BuildSyntheticKotakExtraction(Action<List<PdfPageResult>> configurePages)
    {
        var pages = new List<PdfPageResult>();
        configurePages(pages);

        return new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            OriginalFileName = "Kotak_811_Statement.pdf",
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
            Width = Math.Max(t.Text.Length * 5.0, 5.0),
            Height = 10.0,
            PageNumber = pageNumber,
            ReadingOrderIndex = i + 1
        }).ToList();

        return new PdfCandidateRow
        {
            RowIndex = (int)(y / 10),
            PageNumber = pageNumber,
            Y = y,
            Height = 12.0,
            RawLineText = string.Join(" ", tokens.Select(t => t.Text)),
            LineFragments = frags,
            Cells = frags.Select((f, idx) => new PdfCandidateCell
            {
                ColumnIndex = idx,
                Text = f.Text,
                BoundingBox = new PdfBoundingBox(f.X, f.Y, f.Width, f.Height)
            }).ToList()
        };
    }

    /// <summary>
    /// Builds an extraction result matching the real Kotak 811 2022 dataset
    /// (91 transactions, Opening 0.00, Closing 42.80, Debits 9,620.20, Credits 9,663.00).
    /// </summary>
    private static PdfExtractionResult BuildGroundTruth2022Extraction()
    {
        var rows = new List<PdfCandidateRow>();
        double y = 40.0;

        // Header and metadata
        rows.Add(CreateRow(1, y, [
            ("Kotak", 40.0), ("Mahindra", 75.0), ("Bank", 130.0)
        ]));
        y += 15.0;

        rows.Add(CreateRow(1, y, [
            ("Kotak", 40.0), ("811", 75.0), ("Account", 95.0), ("Statement", 145.0)
        ]));
        y += 15.0;

        rows.Add(CreateRow(1, y, [
            ("Customer", 40.0), ("Name", 90.0), (":", 120.0), ("REHAN", 130.0), ("HARUN", 175.0), ("SHEIKH", 220.0)
        ]));
        y += 15.0;

        rows.Add(CreateRow(1, y, [
            ("Account", 40.0), ("Number", 85.0), (":", 130.0), ("6245778582", 140.0),
            ("CRN", 250.0), (":", 280.0), ("109876543", 290.0)
        ]));
        y += 15.0;

        rows.Add(CreateRow(1, y, [
            ("IFSC", 40.0), ("Code", 70.0), (":", 100.0), ("KKBK0000958", 110.0),
            ("Period", 250.0), (":", 290.0), ("01/01/2022", 300.0), ("to", 360.0), ("31/12/2022", 380.0)
        ]));
        y += 15.0;

        rows.Add(CreateRow(1, y, [
            ("kotak.com", 40.0), ("Registered", 120.0), ("Office:", 180.0), ("27", 220.0), ("BKC,", 235.0), ("Bandra", 260.0)
        ]));
        y += 20.0;

        // Table Header
        rows.Add(CreateRow(1, y, [
            ("#", 40.0),
            ("Date", 65.0),
            ("Description", 135.0),
            ("Chq/Ref.", 285.0), ("No.", 330.0),
            ("Withdrawal", 390.0), ("(Dr.)", 445.0),
            ("Deposit", 485.0), ("(Cr.)", 525.0),
            ("Balance", 570.0)
        ]));
        y += 15.0;

        // Opening Balance row
        rows.Add(CreateRow(1, y, [
            ("-", 40.0), ("-", 65.0), ("Opening", 135.0), ("Balance", 185.0), ("-", 285.0), ("-", 390.0), ("-", 485.0), ("0.00", 570.0)
        ]));
        y += 15.0;

        // If CSV exists, populate all 91 real transactions from CSV; otherwise populate exact representative sample
        if (File.Exists(FixtureCsvPath))
        {
            var lines = File.ReadAllLines(FixtureCsvPath);
            int pageNum = 1;

            // Skip header and Opening Balance row (lines 0 and 1)
            for (int i = 2; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parts = ParseCsvRow(line);
                if (parts.Count < 7) continue;

                var sno = parts[0].Trim();
                var dateStr = parts[1].Trim();
                var desc = parts[2].Trim();
                var refNo = parts[3].Trim();
                var dr = parts[4].Trim();
                var cr = parts[5].Trim();
                var bal = parts[6].Trim();

                if (y > 780.0)
                {
                    // Next page
                    pageNum++;
                    y = 40.0;
                }

                var tokens = new List<(string Text, double X)>
                {
                    (sno, 40.0),
                    (dateStr, 65.0),
                    (desc, 135.0)
                };

                if (!string.IsNullOrWhiteSpace(refNo))
                {
                    tokens.Add((refNo, 285.0));
                }

                if (!string.IsNullOrWhiteSpace(dr) && dr != "-")
                {
                    tokens.Add((dr, 400.0));
                }

                if (!string.IsNullOrWhiteSpace(cr) && cr != "-")
                {
                    tokens.Add((cr, 490.0));
                }

                if (!string.IsNullOrWhiteSpace(bal))
                {
                    tokens.Add((bal, 570.0));
                }

                rows.Add(CreateRow(pageNum, y, tokens));
                y += 14.0;
            }
        }
        else
        {
            // Synthetic ground truth rows
            rows.Add(CreateRow(1, y, [
                ("1", 40.0), ("05 Oct 2022", 65.0), ("UPI/FAIZAN SHEIKH A/227860351839/UPI", 135.0),
                ("UPI-227899615102", 285.0), ("1.00", 490.0), ("1.00", 570.0)
            ]));
            y += 15.0;

            rows.Add(CreateRow(1, y, [
                ("2", 40.0), ("05 Oct 2022", 65.0), ("UPI/RAZORPAY/PAY_TEST/UPI", 135.0),
                ("UPI-227891398917", 285.0), ("0.50", 400.0), ("0.50", 570.0)
            ]));
            y += 15.0;

            rows.Add(CreateRow(1, y, [
                ("3", 40.0), ("31 Dec 2022", 65.0), ("Int.Pd:6245778582:01-10-2022 to 31-12-2022", 135.0),
                ("1.00", 490.0), ("1.50", 570.0)
            ]));
        }

        var pages = rows.GroupBy(r => r.PageNumber)
            .Select(g => CreatePage(g.Key, g.ToList()))
            .ToList();

        return new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            OriginalFileName = "KOTAK-811.pdf",
            PageCount = pages.Count,
            ExtractionStatus = "DigitalTextExtracted",
            HasUsableText = true,
            PdfType = "DigitalWithText",
            Pages = pages
        };
    }

    private static List<string> ParseCsvRow(string line)
    {
        var result = new List<string>();
        bool inQuotes = false;
        var current = new System.Text.StringBuilder();

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '\"')
                {
                    current.Append('\"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString());
        return result;
    }

    #region Test 01: Bank Detection

    [Fact]
    public void Kotak_Detector_IdentifiesKotakBank()
    {
        var extraction = BuildSyntheticKotakExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 30.0, [("Kotak", 40.0), ("Mahindra", 80.0), ("Bank", 135.0)]),
                CreateRow(1, 45.0, [("Kotak", 40.0), ("811", 80.0), ("Account", 110.0), ("Statement", 160.0)]),
                CreateRow(1, 60.0, [("Customer", 40.0), ("Name", 95.0), (":", 125.0), ("REHAN", 135.0), ("HARUN", 180.0), ("SHEIKH", 225.0)]),
                CreateRow(1, 75.0, [("Account", 40.0), ("Number", 85.0), (":", 130.0), ("6245778582", 140.0)]),
                CreateRow(1, 90.0, [("IFSC", 40.0), ("Code", 70.0), (":", 100.0), ("KKBK0000958", 110.0)]),
                CreateRow(1, 105.0, [("Period", 40.0), (":", 80.0), ("01/01/2022", 90.0), ("to", 155.0), ("31/12/2022", 175.0)]),
                CreateRow(1, 120.0, [("kotak.com", 40.0), ("Registered", 120.0), ("Office", 180.0), ("27", 220.0), ("BKC", 240.0)]),
                CreateRow(1, 140.0, [
                    ("#", 40.0), ("Date", 65.0), ("Description", 135.0), ("Chq/Ref.", 285.0), ("No.", 330.0),
                    ("Withdrawal", 390.0), ("(Dr.)", 445.0), ("Deposit", 485.0), ("(Cr.)", 525.0), ("Balance", 570.0)
                ])
            };
            pages.Add(CreatePage(1, rows));
        });

        var result = _detector.DetectBank(extraction);

        Assert.NotNull(result);
        Assert.Equal(BankType.Kotak, result.DetectedBank);
        Assert.Equal("Kotak Mahindra Bank", result.BankName);
        Assert.Equal("KOTAK-v1", result.DetectedFormat);
        Assert.True(result.IsSupported);
        Assert.True(result.Confidence >= 0.90, $"Confidence should be >= 0.90, was {result.Confidence}");
        Assert.Equal("6245778582", result.AccountNumber);
        Assert.Equal("REHAN HARUN SHEIKH", result.CustomerName);
        Assert.Equal(new DateTime(2022, 1, 1), result.StatementFrom);
        Assert.Equal(new DateTime(2022, 12, 31), result.StatementTo);
    }

    #endregion

    #region Test 02: Disambiguation (Does Not Misclassify Other Banks)

    [Fact]
    public void Kotak_Detector_DoesNotMisclassifyExistingBanks()
    {
        // 1. HDFC
        var hdfc = new PdfExtractionResult
        {
            Pages = [new PdfPageResult { PageNumber = 1, RawText = "HDFC BANK LIMITED\nIFSC: HDFC0000060\nDate Narration Chq/Ref.No Value Dt Withdrawal Amt. Deposit Amt. Closing Balance" }]
        };
        Assert.NotEqual(BankType.Kotak, _detector.DetectBank(hdfc).DetectedBank);

        // 2. YES BANK
        var yes = new PdfExtractionResult
        {
            Pages = [new PdfPageResult { PageNumber = 1, RawText = "YES BANK LIMITED\nIFSC: YESB0000001\nTxn Date Value Date Description Chq / Ref No. Debit Credit Balance" }]
        };
        Assert.NotEqual(BankType.Kotak, _detector.DetectBank(yes).DetectedBank);

        // 3. Axis Bank
        var axis = new PdfExtractionResult
        {
            Pages = [new PdfPageResult { PageNumber = 1, RawText = "AXIS BANK LTD\nIFSC: UTIB0000001\nS.NO Transaction Date Value Date Particulars Amount(INR) Debit/Credit Balance(INR)" }]
        };
        Assert.NotEqual(BankType.Kotak, _detector.DetectBank(axis).DetectedBank);

        // 4. Central Bank of India
        var cb = new PdfExtractionResult
        {
            Pages = [new PdfPageResult { PageNumber = 1, RawText = "CENTRAL BANK OF INDIA\nIFSC: CBIN0280624\ncentralbankofindia.co.in\nPOST DATE VALUE DATE DESCRIPTION CHQ NO DEBIT CREDIT BALANCE" }]
        };
        Assert.NotEqual(BankType.Kotak, _detector.DetectBank(cb).DetectedBank);

        // 5. ICICI Bank
        var icici = new PdfExtractionResult
        {
            Pages = [new PdfPageResult { PageNumber = 1, RawText = "ICICI BANK\nIFSC: ICIC0000001\nDATE PARTICULARS CHQ/REF NO DEPOSITS WITHDRAWALS BALANCE" }]
        };
        Assert.NotEqual(BankType.Kotak, _detector.DetectBank(icici).DetectedBank);

        // 6. SBI
        var sbi = new PdfExtractionResult
        {
            Pages = [new PdfPageResult { PageNumber = 1, RawText = "STATE BANK OF INDIA\nIFSC: SBIN0000001\nPost Date Value Date Description Cheque No/Reference No Debit Credit Balance" }]
        };
        Assert.NotEqual(BankType.Kotak, _detector.DetectBank(sbi).DetectedBank);

        // 7. Bank of India
        var boi = new PdfExtractionResult
        {
            Pages = [new PdfPageResult { PageNumber = 1, RawText = "BANK OF INDIA\nIFSC: BKID0001234\nSNO TRAN DATE INST NO DESCRIPTION DEBITS CREDITS BALANCE" }]
        };
        Assert.NotEqual(BankType.Kotak, _detector.DetectBank(boi).DetectedBank);
    }

    #endregion

    #region Test 03: Parser Version & Registry Resolution

    [Fact]
    public void Kotak_Parser_ResolvesVersion_KOTAKv1()
    {
        Assert.Equal(8, _kotakParser.BankCode);
        Assert.Equal((int)BankType.Kotak, _kotakParser.BankCode);
        Assert.Equal("Kotak Mahindra Bank", _kotakParser.BankName);
        Assert.Equal("KOTAK-v1", _kotakParser.ParserVersion);

        var detection = new BankDetectionResult
        {
            DetectedBank = BankType.Kotak,
            BankName = "Kotak Mahindra Bank",
            IsSupported = true,
            Confidence = 0.95,
            DetectedFormat = "KOTAK-v1"
        };

        Assert.True(_kotakParser.CanParse(detection));
        var resolved = _registry.ResolveParser(detection);
        Assert.NotNull(resolved);
        Assert.Equal("KOTAK-v1", resolved.ParserVersion);
        Assert.Equal(8, resolved.BankCode);
    }

    #endregion

    #region Test 04: Real / Ground-Truth Extraction Verification

    [Fact]
    public void Kotak_Parser_RealPdf_ExtractsGroundTruth()
    {
        var extraction = BuildGroundTruth2022Extraction();
        var detection = _detector.DetectBank(extraction);

        var parseResult = _kotakParser.Parse(extraction, detection);

        Assert.True(parseResult.Success, $"Parsing should succeed: {parseResult.ErrorMessage}");
        Assert.NotEmpty(parseResult.Transactions);

        if (File.Exists(FixtureCsvPath))
        {
            // Exact ground-truth reconciliation for 2022 dataset: 91 transactions
            Assert.Equal(91, parseResult.Transactions.Count);

            var debits = parseResult.Transactions.Where(t => t.Debit.HasValue && t.Debit.Value > 0).ToList();
            var credits = parseResult.Transactions.Where(t => t.Credit.HasValue && t.Credit.Value > 0).ToList();

            Assert.Equal(50, debits.Count);
            Assert.Equal(41, credits.Count);

            Assert.Equal(9620.20m, debits.Sum(t => t.Debit!.Value));
            Assert.Equal(9663.00m, credits.Sum(t => t.Credit!.Value));

            // Closing balance of last transaction
            Assert.Equal(42.80m, parseResult.Transactions.Last().Balance!.Value);

            // Zero transactions with both debit and credit
            Assert.DoesNotContain(parseResult.Transactions, t => t.Debit.HasValue && t.Credit.HasValue);
        }
        else
        {
            Assert.True(parseResult.Transactions.Count >= 3);
        }
    }

    #endregion

    #region Test 05: Balance Continuity (Zero Mismatches)

    [Fact]
    public void Kotak_Parser_BalanceContinuity_ZeroMismatches()
    {
        var extraction = BuildGroundTruth2022Extraction();
        var detection = _detector.DetectBank(extraction);

        var parseResult = _kotakParser.Parse(extraction, detection);

        Assert.True(parseResult.Success);
        Assert.Equal(0, parseResult.Warnings.Count(w => w.Contains("running balance mismatch", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(parseResult.Transactions, t => t.NeedsReview);

        // Verify mathematical formula: Previous Balance - Debit + Credit = Current Balance
        decimal running = 0.00m; // Opening balance
        int mismatches = 0;

        foreach (var tx in parseResult.Transactions)
        {
            decimal expected = running - (tx.Debit ?? 0m) + (tx.Credit ?? 0m);
            if (tx.Balance.HasValue && Math.Abs(expected - tx.Balance.Value) > 0.01m)
            {
                mismatches++;
            }
            running = tx.Balance ?? expected;
        }

        Assert.Equal(0, mismatches);
    }

    #endregion

    #region Test 06: Multi-Line Narration Reconstruction

    [Fact]
    public void Kotak_Parser_MultiLineNarration_ReconstructedCorrectly()
    {
        var extraction = BuildSyntheticKotakExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 30.0, [("Kotak", 40.0), ("Mahindra", 80.0), ("Bank", 135.0)]),
                CreateRow(1, 50.0, [
                    ("#", 40.0), ("Date", 65.0), ("Description", 135.0), ("Chq/Ref.", 285.0), ("No.", 330.0),
                    ("Withdrawal", 390.0), ("(Dr.)", 445.0), ("Deposit", 485.0), ("(Cr.)", 525.0), ("Balance", 570.0)
                ]),
                // Tx 1: Anchor row
                CreateRow(1, 70.0, [
                    ("1", 40.0), ("15 Nov 2022", 65.0), ("UPI/BHARATPE MERCHANT", 135.0),
                    ("UPI-227891398917", 285.0), ("500.00", 400.0), ("1,500.00", 570.0)
                ]),
                // Continuation line 1
                CreateRow(1, 82.0, [
                    ("PAYMENT TO STORE 4092", 135.0)
                ]),
                // Continuation line 2
                CreateRow(1, 94.0, [
                    ("BANGALORE KARNATAKA", 135.0)
                ]),
                // Tx 2: Next anchor row
                CreateRow(1, 110.0, [
                    ("2", 40.0), ("16 Nov 2022", 65.0), ("MB:IMPS-OUT/229011827361/TRANSFER", 135.0),
                    ("MB-229011827361", 285.0), ("200.00", 400.0), ("1,300.00", 570.0)
                ])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parseResult = _kotakParser.Parse(extraction, detection);

        Assert.True(parseResult.Success);
        Assert.Equal(2, parseResult.Transactions.Count);

        var tx1 = parseResult.Transactions[0];
        Assert.Equal("UPI/BHARATPE MERCHANT PAYMENT TO STORE 4092 BANGALORE KARNATAKA", tx1.Description);
        Assert.Equal(500.00m, tx1.Debit);
        Assert.Equal(1500.00m, tx1.Balance);

        var tx2 = parseResult.Transactions[1];
        Assert.Equal("MB:IMPS-OUT/229011827361/TRANSFER", tx2.Description);
        Assert.Equal(200.00m, tx2.Debit);
        Assert.Equal(1300.00m, tx2.Balance);
    }

    #endregion

    #region Test 07: Multi-Page Statement (No Duplicate Headers / Phantom Rows)

    [Fact]
    public void Kotak_Parser_MultiPageStatement_NoDuplicates()
    {
        var extraction = BuildSyntheticKotakExtraction(pages =>
        {
            // Page 1
            var page1Rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 30.0, [("Kotak", 40.0), ("Mahindra", 80.0), ("Bank", 135.0)]),
                CreateRow(1, 45.0, [("Account", 40.0), ("Number", 85.0), (":", 130.0), ("6245778582", 140.0)]),
                CreateRow(1, 60.0, [
                    ("#", 40.0), ("Date", 65.0), ("Description", 135.0), ("Chq/Ref.", 285.0), ("No.", 330.0),
                    ("Withdrawal", 390.0), ("(Dr.)", 445.0), ("Deposit", 485.0), ("(Cr.)", 525.0), ("Balance", 570.0)
                ]),
                CreateRow(1, 80.0, [
                    ("1", 40.0), ("10 Oct 2022", 65.0), ("UPI/SHOPPING/123", 135.0),
                    ("UPI-1111", 285.0), ("100.00", 400.0), ("900.00", 570.0)
                ]),
                CreateRow(1, 100.0, [
                    ("Page", 40.0), ("1", 70.0), ("of", 80.0), ("2", 95.0),
                    ("Registered", 150.0), ("Office", 210.0), ("27", 250.0), ("BKC", 270.0)
                ])
            };
            pages.Add(CreatePage(1, page1Rows));

            // Page 2 (Repeated headers)
            var page2Rows = new List<PdfCandidateRow>
            {
                CreateRow(2, 30.0, [("Kotak", 40.0), ("Mahindra", 80.0), ("Bank", 135.0)]),
                CreateRow(2, 45.0, [("Account", 40.0), ("Number", 85.0), (":", 130.0), ("6245778582", 140.0)]),
                CreateRow(2, 60.0, [
                    ("#", 40.0), ("Date", 65.0), ("Description", 135.0), ("Chq/Ref.", 285.0), ("No.", 330.0),
                    ("Withdrawal", 390.0), ("(Dr.)", 445.0), ("Deposit", 485.0), ("(Cr.)", 525.0), ("Balance", 570.0)
                ]),
                CreateRow(2, 80.0, [
                    ("2", 40.0), ("12 Oct 2022", 65.0), ("UPI/SALARY/456", 135.0),
                    ("UPI-2222", 285.0), ("2,000.00", 490.0), ("2,900.00", 570.0)
                ]),
                CreateRow(2, 100.0, [
                    ("Page", 40.0), ("2", 70.0), ("of", 80.0), ("2", 95.0)
                ])
            };
            pages.Add(CreatePage(2, page2Rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parseResult = _kotakParser.Parse(extraction, detection);

        Assert.True(parseResult.Success);
        Assert.Equal(2, parseResult.Transactions.Count);

        Assert.Equal(1, parseResult.Transactions[0].SourcePageNumber);
        Assert.Equal(2, parseResult.Transactions[1].SourcePageNumber);

        // Header text must not appear in descriptions
        Assert.DoesNotContain(parseResult.Transactions, t => t.Description.Contains("Registered Office", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(parseResult.Transactions, t => t.Description.Contains("27 BKC", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Test 08: Reference and UTR Extraction

    [Fact]
    public void Kotak_Parser_ExtractsReferences()
    {
        var extraction = BuildSyntheticKotakExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 30.0, [("Kotak", 40.0), ("Mahindra", 80.0), ("Bank", 135.0)]),
                CreateRow(1, 50.0, [
                    ("#", 40.0), ("Date", 65.0), ("Description", 135.0), ("Chq/Ref.", 285.0), ("No.", 330.0),
                    ("Withdrawal", 390.0), ("(Dr.)", 445.0), ("Deposit", 485.0), ("(Cr.)", 525.0), ("Balance", 570.0)
                ]),
                // Tx 1: UPI Ref
                CreateRow(1, 70.0, [
                    ("1", 40.0), ("01 Oct 2022", 65.0), ("UPI/MERCHANT/227899615102", 135.0),
                    ("UPI-227899615102", 285.0), ("100.00", 400.0), ("900.00", 570.0)
                ]),
                // Tx 2: MB Ref
                CreateRow(1, 90.0, [
                    ("2", 40.0), ("02 Oct 2022", 65.0), ("MB:TRANSFER TO SELF", 135.0),
                    ("MB-987654321012", 285.0), ("200.00", 400.0), ("700.00", 570.0)
                ]),
                // Tx 3: IMPS Ref
                CreateRow(1, 110.0, [
                    ("3", 40.0), ("03 Oct 2022", 65.0), ("IMPS/P2A/327891398917", 135.0),
                    ("IMPS-327891398917", 285.0), ("500.00", 490.0), ("1,200.00", 570.0)
                ]),
                // Tx 4: Cheque
                CreateRow(1, 130.0, [
                    ("4", 40.0), ("04 Oct 2022", 65.0), ("CHQ DEP/CLEARING", 135.0),
                    ("000124", 285.0), ("1,000.00", 490.0), ("2,200.00", 570.0)
                ])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parseResult = _kotakParser.Parse(extraction, detection);

        Assert.True(parseResult.Success);
        Assert.Equal(4, parseResult.Transactions.Count);

        Assert.Equal("UPI-227899615102", parseResult.Transactions[0].Reference);
        Assert.Equal("MB-987654321012", parseResult.Transactions[1].Reference);
        Assert.Equal("IMPS-327891398917", parseResult.Transactions[2].Reference);
        Assert.Equal("000124", parseResult.Transactions[3].Reference);
    }

    #endregion

    #region Test 09: Amount Parsing & Indian Comma Formatting

    [Fact]
    public void Kotak_Parser_AmountParsing_IsAccurate()
    {
        var extraction = BuildSyntheticKotakExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 30.0, [("Kotak", 40.0), ("Mahindra", 80.0), ("Bank", 135.0)]),
                CreateRow(1, 50.0, [
                    ("#", 40.0), ("Date", 65.0), ("Description", 135.0), ("Chq/Ref.", 285.0), ("No.", 330.0),
                    ("Withdrawal", 390.0), ("(Dr.)", 445.0), ("Deposit", 485.0), ("(Cr.)", 525.0), ("Balance", 570.0)
                ]),
                // High value with Indian comma formatting
                CreateRow(1, 70.0, [
                    ("1", 40.0), ("01 Nov 2022", 65.0), ("INVESTMENT REDEMPTION", 135.0),
                    ("1,25,500.75", 490.0), ("1,25,500.75", 570.0)
                ]),
                CreateRow(1, 90.0, [
                    ("2", 40.0), ("02 Nov 2022", 65.0), ("PROPERTY TAX PAYMENT", 135.0),
                    ("25,500.25", 400.0), ("1,00,000.50", 570.0)
                ])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var parseResult = _kotakParser.Parse(extraction, detection);

        Assert.True(parseResult.Success);
        Assert.Equal(2, parseResult.Transactions.Count);

        var tx1 = parseResult.Transactions[0];
        Assert.Equal("Credit", tx1.TransactionType);
        Assert.Equal(125500.75m, tx1.Credit);
        Assert.Null(tx1.Debit);
        Assert.Equal(125500.75m, tx1.Amount);
        Assert.Equal(125500.75m, tx1.Balance);

        var tx2 = parseResult.Transactions[1];
        Assert.Equal("Debit", tx2.TransactionType);
        Assert.Equal(25500.25m, tx2.Debit);
        Assert.Null(tx2.Credit);
        Assert.Equal(25500.25m, tx2.Amount);
        Assert.Equal(100000.50m, tx2.Balance);
    }

    #endregion

    #region Test 10: ClosedXML Excel Export Generation

    [Fact]
    public async Task Kotak_Statement_ExportsToExcel()
    {
        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "Kotak_Statement_2022.pdf",
            UploadedAt = DateTime.UtcNow
        };

        var txReviews = new List<TransactionReviewDto>
        {
            new()
            {
                Id = Guid.NewGuid(),
                SourceLineIndex = 1,
                TransactionDate = new DateTime(2022, 10, 5),
                ValueDate = new DateTime(2022, 10, 5),
                Description = "UPI/FAIZAN SHEIKH A/227860351839/UPI",
                TransactionType = "Credit",
                Credit = 1.00m,
                Amount = 1.00m,
                Balance = 1.00m,
                Reference = "UPI-227899615102",
                BankName = "Kotak Mahindra Bank",
                ParserVersion = "KOTAK-v1",
                ValidationStatus = "Valid"
            },
            new()
            {
                Id = Guid.NewGuid(),
                SourceLineIndex = 2,
                TransactionDate = new DateTime(2022, 10, 5),
                ValueDate = new DateTime(2022, 10, 5),
                Description = "UPI/PAYMENT/123",
                TransactionType = "Debit",
                Debit = 0.50m,
                Amount = 0.50m,
                Balance = 0.50m,
                Reference = "UPI-227891398917",
                BankName = "Kotak Mahindra Bank",
                ParserVersion = "KOTAK-v1",
                ValidationStatus = "Valid"
            }
        };

        var validationService = new Accufex.Server.Validation.Services.TransactionValidationService();
        var summary = validationService.ComputeSummary(fileRecord.Id, 8, "Kotak Mahindra Bank", "KOTAK-v1", txReviews);

        var excelBytes = await _exportService.GenerateStatementWorkbookAsync(fileRecord, summary, txReviews);

        Assert.NotNull(excelBytes);
        Assert.True(excelBytes.Length > 2000, "Generated Excel byte array should be non-empty");

        using var stream = new MemoryStream(excelBytes);
        using var workbook = new XLWorkbook(stream);

        Assert.NotNull(workbook.Worksheet("Transactions"));
        Assert.NotNull(workbook.Worksheet("Statement Info"));
        Assert.NotNull(workbook.Worksheet("Summary"));

        var txWs = workbook.Worksheet("Transactions");
        Assert.Equal(3, txWs.LastRowUsed()!.RowNumber()); // 1 header + 2 transactions
        Assert.Equal("Kotak Mahindra Bank", txWs.Cell(2, 12).GetString());

        var sumWs = workbook.Worksheet("Summary");
        Assert.Equal("BALANCED", sumWs.Cell(8, 2).GetString());
    }

    #endregion

    #region Test 11: Graceful Handling of Empty Input

    [Fact]
    public void Kotak_Parser_HandlesEmptyText_Gracefully()
    {
        var extraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            OriginalFileName = "empty.pdf",
            PageCount = 1,
            ExtractionStatus = "NoTextExtracted",
            HasUsableText = false,
            Pages = [new PdfPageResult { PageNumber = 1, HasUsableText = false, CandidateRows = [] }]
        };

        var detection = new BankDetectionResult
        {
            DetectedBank = BankType.Kotak,
            BankName = "Kotak Mahindra Bank",
            DetectedFormat = "KOTAK-v1"
        };

        var result = _kotakParser.Parse(extraction, detection);

        Assert.NotNull(result);
        Assert.Empty(result.Transactions);
    }

    #endregion

    #region Ground Truth 133-Page Kotak Statement Verification

    private static readonly string[] PossibleGroundTruthPaths =
    [
        @"E:\BrandNew Day\EasyFin Tech\EasyFin_Tech\Accufex.Server\Storage\Statements\Extractions\a54ec1f64b8845a08b542f7110f6c754\2b178ff8-56fd-4632-94bb-383309c3e69b_extraction.json",
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\Accufex.Server\Storage\Statements\Extractions\a54ec1f64b8845a08b542f7110f6c754\2b178ff8-56fd-4632-94bb-383309c3e69b_extraction.json"))
    ];

    private static PdfExtractionResult? LoadGroundTruthExtraction()
    {
        foreach (var path in PossibleGroundTruthPaths)
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<PdfExtractionResult>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
        }
        return null;
    }

    [Fact]
    public void Kotak_GroundTruthStatement_FullPipelineVerification_4360Transactions()
    {
        var extraction = LoadGroundTruthExtraction();
        if (extraction == null)
        {
            _output.WriteLine("Ground-truth Kotak 133-page statement extraction file not found on disk. Skipping file test.");
            return;
        }

        // 1. Bank Detection & Metadata Extraction
        var detection = _detector.DetectBank(extraction);
        Assert.NotNull(detection);
        Assert.Equal(BankType.Kotak, detection.DetectedBank);
        Assert.Equal("Kotak Mahindra Bank", detection.BankName);
        Assert.Equal("KOTAK-v1", detection.DetectedFormat);
        Assert.True(detection.IsSupported);
        Assert.Equal("5046856741", detection.AccountNumber);
        Assert.Equal("SAYYED HAMID", detection.CustomerName);
        Assert.Equal(new DateTime(2025, 4, 1), detection.StatementFrom);
        Assert.Equal(new DateTime(2026, 3, 31), detection.StatementTo);

        // 2. Full Statement Parsing
        var result = _kotakParser.Parse(extraction, detection);
        Assert.True(result.Success);
        Assert.Equal(4360, result.TotalDetected);
        Assert.Equal(4360, result.Transactions.Count);
        Assert.Empty(result.Warnings);

        // 3. Debits & Credits Totals
        var debits = result.Transactions.Where(t => t.Debit.HasValue).ToList();
        var credits = result.Transactions.Where(t => t.Credit.HasValue).ToList();
        Assert.Equal(1974, debits.Count);
        Assert.Equal(2386, credits.Count);

        decimal totalDebits = debits.Sum(t => t.Debit!.Value);
        decimal totalCredits = credits.Sum(t => t.Credit!.Value);
        Assert.Equal(4692032.84m, totalDebits);
        Assert.Equal(4680154.00m, totalCredits);

        // 4. Opening and Closing Balance Continuity
        decimal expectedOpening = 11924.60m;
        decimal expectedClosing = 45.76m;
        decimal netMovement = totalCredits - totalDebits;
        Assert.Equal(expectedClosing, expectedOpening + netMovement);

        // 5. First & Last Transaction Checks
        var firstTx = result.Transactions[0];
        Assert.Equal(new DateTime(2025, 4, 1), firstTx.TransactionDate);
        Assert.Equal(1690.00m, firstTx.Debit);
        Assert.Equal(10234.60m, firstTx.Balance);
        Assert.Equal("UPI-509185432310", firstTx.Reference);
        Assert.Contains("UPI/PhonePe", firstTx.Description);

        var lastTx = result.Transactions[4359];
        Assert.Equal(new DateTime(2026, 3, 31), lastTx.TransactionDate);
        Assert.Equal(31.00m, lastTx.Credit);
        Assert.Equal(45.76m, lastTx.Balance);
        Assert.Contains("Int.Pd:5046856741", lastTx.Description);
        Assert.DoesNotContain("Statement Generated on", lastTx.Description);

        // 6. Non-Transaction Content Rejection: Page 132 (Account Summary) and Page 133 (Important Information) produce 0 transactions
        Assert.DoesNotContain(result.Transactions, t => t.SourcePageNumber == 132);
        Assert.DoesNotContain(result.Transactions, t => t.SourcePageNumber == 133);

        // 7. Balance continuity verification across all 4360 transactions
        decimal runningBal = expectedOpening;
        for (int i = 0; i < result.Transactions.Count; i++)
        {
            var tx = result.Transactions[i];
            decimal expectedBal = runningBal - (tx.Debit ?? 0m) + (tx.Credit ?? 0m);
            Assert.True(tx.Balance.HasValue, $"Tx #{i + 1} must have balance");
            Assert.True(Math.Abs(tx.Balance!.Value - expectedBal) < 0.01m,
                $"Balance mismatch at #{i + 1}: expected {expectedBal:F2}, recorded {tx.Balance.Value:F2}");
            runningBal = tx.Balance.Value;
        }
    }

    #endregion

    #region Generic Parsing Tests (Proving No Hardcoded Client Dependency)

    [Fact]
    public void Kotak_GenericClient_BusinessEntity_ParsesWithoutClientHardcoding()
    {
        var extraction = BuildSyntheticKotakExtraction(pages =>
        {
            var p1Rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 30.0, [("Kotak", 40.0), ("Mahindra", 80.0), ("Bank", 135.0)]),
                CreateRow(1, 45.0, [("Customer", 40.0), ("Name", 90.0), (":", 120.0), ("MAHESH", 130.0), ("LOGISTICS", 180.0), ("PVT", 235.0), ("LTD", 260.0)]),
                CreateRow(1, 60.0, [("Account", 40.0), ("Number", 85.0), (":", 130.0), ("987654321098", 140.0), ("CRN", 250.0), (":", 280.0), ("88776655", 290.0)]),
                CreateRow(1, 75.0, [("Period", 40.0), (":", 80.0), ("01/01/2024", 90.0), ("to", 155.0), ("31/01/2024", 175.0)]),
                CreateRow(1, 90.0, [("IFSC", 40.0), (":", 70.0), ("KKBK0000123", 80.0)]),
                CreateRow(1, 110.0, [
                    ("#", 40.0), ("Date", 65.0), ("Description", 135.0), ("Chq/Ref.", 285.0), ("No.", 330.0),
                    ("Withdrawal", 390.0), ("(Dr.)", 445.0), ("Deposit", 485.0), ("(Cr.)", 525.0), ("Balance", 570.0)
                ]),
                CreateRow(1, 130.0, [
                    ("-", 40.0), ("-", 65.0), ("Opening", 135.0), ("Balance", 185.0), ("50,000.00", 570.0)
                ]),
                CreateRow(1, 150.0, [
                    ("1", 40.0), ("05 Jan 2024", 65.0), ("NEFT/CLIENT VENDOR PAYMENT", 135.0),
                    ("NEFT-991122", 285.0), ("10,000.00", 400.0), ("40,000.00", 570.0)
                ]),
                CreateRow(1, 170.0, [
                    ("2", 40.0), ("15 Jan 2024", 65.0), ("INWARD RTGS REMITTANCE", 135.0),
                    ("RTGS-554433", 285.0), ("1,50,000.00", 490.0), ("1,90,000.00", 570.0)
                ])
            };
            pages.Add(CreatePage(1, p1Rows));
        });

        var detection = _detector.DetectBank(extraction);
        Assert.NotNull(detection);
        Assert.Equal(BankType.Kotak, detection.DetectedBank);
        Assert.Equal("987654321098", detection.AccountNumber);
        Assert.Equal("MAHESH LOGISTICS PVT LTD", detection.CustomerName);
        Assert.Equal(new DateTime(2024, 1, 1), detection.StatementFrom);
        Assert.Equal(new DateTime(2024, 1, 31), detection.StatementTo);

        var parseResult = _kotakParser.Parse(extraction, detection);
        Assert.True(parseResult.Success);
        Assert.Equal(2, parseResult.Transactions.Count);
        Assert.Equal(50000.00m, parseResult.Transactions[0].Balance + (parseResult.Transactions[0].Debit ?? 0m));
        Assert.Equal(190000.00m, parseResult.Transactions[1].Balance);
        Assert.Empty(parseResult.Warnings);
    }

    [Fact]
    public void Kotak_GenericClient_OverdraftScenario_HandlesNegativeBalancesAccurately()
    {
        var extraction = BuildSyntheticKotakExtraction(pages =>
        {
            var p1Rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 30.0, [("Kotak", 40.0), ("Mahindra", 80.0), ("Bank", 135.0)]),
                CreateRow(1, 45.0, [("Customer", 40.0), ("Name", 90.0), (":", 120.0), ("PRIYA", 130.0), ("SHARMA", 175.0)]),
                CreateRow(1, 60.0, [("Account", 40.0), ("Number", 85.0), (":", 130.0), ("1122334455", 140.0)]),
                CreateRow(1, 75.0, [("Period", 40.0), (":", 80.0), ("01/05/2024", 90.0), ("to", 155.0), ("31/05/2024", 175.0)]),
                CreateRow(1, 90.0, [("IFSC", 40.0), (":", 70.0), ("KKBK0000888", 80.0)]),
                CreateRow(1, 110.0, [
                    ("#", 40.0), ("Date", 65.0), ("Description", 135.0), ("Chq/Ref.", 285.0), ("No.", 330.0),
                    ("Withdrawal", 390.0), ("(Dr.)", 445.0), ("Deposit", 485.0), ("(Cr.)", 525.0), ("Balance", 570.0)
                ]),
                CreateRow(1, 130.0, [
                    ("1", 40.0), ("02 May 2024", 65.0), ("ATM WITHDRAWAL OVERDRAFT", 135.0),
                    ("ATM-1002", 285.0), ("5,000.00", 400.0), ("-5,000.00", 570.0)
                ]),
                CreateRow(1, 150.0, [
                    ("2", 40.0), ("08 May 2024", 65.0), ("FUNDS TRANSFER DEPOSIT", 135.0),
                    ("FT-3322", 285.0), ("5,000.00", 490.0), ("0.00", 570.0)
                ])
            };
            pages.Add(CreatePage(1, p1Rows));
        });

        var detection = _detector.DetectBank(extraction);
        Assert.Equal("1122334455", detection.AccountNumber);
        Assert.Equal("PRIYA SHARMA", detection.CustomerName);

        var parseResult = _kotakParser.Parse(extraction, detection);
        Assert.True(parseResult.Success);
        Assert.Equal(2, parseResult.Transactions.Count);
        Assert.Equal(-5000.00m, parseResult.Transactions[0].Balance);
        Assert.Equal(0.00m, parseResult.Transactions[1].Balance);
        Assert.Empty(parseResult.Warnings);
    }

    #endregion

    #region Embedded Date in Narration Regression Tests

    [Fact]
    public void Kotak_RealStatement_EmbeddedDateInNarration_DoesNotCreatePhantomTransaction_AndMaintainsContinuity()
    {
        // Recreates the real statement sequence:
        // Opening Balance: 56.59
        // 1. 01/09/2026 Credit 20     Balance 76.59
        // 2. 01/09/2026 Credit 265    Balance 341.59
        // 3. 01/09/2026 Debit 231     Balance 110.59
        // 4. 01/09/2026 Credit 150    Balance 260.59
        // 5. 01/09/2026 Debit 7.66    Balance 252.93 (REM CHRG: DCC FEE FOR 3301 ECOM / TXN ON 09-JUL-2026)
        // 6. 02/09/2026 Credit 80     Balance 332.93
        var extraction = BuildSyntheticKotakExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 30.0, [("Kotak", 40.0), ("Mahindra", 80.0), ("Bank", 135.0)]),
                CreateRow(1, 45.0, [("Account", 40.0), ("Number", 85.0), (":", 130.0), ("6245778582", 140.0)]),
                CreateRow(1, 60.0, [
                    ("#", 40.0), ("Date", 65.0), ("Description", 135.0), ("Chq/Ref.", 285.0), ("No.", 330.0),
                    ("Withdrawal", 390.0), ("(Dr.)", 445.0), ("Deposit", 485.0), ("(Cr.)", 525.0), ("Balance", 570.0)
                ]),
                // Opening balance
                CreateRow(1, 75.0, [
                    ("-", 40.0), ("-", 65.0), ("Opening", 135.0), ("Balance", 185.0), ("56.59", 570.0)
                ]),
                // Tx 1: 01/09/2026 +20 -> 76.59
                CreateRow(1, 90.0, [
                    ("1", 40.0), ("01/09/2026", 65.0), ("UPI/HUSAIN/112233", 135.0),
                    ("UPI-112233", 285.0), ("20.00", 490.0), ("76.59", 570.0)
                ]),
                // Tx 2: 01/09/2026 +265 -> 341.59
                CreateRow(1, 105.0, [
                    ("2", 40.0), ("01/09/2026", 65.0), ("UPI/Razorpay/out/445566", 135.0),
                    ("UPI-445566", 285.0), ("265.00", 490.0), ("341.59", 570.0)
                ]),
                // Tx 3: 01/09/2026 -231 -> 110.59
                CreateRow(1, 120.0, [
                    ("3", 40.0), ("01/09/2026", 65.0), ("UPI/Flipkart/778899", 135.0),
                    ("UPI-778899", 285.0), ("231.00", 400.0), ("110.59", 570.0)
                ]),
                // Tx 4: 01/09/2026 +150 -> 260.59
                CreateRow(1, 135.0, [
                    ("4", 40.0), ("01/09/2026", 65.0), ("UPI/SHAILENDRA/334455", 135.0),
                    ("UPI-334455", 285.0), ("150.00", 490.0), ("260.59", 570.0)
                ]),
                // Tx 5: Line 1 (Date, Description line 1, Debit 7.66)
                CreateRow(1, 150.0, [
                    ("5", 40.0), ("01/09/2026", 65.0), ("REM CHRG: DCC FEE FOR 3301 ECOM", 135.0),
                    ("7.66", 400.0)
                ]),
                // Tx 5: Line 2 (Continuation narration with embedded date 09-JUL-2026, Balance 252.93)
                CreateRow(1, 162.0, [
                    ("TXN", 135.0), ("ON", 160.0), ("09-JUL-2026", 180.0),
                    ("252.93", 570.0)
                ]),
                // Tx 6: 02/09/2026 +80 -> 332.93
                CreateRow(1, 175.0, [
                    ("6", 40.0), ("02/09/2026", 65.0), ("UPI/Mrs NANDINI/998877", 135.0),
                    ("UPI-998877", 285.0), ("80.00", 490.0), ("332.93", 570.0)
                ])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _kotakParser.Parse(extraction, detection);

        Assert.True(result.Success);
        // Must produce exactly 6 transactions, NOT 7!
        Assert.Equal(6, result.Transactions.Count);

        // Phantom transaction with date 09/07/2026 must NOT exist
        Assert.DoesNotContain(result.Transactions, t => t.TransactionDate == new DateTime(2026, 7, 9));

        // Legitimate transaction 5 verification
        var tx5 = result.Transactions[4];
        Assert.Equal(new DateTime(2026, 9, 1), tx5.TransactionDate);
        Assert.Contains("09-JUL-2026", tx5.Description);
        Assert.Contains("REM CHRG: DCC FEE FOR 3301 ECOM TXN ON 09-JUL-2026", tx5.Description);
        Assert.Equal(7.66m, tx5.Debit);
        Assert.Null(tx5.Credit);
        Assert.Equal(7.66m, tx5.Amount);
        Assert.Equal(252.93m, tx5.Balance);

        // Verification with TransactionValidationService:
        // Prior to the fix, phantom 09/07/2026 row sorted first with 252.93 balance, causing 196.34 false discrepancy.
        var validationService = new Accufex.Server.Validation.Services.TransactionValidationService();
        var rawTransactions = result.Transactions.Select(t => new Transaction
        {
            Id = t.Id,
            TransactionDate = t.TransactionDate,
            Description = t.Description,
            Debit = t.Debit,
            Credit = t.Credit,
            Amount = t.Amount,
            Balance = t.Balance,
            Reference = t.Reference,
            Utr = t.Utr,
            TransactionType = t.TransactionType,
            BankCode = t.BankCode
        }).ToList();

        var enriched = validationService.ValidateAndEnrichStatement(rawTransactions, null);
        var summary = validationService.ComputeSummary(Guid.NewGuid(), 8, "Kotak Mahindra Bank", "KOTAK-v1", enriched);

        Assert.Equal(6, summary.TotalTransactions);
        Assert.Equal(0, summary.InvalidCount);
        Assert.DoesNotContain(enriched, e => e.ValidationWarnings.Any(w => w.Contains("196.34")));
        Assert.DoesNotContain(enriched, e => e.BalanceStatus == BalanceStatus.Mismatch);
    }

    [Fact]
    public void Kotak_NarrationWithEmbeddedDate_Test1_RemChrg09Jul2026_PreservesSingleTransaction()
    {
        // Test 1: "REM CHRG: DCC FEE FOR 3301 ECOM TXN ON 09-JUL-2026"
        // Expected: Transaction date = 01/09/2026, Narration contains 09-JUL-2026, No second transaction created.
        var extraction = BuildSyntheticKotakExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 30.0, [("Kotak", 40.0), ("Mahindra", 80.0), ("Bank", 135.0)]),
                CreateRow(1, 50.0, [
                    ("#", 40.0), ("Date", 65.0), ("Description", 135.0), ("Chq/Ref.", 285.0), ("No.", 330.0),
                    ("Withdrawal", 390.0), ("(Dr.)", 445.0), ("Deposit", 485.0), ("(Cr.)", 525.0), ("Balance", 570.0)
                ]),
                CreateRow(1, 70.0, [
                    ("1", 40.0), ("01/09/2026", 65.0), ("REM CHRG: DCC FEE FOR 3301 ECOM", 135.0),
                    ("7.66", 400.0)
                ]),
                CreateRow(1, 82.0, [
                    ("TXN ON 09-JUL-2026", 135.0),
                    ("252.93", 570.0)
                ])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _kotakParser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Single(result.Transactions);

        var tx = result.Transactions[0];
        Assert.Equal(new DateTime(2026, 9, 1), tx.TransactionDate);
        Assert.Contains("09-JUL-2026", tx.Description);
        Assert.Equal(7.66m, tx.Debit);
        Assert.Equal(252.93m, tx.Balance);
    }

    [Fact]
    public void Kotak_NarrationWithEmbeddedDate_Test2_BillDate15Aug2026_RemainsNarration()
    {
        // Test 2: "PAYMENT FOR BILL DATE 15-AUG-2026"
        // Expected: The embedded date remains narration. No second transaction created.
        var extraction = BuildSyntheticKotakExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 30.0, [("Kotak", 40.0), ("Mahindra", 80.0), ("Bank", 135.0)]),
                CreateRow(1, 50.0, [
                    ("#", 40.0), ("Date", 65.0), ("Description", 135.0), ("Chq/Ref.", 285.0), ("No.", 330.0),
                    ("Withdrawal", 390.0), ("(Dr.)", 445.0), ("Deposit", 485.0), ("(Cr.)", 525.0), ("Balance", 570.0)
                ]),
                CreateRow(1, 70.0, [
                    ("1", 40.0), ("01/09/2026", 65.0), ("ELECTRICITY BILL PAYMENT", 135.0),
                    ("500.00", 400.0), ("5,000.00", 570.0)
                ]),
                CreateRow(1, 82.0, [
                    ("PAYMENT FOR BILL DATE 15-AUG-2026", 135.0)
                ])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _kotakParser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Single(result.Transactions);

        var tx = result.Transactions[0];
        Assert.Equal(new DateTime(2026, 9, 1), tx.TransactionDate);
        Assert.Contains("PAYMENT FOR BILL DATE 15-AUG-2026", tx.Description);
        Assert.DoesNotContain(result.Transactions, t => t.TransactionDate == new DateTime(2026, 8, 15));
    }

    [Fact]
    public void Kotak_NarrationWithEmbeddedDate_Test3_TxnDate20Oct2026_DoesNotCreateNewTransaction()
    {
        // Test 3: "TXN DATE 20-OCT-2026 REF ABC123"
        // Expected: The embedded date does not create another transaction.
        var extraction = BuildSyntheticKotakExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 30.0, [("Kotak", 40.0), ("Mahindra", 80.0), ("Bank", 135.0)]),
                CreateRow(1, 50.0, [
                    ("#", 40.0), ("Date", 65.0), ("Description", 135.0), ("Chq/Ref.", 285.0), ("No.", 330.0),
                    ("Withdrawal", 390.0), ("(Dr.)", 445.0), ("Deposit", 485.0), ("(Cr.)", 525.0), ("Balance", 570.0)
                ]),
                CreateRow(1, 70.0, [
                    ("1", 40.0), ("01/09/2026", 65.0), ("REFUND FROM MERCHANT", 135.0),
                    ("150.00", 490.0), ("1,150.00", 570.0)
                ]),
                CreateRow(1, 82.0, [
                    ("TXN DATE 20-OCT-2026 REF ABC123", 135.0)
                ])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _kotakParser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Single(result.Transactions);

        var tx = result.Transactions[0];
        Assert.Equal(new DateTime(2026, 9, 1), tx.TransactionDate);
        Assert.Contains("TXN DATE 20-OCT-2026 REF ABC123", tx.Description);
        Assert.DoesNotContain(result.Transactions, t => t.TransactionDate == new DateTime(2026, 10, 20));
    }

    [Fact]
    public void Kotak_NormalTransaction_Test4_DateColumnRecognizedCorrectly()
    {
        // Test 4: A normal transaction where the actual Date column contains 02 Sep 2026.
        // Expected: 02 Sep 2026 is correctly recognized as the transaction date.
        var extraction = BuildSyntheticKotakExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 30.0, [("Kotak", 40.0), ("Mahindra", 80.0), ("Bank", 135.0)]),
                CreateRow(1, 50.0, [
                    ("#", 40.0), ("Date", 65.0), ("Description", 135.0), ("Chq/Ref.", 285.0), ("No.", 330.0),
                    ("Withdrawal", 390.0), ("(Dr.)", 445.0), ("Deposit", 485.0), ("(Cr.)", 525.0), ("Balance", 570.0)
                ]),
                CreateRow(1, 70.0, [
                    ("1", 40.0), ("02 Sep 2026", 65.0), ("UPI/Mrs NANDINI/998877", 135.0),
                    ("UPI-998877", 285.0), ("80.00", 490.0), ("332.93", 570.0)
                ])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _kotakParser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Single(result.Transactions);

        var tx = result.Transactions[0];
        Assert.Equal(new DateTime(2026, 9, 2), tx.TransactionDate);
        Assert.Equal(80.00m, tx.Credit);
        Assert.Equal(332.93m, tx.Balance);
    }

    #endregion
}

