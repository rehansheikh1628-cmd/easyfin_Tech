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
using EasyFin_Tech.Server.DTOs;
using EasyFin_Tech.Server.Models;
using EasyFin_Tech.Server.Parsing;
using EasyFin_Tech.Server.Parsing.Interfaces;
using EasyFin_Tech.Server.Parsing.Models;
using EasyFin_Tech.Server.Parsing.Parsers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;
using Xunit;
using Xunit.Abstractions;

namespace EasyFin_Tech.Server.Tests;

public class ICICIParserTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;
    private readonly BankDetector _detector = new(NullLogger<BankDetector>.Instance);
    private readonly ICICIStatementParser _parser = new(NullLogger<ICICIStatementParser>.Instance);
    private readonly BankParserRegistry _registry;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public ICICIParserTests(CustomWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
        _registry = new BankParserRegistry([_parser]);
    }

    private static PdfExtractionResult BuildSyntheticExtraction(string bankName, Action<List<PdfPageResult>> configurePages)
    {
        var pages = new List<PdfPageResult>();
        configurePages(pages);

        return new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            OriginalFileName = $"{bankName}_Statement.pdf",
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
            Width = 612.0,
            Height = 792.0,
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
            Width = t.Text.Length * 6.0,
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

        var email = $"{prefix}_{Guid.NewGuid():N}@icici-test.com";
        var request = new RegisterRequest($"{prefix} Workspace", email, "SecurePass123!");
        var response = await client.PostAsJsonAsync("/api/auth/register", request, JsonOptions);
        Assert.True(response.IsSuccessStatusCode);

        var auth = await response.Content.ReadFromJsonAsync<AuthResponseDto>(JsonOptions);
        Assert.NotNull(auth);
        return (client, auth);
    }

    #region Test 1 — Detector: Identifies ICICI Bank

    [Fact]
    public void Test01_ICICI_Detector_IdentifiesICICIBank()
    {
        var extraction = BuildSyntheticExtraction("ICICI", pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 40.0, [("ICICI", 40.0), ("BANK", 80.0), ("LIMITED", 120.0)]),
                CreateRow(1, 55.0, [("Visit", 40.0), ("www.icicibank.com", 75.0), ("Dial", 200.0), ("1800", 230.0), ("1080", 260.0)]),
                CreateRow(1, 70.0, [("IFSC", 40.0), ("Code", 70.0), (":", 100.0), ("ICIC0000057", 110.0)]),
                CreateRow(1, 85.0, [("Statement", 40.0), ("of", 100.0), ("Transactions", 115.0), ("in", 185.0), ("Savings", 200.0), ("Account", 245.0), ("XXXXXXXX4439", 295.0), ("in", 370.0), ("INR", 385.0), ("for", 410.0), ("the", 430.0), ("period", 450.0), ("April", 490.0), ("01,", 520.0), ("2025", 540.0), ("-", 570.0), ("March", 580.0)]),
                CreateRow(1, 100.0, [("DATE", 30.0), ("MODE", 75.0), ("PARTICULARS", 130.0), ("DEPOSITS", 350.0), ("WITHDRAWALS", 410.0), ("BALANCE", 500.0)])
            };
            pages.Add(CreatePage(1, rows));
        });

        var result = _detector.DetectBank(extraction);

        Assert.Equal(BankType.ICICI, result.DetectedBank);
        Assert.Equal("ICICI Bank", result.BankName);
        Assert.True(result.IsSupported);
        Assert.True(result.Confidence >= 0.90);
        Assert.Contains("XXXXXXXX4439", result.AccountNumber);
    }

    #endregion

    #region Test 2 — Existing Bank Protection

    [Fact]
    public void Test02_ICICI_Detector_DoesNotMisclassifyExistingBanks()
    {
        // 1. HDFC Statement
        var hdfcExtraction = BuildSyntheticExtraction("HDFC", pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 40.0, [("HDFC", 40.0), ("BANK", 80.0), ("LIMITED", 120.0)]),
                CreateRow(1, 55.0, [("IFSC", 40.0), ("HDFC0000060", 80.0)]),
                CreateRow(1, 70.0, [("Date", 30.0), ("Narration", 90.0), ("Chq./Ref.No.", 250.0), ("Value", 330.0), ("Dt", 360.0), ("Withdrawal", 400.0), ("Amt.", 460.0), ("Deposit", 490.0), ("Amt.", 530.0), ("Closing", 560.0), ("Balance", 600.0)])
            };
            pages.Add(CreatePage(1, rows));
        });

        var hdfcResult = _detector.DetectBank(hdfcExtraction);
        Assert.Equal(BankType.Hdfc, hdfcResult.DetectedBank);
        Assert.Equal("HDFC Bank", hdfcResult.BankName);

        // 2. YES BANK Statement
        var yesExtraction = BuildSyntheticExtraction("YESBANK", pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 40.0, [("YES", 40.0), ("BANK", 70.0), ("LIMITED", 110.0)]),
                CreateRow(1, 55.0, [("IFSC", 40.0), ("YESB0000001", 80.0)]),
                CreateRow(1, 70.0, [("Transaction", 30.0), ("Date", 90.0), ("Value", 120.0), ("Date", 150.0), ("Description", 190.0), ("Withdrawals", 350.0), ("Deposits", 420.0), ("Running", 480.0), ("Balance", 530.0)])
            };
            pages.Add(CreatePage(1, rows));
        });

        var yesResult = _detector.DetectBank(yesExtraction);
        Assert.Equal(BankType.YesBank, yesResult.DetectedBank);
        Assert.Equal("YES BANK", yesResult.BankName);

        // 3. Axis Bank Statement
        var axisExtraction = BuildSyntheticExtraction("AXIS", pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 40.0, [("AXIS", 40.0), ("BANK", 75.0), ("LTD", 110.0)]),
                CreateRow(1, 55.0, [("IFSC", 40.0), ("UTIB0000004", 80.0)]),
                CreateRow(1, 70.0, [("S.NO", 30.0), ("Transaction", 60.0), ("Date", 110.0), ("Value", 140.0), ("Date", 170.0), ("Particulars", 210.0), ("Amount(INR)", 310.0), ("Debit/Credit", 380.0), ("Balance(INR)", 450.0)])
            };
            pages.Add(CreatePage(1, rows));
        });

        var axisResult = _detector.DetectBank(axisExtraction);
        Assert.Equal(BankType.Axis, axisResult.DetectedBank);
        Assert.Equal("Axis Bank", axisResult.BankName);

        // 4. Central Bank of India Statement
        var centralExtraction = BuildSyntheticExtraction("CENTRAL", pages =>
        {
            var rows = new List<PdfCandidateRow>
            {
                CreateRow(1, 40.0, [("CENTRAL", 40.0), ("BANK", 95.0), ("OF", 130.0), ("INDIA", 150.0)]),
                CreateRow(1, 55.0, [("IFSC", 40.0), ("CBIN0280624", 80.0)]),
                CreateRow(1, 70.0, [("Value", 30.0), ("Date", 65.0), ("Post", 100.0), ("Date", 130.0), ("Details", 165.0), ("Chq.No.", 300.0), ("Debit", 360.0), ("Credit", 420.0), ("Balance", 480.0)])
            };
            pages.Add(CreatePage(1, rows));
        });

        var centralResult = _detector.DetectBank(centralExtraction);
        Assert.Equal(BankType.CentralBank, centralResult.DetectedBank);
        Assert.Equal("Central Bank of India", centralResult.BankName);
    }

    #endregion

    #region Test 3 — Parser Registration & Version

    [Fact]
    public void Test03_ICICI_Parser_ResolvesVersion_ICICIv1()
    {
        Assert.Equal(5, _parser.BankCode);
        Assert.Equal("ICICI Bank", _parser.BankName);
        Assert.Equal("ICICI-v1", _parser.ParserVersion);

        var detection = new BankDetectionResult
        {
            DetectedBank = BankType.ICICI,
            BankName = "ICICI Bank",
            IsSupported = true
        };

        var resolvedParser = _registry.ResolveParser(detection);
        Assert.NotNull(resolvedParser);
        Assert.Equal("ICICI-v1", resolvedParser.ParserVersion);
        Assert.True(_parser.CanParse(detection));
    }

    #endregion

    #region Test 4 — Real PDF Integration Test (Ground Truth Verification)

    [Fact]
    public void Test04_ICICI_Parser_RealPdf_ExtractsGroundTruth()
    {
        var fixturePath = @"E:\Bank Statements\Icici statement.pdf";
        Assert.True(File.Exists(fixturePath), $"ICICI fixture must exist: {fixturePath}");

        using var doc = PdfDocument.Open(fixturePath);
        Assert.Equal(26, doc.NumberOfPages);

        var extraction = ExtractDocument(doc, fixturePath);
        var detection = _detector.DetectBank(extraction);

        Assert.Equal(BankType.ICICI, detection.DetectedBank);
        Assert.Equal("ICICI Bank", detection.BankName);
        Assert.True(detection.IsSupported);

        var parseResult = _parser.Parse(extraction, detection);

        Assert.True(parseResult.Success);
        Assert.Equal(593, parseResult.Transactions.Count);

        // Debits & Credits count
        int debitCount = parseResult.Transactions.Count(t => t.Debit.HasValue);
        int creditCount = parseResult.Transactions.Count(t => t.Credit.HasValue);
        Assert.Equal(465, debitCount);
        Assert.Equal(128, creditCount);

        // Debits & Credits financial sum
        decimal totalDebits = parseResult.Transactions.Where(t => t.Debit.HasValue).Sum(t => t.Debit!.Value);
        decimal totalCredits = parseResult.Transactions.Where(t => t.Credit.HasValue).Sum(t => t.Credit!.Value);
        Assert.Equal(10900743.38m, totalDebits);
        Assert.Equal(10519929.79m, totalCredits);

        // Opening and Closing balance
        var firstTx = parseResult.Transactions.First();
        decimal openingBalance = firstTx.Balance!.Value + (firstTx.Debit ?? 0m) - (firstTx.Credit ?? 0m);
        Assert.Equal(5596688.34m, openingBalance);

        var lastTx = parseResult.Transactions.Last();
        Assert.Equal(5215874.75m, lastTx.Balance!.Value);

        // Sequential balance continuity (0 mismatches)
        decimal running = openingBalance;
        int mismatches = 0;
        foreach (var tx in parseResult.Transactions)
        {
            decimal expected = tx.Debit.HasValue ? running - tx.Debit.Value : running + tx.Credit!.Value;
            if (Math.Abs(expected - tx.Balance!.Value) > 0.001m)
            {
                mismatches++;
            }
            running = tx.Balance!.Value;
        }

        Assert.Equal(0, mismatches);
        Assert.Equal(5215874.75m, running);

        // Verify first and last transaction details
        Assert.Equal(new DateTime(2025, 4, 1), firstTx.TransactionDate);
        Assert.Equal(1318.00m, firstTx.Debit);
        Assert.Equal(5595370.34m, firstTx.Balance);

        Assert.Equal(new DateTime(2026, 3, 31), lastTx.TransactionDate);
        Assert.Equal(82.00m, lastTx.Debit);
        Assert.Equal(5215874.75m, lastTx.Balance);
    }

    #endregion

    #region Test 5 — Multi-Line Narration Reconstruction

    [Fact]
    public void Test05_ICICI_Parser_MultiLineNarration_ReconstructedCorrectly()
    {
        var fixturePath = @"E:\Bank Statements\Icici statement.pdf";
        using var doc = PdfDocument.Open(fixturePath);
        var extraction = ExtractDocument(doc, fixturePath);
        var detection = _detector.DetectBank(extraction);
        var parseResult = _parser.Parse(extraction, detection);

        // Verify multi-line UPI narration is correctly reconstructed into a coherent string
        var upiTx = parseResult.Transactions.FirstOrDefault(t => t.Description.Contains("UPI/ag764801@okicic"));
        Assert.NotNull(upiTx);
        Assert.Contains("545701377314", upiTx.Description);
        Assert.Equal("545701377314", upiTx.Reference);

        // Verify NEFT narration
        var neftTx = parseResult.Transactions.FirstOrDefault(t => t.Description.Contains("ZERODHA BROKING"));
        Assert.NotNull(neftTx);
        Assert.Contains("YESBN12025040506673000", neftTx.Description);
        Assert.Equal("YESBN12025040506673000", neftTx.Utr);
    }

    #endregion

    #region Test 6 — Multi-Page Continuation & Metadata Filtering

    [Fact]
    public void Test06_ICICI_Parser_MultiPageContinuation_IgnoresPageMetadata()
    {
        var fixturePath = @"E:\Bank Statements\Icici statement.pdf";
        using var doc = PdfDocument.Open(fixturePath);
        var extraction = ExtractDocument(doc, fixturePath);
        var detection = _detector.DetectBank(extraction);
        var parseResult = _parser.Parse(extraction, detection);

        // Ensure no repeated headers or page totals became transactions
        Assert.DoesNotContain(parseResult.Transactions, t => t.Description.StartsWith("Total:", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(parseResult.Transactions, t => t.Description.Contains("Summary of TDS", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(parseResult.Transactions, t => t.Description.Contains("ACCOUNT DETAILS", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(parseResult.Transactions, t => t.Description.Contains("Page ", StringComparison.OrdinalIgnoreCase));

        // Ensure transactions span all transaction pages (pages 3 to 25)
        var pageNumbers = parseResult.Transactions.Select(t => t.SourcePageNumber).Distinct().OrderBy(p => p).ToList();
        Assert.Equal(3, pageNumbers.First());
        Assert.Equal(25, pageNumbers.Last());
        Assert.Equal(23, pageNumbers.Count); // 23 statement pages with transactions (pages 3 to 25)
    }

    #endregion

    #region Test 7 — Amount Parsing & Precision

    [Fact]
    public void Test07_ICICI_Parser_AmountParsing_IndianFormatAndDecimals()
    {
        var fixturePath = @"E:\Bank Statements\Icici statement.pdf";
        using var doc = PdfDocument.Open(fixturePath);
        var extraction = ExtractDocument(doc, fixturePath);
        var detection = _detector.DetectBank(extraction);
        var parseResult = _parser.Parse(extraction, detection);

        // Test transactions with large Indian comma amounts
        var largeTx = parseResult.Transactions.FirstOrDefault(t => t.Amount >= 4500000m);
        Assert.NotNull(largeTx);
        Assert.True(largeTx.Amount > 0);

        // Test transactions with cents/paise
        var paiseTx = parseResult.Transactions.FirstOrDefault(t => t.Amount == 3600.90m);
        Assert.NotNull(paiseTx);
        Assert.Equal(3600.90m, paiseTx.Amount);
    }

    #endregion

    #region Test 8 — Balance Continuity (Zero Mismatches)

    [Fact]
    public void Test08_ICICI_Parser_BalanceContinuity_ZeroMismatches()
    {
        var fixturePath = @"E:\Bank Statements\Icici statement.pdf";
        using var doc = PdfDocument.Open(fixturePath);
        var extraction = ExtractDocument(doc, fixturePath);
        var detection = _detector.DetectBank(extraction);
        var parseResult = _parser.Parse(extraction, detection);

        Assert.Equal(0, parseResult.Warnings.Count(w => w.Contains("running balance continuity mismatch", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(parseResult.Transactions, t => t.NeedsReview && t.ReviewWarnings.Any(w => w.Contains("mismatch", StringComparison.OrdinalIgnoreCase)));
    }

    #endregion

    #region Test 9 — Excel Export Flow via ClosedXML

    [Fact]
    public async Task Test09_ICICI_Statement_RealPdf_UploadParseAndExportExcel()
    {
        var (client, auth) = await RegisterUserAsync("IciciExport");

        var fixturePath = @"E:\Bank Statements\Icici statement.pdf";
        Assert.True(File.Exists(fixturePath), $"Fixture must exist: {fixturePath}");
        var pdfBytes = await File.ReadAllBytesAsync(fixturePath);

        // 1. Upload
        var uploadContent = CreateMultipartPdf("Icici statement.pdf", pdfBytes);
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
        Assert.Equal(593, txList.TotalCount);

        // 4. Validation Summary
        var valSummaryResp = await client.GetAsync($"/api/statements/{fileId}/validation-summary");
        Assert.Equal(HttpStatusCode.OK, valSummaryResp.StatusCode);
        var valSummary = await valSummaryResp.Content.ReadFromJsonAsync<StatementValidationSummaryDto>(JsonOptions);
        Assert.NotNull(valSummary);
        Assert.Equal(0, valSummary.InvalidCount);

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

        // Verify sheets
        Assert.NotNull(workbook.Worksheet("Transactions"));
        Assert.NotNull(workbook.Worksheet("Statement Info"));
        Assert.NotNull(workbook.Worksheet("Summary"));

        var txWs = workbook.Worksheet("Transactions");
        Assert.Equal(594, txWs.LastRowUsed()!.RowNumber()); // 1 header + 593 data rows
        Assert.Equal("ICICI Bank", txWs.Cell(2, 12).GetString());

        var infoWs = workbook.Worksheet("Statement Info");
        Assert.Equal("ICICI Bank", infoWs.Cell(2, 2).GetString());
        Assert.Equal("ICICI-v1", infoWs.Cell(3, 2).GetString());

        var sumWs = workbook.Worksheet("Summary");
        Assert.Equal(10519929.79m, (decimal)sumWs.Cell(4, 2).GetDouble()); // Credits
        Assert.Equal(10900743.38m, (decimal)sumWs.Cell(5, 2).GetDouble()); // Debits
        Assert.Equal("BALANCED", sumWs.Cell(8, 2).GetString());
    }

    #endregion

    private static PdfExtractionResult ExtractDocument(PdfDocument doc, string filePath)
    {
        var extractionPages = new List<PdfPageResult>();
        for (int p = 1; p <= doc.NumberOfPages; p++)
        {
            var pdfPage = doc.GetPage(p);
            var words = pdfPage.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();

            var visualLines = words.GroupBy(w => Math.Round(w.BoundingBox.Bottom / 3.0) * 3.0)
                                   .OrderByDescending(g => g.Key)
                                   .ToList();

            var candidateRows = new List<PdfCandidateRow>();
            int rowIndex = 1;

            foreach (var lineGroup in visualLines)
            {
                var lineWords = lineGroup.OrderBy(w => w.BoundingBox.Left).ToList();
                var lineBlocks = lineWords.Select((w, fIdx) => new PdfTextBlock
                {
                    Text = w.Text,
                    X = Math.Round(w.BoundingBox.Left, 2),
                    Y = Math.Round(pdfPage.Height - w.BoundingBox.Top, 2),
                    Width = Math.Round(w.BoundingBox.Width, 2),
                    Height = Math.Round(w.BoundingBox.Height, 2),
                    PageNumber = p,
                    ReadingOrderIndex = fIdx + 1
                }).ToList();

                var lineTop = lineBlocks.Min(b => b.Y);
                var lineBottom = lineBlocks.Max(b => b.Y + b.Height);

                candidateRows.Add(new PdfCandidateRow
                {
                    RowIndex = rowIndex++,
                    PageNumber = p,
                    Y = Math.Round(lineTop, 2),
                    Height = Math.Round(lineBottom - lineTop, 2),
                    RawLineText = string.Join(" ", lineBlocks.Select(b => b.Text)),
                    LineFragments = lineBlocks
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

        return new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            OriginalFileName = Path.GetFileName(filePath),
            PageCount = doc.NumberOfPages,
            Pages = extractionPages
        };
    }
}
