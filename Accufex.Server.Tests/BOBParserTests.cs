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

public class BOBParserTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;
    private readonly BankDetector _detector = new(NullLogger<BankDetector>.Instance);
    private readonly BOBStatementParser _bobParser = new(NullLogger<BOBStatementParser>.Instance);
    private readonly BankParserRegistry _registry;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public BOBParserTests(CustomWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
        _registry = new BankParserRegistry([_bobParser]);
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

        var email = $"{prefix}_{Guid.NewGuid():N}@bob-test.com";
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

    private static PdfExtractionResult BuildSyntheticBobExtraction(Action<List<PdfPageResult>> configurePages)
    {
        var pages = new List<PdfPageResult>();
        configurePages(pages);

        return new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            OriginalFileName = "BOB_Statement_Sample.pdf",
            PageCount = pages.Count,
            ExtractionStatus = "DigitalTextExtracted",
            HasUsableText = true,
            PdfType = "DigitalWithText",
            Pages = pages
        };
    }

    /// <summary>
    /// Builds a representative Bank of Baroda Format 1 extraction (Baroda Connect / Finacle Layout)
    /// (S.No | Date | Value Date | Description | Cheque No | Withdrawal(Dr) | Deposit(Cr) | Balance(Cr/Dr))
    /// </summary>
    private static PdfExtractionResult BuildStandardBobExtraction(int pageCount = 1, bool includeMismatch = false)
    {
        return BuildSyntheticBobExtraction(pages =>
        {
            for (int p = 1; p <= pageCount; p++)
            {
                var rows = new List<PdfCandidateRow>();
                double y = 40.0;

                // Header Block
                rows.Add(CreateRow(p, y, [
                    ("Bank", 50.0), ("of", 80.0), ("Baroda", 95.0),
                    ("India's", 260.0), ("International", 305.0), ("Bank", 380.0)
                ]));
                y += 18.0;

                rows.Add(CreateRow(p, y, [
                    ("Statement", 50.0), ("of", 115.0), ("Account", 132.0),
                    ("Printed", 420.0), ("Date:", 465.0), ("31/03/2025", 500.0)
                ]));
                y += 16.0;

                rows.Add(CreateRow(p, y, [
                    ("Account", 50.0), ("No", 98.0), (":", 116.0), ("01230200001234", 125.0),
                    ("Customer", 320.0), ("ID:", 375.0), ("BARB12345", 400.0)
                ]));
                y += 16.0;

                rows.Add(CreateRow(p, y, [
                    ("Customer", 50.0), ("Name", 102.0), (":", 135.0), ("BARODA", 145.0), ("TRADING", 195.0), ("ENTERPRISES", 250.0)
                ]));
                y += 16.0;

                rows.Add(CreateRow(p, y, [
                    ("IFSC", 50.0), ("Code", 80.0), (":", 110.0), ("BARB0WADNAG", 120.0),
                    ("Currency", 320.0), (":", 370.0), ("INR", 380.0)
                ]));
                y += 16.0;

                rows.Add(CreateRow(p, y, [
                    ("Statement", 50.0), ("Period", 108.0), (":", 145.0), ("01/04/2024", 155.0), ("to", 225.0), ("31/03/2025", 240.0)
                ]));
                y += 20.0;

                // Opening balance line on Page 1
                if (p == 1)
                {
                    rows.Add(CreateRow(p, y, [
                        ("OPENING", 50.0), ("BALANCE", 105.0), (":", 160.0), ("10,000.00", 520.0), ("Cr.", 575.0)
                    ]));
                    y += 18.0;
                }

                // Table Header
                rows.Add(CreateRow(p, y, [
                    ("S.No", 40.0),
                    ("Date", 80.0),
                    ("Value Date", 125.0),
                    ("Description", 185.0),
                    ("Cheque No", 325.0),
                    ("Withdrawal(Dr)", 380.0),
                    ("Deposit(Cr)", 455.0),
                    ("Balance(Cr/Dr)", 525.0)
                ]));
                y += 18.0;

                if (p == 1)
                {
                    // Row 1: S.No 1 | Date 05/04/2024 | Debit 500.00 -> Balance 9500.00 Cr
                    rows.Add(CreateRow(p, y, [
                        ("1", 40.0),
                        ("05/04/2024", 75.0),
                        ("05/04/2024", 125.0),
                        ("CHQ", 185.0), ("PAID", 215.0), ("TO", 245.0), ("SUPPLIER", 265.0),
                        ("001122", 325.0),
                        ("500.00", 380.0),
                        ("9500.00", 525.0), ("Cr.", 570.0)
                    ]));
                    y += 16.0;

                    // Row 2: S.No 2 | Date 12/04/2024 | Credit 15000.00 -> Balance 24500.00 Cr
                    rows.Add(CreateRow(p, y, [
                        ("2", 40.0),
                        ("12/04/2024", 75.0),
                        ("12/04/2024", 125.0),
                        ("NEFT/BARBN123456789/SETTLEMENT", 185.0),
                        ("15,000.00", 455.0),
                        ("24500.00", 525.0), ("Cr.", 570.0)
                    ]));
                    y += 16.0;

                    // Row 3: S.No 3 | Date 18/04/2024 | Debit 2500.00 -> Balance 22000.00 Cr with multi-line narration
                    rows.Add(CreateRow(p, y, [
                        ("3", 40.0),
                        ("18/04/2024", 75.0),
                        ("18/04/2024", 125.0),
                        ("UPI/412599887766/PAYMENT", 185.0), ("TO", 280.0),
                        ("2,500.00", 380.0),
                        (includeMismatch ? "20000.00" : "22000.00", 525.0), ("Cr.", 570.0)
                    ]));
                    y += 14.0;
                    // Multi-line continuation for Row 3
                    rows.Add(CreateRow(p, y, [
                        ("MERCHANT", 185.0), ("SERVICES", 245.0), ("INVOICE#99881", 305.0)
                    ]));
                    y += 16.0;

                    // Row 4: S.No 4 | Date 25/04/2024 | Debit 118.00 -> Balance 21882.00 Cr
                    rows.Add(CreateRow(p, y, [
                        ("4", 40.0),
                        ("25/04/2024", 75.0),
                        ("25/04/2024", 125.0),
                        ("SMS", 185.0), ("ALERT", 215.0), ("CHARGES", 255.0),
                        ("118.00", 380.0),
                        (includeMismatch ? "19882.00" : "21882.00", 525.0), ("Cr.", 570.0)
                    ]));
                    y += 16.0;

                    // Row 5: S.No 5 | Date 30/04/2024 | Credit 5000.00 -> Balance 26882.00 Cr
                    rows.Add(CreateRow(p, y, [
                        ("5", 40.0),
                        ("30/04/2024", 75.0),
                        ("30/04/2024", 125.0),
                        ("IMPS/512345678901/TRANSFER", 185.0),
                        ("5,000.00", 455.0),
                        (includeMismatch ? "24882.00" : "26882.00", 525.0), ("Cr.", 570.0)
                    ]));
                    y += 20.0;
                }
                else
                {
                    // Page 2 rows
                    // Row 6: S.No 6 | Date 10/05/2024 | Debit 1200.00 -> Balance 25682.00 Cr
                    rows.Add(CreateRow(p, y, [
                        ("6", 40.0),
                        ("10/05/2024", 75.0),
                        ("10/05/2024", 125.0),
                        ("CHEQUE", 185.0), ("CLEARING", 235.0),
                        ("004455", 325.0),
                        ("1,200.00", 380.0),
                        ("25682.00", 525.0), ("Cr.", 570.0)
                    ]));
                    y += 16.0;

                    // Row 7: S.No 7 | Date 20/05/2024 | Credit 3000.00 -> Balance 28682.00 Cr
                    rows.Add(CreateRow(p, y, [
                        ("7", 40.0),
                        ("20/05/2024", 75.0),
                        ("20/05/2024", 125.0),
                        ("CASH", 185.0), ("DEPOSIT", 220.0), ("BRANCH", 270.0),
                        ("3,000.00", 455.0),
                        ("28682.00", 525.0), ("Cr.", 570.0)
                    ]));
                    y += 20.0;
                }

                // Footer Block
                rows.Add(CreateRow(p, y, [
                    ("Page", 45.0), ("Total", 75.0),
                    (p == 1 ? "3118.00" : "1200.00", 380.0),
                    (p == 1 ? "20000.00" : "3000.00", 455.0),
                    ($"Page {p} of {pageCount}", 520.0)
                ]));
                y += 15.0;

                if (p == pageCount)
                {
                    rows.Add(CreateRow(p, y, [
                        ("Grand", 45.0), ("Total", 80.0),
                        ("4318.00", 380.0),
                        ("23000.00", 455.0),
                        ("CLOSING", 490.0), ("BALANCE", 535.0), (":", 565.0), ("28682.00", 575.0)
                    ]));
                }

                pages.Add(CreatePage(p, rows));
            }
        });
    }

    #region Test 01: BOB Format Detection

    [Fact]
    public void Test01_BOB_Format_Detection()
    {
        var extraction = BuildStandardBobExtraction(1);
        var detection = _detector.DetectBank(extraction);

        Assert.NotNull(detection);
        Assert.True(detection.IsSupported, "Document should be detected as a supported bank.");
        Assert.Equal(BankType.BOB, detection.DetectedBank);
        Assert.Equal("Bank of Baroda", detection.BankName);
        Assert.Equal("BOB-v1", detection.DetectedFormat);
        Assert.True(detection.Confidence >= 0.90, $"Confidence {detection.Confidence} should be >= 0.90");
        Assert.Equal("01230200001234", detection.AccountNumber);
        Assert.Contains("BARODA TRADING", detection.CustomerName);
        Assert.Equal(new DateTime(2024, 4, 1), detection.StatementFrom);
        Assert.Equal(new DateTime(2025, 3, 31), detection.StatementTo);
    }

    #endregion

    #region Test 02: BOB Detector Disambiguation (Does NOT misclassify other banks)

    [Fact]
    public void Test02_BOB_Detector_Disambiguation_All_Banks()
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

        // 8. Verify PNB detection remains intact
        var pnbBlocks = new List<PdfTextBlock>
        {
            new() { Text = "PUNJAB NATIONAL BANK", X = 50, Y = 50, Width = 140, Height = 12 },
            new() { Text = "PUNB0123456", X = 50, Y = 70, Width = 80, Height = 10 },
            new() { Text = "...the name you can BANK upon", X = 50, Y = 85, Width = 140, Height = 10 },
            new() { Text = "Tran Date Withdrawal Deposit Balance Alpha CHQ. NO. Narration Additional Info", X = 50, Y = 100, Width = 500, Height = 10 }
        };
        var pnbExtraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            HasUsableText = true,
            Pages = [new PdfPageResult { PageNumber = 1, Width = 595, Height = 842, TextBlocks = pnbBlocks, RawText = string.Join(" ", pnbBlocks.Select(b => b.Text)) }]
        };
        var pnbDet = _detector.DetectBank(pnbExtraction);
        Assert.Equal(BankType.PNB, pnbDet.DetectedBank);
    }

    #endregion

    #region Test 03: CanParse Logic

    [Fact]
    public void Test03_BOB_CanParse_Validation()
    {
        var validBob = new BankDetectionResult
        {
            DetectedBank = BankType.BOB,
            IsSupported = true
        };
        Assert.True(_bobParser.CanParse(validBob));

        var unsupportedBob = new BankDetectionResult
        {
            DetectedBank = BankType.BOB,
            IsSupported = false
        };
        Assert.False(_bobParser.CanParse(unsupportedBob));

        var otherBank = new BankDetectionResult
        {
            DetectedBank = BankType.Hdfc,
            IsSupported = true
        };
        Assert.False(_bobParser.CanParse(otherBank));

        Assert.False(_bobParser.CanParse(null!));
    }

    #endregion

    #region Test 04: Debit and Credit Transaction Direction Integrity

    [Fact]
    public void Test04_BOB_Debit_And_Credit_Direction_Integrity()
    {
        var extraction = BuildStandardBobExtraction(1);
        var detection = _detector.DetectBank(extraction);
        var result = _bobParser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Equal(5, result.Transactions.Count);

        // Transaction 1: Debit 500.00
        var tx1 = result.Transactions[0];
        Assert.Equal(new DateTime(2024, 4, 5), tx1.TransactionDate);
        Assert.Equal(500.00m, tx1.Debit);
        Assert.Null(tx1.Credit);
        Assert.Equal(500.00m, tx1.Amount);
        Assert.Equal("Debit", tx1.TransactionType);
        Assert.Equal(9500.00m, tx1.Balance);
        Assert.Equal("001122", tx1.Reference);

        // Transaction 2: Credit 15,000.00
        var tx2 = result.Transactions[1];
        Assert.Equal(new DateTime(2024, 4, 12), tx2.TransactionDate);
        Assert.Null(tx2.Debit);
        Assert.Equal(15000.00m, tx2.Credit);
        Assert.Equal(15000.00m, tx2.Amount);
        Assert.Equal("Credit", tx2.TransactionType);
        Assert.Equal(24500.00m, tx2.Balance);
        Assert.Equal("BARBN123456789", tx2.Utr);

        // Transaction 4: Debit 118.00
        var tx4 = result.Transactions[3];
        Assert.Equal(118.00m, tx4.Debit);
        Assert.Null(tx4.Credit);
        Assert.Equal(21882.00m, tx4.Balance);
    }

    #endregion

    #region Test 05: Multi-Line Narration Reconstruction

    [Fact]
    public void Test05_BOB_MultiLine_Narration_Reconstruction()
    {
        var extraction = BuildStandardBobExtraction(1);
        var detection = _detector.DetectBank(extraction);
        var result = _bobParser.Parse(extraction, detection);

        Assert.True(result.Success);

        // Transaction 3 narration continues across lines:
        // Line 1: UPI/412599887766/PAYMENT TO
        // Line 2: MERCHANT SERVICES INVOICE#99881
        var tx3 = result.Transactions[2];
        Assert.Contains("UPI/412599887766/PAYMENT TO", tx3.Description);
        Assert.Contains("MERCHANT SERVICES", tx3.Description);
        Assert.Contains("INVOICE#99881", tx3.Description);
    }

    #endregion

    #region Test 06: Multi-Page Statement with Header and Footer Exclusion

    [Fact]
    public void Test06_BOB_MultiPage_ContinuesAcrossPages_IgnoresHeadersAndFooters()
    {
        var extraction = BuildStandardBobExtraction(2);
        var detection = _detector.DetectBank(extraction);
        var result = _bobParser.Parse(extraction, detection);

        Assert.True(result.Success);
        // Page 1 has 5 transactions, Page 2 has 2 transactions -> Total 7
        Assert.Equal(7, result.Transactions.Count);

        Assert.Equal(1, result.Transactions[0].SourcePageNumber);
        Assert.Equal(2, result.Transactions[5].SourcePageNumber);
        Assert.Equal(2, result.Transactions[6].SourcePageNumber);

        // Ensure header/footer labels do not appear as descriptions
        Assert.DoesNotContain(result.Transactions, t => t.Description.Contains("Page Total", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Transactions, t => t.Description.Contains("Grand Total", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Transactions, t => t.Description.Contains("India's International Bank", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Test 07: Running Balance Continuity Validation

    [Fact]
    public void Test07_BOB_RunningBalance_Continuity_Validated()
    {
        var extraction = BuildStandardBobExtraction(1);
        var detection = _detector.DetectBank(extraction);
        var result = _bobParser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Empty(result.Warnings);
    }

    #endregion

    #region Test 08: Running Balance Mismatch Generates Warning

    [Fact]
    public void Test08_BOB_RunningBalance_Mismatch_GeneratesWarning()
    {
        var extraction = BuildStandardBobExtraction(1, includeMismatch: true);
        var detection = _detector.DetectBank(extraction);
        var result = _bobParser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("Balance continuity mismatch"));
    }

    #endregion

    #region Test 09: Opening and Closing Balance Extraction

    [Fact]
    public void Test09_BOB_Opening_And_Closing_Balance_Resolved()
    {
        var extraction = BuildStandardBobExtraction(1);
        var detection = _detector.DetectBank(extraction);
        var result = _bobParser.Parse(extraction, detection);

        Assert.True(result.Success);
        // Opening balance was 10000.00
        // Last row (Row 5) balance is 26882.00
        Assert.Equal(26882.00m, result.Transactions.Last().Balance);
    }

    #endregion

    #region Test 10: Duplicate Protection across Page Boundaries

    [Fact]
    public void Test10_BOB_DuplicateProtection_AcrossPageBoundaries()
    {
        var extraction = BuildSyntheticBobExtraction(pages =>
        {
            // Page 1
            var p1Rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 40, [("Bank", 50.0), ("of", 80.0), ("Baroda", 95.0)]),
                CreateRow(1, 60, [("Account", 50.0), ("No:", 98.0), ("01230200001234", 125.0)]),
                CreateRow(1, 80, [("S.No", 40.0), ("Date", 80.0), ("Particulars", 185.0), ("Withdrawal", 380.0), ("Deposit", 455.0), ("Balance", 525.0)]),
                CreateRow(1, 100, [("1", 40.0), ("05/04/2024", 80.0), ("CHQ", 185.0), ("PAID", 220.0), ("500.00", 380.0), ("9500.00", 525.0), ("Cr.", 570.0)])
            };
            pages.Add(CreatePage(1, p1Rows));

            // Page 2 repeats the boundary transaction
            var p2Rows = new List<PdfCandidateRow>
            {
                CreateRow(2, 40, [("Bank", 50.0), ("of", 80.0), ("Baroda", 95.0)]),
                CreateRow(2, 60, [("S.No", 40.0), ("Date", 80.0), ("Particulars", 185.0), ("Withdrawal", 380.0), ("Deposit", 455.0), ("Balance", 525.0)]),
                CreateRow(2, 80, [("1", 40.0), ("05/04/2024", 80.0), ("CHQ", 185.0), ("PAID", 220.0), ("500.00", 380.0), ("9500.00", 525.0), ("Cr.", 570.0)]),
                CreateRow(2, 100, [("2", 40.0), ("06/04/2024", 80.0), ("ATM", 185.0), ("CASH", 220.0), ("1000.00", 380.0), ("8500.00", 525.0), ("Cr.", 570.0)])
            };
            pages.Add(CreatePage(2, p2Rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _bobParser.Parse(extraction, detection);

        Assert.True(result.Success);
        // Only 2 unique transactions should be recorded
        Assert.Equal(2, result.Transactions.Count);
    }

    #endregion

    #region Test 11: Invalid or Non-BOB PDF Rejected Gracefully

    [Fact]
    public void Test11_BOB_InvalidOrNonBobPdf_RejectedGracefully()
    {
        var emptyExtraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            HasUsableText = false,
            Pages = []
        };

        var detection = new BankDetectionResult { DetectedBank = BankType.Unknown, IsSupported = false };
        var result = _bobParser.Parse(emptyExtraction, detection);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    #endregion

    #region Test 12: BOB Format 2 (bob World Mobile App e-Statement)

    [Fact]
    public void Test12_BOB_bobWorld_MobileLayout_ParsesTabularTransactions()
    {
        var extraction = BuildSyntheticBobExtraction(pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 40, [("bob", 50.0), ("World", 75.0), ("Bank", 120.0), ("of", 150.0), ("Baroda", 165.0)]),
                CreateRow(1, 60, [("Account", 50.0), ("Number:", 95.0), ("01230200001234", 150.0)]),
                CreateRow(1, 80, [("IFSC:", 50.0), ("BARB0WADNAG", 85.0)]),
                // Layout 2 headers: Date | Particulars | Cheque No | Withdrawal | Deposit | Balance
                CreateRow(1, 100, [
                    ("Txn", 40.0), ("Date", 65.0),
                    ("Particulars", 120.0),
                    ("Cheque", 320.0), ("No", 355.0),
                    ("Withdrawal", 380.0),
                    ("Deposit", 460.0),
                    ("Balance", 530.0)
                ]),
                // Txn 1: Debit 350.00
                CreateRow(1, 120, [
                    ("02/05/2024", 40.0),
                    ("ELECTRICITY", 120.0), ("BILL", 195.0), ("PAYMENT", 225.0),
                    ("007788", 320.0),
                    ("350.00", 380.0),
                    ("15000.00", 530.0)
                ]),
                // Txn 2: Credit 8000.00
                CreateRow(1, 140, [
                    ("15/05/2024", 40.0),
                    ("NEFT/BARBN998877/SALARY", 120.0),
                    ("8,000.00", 460.0),
                    ("23000.00", 530.0)
                ])
            };
            pages.Add(CreatePage(1, rows));
        });

        var detection = _detector.DetectBank(extraction);
        var result = _bobParser.Parse(extraction, detection);

        Assert.True(result.Success);
        Assert.Equal(2, result.Transactions.Count);

        Assert.Equal(350.00m, result.Transactions[0].Debit);
        Assert.Equal("007788", result.Transactions[0].Reference);

        Assert.Equal(8000.00m, result.Transactions[1].Credit);
        Assert.Equal("BARBN998877", result.Transactions[1].Utr);
    }

    #endregion

    #region Test 13: Transaction Ordering and Amount Precision

    [Fact]
    public void Test13_BOB_Transaction_Ordering_And_Amount_Precision()
    {
        var extraction = BuildStandardBobExtraction(1);
        var detection = _detector.DetectBank(extraction);
        var result = _bobParser.Parse(extraction, detection);

        Assert.True(result.Success);

        // Assert strictly ascending/original order
        Assert.True(result.Transactions[0].TransactionDate <= result.Transactions[1].TransactionDate);
        Assert.True(result.Transactions[1].TransactionDate <= result.Transactions[2].TransactionDate);
        Assert.True(result.Transactions[2].TransactionDate <= result.Transactions[3].TransactionDate);
        Assert.True(result.Transactions[3].TransactionDate <= result.Transactions[4].TransactionDate);

        // Verify monetary precision
        Assert.Equal(500.00m, result.Transactions[0].Amount);
        Assert.Equal(15000.00m, result.Transactions[1].Amount);
        Assert.Equal(2500.00m, result.Transactions[2].Amount);
        Assert.Equal(118.00m, result.Transactions[3].Amount);
        Assert.Equal(5000.00m, result.Transactions[4].Amount);
    }

    #endregion

    #region Test 14: Parser Registration in Registry

    [Fact]
    public void Test14_BOB_ParserRegistration_In_Registry()
    {
        var detection = new BankDetectionResult
        {
            DetectedBank = BankType.BOB,
            BankName = "Bank of Baroda",
            DetectedFormat = "BOB-v1",
            IsSupported = true
        };

        var resolvedParser = _registry.ResolveParser(detection);
        Assert.NotNull(resolvedParser);
        Assert.Equal((int)BankType.BOB, resolvedParser.BankCode);
        Assert.Equal("Bank of Baroda", resolvedParser.BankName);
        Assert.Equal("BOB-v1", resolvedParser.ParserVersion);
    }

    #endregion

    #region Test 15: End-to-End API Registration, Upload & Parse

    [Fact]
    public async Task Test15_BOB_EndToEnd_ApiUploadAndParse_Succeeds()
    {
        var (client, auth) = await RegisterUserAsync("bob_e2e");

        // Create digital PDF bytes using PdfPig
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);

        // Header
        page.AddText("Bank of Baroda India's International Bank", 10, new PdfPoint(50, 750), font);
        page.AddText("Statement of Account No: 01230200001234", 10, new PdfPoint(50, 735), font);
        page.AddText("Customer Name: BARODA TRADING ENTERPRISES", 10, new PdfPoint(50, 720), font);
        page.AddText("IFSC Code: BARB0WADNAG  Statement Period : 01/04/2024 to 31/03/2025", 9, new PdfPoint(50, 705), font);

        // Columns
        page.AddText("S.No", 8, new PdfPoint(40, 680), font);
        page.AddText("Date", 8, new PdfPoint(80, 680), font);
        page.AddText("Particulars", 8, new PdfPoint(150, 680), font);
        page.AddText("Withdrawal", 8, new PdfPoint(380, 680), font);
        page.AddText("Deposit", 8, new PdfPoint(450, 680), font);
        page.AddText("Balance", 8, new PdfPoint(520, 680), font);

        // Row 1: Debit 1250.00
        page.AddText("1", 8, new PdfPoint(40, 660), font);
        page.AddText("10/04/2024", 8, new PdfPoint(80, 660), font);
        page.AddText("OFFICE SUPPLIES PURCHASE", 8, new PdfPoint(150, 660), font);
        page.AddText("1,250.00", 8, new PdfPoint(380, 660), font);
        page.AddText("18,750.00 Cr", 8, new PdfPoint(520, 660), font);

        // Row 2: Credit 5000.00
        page.AddText("2", 8, new PdfPoint(40, 640), font);
        page.AddText("15/04/2024", 8, new PdfPoint(80, 640), font);
        page.AddText("NEFT INWARD CLIENT PAYMENT", 8, new PdfPoint(150, 640), font);
        page.AddText("5,000.00", 8, new PdfPoint(450, 640), font);
        page.AddText("23,750.00 Cr", 8, new PdfPoint(520, 640), font);

        byte[] pdfBytes = builder.Build();

        // 1. Upload statement PDF
        var form = CreateMultipartPdf("Bank_Of_Baroda_Statement.pdf", pdfBytes);
        var uploadResp = await client.PostAsync("/api/statements/upload", form);
        Assert.Equal(HttpStatusCode.OK, uploadResp.StatusCode);

        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>(JsonOptions);
        Assert.NotNull(uploadResult);
        var fileId = uploadResult.FileId;

        // 2. Parse statement
        var parseResp = await client.PostAsync($"/api/statements/{fileId}/parse", null);
        var contentStr = await parseResp.Content.ReadAsStringAsync();
        Assert.True(parseResp.IsSuccessStatusCode, $"Parse failed with status {parseResp.StatusCode}: {contentStr}");

        // 3. Verify transactions endpoint
        var reviewResp = await client.GetAsync($"/api/statements/{fileId}/transactions");
        Assert.Equal(HttpStatusCode.OK, reviewResp.StatusCode);
        var reviewPage = await reviewResp.Content.ReadFromJsonAsync<StatementTransactionsResponse>(JsonOptions);
        Assert.NotNull(reviewPage);
        Assert.Equal(2, reviewPage.TotalCount);
        Assert.Equal("Bank of Baroda", reviewPage.BankName);
        Assert.Equal("BOB-v1", reviewPage.ParserVersion);
    }

    #endregion

    #region Test 16: Reference Image Exact Format Validation (bob World Multi-Page & Non-Transaction Filtering)

    [Fact]
    public void Test16_BOB_ReferenceImage_ExactLayout_Validated()
    {
        // Synthesizes the exact text coordinate stream corresponding to the provided Bank of Baroda bob World reference document:
        // Pages 1-2 with transaction table, Page 3 with non-transaction promotional/ad content
        var extraction = BuildSyntheticBobExtraction(pages =>
        {
            // === PAGE 1 ===
            var p1Rows = new List<PdfCandidateRow>();
            double y = 40.0;

            // Brand Header: bob World (left) and Bank of Baroda (right)
            p1Rows.Add(CreateRow(1, y, [
                ("bob", 45.0), ("World", 75.0),
                ("बैंक", 430.0), ("ऑफ़", 465.0), ("बड़ौदा", 495.0),
                ("Bank", 540.0), ("of", 575.0), ("Baroda", 595.0)
            ]));
            y += 20.0;

            // Orange Banner: Account Statement from 01-10-2025 to 11-04-2026
            p1Rows.Add(CreateRow(1, y, [
                ("Account", 45.0), ("Statement", 100.0), ("from", 165.0),
                ("01-10-2025", 195.0), ("to", 265.0), ("11-04-2026", 285.0)
            ]));
            y += 20.0;

            // Account Details Card
            p1Rows.Add(CreateRow(1, y, [
                ("Account", 45.0), ("Details", 100.0)
            ]));
            y += 16.0;

            p1Rows.Add(CreateRow(1, y, [
                ("Account", 45.0), ("Number", 95.0), (":", 145.0), ("37520100000001", 155.0),
                ("IFSC", 400.0), ("Code", 430.0), (":", 465.0), ("BARB0NANAKH", 475.0)
            ]));
            y += 16.0;

            p1Rows.Add(CreateRow(1, y, [
                ("Customer", 45.0), ("Name", 105.0), (":", 145.0), ("KARAMJEET", 155.0), ("SINGH", 230.0),
                ("Branch", 400.0), (":", 445.0), ("NANAKMATTA", 455.0)
            ]));
            y += 16.0;

            p1Rows.Add(CreateRow(1, y, [
                ("Customer", 45.0), ("Address", 105.0), (":", 150.0), ("PRATAPPUR", 160.0), ("KHATIMA", 230.0), ("NANAKMATTA", 285.0),
                ("UTTARAKHAND", 45.0), ("262311", 130.0)
            ]));
            y += 22.0;

            // Table Header: Serial No | Transaction Date | Value Date | Description | Cheque Number | Debit | Credit | Balance
            p1Rows.Add(CreateRow(1, y, [
                ("Serial", 40.0), ("No", 70.0),
                ("Transaction", 95.0), ("Date", 160.0),
                ("Value", 195.0), ("Date", 230.0),
                ("Description", 270.0),
                ("Cheque", 365.0), ("Number", 405.0),
                ("Debit", 455.0),
                ("Credit", 515.0),
                ("Balance", 575.0)
            ]));
            y += 18.0;

            // Row 1: Opening Balance (Ledger Anchor - NOT a financial movement)
            p1Rows.Add(CreateRow(1, y, [
                ("1", 40.0),
                ("01-10-2025", 95.0),
                ("01-10-2025", 195.0),
                ("Opening", 270.0), ("Balance", 315.0),
                ("-", 380.0),
                ("-", 460.0),
                ("-", 520.0),
                ("7,94,280.00", 565.0)
            ]));
            y += 16.0;

            // Row 2: UPI Debit 20.00
            p1Rows.Add(CreateRow(1, y, [
                ("2", 40.0),
                ("02-10-2025", 95.0),
                ("02-10-2025", 195.0),
                ("UPI/527581177301/PAYTM", 270.0),
                ("-", 380.0),
                ("20.00", 455.0),
                ("-", 520.0),
                ("7,94,260.00", 565.0)
            ]));
            y += 16.0;

            // Row 3: NEFT Credit 20,000.00
            p1Rows.Add(CreateRow(1, y, [
                ("3", 40.0),
                ("02-10-2025", 95.0),
                ("02-10-2025", 195.0),
                ("NEFT/BARBN25091876543/CREDIT", 270.0),
                ("-", 380.0),
                ("-", 460.0),
                ("20,000.00", 510.0),
                ("8,14,260.00", 565.0)
            ]));
            y += 24.0;

            // Footer Page 1
            p1Rows.Add(CreateRow(1, y, [
                ("This", 45.0), ("is", 70.0), ("a", 85.0), ("computer", 95.0), ("generated", 145.0), ("statement", 195.0),
                ("Page", 480.0), ("1", 510.0), ("of", 525.0), ("11", 540.0)
            ]));
            pages.Add(CreatePage(1, p1Rows));

            // === PAGE 2 ===
            var p2Rows = new List<PdfCandidateRow>();
            y = 40.0;

            p2Rows.Add(CreateRow(2, y, [
                ("bob", 45.0), ("World", 75.0),
                ("Bank", 540.0), ("of", 575.0), ("Baroda", 595.0)
            ]));
            y += 20.0;

            p2Rows.Add(CreateRow(2, y, [
                ("Account", 45.0), ("Statement", 100.0), ("from", 165.0),
                ("01-10-2025", 195.0), ("to", 265.0), ("11-04-2026", 285.0)
            ]));
            y += 20.0;

            // Repeated Table Header
            p2Rows.Add(CreateRow(2, y, [
                ("Serial", 40.0), ("No", 70.0),
                ("Transaction", 95.0), ("Date", 160.0),
                ("Value", 195.0), ("Date", 230.0),
                ("Description", 270.0),
                ("Cheque", 365.0), ("Number", 405.0),
                ("Debit", 455.0),
                ("Credit", 515.0),
                ("Balance", 575.0)
            ]));
            y += 18.0;

            // Row 4: Cheque Paid 1,500.00
            p2Rows.Add(CreateRow(2, y, [
                ("4", 40.0),
                ("03-10-2025", 95.0),
                ("03-10-2025", 195.0),
                ("CHQ", 270.0), ("PAID", 300.0), ("001234", 335.0), ("TO", 380.0), ("SUPPLIER", 400.0),
                ("001234", 370.0),
                ("1,500.00", 450.0),
                ("-", 520.0),
                ("8,12,760.00", 565.0)
            ]));
            y += 16.0;

            // Row 5: IMPS Inward 5,000.00
            p2Rows.Add(CreateRow(2, y, [
                ("5", 40.0),
                ("05-10-2025", 95.0),
                ("05-10-2025", 195.0),
                ("IMPS/527811998877/SETTLEMENT", 270.0),
                ("-", 380.0),
                ("-", 460.0),
                ("5,000.00", 510.0),
                ("8,17,760.00", 565.0)
            ]));
            y += 24.0;

            // Footer Page 2
            p2Rows.Add(CreateRow(2, y, [
                ("This", 45.0), ("is", 70.0), ("a", 85.0), ("computer", 95.0), ("generated", 145.0), ("statement", 195.0),
                ("Page", 480.0), ("2", 510.0), ("of", 525.0), ("11", 540.0)
            ]));
            pages.Add(CreatePage(2, p2Rows));

            // === PAGE 3: NON-TRANSACTION / PROMOTIONAL PLACEHOLDER PAGE ===
            var p3Rows = new List<PdfCandidateRow>();
            y = 50.0;
            p3Rows.Add(CreateRow(3, y, [
                ("Ad", 50.0), ("Download", 80.0), ("to", 140.0), ("read", 160.0), ("ad-free", 190.0)
            ]));
            y += 30.0;
            p3Rows.Add(CreateRow(3, y, [
                ("[SCRIBD]", 50.0), ("Get", 120.0), ("unlimited", 150.0), ("access", 210.0), ("to", 255.0), ("books", 275.0), ("and", 315.0), ("documents", 345.0)
            ]));
            pages.Add(CreatePage(3, p3Rows));
        });

        // 1. Detection
        var detection = _detector.DetectBank(extraction);
        Assert.NotNull(detection);
        Assert.True(detection.IsSupported);
        Assert.Equal(BankType.BOB, detection.DetectedBank);
        Assert.Equal("37520100000001", detection.AccountNumber);
        Assert.Contains("KARAMJEET SINGH", detection.CustomerName);
        Assert.Equal(new DateTime(2025, 10, 1), detection.StatementFrom);
        Assert.Equal(new DateTime(2026, 4, 11), detection.StatementTo);

        // Disambiguation verification
        Assert.NotEqual(BankType.PNB, detection.DetectedBank);

        // 2. Parsing
        var result = _bobParser.Parse(extraction, detection);
        Assert.True(result.Success);
        // Exactly 4 transactions (Opening Balance row is captured as Opening Balance, NOT a false transaction; Page 3 has 0 transactions)
        Assert.Equal(4, result.Transactions.Count);
        Assert.Empty(result.Warnings);

        // 3. Exact Transaction Assertions
        var tx1 = result.Transactions[0];
        Assert.Equal(new DateTime(2025, 10, 2), tx1.TransactionDate);
        Assert.Equal(20.00m, tx1.Debit);
        Assert.Null(tx1.Credit);
        Assert.Equal(794260.00m, tx1.Balance);
        Assert.Equal("UPI/527581177301/PAYTM", tx1.Description);
        Assert.DoesNotContain("-", tx1.Description);

        var tx2 = result.Transactions[1];
        Assert.Equal(new DateTime(2025, 10, 2), tx2.TransactionDate);
        Assert.Null(tx2.Debit);
        Assert.Equal(20000.00m, tx2.Credit);
        Assert.Equal(814260.00m, tx2.Balance);
        Assert.Equal("BARBN25091876543", tx2.Utr);

        var tx3 = result.Transactions[2];
        Assert.Equal(new DateTime(2025, 10, 3), tx3.TransactionDate);
        Assert.Equal(1500.00m, tx3.Debit);
        Assert.Null(tx3.Credit);
        Assert.Equal(812760.00m, tx3.Balance);
        Assert.Equal("001234", tx3.Reference);

        var tx4 = result.Transactions[3];
        Assert.Equal(new DateTime(2025, 10, 5), tx4.TransactionDate);
        Assert.Null(tx4.Debit);
        Assert.Equal(5000.00m, tx4.Credit);
        Assert.Equal(817760.00m, tx4.Balance);

        // 4. Financial Calculations & Ledger Reconciliation
        decimal totalDebits = result.Transactions.Sum(t => t.Debit ?? 0m);
        decimal totalCredits = result.Transactions.Sum(t => t.Credit ?? 0m);
        Assert.Equal(1520.00m, totalDebits);
        Assert.Equal(25000.00m, totalCredits);

        decimal openingBalance = 794280.00m; // From row 1
        decimal closingBalance = result.Transactions.Last().Balance ?? 0m;
        Assert.Equal(817760.00m, closingBalance);
        Assert.Equal(closingBalance, openingBalance + totalCredits - totalDebits);
    }

    #endregion
}
