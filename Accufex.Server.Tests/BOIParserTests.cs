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
using Accufex.Server.Models;
using Accufex.Server.Parsing;
using Accufex.Server.Parsing.Interfaces;
using Accufex.Server.Parsing.Models;
using Accufex.Server.Parsing.Parsers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;
using Xunit;
using Xunit.Abstractions;

namespace Accufex.Server.Tests;

public class BOIParserTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;
    private readonly BankDetector _detector = new(NullLogger<BankDetector>.Instance);
    private readonly BOIStatementParser _boiParser = new(NullLogger<BOIStatementParser>.Instance);
    private readonly BankParserRegistry _registry;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string FixturePath = @"E:\Bank Statements\BOI-182.pdf";

    public BOIParserTests(CustomWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
        _registry = new BankParserRegistry([_boiParser]);
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

        var email = $"{prefix}_{Guid.NewGuid():N}@boi-test.com";
        var request = new RegisterRequest($"{prefix} Workspace", email, "SecurePass123!");
        var response = await client.PostAsJsonAsync("/api/auth/register", request, JsonOptions);
        Assert.True(response.IsSuccessStatusCode);

        var auth = await response.Content.ReadFromJsonAsync<AuthResponseDto>(JsonOptions);
        Assert.NotNull(auth);
        return (client, auth);
    }

    private static PdfExtractionResult ExtractDocument(PdfDocument doc, string filePath)
    {
        var extractionPages = new List<PdfPageResult>();
        for (int p = 1; p <= doc.NumberOfPages; p++)
        {
            var pdfPage = doc.GetPage(p);
            var words = pdfPage.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();

            var textBlocks = words.Select((w, idx) => new PdfTextBlock
            {
                Text = w.Text,
                X = Math.Round(w.BoundingBox.Left, 2),
                Y = Math.Round(pdfPage.Height - w.BoundingBox.Top, 2),
                Width = Math.Round(w.BoundingBox.Width, 2),
                Height = Math.Round(w.BoundingBox.Height, 2),
                PageNumber = p,
                ReadingOrderIndex = idx + 1
            }).ToList();

            var visualLines = textBlocks
                .GroupBy(b => Math.Round(b.Y / 4.0) * 4.0)
                .OrderBy(g => g.Key)
                .ToList();

            var candidateRows = new List<PdfCandidateRow>();
            int rowIndex = 1;

            foreach (var lineGroup in visualLines)
            {
                var lineBlocks = lineGroup.OrderBy(b => b.X).ToList();
                var rawLineText = string.Join(" ", lineBlocks.Select(b => b.Text));

                var cells = lineBlocks.Select((b, cIdx) => new PdfCandidateCell
                {
                    ColumnIndex = cIdx,
                    Text = b.Text,
                    BoundingBox = new PdfBoundingBox(b.X, b.Y, b.Width, b.Height)
                }).ToList();

                candidateRows.Add(new PdfCandidateRow
                {
                    RowIndex = rowIndex++,
                    PageNumber = p,
                    Y = lineGroup.Key,
                    Height = lineBlocks.Max(b => b.Height),
                    RawLineText = rawLineText,
                    Cells = cells,
                    LineFragments = lineBlocks
                });
            }

            extractionPages.Add(new PdfPageResult
            {
                PageNumber = p,
                Width = pdfPage.Width,
                Height = pdfPage.Height,
                RawText = string.Join(" ", words.Select(w => w.Text)),
                WordCount = words.Count,
                CharacterCount = words.Sum(w => w.Text.Length),
                HasUsableText = words.Count > 0,
                TextBlocks = textBlocks,
                CandidateRows = candidateRows
            });
        }

        return new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            OriginalFileName = Path.GetFileName(filePath),
            PageCount = doc.NumberOfPages,
            ExtractionStatus = "DigitalTextExtracted",
            PdfType = "DigitalWithText",
            HasUsableText = true,
            WordCount = extractionPages.Sum(p => p.WordCount),
            CharacterCount = extractionPages.Sum(p => p.CharacterCount),
            TextBlockCount = extractionPages.Sum(p => p.TextBlocks.Count),
            CandidateRowCount = extractionPages.Sum(p => p.CandidateRows.Count),
            Pages = extractionPages
        };
    }

    private static PdfExtractionResult BuildSyntheticBoiExtraction(Action<List<PdfPageResult>> configurePages)
    {
        var pages = new List<PdfPageResult>();
        configurePages(pages);

        return new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            OriginalFileName = "BOI_Synthetic_Statement.pdf",
            PageCount = pages.Count,
            ExtractionStatus = "DigitalTextExtracted",
            HasUsableText = true,
            PdfType = "DigitalWithText",
            Pages = pages
        };
    }

    #region Test 01: BOI Format Detection

    [Fact]
    public void Test01_BOI_Format_Detection()
    {
        Assert.True(File.Exists(FixturePath), $"Fixture must exist: {FixturePath}");
        using var doc = PdfDocument.Open(FixturePath);
        var extraction = ExtractDocument(doc, FixturePath);

        var detection = _detector.DetectBank(extraction);

        Assert.NotNull(detection);
        Assert.True(detection.IsSupported, "Document should be detected as a supported bank.");
        Assert.Equal(BankType.BOI, detection.DetectedBank);
        Assert.Equal("Bank of India", detection.BankName);
        Assert.Equal("BOI-v1", detection.DetectedFormat);
        Assert.True(detection.Confidence >= 0.90, $"Confidence {detection.Confidence} should be >= 0.90");
        Assert.Equal("872330110000182", detection.AccountNumber);
    }

    #endregion

    #region Test 02: BOI Detector Disambiguation (Does NOT misclassify other banks)

    [Fact]
    public void Test02_BOI_Detector_Disambiguation_All_Banks()
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
            new() { Text = "POST DATE VALUE DATE DESCRIPTION CHQ NO DEBIT CREDIT BALANCE", X = 50, Y = 100, Width = 400, Height = 10 }
        };
        var cbExtraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            HasUsableText = true,
            Pages = [new PdfPageResult { PageNumber = 1, Width = 595, Height = 842, TextBlocks = cbBlocks, RawText = string.Join(" ", cbBlocks.Select(b => b.Text)) }]
        };
        var cbDet = _detector.DetectBank(cbExtraction);
        Assert.Equal(BankType.CentralBank, cbDet.DetectedBank);

        // 5. Verify ICICI Bank detection remains intact
        var iciciBlocks = new List<PdfTextBlock>
        {
            new() { Text = "ICICI BANK", X = 50, Y = 50, Width = 80, Height = 12 },
            new() { Text = "ICIC0000123", X = 50, Y = 70, Width = 80, Height = 10 },
            new() { Text = "DATE PARTICULARS CHQ/REF NO DEPOSITS WITHDRAWALS BALANCE", X = 50, Y = 100, Width = 400, Height = 10 }
        };
        var iciciExtraction = new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            HasUsableText = true,
            Pages = [new PdfPageResult { PageNumber = 1, Width = 595, Height = 842, TextBlocks = iciciBlocks, RawText = string.Join(" ", iciciBlocks.Select(b => b.Text)) }]
        };
        var iciciDet = _detector.DetectBank(iciciExtraction);
        Assert.Equal(BankType.ICICI, iciciDet.DetectedBank);

        // 6. Verify SBI detection remains intact
        var sbiBlocks = new List<PdfTextBlock>
        {
            new() { Text = "STATE BANK OF INDIA", X = 50, Y = 50, Width = 140, Height = 12 },
            new() { Text = "SBIN0000123", X = 50, Y = 70, Width = 80, Height = 10 },
            new() { Text = "Post Date Value Date Description Cheque No/Reference No Debit Credit Balance", X = 50, Y = 100, Width = 450, Height = 10 }
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

    #region Test 03: Parser Metadata & Registration

    [Fact]
    public void Test03_Parser_Metadata_Registration()
    {
        Assert.Equal(7, _boiParser.BankCode);
        Assert.Equal((int)BankType.BOI, _boiParser.BankCode);
        Assert.Equal("Bank of India", _boiParser.BankName);
        Assert.Equal("BOI-v1", _boiParser.ParserVersion);

        var detection = new BankDetectionResult
        {
            DetectedBank = BankType.BOI,
            BankName = "Bank of India",
            IsSupported = true,
            DetectedFormat = "BOI-v1"
        };

        Assert.True(_boiParser.CanParse(detection));

        var resolved = _registry.ResolveParser(detection);
        Assert.NotNull(resolved);
        Assert.Equal("BOI-v1", resolved.ParserVersion);
    }

    #endregion

    #region Test 04: Real PDF Transaction Extraction (Ground Truth)

    [Fact]
    public void Test04_Real_PDF_Transaction_Extraction()
    {
        using var doc = PdfDocument.Open(FixturePath);
        var extraction = ExtractDocument(doc, FixturePath);
        var detection = _detector.DetectBank(extraction);

        var parseResult = _boiParser.Parse(extraction, detection);

        Assert.True(parseResult.Success, $"Parsing should succeed: {parseResult.ErrorMessage}");
        Assert.Equal(641, parseResult.Transactions.Count);

        var debits = parseResult.Transactions.Where(t => t.Debit.HasValue && t.Debit.Value > 0).ToList();
        var credits = parseResult.Transactions.Where(t => t.Credit.HasValue && t.Credit.Value > 0).ToList();

        Assert.Equal(510, debits.Count);
        Assert.Equal(131, credits.Count);

        Assert.Equal(29889032.04m, debits.Sum(t => t.Debit!.Value));
        Assert.Equal(29468290.94m, credits.Sum(t => t.Credit!.Value));

        // Zero transactions should have both debit and credit
        Assert.DoesNotContain(parseResult.Transactions, t => t.Debit.HasValue && t.Credit.HasValue);
    }

    #endregion

    #region Test 05: Opening and Closing Balance

    [Fact]
    public void Test05_Opening_And_Closing_Balance()
    {
        using var doc = PdfDocument.Open(FixturePath);
        var extraction = ExtractDocument(doc, FixturePath);
        var detection = _detector.DetectBank(extraction);

        var parseResult = _boiParser.Parse(extraction, detection);

        Assert.True(parseResult.Success);
        var firstTx = parseResult.Transactions.First();
        var lastTx = parseResult.Transactions.Last();

        // Inferred Opening Balance: firstTx.Balance - credit + debit = -461,386.31 - 0 + 100,000 = -361,386.31
        decimal inferredOpening = firstTx.Balance!.Value - (firstTx.Credit ?? 0m) + (firstTx.Debit ?? 0m);
        Assert.Equal(-361386.31m, inferredOpening);
        Assert.Equal(-782127.41m, lastTx.Balance!.Value);

        // Match continuity: Opening - Total Debits + Total Credits = Closing
        decimal computed = inferredOpening - parseResult.Transactions.Sum(t => t.Debit ?? 0m) + parseResult.Transactions.Sum(t => t.Credit ?? 0m);
        Assert.Equal(-782127.41m, computed);
    }

    #endregion

    #region Test 06: Balance Continuity (Zero Mismatches)

    [Fact]
    public void Test06_Balance_Continuity_Zero_Mismatches()
    {
        using var doc = PdfDocument.Open(FixturePath);
        var extraction = ExtractDocument(doc, FixturePath);
        var detection = _detector.DetectBank(extraction);

        var parseResult = _boiParser.Parse(extraction, detection);

        Assert.True(parseResult.Success);
        Assert.Equal(0, parseResult.Warnings.Count(w => w.Contains("running balance mismatch", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(parseResult.Transactions, t => t.NeedsReview);

        var firstTx = parseResult.Transactions.First();
        decimal running = firstTx.Balance!.Value - (firstTx.Credit ?? 0m) + (firstTx.Debit ?? 0m);
        int mismatches = 0;

        foreach (var tx in parseResult.Transactions)
        {
            decimal expected = running - (tx.Debit ?? 0m) + (tx.Credit ?? 0m);
            if (Math.Abs(expected - tx.Balance!.Value) > 0.01m)
            {
                mismatches++;
            }
            running = tx.Balance!.Value;
        }

        Assert.Equal(0, mismatches);
        Assert.Equal(-782127.41m, running);
    }

    #endregion

    #region Test 07: Multi-Line Narration Reconstruction

    [Fact]
    public void Test07_MultiLine_Narration_Reconstruction()
    {
        using var doc = PdfDocument.Open(FixturePath);
        var extraction = ExtractDocument(doc, FixturePath);
        var detection = _detector.DetectBank(extraction);

        var parseResult = _boiParser.Parse(extraction, detection);

        Assert.True(parseResult.Success);

        // Check clean narration on first transaction
        var tx1 = parseResult.Transactions[0];
        Assert.Equal("HINDUSTAN CARGO MOVERS", tx1.Description);

        // Check NEFT narration
        var tx2 = parseResult.Transactions[1];
        Assert.Contains("NEFT/ICIN409200023323/ICIC/OM LOGIS", tx2.Description);

        // Check that footer text is NEVER mixed into transaction narration
        Assert.DoesNotContain(parseResult.Transactions, t => t.Description.Contains("This is a system generated statement", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(parseResult.Transactions, t => t.Description.Contains("RELATIONSHIP BEYOND BANKING", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(parseResult.Transactions, t => t.Description.Contains("Helpline No", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(parseResult.Transactions, t => t.Description.Contains("Toll free no", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Test 08: Cross-Page Continuity (SNo Sequence 1..641)

    [Fact]
    public void Test08_Cross_Page_Continuity()
    {
        using var doc = PdfDocument.Open(FixturePath);
        var extraction = ExtractDocument(doc, FixturePath);
        var detection = _detector.DetectBank(extraction);

        var parseResult = _boiParser.Parse(extraction, detection);

        Assert.True(parseResult.Success);
        Assert.Equal(641, parseResult.Transactions.Count);

        // Verify transactions are found on Page 1 and across all 23 pages
        int minPage = parseResult.Transactions.Min(t => t.SourcePageNumber);
        int maxPage = parseResult.Transactions.Max(t => t.SourcePageNumber);

        Assert.Equal(1, minPage);
        Assert.Equal(23, maxPage);

        // Verify sequential index order
        for (int i = 0; i < parseResult.Transactions.Count; i++)
        {
            Assert.Equal(i + 1, parseResult.Transactions[i].SourceLineIndex);
        }
    }

    #endregion

    #region Test 09: Cheque / InstNo and UTR Extraction

    [Fact]
    public void Test09_Cheque_InstNo_And_Utr_Extraction()
    {
        using var doc = PdfDocument.Open(FixturePath);
        var extraction = ExtractDocument(doc, FixturePath);
        var detection = _detector.DetectBank(extraction);

        var parseResult = _boiParser.Parse(extraction, detection);

        Assert.True(parseResult.Success);

        // Tx 1 has instrument number 107483
        var tx1 = parseResult.Transactions[0];
        Assert.Equal("107483", tx1.Reference);

        // Tx 5 has instrument number 107482
        var tx5 = parseResult.Transactions[4];
        Assert.Equal("107482", tx5.Reference);

        // Check transactions with references or UTR
        var txWithRef = parseResult.Transactions.Where(t => !string.IsNullOrEmpty(t.Reference) || !string.IsNullOrEmpty(t.Utr)).ToList();
        Assert.True(txWithRef.Count >= 200, "Substantial proportion of transactions have instrument or UTR identifier.");

        // Check UTR extraction from NEFT narration
        var tx2 = parseResult.Transactions[1];
        Assert.NotNull(tx2.Utr);
        Assert.Contains("ICIN409200023323", tx2.Utr);
    }

    #endregion

    #region Test 10: Synthetic BOI Statement (Format Generalization)

    [Fact]
    public void Test10_Synthetic_BOI_Statement()
    {
        var extraction = BuildSyntheticBoiExtraction(pages =>
        {
            var textBlocks = new List<PdfTextBlock>
            {
                // Header
                new() { Text = "BANK OF INDIA", X = 30, Y = 20, Width = 120, Height = 12 },
                new() { Text = "Statement of Account", X = 30, Y = 35, Width = 150, Height = 10 },
                new() { Text = "Account No : 123456789012345", X = 30, Y = 50, Width = 150, Height = 10 },
                new() { Text = "M/S GLOBAL ENTERPRISES", X = 30, Y = 65, Width = 160, Height = 10 },
                new() { Text = "FROM : 01-01-2026 TO : 31-01-2026", X = 30, Y = 80, Width = 180, Height = 10 },
                new() { Text = "IFSC : BKID0001234", X = 30, Y = 95, Width = 100, Height = 10 },
                new() { Text = "OPENING BALANCE : 50000.00Cr.", X = 30, Y = 110, Width = 160, Height = 10 },

                // Table Header
                new() { Text = "SNO", X = 35, Y = 130, Width = 25, Height = 10 },
                new() { Text = "TRAN DATE", X = 75, Y = 130, Width = 50, Height = 10 },
                new() { Text = "INST NO", X = 150, Y = 130, Width = 40, Height = 10 },
                new() { Text = "DESCRIPTION", X = 220, Y = 130, Width = 60, Height = 10 },
                new() { Text = "DEBITS", X = 480, Y = 130, Width = 40, Height = 10 },
                new() { Text = "CREDITS", X = 560, Y = 130, Width = 40, Height = 10 },
                new() { Text = "BALANCE", X = 650, Y = 130, Width = 40, Height = 10 },

                // Tx 1: Debit
                new() { Text = "1", X = 35, Y = 150, Width = 10, Height = 10 },
                new() { Text = "05-01-2026", X = 75, Y = 150, Width = 50, Height = 10 },
                new() { Text = "998877", X = 150, Y = 150, Width = 40, Height = 10 },
                new() { Text = "VENDOR PAYMENT", X = 220, Y = 150, Width = 100, Height = 10 },
                new() { Text = "10,000.00", X = 480, Y = 150, Width = 50, Height = 10 },
                new() { Text = "40000.00", X = 650, Y = 150, Width = 50, Height = 10 },
                new() { Text = "Cr.", X = 745, Y = 150, Width = 15, Height = 10 },

                // Tx 2: Credit
                new() { Text = "2", X = 35, Y = 175, Width = 10, Height = 10 },
                new() { Text = "10-01-2026", X = 75, Y = 175, Width = 50, Height = 10 },
                new() { Text = "NEFT/SBIN1234567890/CLIENT RECEIPT", X = 220, Y = 175, Width = 180, Height = 10 },
                new() { Text = "25,000.00", X = 560, Y = 175, Width = 50, Height = 10 },
                new() { Text = "65000.00", X = 650, Y = 175, Width = 50, Height = 10 },
                new() { Text = "Cr.", X = 745, Y = 175, Width = 15, Height = 10 },

                // Footer
                new() { Text = "This is a system generated statement", X = 30, Y = 250, Width = 200, Height = 10 },
                new() { Text = "RELATIONSHIP BEYOND BANKING", X = 30, Y = 265, Width = 180, Height = 10 }
            };

            pages.Add(new PdfPageResult
            {
                PageNumber = 1,
                Width = 792,
                Height = 842,
                HasUsableText = true,
                TextBlocks = textBlocks,
                RawText = string.Join(" ", textBlocks.Select(b => b.Text))
            });
        });

        var detection = _detector.DetectBank(extraction);
        Assert.Equal(BankType.BOI, detection.DetectedBank);
        Assert.Equal("123456789012345", detection.AccountNumber);
        Assert.Equal("Bank of India", detection.BankName);

        var parseResult = _boiParser.Parse(extraction, detection);
        Assert.True(parseResult.Success);
        Assert.Equal(2, parseResult.Transactions.Count);

        Assert.Equal("Debit", parseResult.Transactions[0].TransactionType);
        Assert.Equal(10000.00m, parseResult.Transactions[0].Amount);
        Assert.Equal(40000.00m, parseResult.Transactions[0].Balance);
        Assert.Equal("998877", parseResult.Transactions[0].Reference);

        Assert.Equal("Credit", parseResult.Transactions[1].TransactionType);
        Assert.Equal(25000.00m, parseResult.Transactions[1].Amount);
        Assert.Equal(65000.00m, parseResult.Transactions[1].Balance);
    }

    #endregion

    #region Test 11: End-to-End Excel Export Integration

    [Fact]
    public async Task Test11_End_To_End_Excel_Export()
    {
        var (client, _) = await RegisterUserAsync("BoiExportFlow");

        var pdfBytes = await File.ReadAllBytesAsync(FixturePath);

        // 1. Upload
        var uploadContent = CreateMultipartPdf("BOI-182.pdf", pdfBytes);
        var uploadResp = await client.PostAsync("/api/statements/upload", uploadContent);
        Assert.Equal(HttpStatusCode.OK, uploadResp.StatusCode);

        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>(JsonOptions);
        Assert.NotNull(uploadResult);
        var fileId = uploadResult.FileId;

        // 2. Parse
        var parseResp = await client.PostAsync($"/api/statements/{fileId}/parse", null);
        Assert.Equal(HttpStatusCode.OK, parseResp.StatusCode);

        // 3. Transactions Preview
        var txResp = await client.GetAsync($"/api/statements/{fileId}/transactions");
        Assert.Equal(HttpStatusCode.OK, txResp.StatusCode);
        var txList = await txResp.Content.ReadFromJsonAsync<StatementTransactionsResponse>(JsonOptions);
        Assert.NotNull(txList);
        Assert.Equal(641, txList.TotalCount);
        Assert.Equal("Bank of India", txList.BankName);
        Assert.Equal("BOI-v1", txList.ParserVersion);

        // 4. Validation Summary
        var valSummaryResp = await client.GetAsync($"/api/statements/{fileId}/validation-summary");
        Assert.Equal(HttpStatusCode.OK, valSummaryResp.StatusCode);
        var valSummary = await valSummaryResp.Content.ReadFromJsonAsync<StatementValidationSummaryDto>(JsonOptions);
        Assert.NotNull(valSummary);
        Assert.Equal(0, valSummary.InvalidCount);
        Assert.True(valSummary.IsReadyForExport);

        // 5. Export to Excel
        var exportResp = await client.GetAsync($"/api/statements/{fileId}/export/excel");
        Assert.Equal(HttpStatusCode.OK, exportResp.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", exportResp.Content.Headers.ContentType?.MediaType);

        var fileBytes = await exportResp.Content.ReadAsByteArrayAsync();
        Assert.NotNull(fileBytes);
        Assert.True(fileBytes.Length > 2000, "Exported file should be non-empty .xlsx");

        // 6. Inspect workbook using ClosedXML
        using var stream = new MemoryStream(fileBytes);
        using var workbook = new XLWorkbook(stream);

        Assert.NotNull(workbook.Worksheet("Transactions"));
        Assert.NotNull(workbook.Worksheet("Statement Info"));
        Assert.NotNull(workbook.Worksheet("Summary"));

        var txWs = workbook.Worksheet("Transactions");
        Assert.Equal(642, txWs.LastRowUsed()!.RowNumber()); // 1 header + 641 data rows
        Assert.Equal("Bank of India", txWs.Cell(2, 12).GetString());

        var infoWs = workbook.Worksheet("Statement Info");
        Assert.Equal("Bank of India", infoWs.Cell(2, 2).GetString());
        Assert.Equal("BOI-v1", infoWs.Cell(3, 2).GetString());
        Assert.Equal("872330110000182", infoWs.Cell(4, 2).GetString());

        var sumWs = workbook.Worksheet("Summary");
        Assert.Equal(29468290.94m, (decimal)sumWs.Cell(4, 2).GetDouble()); // Total Credits
        Assert.Equal(29889032.04m, (decimal)sumWs.Cell(5, 2).GetDouble()); // Total Debits
        Assert.Equal("BALANCED", sumWs.Cell(8, 2).GetString());
    }

    #endregion
}
