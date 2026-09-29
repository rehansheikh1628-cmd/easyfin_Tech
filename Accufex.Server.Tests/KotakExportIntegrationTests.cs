using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Accufex.Server.Data;
using Accufex.Server.DTOs;
using Accufex.Server.Export.Services;
using Accufex.Server.Models;
using Accufex.Server.Parsing;
using Accufex.Server.Parsing.Parsers;
using Accufex.Server.Validation.Models;
using Accufex.Server.Validation.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accufex.Server.Tests;

public class KotakExportIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;
    private readonly ExcelExportService _exportService = new(NullLogger<ExcelExportService>.Instance);
    private readonly TransactionValidationService _validationService = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public KotakExportIntegrationTests(CustomWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    private async Task<(HttpClient Client, AuthResponseDto Auth)> RegisterUserAsync(string prefix)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            BaseAddress = new Uri("http://localhost")
        });

        var email = $"{prefix}_{Guid.NewGuid():N}@kotak-test.com";
        var request = new RegisterRequest($"{prefix} Workspace", email, "SecurePass123!");
        var response = await client.PostAsJsonAsync("/api/auth/register", request, JsonOptions);
        Assert.True(response.IsSuccessStatusCode);

        var auth = await response.Content.ReadFromJsonAsync<AuthResponseDto>(JsonOptions);
        Assert.NotNull(auth);
        return (client, auth);
    }

    [Fact]
    public async Task Kotak_StatementWithZeroAmountEntries_ValidatesAsExportReady_AndGeneratesValidWorkbook()
    {
        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "50XXXXX741_01-04-2025_31-03-2026.pdf",
            UploadedAt = DateTime.UtcNow
        };

        // Construct Kotak transactions including normal debits/credits and zero-amount entries
        var rawTransactions = new List<Transaction>
        {
            new()
            {
                Id = Guid.NewGuid(),
                SourceFileId = fileRecord.Id,
                TransactionDate = new DateTime(2025, 4, 1),
                Description = "OPENING BALANCE / CHQ RETURN NOTICE",
                Debit = null,
                Credit = null,
                Amount = 0.00m,
                Balance = 11924.60m,
                Reference = "REF-001",
                Utr = null,
                TransactionType = "Credit",
                BankCode = (int)BankType.Kotak
            },
            new()
            {
                Id = Guid.NewGuid(),
                SourceFileId = fileRecord.Id,
                TransactionDate = new DateTime(2025, 4, 2),
                Description = "UPI/MERCHANT PAYMENT/999",
                Debit = 500.00m,
                Credit = null,
                Amount = 500.00m,
                Balance = 11424.60m,
                Reference = "UPI-227899615102",
                Utr = "KOTAK227899615102",
                TransactionType = "Debit",
                BankCode = (int)BankType.Kotak
            },
            new()
            {
                Id = Guid.NewGuid(),
                SourceFileId = fileRecord.Id,
                TransactionDate = new DateTime(2025, 4, 3),
                Description = "SALARY CREDIT NEFT",
                Debit = null,
                Credit = 45000.00m,
                Amount = 45000.00m,
                Balance = 56424.60m,
                Reference = "NEFT-KOTAK8821",
                Utr = "KOTAKN52025040301",
                TransactionType = "Credit",
                BankCode = (int)BankType.Kotak
            },
            new()
            {
                Id = Guid.NewGuid(),
                SourceFileId = fileRecord.Id,
                TransactionDate = new DateTime(2025, 4, 4),
                Description = "MEMO NOTICE / ACCOUNT SUMMARY RECORD",
                Debit = null,
                Credit = null,
                Amount = 0.00m,
                Balance = 56424.60m,
                Reference = null,
                Utr = null,
                TransactionType = "Debit",
                BankCode = (int)BankType.Kotak
            }
        };

        // 1. Validation & Enrichment
        var enriched = _validationService.ValidateAndEnrichStatement(rawTransactions, null);
        var summary = _validationService.ComputeSummary(fileRecord.Id, (int)BankType.Kotak, "Kotak Mahindra Bank", "KOTAK-v1", enriched);

        Assert.Equal(0, summary.InvalidCount);
        Assert.True(summary.IsReadyForExport, "Kotak statement with zero-amount entries must be eligible for export");
        Assert.Equal("Kotak Mahindra Bank", summary.BankName);
        Assert.Equal("KOTAK-v1", summary.ParserVersion);

        // 2. Generate Excel Workbook
        var bytes = await _exportService.GenerateStatementWorkbookAsync(fileRecord, summary, enriched);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0, "Workbook bytes must be non-empty");

        // 3. Re-open with ClosedXML and verify sheets & cell values
        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);

        var txSheet = workbook.Worksheet("Transactions");
        var infoSheet = workbook.Worksheet("Statement Info");
        var sumSheet = workbook.Worksheet("Summary");

        Assert.NotNull(txSheet);
        Assert.NotNull(infoSheet);
        Assert.NotNull(sumSheet);

        // Header + 4 transactions
        Assert.Equal(5, txSheet.LastRowUsed()!.RowNumber());

        // Check first row (zero-amount)
        Assert.Equal(new DateTime(2025, 4, 1), txSheet.Cell(2, 2).GetDateTime().Date);
        Assert.Equal("OPENING BALANCE / CHQ RETURN NOTICE", txSheet.Cell(2, 4).GetString());
        Assert.Equal("REF-001", txSheet.Cell(2, 5).GetString());
        Assert.Equal(0.00, txSheet.Cell(2, 9).GetDouble(), 2);
        Assert.Equal(11924.60, txSheet.Cell(2, 10).GetDouble(), 2);

        // Check second row (debit)
        Assert.Equal("UPI/MERCHANT PAYMENT/999", txSheet.Cell(3, 4).GetString());
        Assert.Equal("UPI-227899615102", txSheet.Cell(3, 5).GetString());
        Assert.Equal(500.00, txSheet.Cell(3, 7).GetDouble(), 2);
        Assert.Equal(11424.60, txSheet.Cell(3, 10).GetDouble(), 2);

        // Check third row (credit)
        Assert.Equal("SALARY CREDIT NEFT", txSheet.Cell(4, 4).GetString());
        Assert.Equal(45000.00, txSheet.Cell(4, 8).GetDouble(), 2);
        Assert.Equal(56424.60, txSheet.Cell(4, 10).GetDouble(), 2);

        // Check Statement Info sheet
        Assert.Equal("Kotak Mahindra Bank", infoSheet.Cell(2, 2).GetString());
        Assert.Equal("KOTAK-v1", infoSheet.Cell(3, 2).GetString());
    }

    [Fact]
    public async Task Kotak_ExportApiEndpoint_ReturnsHttp200AndValidReadableXlsx()
    {
        var (client, auth) = await RegisterUserAsync("KotakExport");

        // 1. Direct database setup: insert Kotak statement with zero-amount and normal transactions
        Guid statementId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccufexDbContext>();
            var user = db.Users.First(u => u.Email == auth.User!.Email);
            var appClient = db.Clients.First(c => c.UserId == user.Id);
            var fy = db.FinancialYears.First(f => f.ClientId == appClient.Id);

            var fileRecord = new FileRecord
            {
                Id = Guid.NewGuid(),
                ClientId = appClient.Id,
                FinancialYearId = fy.Id,
                OriginalFileName = "50XXXXX741_01-04-2025_31-03-2026.pdf",
                StoredFileName = $"{Guid.NewGuid()}.pdf",
                Extension = ".pdf",
                ContentType = "application/pdf",
                SizeBytes = 1024,
                ProcessingStatus = 2, // Parsed
                UploadedAt = DateTime.UtcNow
            };

            db.FileRecords.Add(fileRecord);
            statementId = fileRecord.Id;

            var importResult = new TransactionImportResult
            {
                Id = Guid.NewGuid(),
                SourceFileId = fileRecord.Id,
                ClientId = appClient.Id,
                FinancialYearId = fy.Id,
                TotalDetected = 3,
                ProcessedCount = 3,
                DetectedBank = (int)BankType.Kotak,
                Status = "Success"
            };
            db.TransactionImportResults.Add(importResult);

            var tx1 = new Transaction
            {
                Id = Guid.NewGuid(),
                SourceFileId = fileRecord.Id,
                TransactionDate = new DateTime(2025, 4, 1),
                Description = "KOTAK ZERO ENTRY MEMO",
                Debit = null,
                Credit = null,
                Amount = 0.00m,
                Balance = 5000.00m,
                BankCode = (int)BankType.Kotak,
                TransactionType = "Credit",
                CreatedAt = DateTime.UtcNow
            };
            var tx2 = new Transaction
            {
                Id = Guid.NewGuid(),
                SourceFileId = fileRecord.Id,
                TransactionDate = new DateTime(2025, 4, 2),
                Description = "KOTAK DEBIT PAYMENT",
                Debit = 1200.00m,
                Credit = null,
                Amount = 1200.00m,
                Balance = 3800.00m,
                BankCode = (int)BankType.Kotak,
                TransactionType = "Debit",
                Reference = "UPI-99214455",
                CreatedAt = DateTime.UtcNow
            };
            var tx3 = new Transaction
            {
                Id = Guid.NewGuid(),
                SourceFileId = fileRecord.Id,
                TransactionDate = new DateTime(2025, 4, 3),
                Description = "KOTAK INTEREST CREDIT",
                Debit = null,
                Credit = 150.00m,
                Amount = 150.00m,
                Balance = 3950.00m,
                BankCode = (int)BankType.Kotak,
                TransactionType = "Credit",
                Reference = "INT-2025-04",
                CreatedAt = DateTime.UtcNow
            };

            db.Transactions.AddRange(tx1, tx2, tx3);
            await db.SaveChangesAsync();
        }

        // 2. Call Export endpoint
        var exportResp = await client.GetAsync($"/api/statements/{statementId}/export/excel");
        Assert.Equal(HttpStatusCode.OK, exportResp.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", exportResp.Content.Headers.ContentType?.MediaType);

        var bytes = await exportResp.Content.ReadAsByteArrayAsync();
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 2000, "Exported file bytes must be non-empty");

        // 3. Validate workbook structure with ClosedXML
        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);

        var txWs = workbook.Worksheet("Transactions");
        Assert.NotNull(txWs);
        Assert.Equal(4, txWs.LastRowUsed()!.RowNumber()); // header + 3 rows
        Assert.Equal("KOTAK ZERO ENTRY MEMO", txWs.Cell(2, 4).GetString());
        Assert.Equal("KOTAK DEBIT PAYMENT", txWs.Cell(3, 4).GetString());
        Assert.Equal("KOTAK INTEREST CREDIT", txWs.Cell(4, 4).GetString());

        var infoWs = workbook.Worksheet("Statement Info");
        Assert.NotNull(infoWs);
        Assert.Equal("Kotak Mahindra Bank", infoWs.Cell(2, 2).GetString());
        Assert.Equal("KOTAK-v1", infoWs.Cell(3, 2).GetString());
    }

    [Fact]
    public async Task Kotak_GroundTruthStatement_FullExportPipeline_GeneratesValidExcelWorkbook()
    {
        string[] possiblePaths =
        [
            @"E:\BrandNew Day\EasyFin Tech\EasyFin_Tech\Accufex.Server\Storage\Statements\Extractions\a54ec1f64b8845a08b542f7110f6c754\2b178ff8-56fd-4632-94bb-383309c3e69b_extraction.json",
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\Accufex.Server\Storage\Statements\Extractions\a54ec1f64b8845a08b542f7110f6c754\2b178ff8-56fd-4632-94bb-383309c3e69b_extraction.json"))
        ];

        string? jsonPath = possiblePaths.FirstOrDefault(File.Exists);
        if (jsonPath == null)
        {
            _output.WriteLine("Ground truth Kotak statement extraction file not found. Skipping test.");
            return;
        }

        var json = await File.ReadAllTextAsync(jsonPath);
        var extraction = JsonSerializer.Deserialize<PdfExtractionResult>(json, JsonOptions);
        Assert.NotNull(extraction);

        var detector = new BankDetector(NullLogger<BankDetector>.Instance);
        var detection = detector.DetectBank(extraction);
        Assert.Equal(BankType.Kotak, detection.DetectedBank);
        Assert.Equal("5046856741", detection.AccountNumber);
        Assert.Equal("SAYYED HAMID", detection.CustomerName);

        var parser = new KotakStatementParser(NullLogger<KotakStatementParser>.Instance);
        var parseResult = parser.Parse(extraction, detection);
        Assert.True(parseResult.Success);
        Assert.Equal(4360, parseResult.Transactions.Count);
        Assert.Empty(parseResult.Warnings);

        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "50XXXXX741_01-04-2025_31-03-2026.pdf",
            UploadedAt = DateTime.UtcNow
        };

        var rawTransactions = parseResult.Transactions.Select(t => new Transaction
        {
            Id = t.Id,
            SourceFileId = fileRecord.Id,
            TransactionDate = t.TransactionDate,
            Description = t.Description,
            Reference = t.Reference,
            Utr = t.Utr,
            Debit = t.Debit,
            Credit = t.Credit,
            Amount = t.Amount,
            Balance = t.Balance,
            TransactionType = t.TransactionType,
            BankCode = t.BankCode
        }).ToList();

        var enriched = _validationService.ValidateAndEnrichStatement(rawTransactions, null);
        var summary = _validationService.ComputeSummary(fileRecord.Id, (int)BankType.Kotak, "Kotak Mahindra Bank", "KOTAK-v1", enriched);

        Assert.Equal(4360, summary.TotalTransactions);
        Assert.Equal(4360, summary.ValidCount);
        Assert.Equal(0, summary.InvalidCount);
        Assert.True(summary.IsReadyForExport);

        var bytes = await _exportService.GenerateStatementWorkbookAsync(fileRecord, summary, enriched);
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 200000, "Workbook bytes should exceed 200KB for 4,360 transactions");

        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);

        var txSheet = workbook.Worksheet("Transactions");
        var infoSheet = workbook.Worksheet("Statement Info");
        var sumSheet = workbook.Worksheet("Summary");

        Assert.NotNull(txSheet);
        Assert.NotNull(infoSheet);
        Assert.NotNull(sumSheet);

        // Header + 4360 transactions
        Assert.Equal(4361, txSheet.LastRowUsed()!.RowNumber());

        // First row
        Assert.Equal(new DateTime(2025, 4, 1), txSheet.Cell(2, 2).GetDateTime().Date);
        Assert.Contains("UPI/PhonePe", txSheet.Cell(2, 4).GetString());
        Assert.Equal("UPI-509185432310", txSheet.Cell(2, 5).GetString());
        Assert.Equal(1690.00, txSheet.Cell(2, 7).GetDouble(), 2);
        Assert.Equal(10234.60, txSheet.Cell(2, 10).GetDouble(), 2);

        // Last row
        Assert.Equal(new DateTime(2026, 3, 31), txSheet.Cell(4361, 2).GetDateTime().Date);
        Assert.Contains("Int.Pd:5046856741", txSheet.Cell(4361, 4).GetString());
        Assert.Equal(31.00, txSheet.Cell(4361, 8).GetDouble(), 2);
        Assert.Equal(45.76, txSheet.Cell(4361, 10).GetDouble(), 2);

        // Statement Info
        Assert.Equal("Kotak Mahindra Bank", infoSheet.Cell(2, 2).GetString());
        Assert.Equal("KOTAK-v1", infoSheet.Cell(3, 2).GetString());

        // Summary
        Assert.Equal("BALANCED", sumSheet.Cell(8, 2).GetString());
    }
}
