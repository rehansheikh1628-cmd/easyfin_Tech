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
using Accufex.Server.DTOs;
using Accufex.Server.Models;
using Accufex.Server.Parsing;
using Accufex.Server.Parsing.Interfaces;
using Accufex.Server.Parsing.Models;
using Accufex.Server.Parsing.Parsers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;
using Xunit;
using Xunit.Abstractions;

namespace Accufex.Server.Tests;

public class PNBParserTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;
    private readonly BankDetector _detector = new(NullLogger<BankDetector>.Instance);
    private readonly PNBStatementParser _pnbParser = new(NullLogger<PNBStatementParser>.Instance);
    private readonly BankParserRegistry _registry;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string RealScannedFixturePath = @"E:\Bank Statements\New 07-08-2026\PNB.pdf";

    public PNBParserTests(CustomWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
        _registry = new BankParserRegistry([_pnbParser]);
    }

    private static MultipartFormDataContent CreateMultipartPdf(string fileName, byte[] bytes)
    {
        var multipart = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        multipart.Add(fileContent, "file", fileName);
        return multipart;
    }

    private async Task<(HttpClient Client, AuthResponseDto Auth)> RegisterUserAsync(string prefix)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            BaseAddress = new Uri("http://localhost")
        });

        var email = $"{prefix}_{Guid.NewGuid():N}@pnb-test.com";
        var request = new RegisterRequest($"{prefix} Workspace", email, "SecurePass123!");
        var response = await client.PostAsJsonAsync("/api/auth/register", request, JsonOptions);
        Assert.True(response.IsSuccessStatusCode);

        var auth = await response.Content.ReadFromJsonAsync<AuthResponseDto>(JsonOptions);
        Assert.NotNull(auth);
        return (client, auth);
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

    private static PdfPageResult CreatePage(int pageNumber, List<PdfCandidateRow> rows)
    {
        var blocks = rows.SelectMany(r => r.LineFragments).ToList();
        return new PdfPageResult
        {
            PageNumber = pageNumber,
            Width = 595.0,
            Height = 842.0,
            HasUsableText = blocks.Count > 0,
            TextBlocks = blocks,
            CandidateRows = rows,
            RawText = string.Join("\n", rows.Select(r => r.RawLineText))
        };
    }

    private static PdfExtractionResult BuildSyntheticPnbExtraction(Action<List<PdfPageResult>> configurePages)
    {
        var pages = new List<PdfPageResult>();
        configurePages(pages);

        return new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            OriginalFileName = "PNB_Statement_Sample.pdf",
            PageCount = pages.Count,
            ExtractionStatus = "DigitalTextExtracted",
            HasUsableText = true,
            PdfType = "DigitalWithText",
            Pages = pages
        };
    }

    /// <summary>
    /// Builds a representative PNB Format 1 extraction matching the exact layout of the real PNB statement
    /// (Tran Date | Withdrawal | Deposit | Balance | Alpha | CHQ. NO. | Narration | Additional Info)
    /// </summary>
    private static PdfExtractionResult BuildStandardPnbExtraction(int pageCount = 1, bool includeMismatch = false)
    {
        return BuildSyntheticPnbExtraction(pages =>
        {
            for (int p = 1; p <= pageCount; p++)
            {
                var rows = new List<PdfCandidateRow>();
                double y = 40.0;

                // Header Block
                rows.Add(CreateRow(p, y, [
                    ("punjab", 50.0), ("national", 100.0), ("bank", 155.0),
                    ("...the", 260.0), ("name", 295.0), ("you", 325.0), ("can", 348.0), ("BANK", 370.0), ("upon!", 405.0)
                ]));
                y += 18.0;

                rows.Add(CreateRow(p, y, [
                    ("Statement", 50.0), ("of", 115.0), ("Account", 132.0), ("No:", 185.0), ("9967002100001746", 210.0),
                    ("Printed", 420.0), ("By:", 465.0), ("5189615", 485.0)
                ]));
                y += 16.0;

                rows.Add(CreateRow(p, y, [
                    ("Customer", 50.0), ("Name:", 110.0), ("HINDUSTAN", 155.0), ("CARGO", 230.0), ("MOVERS", 280.0), ("AND", 335.0), ("NAYEEM", 365.0), ("UDDIN", 415.0), ("KHAN", 455.0)
                ]));
                y += 16.0;

                rows.Add(CreateRow(p, y, [
                    ("Customer", 50.0), ("Address:", 110.0), ("SHIVKRUPA", 165.0), ("COMPLEX", 240.0), ("WADDHAMNAA", 300.0), ("NAGPUR", 390.0), ("440023", 445.0)
                ]));
                y += 16.0;

                rows.Add(CreateRow(p, y, [
                    ("IFSC", 50.0), ("Code:", 85.0), ("PUNB0996700", 125.0),
                    ("MICR", 280.0), ("Code:", 315.0), ("440024503", 355.0),
                    ("Acct", 420.0), ("Currency:", 450.0), ("INR", 505.0)
                ]));
                y += 16.0;

                rows.Add(CreateRow(p, y, [
                    ("Statement", 50.0), ("for", 108.0), ("Period", 128.0), (":", 168.0), ("01-04-2024", 175.0), ("to", 245.0), ("31-03-2025", 260.0)
                ]));
                y += 20.0;

                // Table Header (Format 1: Tran Date, Withdrawal, Deposit, Balance, Alpha, CHQ. NO., Narration, Additional Info)
                rows.Add(CreateRow(p, y, [
                    ("Tran", 45.0), ("Date", 70.0),
                    ("Withdrawal", 110.0),
                    ("Deposit", 175.0),
                    ("Balance", 235.0),
                    ("Alpha", 295.0),
                    ("CHQ.", 335.0), ("NO.", 365.0),
                    ("Narration", 400.0),
                    ("Additional", 480.0), ("Info", 535.0)
                ]));
                y += 18.0;

                if (p == 1)
                {
                    // Row 1: Debit 118.00 -> Balance 7471.88 Cr.
                    rows.Add(CreateRow(p, y, [
                        ("14-04-2024", 45.0),
                        ("118.00", 110.0),
                        ("7471.88", 235.0), ("Cr.", 275.0),
                        ("INCIDENTAL", 400.0), ("CHARGES", 455.0)
                    ]));
                    y += 16.0;

                    // Row 2: Debit 118.00 -> Balance 7353.88 Cr.
                    rows.Add(CreateRow(p, y, [
                        ("30-04-2024", 45.0),
                        ("118.00", 110.0),
                        ("7353.88", 235.0), ("Cr.", 275.0),
                        ("STT", 400.0), ("OF", 425.0), ("AC", 442.0), ("CHARGES", 462.0)
                    ]));
                    y += 16.0;

                    // Row 3: Credit 5000.00 -> Balance 12353.88 Cr.
                    rows.Add(CreateRow(p, y, [
                        ("15-05-2024", 45.0),
                        ("5,000.00", 175.0),
                        ("12353.88", 235.0), ("Cr.", 280.0),
                        ("UPI/413612345678/PAYMENT", 400.0),
                        ("FROM", 480.0), ("CLIENT", 515.0)
                    ]));
                    y += 16.0;

                    // Row 4: Debit 118.00 with multi-line narration
                    rows.Add(CreateRow(p, y, [
                        ("13-07-2024", 45.0),
                        ("118.00", 110.0),
                        (includeMismatch ? "8000.00" : "12235.88", 235.0), ("Cr.", 280.0),
                        ("QUARTERLY", 400.0), ("SERVICE", 460.0),
                        ("GST", 480.0), ("EXTRA", 505.0)
                    ]));
                    y += 14.0;
                    // Multi-line continuation for Row 4
                    rows.Add(CreateRow(p, y, [
                        ("CHARGES", 400.0), ("FOR", 450.0), ("Q1", 475.0),
                        ("REF:", 480.0), ("TAX9988", 505.0)
                    ]));
                    y += 16.0;

                    // Row 5: Debit with Cheque No
                    rows.Add(CreateRow(p, y, [
                        ("12-10-2024", 45.0),
                        ("1,500.00", 110.0),
                        (includeMismatch ? "6500.00" : "10735.88", 235.0), ("Cr.", 280.0),
                        ("044123", 335.0),
                        ("CHEQUE", 400.0), ("CLEARING", 445.0),
                        ("SELF", 480.0), ("WITHDRAWAL", 510.0)
                    ]));
                    y += 20.0;
                }
                else
                {
                    // Page 2 rows
                    // Row 6: Debit 118.00
                    rows.Add(CreateRow(p, y, [
                        ("11-01-2025", 45.0),
                        ("118.00", 110.0),
                        ("10617.88", 235.0), ("Cr.", 280.0),
                        ("INCIDENTAL", 400.0), ("CHARGES", 455.0)
                    ]));
                    y += 16.0;

                    // Row 7: Credit 2000.00
                    rows.Add(CreateRow(p, y, [
                        ("20-02-2025", 45.0),
                        ("2,000.00", 175.0),
                        ("12617.88", 235.0), ("Cr.", 280.0),
                        ("IMPS/505112349999/REF", 400.0),
                        ("SETTLEMENT", 480.0)
                    ]));
                    y += 20.0;
                }

                // Footer Block
                rows.Add(CreateRow(p, y, [
                    ("Page", 45.0), ("Total", 75.0),
                    (p == 1 ? "1854.00" : "118.00", 110.0),
                    (p == 1 ? "5000.00" : "2000.00", 175.0),
                    ($"Page {p} of {pageCount}", 480.0)
                ]));
                y += 15.0;

                if (p == pageCount)
                {
                    rows.Add(CreateRow(p, y, [
                        ("Grand", 45.0), ("Total", 80.0),
                        ("1972.00", 110.0),
                        ("7000.00", 175.0)
                    ]));
                }

                pages.Add(CreatePage(p, rows));
            }
        });
    }

    #region Test 01: PNB Format Detection

    [Fact]
    public void Test01_PNB_Format_Detection()
    {
        var extraction = BuildStandardPnbExtraction(1);
        var detection = _detector.DetectBank(extraction);

        Assert.NotNull(detection);
        Assert.True(detection.IsSupported, "Document should be detected as a supported bank.");
        Assert.Equal(BankType.PNB, detection.DetectedBank);
        Assert.Equal("Punjab National Bank", detection.BankName);
        Assert.Equal("PNB-v1", detection.DetectedFormat);
        Assert.True(detection.Confidence >= 0.90, $"Confidence {detection.Confidence} should be >= 0.90");
        Assert.Equal("9967002100001746", detection.AccountNumber);
        Assert.Contains("HINDUSTAN CARGO MOVERS", detection.CustomerName);
        Assert.Equal(new DateTime(2024, 4, 1), detection.StatementFrom);
        Assert.Equal(new DateTime(2025, 3, 31), detection.StatementTo);
    }

    #endregion

    #region Test 02: PNB Detector Disambiguation (Does NOT misclassify other banks)

    [Fact]
    public void Test02_PNB_Detector_Disambiguation_All_Banks()
    {
        // 1. Verify HDFC detection remains intact
        var hdfcBlocks = new List<PdfTextBlock>
        {
            new() { Text = "HDFC BANK LIMITED", X = 50, Y = 50, Width = 120, Height = 12 },
            new() { Text = "HDFC0000123", X = 50, Y = 70, Width = 80, Height = 10 },
            new() { Text = "Date Narration Chq/Ref No Value Dt Withdrawal Amount Deposit Amount Closing Balance", X = 50, Y = 100, Width = 400, Height = 10 }
        };
        var hdfcExtraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            HasUsableText = true,
            Pages = [new PdfPageResult { PageNumber = 1, Width = 595, Height = 842, TextBlocks = hdfcBlocks, RawText = string.Join(" ", hdfcBlocks.Select(b => b.Text)) }]
        };
        var hdfcDet = _detector.DetectBank(hdfcExtraction);
        Assert.Equal(BankType.Hdfc, hdfcDet.DetectedBank);

        // 2. Verify YES BANK detection remains intact
        var yesBlocks = new List<PdfTextBlock>
        {
            new() { Text = "YES BANK", X = 50, Y = 50, Width = 80, Height = 12 },
            new() { Text = "YESB0000123", X = 50, Y = 70, Width = 80, Height = 10 },
            new() { Text = "Transaction Date Value Date Cheque No/ Reference No Description Withdrawals Deposits Running Balance", X = 50, Y = 100, Width = 500, Height = 10 }
        };
        var yesExtraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            HasUsableText = true,
            Pages = [new PdfPageResult { PageNumber = 1, Width = 595, Height = 842, TextBlocks = yesBlocks, RawText = string.Join(" ", yesBlocks.Select(b => b.Text)) }]
        };
        var yesDet = _detector.DetectBank(yesExtraction);
        Assert.Equal(BankType.YesBank, yesDet.DetectedBank);

        // 3. Verify Axis Bank detection remains intact
        var axisBlocks = new List<PdfTextBlock>
        {
            new() { Text = "AXIS BANK", X = 50, Y = 50, Width = 80, Height = 12 },
            new() { Text = "UTIB0000123", X = 50, Y = 70, Width = 80, Height = 10 },
            new() { Text = "S.NO Transaction Date Value Date Particulars Amount(INR) Debit/Credit Balance(INR) Cheque Number Branch Name", X = 50, Y = 100, Width = 500, Height = 10 }
        };
        var axisExtraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            HasUsableText = true,
            Pages = [new PdfPageResult { PageNumber = 1, Width = 595, Height = 842, TextBlocks = axisBlocks, RawText = string.Join(" ", axisBlocks.Select(b => b.Text)) }]
        };
        var axisDet = _detector.DetectBank(axisExtraction);
        Assert.Equal(BankType.Axis, axisDet.DetectedBank);

        // 4. Verify Central Bank detection remains intact
        var cbBlocks = new List<PdfTextBlock>
        {
            new() { Text = "CENTRAL BANK OF INDIA", X = 50, Y = 50, Width = 140, Height = 12 },
            new() { Text = "CBIN0000123", X = 50, Y = 70, Width = 80, Height = 10 },
            new() { Text = "centralbankofindia.co.in", X = 50, Y = 85, Width = 120, Height = 10 },
            new() { Text = "Post Date Value Date Details Cheque No Debit Credit Balance", X = 50, Y = 100, Width = 450, Height = 10 }
        };
        var cbExtraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            HasUsableText = true,
            Pages = [new PdfPageResult { PageNumber = 1, Width = 595, Height = 842, TextBlocks = cbBlocks, RawText = string.Join(" ", cbBlocks.Select(b => b.Text)) }]
        };
        var cbDet = _detector.DetectBank(cbExtraction);
        Assert.Equal(BankType.CentralBank, cbDet.DetectedBank);

        // 5. Verify Kotak detection remains intact
        var kotakBlocks = new List<PdfTextBlock>
        {
            new() { Text = "Kotak Mahindra Bank", X = 50, Y = 50, Width = 140, Height = 12 },
            new() { Text = "KKBK0000123", X = 50, Y = 70, Width = 80, Height = 10 },
            new() { Text = "Date Description Chq/Ref No. Withdrawal (Dr.) Deposit (Cr.) Balance", X = 50, Y = 100, Width = 450, Height = 10 }
        };
        var kotakExtraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            HasUsableText = true,
            Pages = [new PdfPageResult { PageNumber = 1, Width = 595, Height = 842, TextBlocks = kotakBlocks, RawText = string.Join(" ", kotakBlocks.Select(b => b.Text)) }]
        };
        var kotakDet = _detector.DetectBank(kotakExtraction);
        Assert.Equal(BankType.Kotak, kotakDet.DetectedBank);

        // 6. Verify BOI detection remains intact
        var boiBlocks = new List<PdfTextBlock>
        {
            new() { Text = "BANK OF INDIA", X = 50, Y = 50, Width = 100, Height = 12 },
            new() { Text = "BKID0000123", X = 50, Y = 70, Width = 80, Height = 10 },
            new() { Text = "bankofindia.co.in", X = 50, Y = 85, Width = 100, Height = 10 },
            new() { Text = "Date Particulars Cheque No Debit Credit Balance Dr/Cr", X = 50, Y = 100, Width = 450, Height = 10 }
        };
        var boiExtraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            HasUsableText = true,
            Pages = [new PdfPageResult { PageNumber = 1, Width = 595, Height = 842, TextBlocks = boiBlocks, RawText = string.Join(" ", boiBlocks.Select(b => b.Text)) }]
        };
        var boiDet = _detector.DetectBank(boiExtraction);
        Assert.Equal(BankType.BOI, boiDet.DetectedBank);

        // 7. Verify SBI detection remains intact
        var sbiBlocks = new List<PdfTextBlock>
        {
            new() { Text = "STATE BANK OF INDIA", X = 50, Y = 50, Width = 140, Height = 12 },
            new() { Text = "SBIN0000123", X = 50, Y = 70, Width = 80, Height = 10 },
            new() { Text = "Post Date Value Date Description Ref No/Cheque No Debit Credit Balance", X = 50, Y = 100, Width = 500, Height = 10 }
        };
        var sbiExtraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            HasUsableText = true,
            Pages = [new PdfPageResult { PageNumber = 1, Width = 595, Height = 842, TextBlocks = sbiBlocks, RawText = string.Join(" ", sbiBlocks.Select(b => b.Text)) }]
        };
        var sbiDet = _detector.DetectBank(sbiExtraction);
        Assert.Equal(BankType.SBI, sbiDet.DetectedBank);
    }

    #endregion

    #region Test 03: CanParse Logic

    [Fact]
    public void Test03_PNB_CanParse_Validation()
    {
        var validPnb = new BankDetectionResult
        {
            DetectedBank = BankType.PNB,
            IsSupported = true
        };
        Assert.True(_pnbParser.CanParse(validPnb));

        var unsupportedPnb = new BankDetectionResult
        {
            DetectedBank = BankType.PNB,
            IsSupported = false
        };
        Assert.False(_pnbParser.CanParse(unsupportedPnb));

        var otherBank = new BankDetectionResult
        {
            DetectedBank = BankType.Hdfc,
            IsSupported = true
        };
        Assert.False(_pnbParser.CanParse(otherBank));

        Assert.False(_pnbParser.CanParse(null!));
    }

    #endregion

    #region Test 04: Debit and Credit Transaction Direction Integrity

    [Fact]
    public void Test04_PNB_Debit_And_Credit_Direction_Integrity()
    {
        var extraction = BuildStandardPnbExtraction(1);
        var detection = _detector.DetectBank(extraction);
        var result = _pnbParser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Equal(5, result.Transactions.Count);

        // Transaction 1: Debit 118.00
        var tx1 = result.Transactions[0];
        Assert.Equal(new DateTime(2024, 4, 14), tx1.TransactionDate);
        Assert.Equal(118.00m, tx1.Debit);
        Assert.Null(tx1.Credit);
        Assert.Equal(118.00m, tx1.Amount);
        Assert.Equal("Debit", tx1.TransactionType);
        Assert.Equal(7471.88m, tx1.Balance);

        // Transaction 3: Credit 5,000.00
        var tx3 = result.Transactions[2];
        Assert.Equal(new DateTime(2024, 5, 15), tx3.TransactionDate);
        Assert.Null(tx3.Debit);
        Assert.Equal(5000.00m, tx3.Credit);
        Assert.Equal(5000.00m, tx3.Amount);
        Assert.Equal("Credit", tx3.TransactionType);
        Assert.Equal(12353.88m, tx3.Balance);

        // Transaction 5: Cheque extraction
        var tx5 = result.Transactions[4];
        Assert.Equal("044123", tx5.Reference);
        Assert.Equal(1500.00m, tx5.Debit);
        Assert.Null(tx5.Credit);
    }

    #endregion

    #region Test 05: Multi-Line Narration Reconstruction

    [Fact]
    public void Test05_PNB_MultiLine_Narration_Reconstruction()
    {
        var extraction = BuildStandardPnbExtraction(1);
        var detection = _detector.DetectBank(extraction);
        var result = _pnbParser.Parse(extraction, detection);

        Assert.True(result.Success);

        // Transaction 4 has narration split across two lines:
        // Line 1: QUARTERLY SERVICE GST EXTRA
        // Line 2: CHARGES FOR Q1 REF: TAX9988
        var tx4 = result.Transactions[3];
        Assert.Contains("QUARTERLY SERVICE", tx4.Description);
        Assert.Contains("CHARGES FOR Q1", tx4.Description);
        Assert.Contains("GST EXTRA", tx4.Description);
        Assert.Contains("TAX9988", tx4.Description);
    }

    #endregion

    #region Test 06: Multi-Page Statement with Header and Footer Exclusion

    [Fact]
    public void Test06_PNB_MultiPage_ContinuesAcrossPages_IgnoresHeadersAndFooters()
    {
        var extraction = BuildStandardPnbExtraction(2);
        var detection = _detector.DetectBank(extraction);
        var result = _pnbParser.Parse(extraction, detection);

        Assert.True(result.Success);
        // Page 1 has 5 transactions, Page 2 has 2 transactions -> Total 7
        Assert.Equal(7, result.Transactions.Count);

        // Ensure page 2 transactions are recorded with page 2
        Assert.Equal(1, result.Transactions[0].SourcePageNumber);
        Assert.Equal(2, result.Transactions[5].SourcePageNumber);
        Assert.Equal(2, result.Transactions[6].SourcePageNumber);

        // Ensure repeated column headers or "Page Total" do NOT produce transactions
        Assert.DoesNotContain(result.Transactions, t => t.Description.Contains("Tran Date", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Transactions, t => t.Description.Contains("Page Total", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Transactions, t => t.Description.Contains("Grand Total", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Test 07: Running Balance Continuity Validation

    [Fact]
    public void Test07_PNB_RunningBalance_Continuity_Validated()
    {
        var extraction = BuildStandardPnbExtraction(1);
        var detection = _detector.DetectBank(extraction);
        var result = _pnbParser.Parse(extraction, detection);

        Assert.True(result.Success);
        // All transactions mathematically continuous -> 0 warnings
        Assert.Empty(result.Warnings);
    }

    #endregion

    #region Test 08: Running Balance Mismatch Generates Warning

    [Fact]
    public void Test08_PNB_RunningBalance_Mismatch_GeneratesWarning()
    {
        var extraction = BuildStandardPnbExtraction(1, includeMismatch: true);
        var detection = _detector.DetectBank(extraction);
        var result = _pnbParser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("Balance continuity mismatch"));
    }

    #endregion

    #region Test 09: Opening and Closing Balance Extraction

    [Fact]
    public void Test09_PNB_Opening_And_Closing_Balance_Resolved()
    {
        var extraction = BuildStandardPnbExtraction(1);
        var detection = _detector.DetectBank(extraction);
        var result = _pnbParser.Parse(extraction, detection);

        Assert.True(result.Success);
        // Row 1 balance is 7471.88 with Debit 118.00 -> Opening was 7471.88 + 118.00 = 7589.88
        // Last row (Row 5) balance is 10735.88
        Assert.Equal(10735.88m, result.Transactions.Last().Balance);
    }

    #endregion

    #region Test 10: Duplicate Protection across Page Boundaries

    [Fact]
    public void Test10_PNB_DuplicateProtection_AcrossPageBoundaries()
    {
        var extraction = BuildSyntheticPnbExtraction(pages =>
        {
            // Page 1
            var p1Rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 40, [("punjab", 50.0), ("national", 100.0), ("bank", 155.0)]),
                CreateRow(1, 60, [("Statement", 50.0), ("of", 115.0), ("Account", 132.0), ("No:", 185.0), ("9967002100001746", 210.0)]),
                CreateRow(1, 80, [("Tran", 45.0), ("Date", 70.0), ("Withdrawal", 110.0), ("Deposit", 175.0), ("Balance", 235.0), ("CHQ.", 335.0), ("Narration", 400.0)]),
                CreateRow(1, 100, [("14-04-2024", 45.0), ("118.00", 110.0), ("7471.88", 235.0), ("Cr.", 275.0), ("INCIDENTAL", 400.0), ("CHARGES", 455.0)])
            };
            pages.Add(CreatePage(1, p1Rows));

            // Page 2 repeats the exact same transaction as a boundary artifact
            var p2Rows = new List<PdfCandidateRow>
            {
                CreateRow(2, 40, [("punjab", 50.0), ("national", 100.0), ("bank", 155.0)]),
                CreateRow(2, 60, [("Tran", 45.0), ("Date", 70.0), ("Withdrawal", 110.0), ("Deposit", 175.0), ("Balance", 235.0), ("CHQ.", 335.0), ("Narration", 400.0)]),
                CreateRow(2, 80, [("14-04-2024", 45.0), ("118.00", 110.0), ("7471.88", 235.0), ("Cr.", 275.0), ("INCIDENTAL", 400.0), ("CHARGES", 455.0)]),
                CreateRow(2, 100, [("15-04-2024", 45.0), ("500.00", 110.0), ("6971.88", 235.0), ("Cr.", 275.0), ("ATM", 400.0), ("CASH", 435.0)])
            };
            pages.Add(CreatePage(2, p2Rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _pnbParser.Parse(extraction, detection);

        Assert.True(result.Success);
        // Only 2 unique transactions should be recorded, the duplicate on page 2 ignored
        Assert.Equal(2, result.Transactions.Count);
    }

    #endregion

    #region Test 11: Invalid or Non-PNB PDF Rejected Gracefully

    [Fact]
    public void Test11_PNB_InvalidOrNonPnbPdf_RejectedGracefully()
    {
        var emptyExtraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            HasUsableText = false,
            Pages = []
        };

        var detection = new BankDetectionResult { DetectedBank = BankType.Unknown, IsSupported = false };
        var result = _pnbParser.Parse(emptyExtraction, detection);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    #endregion

    #region Test 12: PNB Finacle Layout 2 (NetBanking Tabular Statement)

    [Fact]
    public void Test12_PNB_FinacleLayout2_ParsesTabularTransactions()
    {
        var extraction = BuildSyntheticPnbExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 40, [("PUNJAB", 50.0), ("NATIONAL", 100.0), ("BANK", 160.0)]),
                CreateRow(1, 60, [("Statement", 50.0), ("of", 115.0), ("Account", 135.0), ("9967002100001746", 195.0)]),
                CreateRow(1, 80, [("IFSC:", 50.0), ("PUNB0996700", 90.0)]),
                // Layout 2 header: Date | Value Date | Description | Cheque No | Withdrawal | Deposit | Balance
                CreateRow(1, 100, [
                    ("Txn", 40.0), ("Date", 60.0),
                    ("Value", 90.0), ("Date", 118.0),
                    ("Description", 150.0),
                    ("Cheque", 330.0), ("No", 365.0),
                    ("Withdrawal", 400.0),
                    ("Deposit", 465.0),
                    ("Balance", 530.0)
                ]),
                // Txn 1: Debit 250.00
                CreateRow(1, 120, [
                    ("05-04-2024", 40.0),
                    ("05-04-2024", 90.0),
                    ("CHQ", 150.0), ("PAID", 175.0), ("TO", 205.0), ("SUPPLIER", 225.0),
                    ("001234", 330.0),
                    ("250.00", 400.0),
                    ("5000.00", 530.0)
                ]),
                // Txn 2: Credit 1500.00
                CreateRow(1, 140, [
                    ("10-04-2024", 40.0),
                    ("10-04-2024", 90.0),
                    ("NEFT", 150.0), ("INWARD", 185.0), ("PUNBN123456", 230.0),
                    ("1,500.00", 465.0),
                    ("6500.00", 530.0)
                ])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _pnbParser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Equal(2, result.Transactions.Count);

        Assert.Equal(250.00m, result.Transactions[0].Debit);
        Assert.Equal("001234", result.Transactions[0].Reference);

        Assert.Equal(1500.00m, result.Transactions[1].Credit);
        Assert.Equal("PUNBN123456", result.Transactions[1].Utr);
    }

    #endregion

    #region Test 13: End-to-End API Registration, Upload & Parse

    [Fact]
    public async Task Test13_PNB_EndToEnd_ApiUploadAndParse_Succeeds()
    {
        var (client, auth) = await RegisterUserAsync("pnb_e2e");

        // Create digital PDF bytes using PdfPig
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);

        // Header
        page.AddText("punjab national bank ...the name you can BANK upon!", 10, new PdfPoint(50, 750), font);
        page.AddText("Statement of Account No: 9967002100001746", 10, new PdfPoint(50, 735), font);
        page.AddText("Customer Name: HINDUSTAN CARGO MOVERS", 10, new PdfPoint(50, 720), font);
        page.AddText("IFSC Code: PUNB0996700  Statement for Period : 01-04-2024 to 31-03-2025", 9, new PdfPoint(50, 705), font);

        // Columns
        page.AddText("Tran Date", 8, new PdfPoint(50, 680), font);
        page.AddText("Withdrawal", 8, new PdfPoint(110, 680), font);
        page.AddText("Deposit", 8, new PdfPoint(175, 680), font);
        page.AddText("Balance", 8, new PdfPoint(235, 680), font);
        page.AddText("Alpha", 8, new PdfPoint(300, 680), font);
        page.AddText("CHQ. NO.", 8, new PdfPoint(335, 680), font);
        page.AddText("Narration", 8, new PdfPoint(400, 680), font);
        page.AddText("Additional Info", 8, new PdfPoint(500, 680), font);

        // Row 1
        page.AddText("14-04-2024", 8, new PdfPoint(50, 660), font);
        page.AddText("118.00", 8, new PdfPoint(110, 660), font);
        page.AddText("7471.88 Cr.", 8, new PdfPoint(235, 660), font);
        page.AddText("INCIDENTAL CHARGES", 8, new PdfPoint(400, 660), font);

        // Row 2
        page.AddText("30-04-2024", 8, new PdfPoint(50, 640), font);
        page.AddText("118.00", 8, new PdfPoint(110, 640), font);
        page.AddText("7353.88 Cr.", 8, new PdfPoint(235, 640), font);
        page.AddText("STT OF AC CHARGES", 8, new PdfPoint(400, 640), font);

        // Row 3
        page.AddText("15-05-2024", 8, new PdfPoint(50, 620), font);
        page.AddText("5000.00", 8, new PdfPoint(175, 620), font);
        page.AddText("12353.88 Cr.", 8, new PdfPoint(235, 620), font);
        page.AddText("UPI/413612345678/CREDIT", 8, new PdfPoint(400, 620), font);

        byte[] pdfBytes = builder.Build();

        // 1. Upload statement PDF
        var form = CreateMultipartPdf("PNB_Statement_April.pdf", pdfBytes);
        var uploadResp = await client.PostAsync("/api/statements/upload", form);
        Assert.Equal(HttpStatusCode.OK, uploadResp.StatusCode);

        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>(JsonOptions);
        Assert.NotNull(uploadResult);
        var fileId = uploadResult.FileId;

        // 2. Parse statement
        var parseResp = await client.PostAsync($"/api/statements/{fileId}/parse", null);
        var contentStr = await parseResp.Content.ReadAsStringAsync();
        Assert.True(parseResp.IsSuccessStatusCode, $"Parse failed with status {parseResp.StatusCode}: {contentStr}");

        // 3. Verify review endpoint
        var reviewResp = await client.GetAsync($"/api/statements/{fileId}/transactions");
        Assert.Equal(HttpStatusCode.OK, reviewResp.StatusCode);
        var reviewPage = await reviewResp.Content.ReadFromJsonAsync<StatementTransactionsResponse>(JsonOptions);
        Assert.NotNull(reviewPage);
        Assert.Equal(3, reviewPage.TotalCount);
        Assert.Equal("Punjab National Bank", reviewPage.BankName);
        Assert.Equal("PNB-v1", reviewPage.ParserVersion);
    }

    #endregion

    #region Test 14: Real Scanned PDF Handling

    [Fact]
    public void Test14_PNB_RealScannedPdf_HandledGracefully()
    {
        if (!File.Exists(RealScannedFixturePath))
        {
            _output.WriteLine($"Scanned fixture not found at {RealScannedFixturePath}, skipping.");
            return;
        }

        using var doc = PdfDocument.Open(RealScannedFixturePath);
        var page1 = doc.GetPage(1);
        var words = page1.GetWords().ToList();

        // The real PNB scan has 0 digital words extracted by PdfPig
        Assert.Empty(words);
    }

    #endregion

    #region Test 15: Reference Image Exact Format Validation

    [Fact]
    public void Test15_PNB_ReferenceImage_ExactLayout_Validated()
    {
        // Synthesizes the exact text coordinate stream corresponding to the provided PNB statement reference image:
        // Punjab National Bank Passbook/Statement:
        // Tran Date | Withdrawal | Deposit | Balance | Alpha CHQ. NO. | Narration | Additional Info
        var extraction = BuildSyntheticPnbExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>();
            double y = 40.0;

            // Bilingual Bank Header
            rows.Add(CreateRow(1, y, [
                ("पंजाब", 45.0), ("नैशनल", 85.0), ("बैंक", 130.0),
                ("punjab", 200.0), ("national", 245.0), ("bank", 300.0),
                ("...भरोसे", 350.0), ("का", 400.0), ("प्रतीक", 420.0), ("!", 460.0),
                ("...the", 475.0), ("name", 505.0), ("you", 535.0), ("can", 555.0), ("BANK", 575.0), ("upon!", 610.0)
            ]));
            y += 18.0;

            rows.Add(CreateRow(1, y, [
                ("Statement", 45.0), ("of", 110.0), ("Account", 130.0), ("No:", 185.0), ("9967002100001746", 210.0),
                ("Printed", 450.0), ("By:", 495.0), ("5189615", 515.0)
            ]));
            y += 16.0;

            rows.Add(CreateRow(1, y, [
                ("DATE:", 450.0), ("Aug", 485.0), ("29,", 510.0), ("2025", 530.0), ("3:39:04", 560.0), ("PM", 605.0)
            ]));
            y += 16.0;

            rows.Add(CreateRow(1, y, [
                ("Customer", 45.0), ("Name:", 105.0),
                ("HINDUSTAN", 150.0), ("CARGO", 225.0), ("MOVERS", 275.0), ("AND", 330.0), ("NAYEEM", 360.0), ("UDDIN", 410.0), ("KHAN", 450.0)
            ]));
            y += 16.0;

            rows.Add(CreateRow(1, y, [
                ("CKYC", 45.0), ("No.:", 85.0), ("XXXXXXXX", 150.0)
            ]));
            y += 16.0;

            rows.Add(CreateRow(1, y, [
                ("Customer", 45.0), ("Address:", 105.0),
                ("SHIVKRUPA", 150.0), ("COMPLEX", 225.0), ("WADDHAMNAA", 285.0), ("NAGPUR", 375.0), ("MAHARASHTRA", 430.0), ("440023", 525.0)
            ]));
            y += 16.0;

            rows.Add(CreateRow(1, y, [
                ("Branch", 45.0), ("Address:", 95.0),
                ("PLOT", 150.0), ("NO.5,WADDHMNA,SQR", 185.0), ("AMRAVATI", 310.0), ("RD,DIST,NAGPR", 375.0), ("MAHARASHTRA", 470.0), ("WADDHAMNA", 565.0), ("440023", 645.0)
            ]));
            y += 16.0;

            rows.Add(CreateRow(1, y, [
                ("Branch", 45.0), ("Contact", 90.0), ("No.:", 140.0), ("0712-2553420", 170.0)
            ]));
            y += 16.0;

            rows.Add(CreateRow(1, y, [
                ("Customer", 45.0), ("Care", 100.0), ("No.:", 135.0), ("1800", 165.0), ("1800/1800", 200.0), ("2021", 265.0)
            ]));
            y += 16.0;

            rows.Add(CreateRow(1, y, [
                ("IFSC", 45.0), ("Code:", 80.0), ("PUNB0996700", 120.0),
                ("MICR", 300.0), ("Code:", 335.0), ("440024503", 375.0)
            ]));
            y += 16.0;

            rows.Add(CreateRow(1, y, [
                ("Acct", 45.0), ("Currency:", 80.0), ("INR", 140.0)
            ]));
            y += 16.0;

            rows.Add(CreateRow(1, y, [
                ("Statement", 45.0), ("for", 105.0), ("Period", 125.0), (":", 165.0), ("01-04-2024", 175.0), ("to", 245.0), ("31-03-2025", 260.0)
            ]));
            y += 22.0;

            // Table Header: Tran Date | Withdrawal | Deposit | Balance | Alpha CHQ. NO. | Narration | Additional Info
            rows.Add(CreateRow(1, y, [
                ("Tran", 45.0), ("Date", 70.0),
                ("Withdrawal", 110.0),
                ("Deposit", 175.0),
                ("Balance", 235.0),
                ("Alpha", 295.0), ("CHQ.", 335.0), ("NO.", 365.0),
                ("Narration", 400.0),
                ("Additional", 500.0), ("Info", 555.0)
            ]));
            y += 18.0;

            // Row 1: 14-04-2024 | 118.00 | [blank] | 7471.88 Cr. | [blank] | INCIDENTAL CHARGES
            rows.Add(CreateRow(1, y, [
                ("14-04-2024", 45.0),
                ("118.00", 110.0),
                ("7471.88", 235.0), ("Cr.", 275.0),
                ("INCIDENTAL", 400.0), ("CHARGES", 465.0)
            ]));
            y += 16.0;

            // Row 2: 30-04-2024 | 118.00 | [blank] | 7353.88 Cr. | [blank] | STT OF AC CHARGES
            rows.Add(CreateRow(1, y, [
                ("30-04-2024", 45.0),
                ("118.00", 110.0),
                ("7353.88", 235.0), ("Cr.", 275.0),
                ("STT", 400.0), ("OF", 425.0), ("AC", 445.0), ("CHARGES", 465.0)
            ]));
            y += 16.0;

            // Row 3: 13-07-2024 | 118.00 | [blank] | 7235.88 Cr. | [blank] | INCIDENTAL CHARGES
            rows.Add(CreateRow(1, y, [
                ("13-07-2024", 45.0),
                ("118.00", 110.0),
                ("7235.88", 235.0), ("Cr.", 275.0),
                ("INCIDENTAL", 400.0), ("CHARGES", 465.0)
            ]));
            y += 16.0;

            // Row 4: 12-10-2024 | 118.00 | [blank] | 7117.88 Cr. | [blank] | INCIDENTAL CHARGES
            rows.Add(CreateRow(1, y, [
                ("12-10-2024", 45.0),
                ("118.00", 110.0),
                ("7117.88", 235.0), ("Cr.", 275.0),
                ("INCIDENTAL", 400.0), ("CHARGES", 465.0)
            ]));
            y += 16.0;

            // Row 5: 11-01-2025 | 118.00 | [blank] | 6999.88 Cr. | [blank] | INCIDENTAL CHARGES
            rows.Add(CreateRow(1, y, [
                ("11-01-2025", 45.0),
                ("118.00", 110.0),
                ("6999.88", 235.0), ("Cr.", 275.0),
                ("INCIDENTAL", 400.0), ("CHARGES", 465.0)
            ]));
            y += 20.0;

            // Footer Section: Page Total & Grand
            rows.Add(CreateRow(1, y, [
                ("Page", 45.0), ("Total", 75.0),
                ("590.00", 110.0),
                ("0.00", 175.0),
                ("Page 1 of 1", 500.0)
            ]));
            y += 16.0;

            rows.Add(CreateRow(1, y, [
                ("Grand", 45.0),
                ("590.00", 110.0),
                ("0.00", 175.0)
            ]));

            pages.Add(CreatePage(1, rows));
        });

        // 1. Detection
        var detection = _detector.DetectBank(extraction);
        Assert.NotNull(detection);
        Assert.True(detection.IsSupported);
        Assert.Equal(BankType.PNB, detection.DetectedBank);
        Assert.Equal("9967002100001746", detection.AccountNumber);
        Assert.Contains("HINDUSTAN CARGO MOVERS", detection.CustomerName);
        Assert.Equal(new DateTime(2024, 4, 1), detection.StatementFrom);
        Assert.Equal(new DateTime(2025, 3, 31), detection.StatementTo);

        // 2. Parsing
        var result = _pnbParser.Parse(extraction, detection);
        Assert.True(result.Success);
        Assert.Equal(5, result.Transactions.Count);
        Assert.Empty(result.Warnings);

        // 3. Exact Transaction Assertions
        var tx1 = result.Transactions[0];
        Assert.Equal(new DateTime(2024, 4, 14), tx1.TransactionDate);
        Assert.Equal(118.00m, tx1.Debit);
        Assert.Null(tx1.Credit);
        Assert.Equal(7471.88m, tx1.Balance);
        Assert.Equal("INCIDENTAL CHARGES", tx1.Description);

        var tx2 = result.Transactions[1];
        Assert.Equal(new DateTime(2024, 4, 30), tx2.TransactionDate);
        Assert.Equal(118.00m, tx2.Debit);
        Assert.Null(tx2.Credit);
        Assert.Equal(7353.88m, tx2.Balance);
        Assert.Equal("STT OF AC CHARGES", tx2.Description);

        var tx3 = result.Transactions[2];
        Assert.Equal(new DateTime(2024, 7, 13), tx3.TransactionDate);
        Assert.Equal(118.00m, tx3.Debit);
        Assert.Equal(7235.88m, tx3.Balance);
        Assert.Equal("INCIDENTAL CHARGES", tx3.Description);

        var tx4 = result.Transactions[3];
        Assert.Equal(new DateTime(2024, 10, 12), tx4.TransactionDate);
        Assert.Equal(118.00m, tx4.Debit);
        Assert.Equal(7117.88m, tx4.Balance);
        Assert.Equal("INCIDENTAL CHARGES", tx4.Description);

        var tx5 = result.Transactions[4];
        Assert.Equal(new DateTime(2025, 1, 11), tx5.TransactionDate);
        Assert.Equal(118.00m, tx5.Debit);
        Assert.Equal(6999.88m, tx5.Balance);
        Assert.Equal("INCIDENTAL CHARGES", tx5.Description);

        // 4. Financial Calculations & Reconciliation
        decimal totalDebits = result.Transactions.Sum(t => t.Debit ?? 0m);
        decimal totalCredits = result.Transactions.Sum(t => t.Credit ?? 0m);
        Assert.Equal(590.00m, totalDebits);
        Assert.Equal(0.00m, totalCredits);

        decimal openingBalance = (result.Transactions[0].Balance ?? 0m) + (result.Transactions[0].Debit ?? 0m) - (result.Transactions[0].Credit ?? 0m);
        decimal closingBalance = result.Transactions.Last().Balance ?? 0m;
        Assert.Equal(7589.88m, openingBalance);
        Assert.Equal(6999.88m, closingBalance);
        Assert.Equal(closingBalance, openingBalance + totalCredits - totalDebits);
    }

    #endregion
}
