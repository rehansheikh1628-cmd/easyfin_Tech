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

public class SBIParserTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;
    private readonly BankDetector _detector = new(NullLogger<BankDetector>.Instance);
    private readonly SBIStatementParser _sbiParser = new(NullLogger<SBIStatementParser>.Instance);
    private readonly BankParserRegistry _registry;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string FixturePath = @"E:\Bank Statements\New 07-08-2026\SBI.pdf";

    public SBIParserTests(CustomWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
        _registry = new BankParserRegistry([_sbiParser]);
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

        var email = $"{prefix}_{Guid.NewGuid():N}@sbi-test.com";
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
                TextBlocks = textBlocks,
                CandidateRows = candidateRows,
                RawText = string.Join("\n", candidateRows.Select(r => r.RawLineText))
            });
        }

        return new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            OriginalFileName = Path.GetFileName(filePath),
            PageCount = doc.NumberOfPages,
            HasUsableText = true,
            Pages = extractionPages
        };
    }

    private static PdfExtractionResult BuildSyntheticSbiExtraction(Action<List<PdfPageResult>> configurePages)
    {
        var pages = new List<PdfPageResult>();
        configurePages(pages);

        return new PdfExtractionResult
        {
            FileId = Guid.NewGuid(),
            OriginalFileName = "SBI_Synthetic_Statement.pdf",
            PageCount = pages.Count,
            ExtractionStatus = "DigitalTextExtracted",
            HasUsableText = true,
            PdfType = "DigitalWithText",
            Pages = pages
        };
    }

    #region Test 01: SBI Format Detection

    [Fact]
    public void Test01_SBI_Format_Detection()
    {
        Assert.True(File.Exists(FixturePath), $"Fixture must exist: {FixturePath}");
        using var doc = PdfDocument.Open(FixturePath);
        var extraction = ExtractDocument(doc, FixturePath);

        var detection = _detector.DetectBank(extraction);

        Assert.NotNull(detection);
        Assert.True(detection.IsSupported, "Document should be detected as a supported bank.");
        Assert.Equal(BankType.SBI, detection.DetectedBank);
        Assert.Equal("State Bank of India", detection.BankName);
        Assert.Equal("SBI-v1", detection.DetectedFormat);
        Assert.True(detection.Confidence >= 0.90, $"Confidence {detection.Confidence} should be >= 0.90");
        Assert.Equal("30658259384", detection.AccountNumber);
    }

    #endregion

    #region Test 02: Multi-Page Transaction Extraction

    [Fact]
    public void Test02_MultiPage_Transaction_Extraction()
    {
        using var doc = PdfDocument.Open(FixturePath);
        var extraction = ExtractDocument(doc, FixturePath);
        var detection = _detector.DetectBank(extraction);

        var parseResult = _sbiParser.Parse(extraction, detection);

        Assert.True(parseResult.Success, $"Parsing should succeed: {parseResult.ErrorMessage}");
        Assert.Equal(19, parseResult.Transactions.Count);

        // Verify distribution across pages: 5 on Page 1, 13 on Page 2, 1 on Page 3
        int p1Count = parseResult.Transactions.Count(t => t.SourcePageNumber == 1);
        int p2Count = parseResult.Transactions.Count(t => t.SourcePageNumber == 2);
        int p3Count = parseResult.Transactions.Count(t => t.SourcePageNumber == 3);

        Assert.Equal(5, p1Count);
        Assert.Equal(13, p2Count);
        Assert.Equal(1, p3Count);
    }

    #endregion

    #region Test 03: Opening and Closing Balance

    [Fact]
    public void Test03_Opening_And_Closing_Balance()
    {
        using var doc = PdfDocument.Open(FixturePath);
        var extraction = ExtractDocument(doc, FixturePath);
        var detection = _detector.DetectBank(extraction);

        var parseResult = _sbiParser.Parse(extraction, detection);

        Assert.True(parseResult.Success);
        var firstTx = parseResult.Transactions.First();
        var lastTx = parseResult.Transactions.Last();

        // Continuity: firstTx balance (2,691.83) - credit (40.10) = 2,651.73 (Opening Balance)
        decimal inferredOpening = firstTx.Balance!.Value - (firstTx.Credit ?? 0m) + (firstTx.Debit ?? 0m);
        Assert.Equal(2651.73m, inferredOpening);

        // Closing balance on last tx is 2,064.83
        Assert.Equal(2064.83m, lastTx.Balance!.Value);
    }

    #endregion

    #region Test 04: Narration Reconstruction

    [Fact]
    public void Test04_Narration_Reconstruction()
    {
        using var doc = PdfDocument.Open(FixturePath);
        var extraction = ExtractDocument(doc, FixturePath);
        var detection = _detector.DetectBank(extraction);

        var parseResult = _sbiParser.Parse(extraction, detection);

        Assert.True(parseResult.Success);
        var tx1 = parseResult.Transactions[0];
        Assert.Contains("DEP TFR", tx1.Description);
        Assert.Contains("HPCL LPG SUBSIDY", tx1.Description);
        Assert.Contains("AMBAJHARI", tx1.Description);

        var tx2 = parseResult.Transactions[1];
        Assert.Contains("WDL TFR", tx2.Description);
        Assert.Contains("SBI GENERAL", tx2.Description);
    }

    #endregion

    #region Test 05: Cheque and Reference Extraction

    [Fact]
    public void Test05_Cheque_And_Reference_Extraction()
    {
        using var doc = PdfDocument.Open(FixturePath);
        var extraction = ExtractDocument(doc, FixturePath);
        var detection = _detector.DetectBank(extraction);

        var parseResult = _sbiParser.Parse(extraction, detection);

        Assert.True(parseResult.Success);
        // Ensure no transaction has "NA" as reference
        Assert.DoesNotContain(parseResult.Transactions, t => string.Equals(t.Reference, "NA", StringComparison.OrdinalIgnoreCase));

        // Ensure transactions have UTR/Reference when present in narration or ref column
        var txWithRef = parseResult.Transactions.Where(t => !string.IsNullOrEmpty(t.Reference) || !string.IsNullOrEmpty(t.Utr)).ToList();
        Assert.True(txWithRef.Count >= 10, "Most transactions have a reference or UTR identifier.");
    }

    #endregion

    #region Test 06: Debit and Credit Separation

    [Fact]
    public void Test06_Debit_Credit_Separation()
    {
        using var doc = PdfDocument.Open(FixturePath);
        var extraction = ExtractDocument(doc, FixturePath);
        var detection = _detector.DetectBank(extraction);

        var parseResult = _sbiParser.Parse(extraction, detection);

        Assert.True(parseResult.Success);

        var debits = parseResult.Transactions.Where(t => t.Debit.HasValue).ToList();
        var credits = parseResult.Transactions.Where(t => t.Credit.HasValue).ToList();

        Assert.Equal(4, debits.Count);
        Assert.Equal(15, credits.Count);

        Assert.Equal(1090.00m, debits.Sum(t => t.Debit!.Value));
        Assert.Equal(503.10m, credits.Sum(t => t.Credit!.Value));

        // Zero transactions should have both debit and credit
        Assert.DoesNotContain(parseResult.Transactions, t => t.Debit.HasValue && t.Credit.HasValue);
    }

    #endregion

    #region Test 07: Running Balance Continuity

    [Fact]
    public void Test07_Running_Balance_Continuity()
    {
        using var doc = PdfDocument.Open(FixturePath);
        var extraction = ExtractDocument(doc, FixturePath);
        var detection = _detector.DetectBank(extraction);

        var parseResult = _sbiParser.Parse(extraction, detection);

        Assert.True(parseResult.Success);
        Assert.Equal(0, parseResult.Warnings.Count(w => w.Contains("running balance continuity mismatch", StringComparison.OrdinalIgnoreCase)));

        decimal running = 2651.73m; // Opening balance
        int mismatches = 0;

        foreach (var tx in parseResult.Transactions)
        {
            decimal expected = tx.Debit.HasValue ? (running - tx.Debit.Value) : (running + tx.Credit!.Value);
            if (Math.Abs(expected - tx.Balance!.Value) > 0.01m)
            {
                mismatches++;
            }
            running = tx.Balance!.Value;
        }

        Assert.Equal(0, mismatches);
        Assert.Equal(2064.83m, running);
    }

    #endregion

    #region Test 08: Format-Specific Edge Cases

    [Fact]
    public void Test08_Format_Specific_Edge_Cases()
    {
        using var doc = PdfDocument.Open(FixturePath);
        var extraction = ExtractDocument(doc, FixturePath);
        var detection = _detector.DetectBank(extraction);

        var parseResult = _sbiParser.Parse(extraction, detection);

        // 1. Non-transaction rows (headers, summaries, page numbers) are excluded
        Assert.DoesNotContain(parseResult.Transactions, t => t.Description.Contains("Statement Summary", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(parseResult.Transactions, t => t.Description.Contains("CLOSING BALANCE", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(parseResult.Transactions, t => t.Description.Contains("BROUGHT FORWARD", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(parseResult.Transactions, t => t.Description.Contains("Page no.", StringComparison.OrdinalIgnoreCase));

        // 2. Post Date and Value Date are both present and valid
        foreach (var tx in parseResult.Transactions)
        {
            Assert.True(tx.TransactionDate > new DateTime(2020, 1, 1));
            Assert.True(tx.ValueDate.HasValue && tx.ValueDate.Value > new DateTime(2020, 1, 1));
        }
    }

    #endregion

    #region Test 09: Synthetic SBI Statements

    [Fact]
    public void Test09_Synthetic_SBI_Statements()
    {
        var extraction = BuildSyntheticSbiExtraction(pages =>
        {
            var textBlocks = new List<PdfTextBlock>
            {
                // Header
                new() { Text = "STATE BANK OF INDIA", X = 20, Y = 20, Width = 150, Height = 12 },
                new() { Text = "Account Number: 98765432101", X = 20, Y = 35, Width = 150, Height = 10 },
                new() { Text = "IFS Code: SBIN0001234", X = 20, Y = 50, Width = 120, Height = 10 },
                new() { Text = "Post Date", X = 20, Y = 80, Width = 40, Height = 10 },
                new() { Text = "Value Date", X = 70, Y = 80, Width = 40, Height = 10 },
                new() { Text = "Description", X = 150, Y = 80, Width = 60, Height = 10 },
                new() { Text = "Debit", X = 380, Y = 80, Width = 30, Height = 10 },
                new() { Text = "Credit", X = 450, Y = 80, Width = 30, Height = 10 },
                new() { Text = "Balance", X = 510, Y = 80, Width = 40, Height = 10 },

                // Brought Forward
                new() { Text = "BROUGHT FORWARD", X = 150, Y = 100, Width = 100, Height = 10 },
                new() { Text = "10,000.00CR", X = 510, Y = 100, Width = 60, Height = 10 },

                // Tx 1: Credit
                new() { Text = "01-01-2026", X = 20, Y = 120, Width = 50, Height = 10 },
                new() { Text = "01-01-2026", X = 70, Y = 120, Width = 50, Height = 10 },
                new() { Text = "SALARY CREDIT TECH CORP", X = 150, Y = 120, Width = 140, Height = 10 },
                new() { Text = "5,000.00", X = 450, Y = 120, Width = 40, Height = 10 },
                new() { Text = "15,000.00CR", X = 510, Y = 120, Width = 60, Height = 10 },

                // Tx 2: Debit
                new() { Text = "05-01-2026", X = 20, Y = 140, Width = 50, Height = 10 },
                new() { Text = "05-01-2026", X = 70, Y = 140, Width = 50, Height = 10 },
                new() { Text = "UPI RENT PAYMENT", X = 150, Y = 140, Width = 100, Height = 10 },
                new() { Text = "2,000.00", X = 380, Y = 140, Width = 40, Height = 10 },
                new() { Text = "13,000.00CR", X = 510, Y = 140, Width = 60, Height = 10 },

                // Closing Balance
                new() { Text = "CLOSING BALANCE", X = 150, Y = 170, Width = 100, Height = 10 },
                new() { Text = "13,000.00CR", X = 510, Y = 170, Width = 60, Height = 10 },
            };

            pages.Add(new PdfPageResult
            {
                PageNumber = 1,
                Width = 595,
                Height = 842,
                HasUsableText = true,
                TextBlocks = textBlocks,
                RawText = string.Join(" ", textBlocks.Select(b => b.Text))
            });
        });

        var detection = _detector.DetectBank(extraction);
        Assert.Equal(BankType.SBI, detection.DetectedBank);
        Assert.Equal("98765432101", detection.AccountNumber);

        var parseResult = _sbiParser.Parse(extraction, detection);
        Assert.True(parseResult.Success);
        Assert.Equal(2, parseResult.Transactions.Count);

        Assert.Equal("Credit", parseResult.Transactions[0].TransactionType);
        Assert.Equal(5000.00m, parseResult.Transactions[0].Amount);
        Assert.Equal(15000.00m, parseResult.Transactions[0].Balance);

        Assert.Equal("Debit", parseResult.Transactions[1].TransactionType);
        Assert.Equal(2000.00m, parseResult.Transactions[1].Amount);
        Assert.Equal(13000.00m, parseResult.Transactions[1].Balance);
    }

    #endregion

    #region Test 10: End-to-End Excel Export

    [Fact]
    public async Task Test10_End_To_End_Excel_Export()
    {
        var (client, _) = await RegisterUserAsync("SbiExportFlow");

        var pdfBytes = await File.ReadAllBytesAsync(FixturePath);

        // 1. Upload
        var uploadContent = CreateMultipartPdf("SBI.pdf", pdfBytes);
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
        Assert.Equal(19, txList.TotalCount);
        Assert.Equal("State Bank of India", txList.BankName);
        Assert.Equal("SBI-v1", txList.ParserVersion);

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
        Assert.Equal(20, txWs.LastRowUsed()!.RowNumber()); // 1 header + 19 data rows
        Assert.Equal("State Bank of India", txWs.Cell(2, 12).GetString());

        var infoWs = workbook.Worksheet("Statement Info");
        Assert.Equal("State Bank of India", infoWs.Cell(2, 2).GetString());
        Assert.Equal("SBI-v1", infoWs.Cell(3, 2).GetString());
        Assert.Equal("30658259384", infoWs.Cell(4, 2).GetString());

        var sumWs = workbook.Worksheet("Summary");
        Assert.Equal(503.10m, (decimal)sumWs.Cell(4, 2).GetDouble()); // Total Credits
        Assert.Equal(1090.00m, (decimal)sumWs.Cell(5, 2).GetDouble()); // Total Debits
        Assert.Equal("BALANCED", sumWs.Cell(8, 2).GetString());
    }

    #endregion

    #region Test 11: Regression Safety for All Existing Banks

    [Fact]
    public void Test11_Regression_Safety_All_Banks()
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
    }

    #endregion
}
