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
using Accufex.Server.Validation.Models;
using Accufex.Server.Validation.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;
using Xunit;
using Xunit.Abstractions;

namespace Accufex.Server.Tests;

public class ICICIv2ParserTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;
    private readonly BankDetector _detector = new(NullLogger<BankDetector>.Instance);
    private readonly ICICIStatementParser _v1Parser = new(NullLogger<ICICIStatementParser>.Instance);
    private readonly ICICIStatementParserV2 _v2Parser = new(NullLogger<ICICIStatementParserV2>.Instance);
    private readonly BankParserRegistry _registry;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string FixtureV1Path = @"E:\Bank Statements\Icici statement.pdf";
    private const string FixtureV2Path = @"E:\Bank Statements\ICICI 1.pdf";

    public ICICIv2ParserTests(CustomWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
        _registry = new BankParserRegistry([_v1Parser, _v2Parser]);
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

        var email = $"{prefix}_{Guid.NewGuid():N}@icici-v2-test.com";
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

    #region 1. New ICICI format detection

    [Fact]
    public void Test01_New_ICICI_Format_Detection()
    {
        Assert.True(File.Exists(FixtureV2Path), $"Fixture must exist: {FixtureV2Path}");
        using var doc = PdfDocument.Open(FixtureV2Path);
        var extraction = ExtractDocument(doc, FixtureV2Path);

        var result = _detector.DetectBank(extraction);

        Assert.Equal(BankType.ICICI, result.DetectedBank);
        Assert.Equal("ICICI Bank", result.BankName);
        Assert.Equal("ICICI-v2", result.DetectedFormat);
        Assert.True(result.IsSupported);
        Assert.True(result.Confidence >= 0.90);
    }

    #endregion

    #region 2. Existing ICICI-v1 is still detected correctly

    [Fact]
    public void Test02_Existing_ICICIv1_Is_Still_Detected_Correctly()
    {
        Assert.True(File.Exists(FixtureV1Path), $"Fixture must exist: {FixtureV1Path}");
        using var doc = PdfDocument.Open(FixtureV1Path);
        var extraction = ExtractDocument(doc, FixtureV1Path);

        var result = _detector.DetectBank(extraction);

        Assert.Equal(BankType.ICICI, result.DetectedBank);
        Assert.Equal("ICICI Bank", result.BankName);
        Assert.Equal("ICICI-v1", result.DetectedFormat);
        Assert.True(result.IsSupported);
    }

    #endregion

    #region 3. HDFC is not detected as ICICI-v2

    [Fact]
    public void Test03_HDFC_Is_Not_Detected_As_ICICIv2()
    {
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

        var result = _detector.DetectBank(hdfcExtraction);
        Assert.Equal(BankType.Hdfc, result.DetectedBank);
        Assert.NotEqual("ICICI-v2", result.DetectedFormat);
    }

    #endregion

    #region 4. YES BANK is not detected as ICICI-v2

    [Fact]
    public void Test04_YES_BANK_Is_Not_Detected_As_ICICIv2()
    {
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

        var result = _detector.DetectBank(yesExtraction);
        Assert.Equal(BankType.YesBank, result.DetectedBank);
        Assert.NotEqual("ICICI-v2", result.DetectedFormat);
    }

    #endregion

    #region 5. Axis is not detected as ICICI-v2

    [Fact]
    public void Test05_Axis_Is_Not_Detected_As_ICICIv2()
    {
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

        var result = _detector.DetectBank(axisExtraction);
        Assert.Equal(BankType.Axis, result.DetectedBank);
        Assert.NotEqual("ICICI-v2", result.DetectedFormat);
    }

    #endregion

    #region 6. Central Bank is not detected as ICICI-v2

    [Fact]
    public void Test06_Central_Bank_Is_Not_Detected_As_ICICIv2()
    {
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

        var result = _detector.DetectBank(centralExtraction);
        Assert.Equal(BankType.CentralBank, result.DetectedBank);
        Assert.NotEqual("ICICI-v2", result.DetectedFormat);
    }

    #endregion

    #region 7. Parser resolves as ICICI-v2

    [Fact]
    public void Test07_Parser_Resolves_As_ICICIv2()
    {
        var detection = new BankDetectionResult
        {
            DetectedBank = BankType.ICICI,
            BankName = "ICICI Bank",
            DetectedFormat = "ICICI-v2",
            IsSupported = true
        };

        var resolved = _registry.ResolveParser(detection);
        Assert.NotNull(resolved);
        Assert.Equal("ICICI-v2", resolved.ParserVersion);
        Assert.Equal(5, resolved.BankCode);
        Assert.True(_v2Parser.CanParse(detection));
        Assert.False(_v1Parser.CanParse(detection));
    }

    #endregion

    #region 8. Correct account number extraction

    [Fact]
    public void Test08_Correct_Account_Number_Extraction()
    {
        using var doc = PdfDocument.Open(FixtureV2Path);
        var extraction = ExtractDocument(doc, FixtureV2Path);
        var result = _detector.DetectBank(extraction);

        Assert.Equal("739805500141", result.AccountNumber);
    }

    #endregion

    #region 9. Correct customer name extraction

    [Fact]
    public void Test09_Correct_Customer_Name_Extraction()
    {
        using var doc = PdfDocument.Open(FixtureV2Path);
        var extraction = ExtractDocument(doc, FixtureV2Path);
        var result = _detector.DetectBank(extraction);

        Assert.Equal("BHARAT GLASS AND HARDWARE", result.CustomerName);
    }

    #endregion

    #region 10. Correct statement period extraction

    [Fact]
    public void Test10_Correct_Statement_Period_Extraction()
    {
        using var doc = PdfDocument.Open(FixtureV2Path);
        var extraction = ExtractDocument(doc, FixtureV2Path);
        var result = _detector.DetectBank(extraction);

        Assert.NotNull(result.StatementFrom);
        Assert.NotNull(result.StatementTo);
        Assert.Equal(new DateTime(2026, 1, 25), result.StatementFrom!.Value.Date);
        Assert.Equal(new DateTime(2026, 7, 25), result.StatementTo!.Value.Date);
    }

    #endregion

    #region 11. Correct opening balance

    [Fact]
    public void Test11_Correct_Opening_Balance()
    {
        using var doc = PdfDocument.Open(FixtureV2Path);
        var extraction = ExtractDocument(doc, FixtureV2Path);
        var detection = _detector.DetectBank(extraction);
        var parseResult = _v2Parser.Parse(extraction, detection);

        var firstTx = parseResult.Transactions.First();
        decimal openingBalance = firstTx.Balance!.Value + (firstTx.Debit ?? 0m) - (firstTx.Credit ?? 0m);
        Assert.Equal(-65140.82m, openingBalance);
    }

    #endregion

    #region 12. Correct closing balance

    [Fact]
    public void Test12_Correct_Closing_Balance()
    {
        using var doc = PdfDocument.Open(FixtureV2Path);
        var extraction = ExtractDocument(doc, FixtureV2Path);
        var detection = _detector.DetectBank(extraction);
        var parseResult = _v2Parser.Parse(extraction, detection);

        var lastTx = parseResult.Transactions.Last();
        Assert.Equal(32407.93m, lastTx.Balance!.Value);
    }

    #endregion

    #region 13. Correct transaction count

    [Fact]
    public void Test13_Correct_Transaction_Count()
    {
        using var doc = PdfDocument.Open(FixtureV2Path);
        var extraction = ExtractDocument(doc, FixtureV2Path);
        var detection = _detector.DetectBank(extraction);
        var parseResult = _v2Parser.Parse(extraction, detection);

        Assert.True(parseResult.Success);
        Assert.Equal(521, parseResult.Transactions.Count);
        Assert.Equal(521, parseResult.ProcessedCount);
        Assert.Equal(0, parseResult.RejectedCount);
    }

    #endregion

    #region 14. Correct debit count

    [Fact]
    public void Test14_Correct_Debit_Count()
    {
        using var doc = PdfDocument.Open(FixtureV2Path);
        var extraction = ExtractDocument(doc, FixtureV2Path);
        var detection = _detector.DetectBank(extraction);
        var parseResult = _v2Parser.Parse(extraction, detection);

        int debitCount = parseResult.Transactions.Count(t => t.Debit.HasValue);
        Assert.Equal(66, debitCount);
    }

    #endregion

    #region 15. Correct credit count

    [Fact]
    public void Test15_Correct_Credit_Count()
    {
        using var doc = PdfDocument.Open(FixtureV2Path);
        var extraction = ExtractDocument(doc, FixtureV2Path);
        var detection = _detector.DetectBank(extraction);
        var parseResult = _v2Parser.Parse(extraction, detection);

        int creditCount = parseResult.Transactions.Count(t => t.Credit.HasValue);
        Assert.Equal(455, creditCount);
    }

    #endregion

    #region 16. Exact debit total

    [Fact]
    public void Test16_Exact_Debit_Total()
    {
        using var doc = PdfDocument.Open(FixtureV2Path);
        var extraction = ExtractDocument(doc, FixtureV2Path);
        var detection = _detector.DetectBank(extraction);
        var parseResult = _v2Parser.Parse(extraction, detection);

        decimal totalDebits = parseResult.Transactions.Where(t => t.Debit.HasValue).Sum(t => t.Debit!.Value);
        Assert.Equal(2777674.50m, totalDebits);
    }

    #endregion

    #region 17. Exact credit total

    [Fact]
    public void Test17_Exact_Credit_Total()
    {
        using var doc = PdfDocument.Open(FixtureV2Path);
        var extraction = ExtractDocument(doc, FixtureV2Path);
        var detection = _detector.DetectBank(extraction);
        var parseResult = _v2Parser.Parse(extraction, detection);

        decimal totalCredits = parseResult.Transactions.Where(t => t.Credit.HasValue).Sum(t => t.Credit!.Value);
        Assert.Equal(2875223.25m, totalCredits);
    }

    #endregion

    #region 18. Zero duplicate transactions

    [Fact]
    public void Test18_Zero_Duplicate_Transactions()
    {
        using var doc = PdfDocument.Open(FixtureV2Path);
        var extraction = ExtractDocument(doc, FixtureV2Path);
        var detection = _detector.DetectBank(extraction);
        var parseResult = _v2Parser.Parse(extraction, detection);

        // Every transaction has a distinct SourceLineIndex (1 to 521)
        var lineIndices = parseResult.Transactions.Select(t => t.SourceLineIndex).Distinct().ToList();
        Assert.Equal(521, lineIndices.Count);

        // Deterministic validation identifies zero business duplicates
        var valService = new TransactionValidationService();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entities = parseResult.Transactions.Select((t, i) => new Transaction
        {
            Id = t.Id,
            SourceFileId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            FinancialYearId = Guid.NewGuid(),
            TransactionDate = t.TransactionDate,
            Description = t.Description,
            Debit = t.Debit,
            Credit = t.Credit,
            Amount = t.Amount,
            Balance = t.Balance,
            Reference = t.Reference,
            Utr = t.Utr,
            TransactionType = t.TransactionType,
            BankCode = t.BankCode,
            Account = detection.AccountNumber,
            CreatedAt = baseTime.AddSeconds(i)
        }).ToList();

        var enriched = valService.ValidateAndEnrichStatement(entities, null);
        var summary = valService.ComputeSummary(Guid.NewGuid(), 5, "ICICI Bank", "ICICI-v2", enriched);
        Assert.Equal(0, summary.DuplicateCount);
    }

    #endregion

    #region 19. Multi-line narration reconstruction

    [Fact]
    public void Test19_Multi_Line_Narration_Reconstruction()
    {
        using var doc = PdfDocument.Open(FixtureV2Path);
        var extraction = ExtractDocument(doc, FixtureV2Path);
        var detection = _detector.DetectBank(extraction);
        var parseResult = _v2Parser.Parse(extraction, detection);

        // Verify Tx 1 has complete multi-line reconstructed description spanning 4 source lines
        var tx1 = parseResult.Transactions.First();
        Assert.Contains("UPI/109054499642", tx1.Description);
        Assert.Contains("7452041450@", tx1.Description);
        Assert.Contains("ICI509206", tx1.Description);

        // Verify multi-line narration for cash payment
        var cashTx = parseResult.Transactions.FirstOrDefault(t => t.Description.Contains("CASH PAID:Self 7398 NARSALA"));
        Assert.NotNull(cashTx);
        Assert.True(cashTx.Debit > 0m);
    }

    #endregion

    #region 20. Multi-page continuation

    [Fact]
    public void Test20_Multi_Page_Continuation()
    {
        using var doc = PdfDocument.Open(FixtureV2Path);
        var extraction = ExtractDocument(doc, FixtureV2Path);
        var detection = _detector.DetectBank(extraction);
        var parseResult = _v2Parser.Parse(extraction, detection);

        var pageNumbers = parseResult.Transactions.Select(t => t.SourcePageNumber).Distinct().OrderBy(p => p).ToList();

        Assert.Equal(1, pageNumbers.First());
        Assert.Equal(28, pageNumbers.Last());
        Assert.Equal(28, pageNumbers.Count); // All 28 data pages have transactions
    }

    #endregion

    #region 21. Reference/UTR extraction

    [Fact]
    public void Test21_Reference_And_UTR_Extraction()
    {
        using var doc = PdfDocument.Open(FixtureV2Path);
        var extraction = ExtractDocument(doc, FixtureV2Path);
        var detection = _detector.DetectBank(extraction);
        var parseResult = _v2Parser.Parse(extraction, detection);

        // 1. Cheque reference on Tx 373
        var tx373 = parseResult.Transactions.FirstOrDefault(t => t.Description.Contains("CASH PAID:Self 7398 NARSALA") && t.Reference == "443");
        Assert.NotNull(tx373);
        Assert.Equal("443", tx373.Reference);

        // 2. UPI UTR extraction
        var upiTx = parseResult.Transactions.FirstOrDefault(t => !string.IsNullOrEmpty(t.Utr));
        Assert.NotNull(upiTx);
        Assert.True(upiTx.Utr!.Length >= 10);

        // 3. "NA" is never stored as reference
        Assert.DoesNotContain(parseResult.Transactions, t => string.Equals(t.Reference, "NA", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region 22. Non-transaction rows are ignored

    [Fact]
    public void Test22_Non_Transaction_Rows_Are_Ignored()
    {
        using var doc = PdfDocument.Open(FixtureV2Path);
        var extraction = ExtractDocument(doc, FixtureV2Path);
        var detection = _detector.DetectBank(extraction);
        var parseResult = _v2Parser.Parse(extraction, detection);

        // Page 29 (legends only) has 0 transactions
        Assert.DoesNotContain(parseResult.Transactions, t => t.SourcePageNumber == 29);

        // Table headers and section labels are excluded
        Assert.DoesNotContain(parseResult.Transactions, t => t.Description.StartsWith("Sr No", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(parseResult.Transactions, t => t.Description.StartsWith("Detailed Statement", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(parseResult.Transactions, t => t.Description.Contains("Legends Used", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(parseResult.Transactions, t => t.Description.Contains("Transaction Period:", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(parseResult.Transactions, t => t.Description.Contains("Customer Service", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region 23. Balance continuity

    [Fact]
    public void Test23_Balance_Continuity()
    {
        using var doc = PdfDocument.Open(FixtureV2Path);
        var extraction = ExtractDocument(doc, FixtureV2Path);
        var detection = _detector.DetectBank(extraction);
        var parseResult = _v2Parser.Parse(extraction, detection);

        Assert.Equal(0, parseResult.Warnings.Count(w => w.Contains("running balance continuity mismatch", StringComparison.OrdinalIgnoreCase)));

        var firstTx = parseResult.Transactions.First();
        decimal running = firstTx.Balance!.Value + (firstTx.Debit ?? 0m) - (firstTx.Credit ?? 0m);
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
        Assert.Equal(32407.93m, running);
    }

    #endregion

    #region 24. Real PDF Excel export

    [Fact]
    public async Task Test24_Real_PDF_Excel_Export()
    {
        var (client, _) = await RegisterUserAsync("IciciV2ExportFlow");

        var pdfBytes = await File.ReadAllBytesAsync(FixtureV2Path);

        // 1. Upload
        var uploadContent = CreateMultipartPdf("ICICI 1.pdf", pdfBytes);
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
        Assert.Equal(521, txList.TotalCount);

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
        Assert.Equal(522, txWs.LastRowUsed()!.RowNumber()); // 1 header + 521 data rows
        Assert.Equal("ICICI Bank", txWs.Cell(2, 12).GetString());

        var infoWs = workbook.Worksheet("Statement Info");
        Assert.Equal("ICICI Bank", infoWs.Cell(2, 2).GetString());
        Assert.Equal("ICICI-v2", infoWs.Cell(3, 2).GetString());
        Assert.Equal("739805500141", infoWs.Cell(4, 2).GetString());

        var sumWs = workbook.Worksheet("Summary");
        Assert.Equal(2875223.25m, (decimal)sumWs.Cell(4, 2).GetDouble()); // Credits
        Assert.Equal(2777674.50m, (decimal)sumWs.Cell(5, 2).GetDouble()); // Debits
        Assert.Equal("BALANCED", sumWs.Cell(8, 2).GetString());
    }

    #endregion

    #region 25. Existing ICICI-v1 regression test

    [Fact]
    public void Test25_Existing_ICICIv1_Regression_Test()
    {
        Assert.True(File.Exists(FixtureV1Path), $"Fixture must exist: {FixtureV1Path}");
        using var doc = PdfDocument.Open(FixtureV1Path);
        var extraction = ExtractDocument(doc, FixtureV1Path);
        var detection = _detector.DetectBank(extraction);

        Assert.Equal(BankType.ICICI, detection.DetectedBank);
        Assert.Equal("ICICI-v1", detection.DetectedFormat);

        var parseResult = _v1Parser.Parse(extraction, detection);

        Assert.True(parseResult.Success);
        Assert.Equal(593, parseResult.Transactions.Count);
        Assert.Equal(465, parseResult.Transactions.Count(t => t.Debit.HasValue));
        Assert.Equal(128, parseResult.Transactions.Count(t => t.Credit.HasValue));

        decimal totalDebits = parseResult.Transactions.Where(t => t.Debit.HasValue).Sum(t => t.Debit!.Value);
        decimal totalCredits = parseResult.Transactions.Where(t => t.Credit.HasValue).Sum(t => t.Credit!.Value);
        Assert.Equal(10900743.38m, totalDebits);
        Assert.Equal(10519929.79m, totalCredits);

        var firstTx = parseResult.Transactions.First();
        decimal openingBalance = firstTx.Balance!.Value + (firstTx.Debit ?? 0m) - (firstTx.Credit ?? 0m);
        Assert.Equal(5596688.34m, openingBalance);

        var lastTx = parseResult.Transactions.Last();
        Assert.Equal(5215874.75m, lastTx.Balance!.Value);
    }

    #endregion
}
