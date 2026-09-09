using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using EasyFin_Tech.Server.DTOs;
using EasyFin_Tech.Server.Export.Interfaces;
using EasyFin_Tech.Server.Export.Services;
using EasyFin_Tech.Server.Models;
using EasyFin_Tech.Server.Services;
using EasyFin_Tech.Server.Validation.Models;
using EasyFin_Tech.Server.Validation.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;
using Xunit.Abstractions;

namespace EasyFin_Tech.Server.Tests;

public class ExcelExportTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly ExcelExportService _exportService = new(NullLogger<ExcelExportService>.Instance);
    private readonly TransactionValidationService _validationService = new();

    public ExcelExportTests(CustomWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    #region Unit Tests with ClosedXML Workbook Inspection

    [Fact]
    public async Task Test01_GenerateStatementWorkbook_ProducesNonEmptyValidExcelBytes()
    {
        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "HDFC_Aug2026.pdf",
            UploadedAt = DateTime.UtcNow
        };

        var txs = new List<TransactionReviewDto>
        {
            new()
            {
                Id = Guid.NewGuid(),
                TransactionDate = new DateTime(2026, 8, 1),
                ValueDate = new DateTime(2026, 8, 1),
                Description = "SALARY CREDIT TECH CORP",
                Credit = 75000.00m,
                Amount = 75000.00m,
                Balance = 150000.00m,
                Reference = "REF882104",
                Utr = "HDFCR520260801001",
                TransactionType = "Credit",
                BankName = "HDFC Bank",
                Account = "50200031189753",
                ValidationStatus = "VALID"
            }
        };

        var summary = _validationService.ComputeSummary(fileRecord.Id, 1, "HDFC Bank", "HDFC-v1", txs);

        var bytes = await _exportService.GenerateStatementWorkbookAsync(fileRecord, summary, txs);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 1000);

        // Inspect actual workbook
        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);

        Assert.Equal(3, workbook.Worksheets.Count);
        Assert.NotNull(workbook.Worksheet("Transactions"));
        Assert.NotNull(workbook.Worksheet("Statement Info"));
        Assert.NotNull(workbook.Worksheet("Summary"));
    }

    [Fact]
    public async Task Test02_TransactionsSheet_ContainsExactHeadersAndFormatting()
    {
        var fileRecord = new FileRecord { Id = Guid.NewGuid(), OriginalFileName = "Statement.pdf", UploadedAt = DateTime.UtcNow };
        var txs = new List<TransactionReviewDto>();
        var summary = _validationService.ComputeSummary(fileRecord.Id, 1, "HDFC Bank", "HDFC-v1", txs);

        var bytes = await _exportService.GenerateStatementWorkbookAsync(fileRecord, summary, txs);

        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);
        var ws = workbook.Worksheet("Transactions");

        Assert.Equal("Sr No", ws.Cell(1, 1).GetString());
        Assert.Equal("Transaction Date", ws.Cell(1, 2).GetString());
        Assert.Equal("Value Date", ws.Cell(1, 3).GetString());
        Assert.Equal("Particulars / Narration", ws.Cell(1, 4).GetString());
        Assert.Equal("Cheque / Reference", ws.Cell(1, 5).GetString());
        Assert.Equal("UTR", ws.Cell(1, 6).GetString());
        Assert.Equal("Debit (-)", ws.Cell(1, 7).GetString());
        Assert.Equal("Credit (+)", ws.Cell(1, 8).GetString());
        Assert.Equal("Amount", ws.Cell(1, 9).GetString());
        Assert.Equal("Balance", ws.Cell(1, 10).GetString());
        Assert.Equal("Transaction Type", ws.Cell(1, 11).GetString());
        Assert.Equal("Bank", ws.Cell(1, 12).GetString());
        Assert.Equal("Account", ws.Cell(1, 13).GetString());
        Assert.Equal("Validation Status", ws.Cell(1, 14).GetString());
        Assert.Equal("Review Notes", ws.Cell(1, 15).GetString());

        // Verify frozen header row
        Assert.True(ws.SheetView.SplitRow > 0);
    }

    [Fact]
    public async Task Test03_DateAndNumericCellTypes_AreRealExcelTypesNotStrings()
    {
        var fileRecord = new FileRecord { Id = Guid.NewGuid(), OriginalFileName = "Statement.pdf", UploadedAt = DateTime.UtcNow };
        var txDate = new DateTime(2026, 8, 15);
        var valDate = new DateTime(2026, 8, 16);

        var txs = new List<TransactionReviewDto>
        {
            new()
            {
                Id = Guid.NewGuid(),
                TransactionDate = txDate,
                ValueDate = valDate,
                Description = "OFFICE EXPENSE VENDOR PAYMENT",
                Debit = 12500.50m,
                Credit = null,
                Amount = 12500.50m,
                Balance = 87499.50m,
                Reference = "CHQ-990214",
                Utr = "002910482194",
                TransactionType = "Debit",
                BankName = "YES BANK",
                Account = "0014901000214",
                ValidationStatus = "VALID"
            }
        };

        var summary = _validationService.ComputeSummary(fileRecord.Id, 2, "YES BANK", "YES-v1", txs);
        var bytes = await _exportService.GenerateStatementWorkbookAsync(fileRecord, summary, txs);

        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);
        var ws = workbook.Worksheet("Transactions");

        // Transaction Date: Real Excel Date
        var cellDate = ws.Cell(2, 2);
        Assert.Equal(XLDataType.DateTime, cellDate.DataType);
        Assert.Equal(txDate.Date, cellDate.GetDateTime().Date);

        // Value Date: Real Excel Date
        var cellValDate = ws.Cell(2, 3);
        Assert.Equal(XLDataType.DateTime, cellValDate.DataType);
        Assert.Equal(valDate.Date, cellValDate.GetDateTime().Date);

        // Debit: Numeric with 2 decimals
        var cellDebit = ws.Cell(2, 7);
        Assert.Equal(XLDataType.Number, cellDebit.DataType);
        Assert.Equal(12500.50, cellDebit.GetDouble(), precision: 2);
        Assert.Equal("#,##0.00", cellDebit.Style.NumberFormat.Format);

        // Credit: Blank
        var cellCredit = ws.Cell(2, 8);
        Assert.True(cellCredit.IsEmpty());

        // Amount: Numeric
        var cellAmt = ws.Cell(2, 9);
        Assert.Equal(XLDataType.Number, cellAmt.DataType);
        Assert.Equal(12500.50, cellAmt.GetDouble(), precision: 2);

        // Balance: Numeric
        var cellBal = ws.Cell(2, 10);
        Assert.Equal(XLDataType.Number, cellBal.DataType);
        Assert.Equal(87499.50, cellBal.GetDouble(), precision: 2);

        // Reference and UTR: Formatted as text to prevent scientific notation
        var cellRef = ws.Cell(2, 5);
        Assert.Equal("CHQ-990214", cellRef.GetString());
        Assert.Equal("@", cellRef.Style.NumberFormat.Format);

        var cellUtr = ws.Cell(2, 6);
        Assert.Equal("002910482194", cellUtr.GetString());
        Assert.Equal("@", cellUtr.Style.NumberFormat.Format);

        // Account: Text formatted
        var cellAcc = ws.Cell(2, 13);
        Assert.Equal("0014901000214", cellAcc.GetString());
        Assert.Equal("@", cellAcc.Style.NumberFormat.Format);
    }

    [Fact]
    public async Task Test04_NegativeBalances_PreservedAsSignedNumericWithoutRounding()
    {
        var fileRecord = new FileRecord { Id = Guid.NewGuid(), OriginalFileName = "Statement.pdf", UploadedAt = DateTime.UtcNow };
        var txs = new List<TransactionReviewDto>
        {
            new()
            {
                Id = Guid.NewGuid(),
                TransactionDate = new DateTime(2026, 8, 1),
                Description = "OVERDRAFT FACILITY CHARGE",
                Debit = 5000.00m,
                Amount = 5000.00m,
                Balance = -14250.75m,
                TransactionType = "Debit",
                ValidationStatus = "VALID"
            }
        };

        var summary = _validationService.ComputeSummary(fileRecord.Id, 1, "HDFC Bank", "HDFC-v1", txs);
        var bytes = await _exportService.GenerateStatementWorkbookAsync(fileRecord, summary, txs);

        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);
        var ws = workbook.Worksheet("Transactions");

        var balCell = ws.Cell(2, 10);
        Assert.Equal(XLDataType.Number, balCell.DataType);
        Assert.Equal(-14250.75, balCell.GetDouble(), precision: 2);
    }

    [Fact]
    public async Task Test05_MultilineDescriptions_PreservedWithWrapText()
    {
        var multilineDesc = "NEFT-INWARD-SBIN000142\nINVOICE PAYMENT FOR AUGUST 2026\nPO NUMBER: 99182410-Q2\nBRANCH: COMMERCIAL MUMBAI";
        var fileRecord = new FileRecord { Id = Guid.NewGuid(), OriginalFileName = "Statement.pdf", UploadedAt = DateTime.UtcNow };

        var txs = new List<TransactionReviewDto>
        {
            new()
            {
                Id = Guid.NewGuid(),
                TransactionDate = new DateTime(2026, 8, 10),
                Description = multilineDesc,
                Amount = 1000.00m,
                Credit = 1000.00m,
                Balance = 1000.00m,
                TransactionType = "Credit",
                ValidationStatus = "VALID"
            }
        };

        var summary = _validationService.ComputeSummary(fileRecord.Id, 1, "HDFC Bank", "HDFC-v1", txs);
        var bytes = await _exportService.GenerateStatementWorkbookAsync(fileRecord, summary, txs);

        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);
        var ws = workbook.Worksheet("Transactions");

        var descCell = ws.Cell(2, 4);
        Assert.Equal(multilineDesc, descCell.GetString());
        Assert.True(descCell.Style.Alignment.WrapText);
    }

    [Fact]
    public async Task Test06_ExportUsesFinalCurrentValues_NeverOriginalValuesWhenCorrected()
    {
        var fileRecord = new FileRecord { Id = Guid.NewGuid(), OriginalFileName = "Statement.pdf", UploadedAt = DateTime.UtcNow };

        var tx = new TransactionReviewDto
        {
            Id = Guid.NewGuid(),
            TransactionDate = new DateTime(2026, 8, 20),
            Description = "CORRECTED NARRATION PER AUDIT VOUCHER",
            Debit = 3500.00m,
            Credit = null,
            Amount = 3500.00m,
            Balance = 46500.00m,
            Reference = "REF-CORRECTED",
            TransactionType = "Debit",
            ValidationStatus = "CORRECTED",
            IsCorrected = true,
            CorrectionCount = 2,
            OriginalValues = new OriginalTransactionSnapshotDto
            {
                Description = "ORIGINAL WRONG TYPO NARRATION",
                Amount = 3000.00m,
                Debit = 3000.00m,
                Reference = "REF-ORIGINAL"
            }
        };

        var txs = new List<TransactionReviewDto> { tx };
        var summary = _validationService.ComputeSummary(fileRecord.Id, 1, "HDFC Bank", "HDFC-v1", txs);

        var bytes = await _exportService.GenerateStatementWorkbookAsync(fileRecord, summary, txs);

        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);
        var ws = workbook.Worksheet("Transactions");

        // Cell must contain the corrected description and reference, NOT original values
        Assert.Equal("CORRECTED NARRATION PER AUDIT VOUCHER", ws.Cell(2, 4).GetString());
        Assert.Equal("REF-CORRECTED", ws.Cell(2, 5).GetString());
        Assert.Equal(3500.00, ws.Cell(2, 7).GetDouble(), precision: 2);
        Assert.Equal("CORRECTED", ws.Cell(2, 14).GetString());
    }

    [Fact]
    public async Task Test07_ReviewRows_ExportedWithDetailedReviewNotes()
    {
        var fileRecord = new FileRecord { Id = Guid.NewGuid(), OriginalFileName = "Statement.pdf", UploadedAt = DateTime.UtcNow };

        var tx = new TransactionReviewDto
        {
            Id = Guid.NewGuid(),
            TransactionDate = new DateTime(2026, 8, 5),
            Description = "ROUND SUM SUSPICIOUS TRANSFER",
            Debit = 100000.00m,
            Amount = 100000.00m,
            Balance = 200000.00m,
            TransactionType = "Debit",
            ValidationStatus = "REVIEW",
            ValidationWarnings = ["High-value round amount outflow requires confirmation", "Running balance discrepancy detected"]
        };

        var txs = new List<TransactionReviewDto> { tx };
        var summary = _validationService.ComputeSummary(fileRecord.Id, 1, "HDFC Bank", "HDFC-v1", txs);

        var bytes = await _exportService.GenerateStatementWorkbookAsync(fileRecord, summary, txs);

        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);
        var ws = workbook.Worksheet("Transactions");

        Assert.Equal("REVIEW", ws.Cell(2, 14).GetString());
        var notes = ws.Cell(2, 15).GetString();
        Assert.Contains("High-value round amount", notes);
        Assert.Contains("Running balance discrepancy", notes);
    }

    [Fact]
    public async Task Test08_StatementInfoSheet_ContainsAccurateMetadata()
    {
        var fileId = Guid.NewGuid();
        var fileRecord = new FileRecord
        {
            Id = fileId,
            OriginalFileName = "August_HDFC_Statement.pdf",
            UploadedAt = new DateTime(2026, 8, 25, 10, 30, 0, DateTimeKind.Utc)
        };

        var txs = new List<TransactionReviewDto>
        {
            new() { Id = Guid.NewGuid(), TransactionDate = new DateTime(2026, 8, 1), Description = "SALARY", Credit = 50000m, Amount = 50000m, Balance = 50000m, Account = "00214820", ValidationStatus = "VALID" },
            new() { Id = Guid.NewGuid(), TransactionDate = new DateTime(2026, 8, 2), Description = "RENT", Debit = 20000m, Amount = 20000m, Balance = 30000m, Account = "00214820", ValidationStatus = "VALID" }
        };

        var summary = _validationService.ComputeSummary(fileId, 1, "HDFC Bank", "HDFC-v1", txs);
        var bytes = await _exportService.GenerateStatementWorkbookAsync(fileRecord, summary, txs);

        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);
        var ws = workbook.Worksheet("Statement Info");

        // Inspect metadata rows
        Assert.Equal("HDFC Bank", ws.Cell(2, 2).GetString());
        Assert.Equal("HDFC-v1", ws.Cell(3, 2).GetString());
        Assert.Equal("00214820", ws.Cell(4, 2).GetString());
        Assert.Equal("August_HDFC_Statement.pdf", ws.Cell(5, 2).GetString());
        Assert.Equal(fileId.ToString(), ws.Cell(6, 2).GetString());
        Assert.Equal(2, ws.Cell(9, 2).GetDouble());
    }

    [Fact]
    public async Task Test09_SummarySheet_TotalsMatchTransactionData()
    {
        var fileRecord = new FileRecord { Id = Guid.NewGuid(), OriginalFileName = "Statement.pdf", UploadedAt = DateTime.UtcNow };

        var txs = new List<TransactionReviewDto>
        {
            new() { Id = Guid.NewGuid(), TransactionDate = new DateTime(2026, 8, 1), Description = "TX1", Credit = 10000m, Amount = 10000m, Balance = 20000m, ValidationStatus = "VALID" },
            new() { Id = Guid.NewGuid(), TransactionDate = new DateTime(2026, 8, 2), Description = "TX2", Debit = 4000m, Amount = 4000m, Balance = 16000m, ValidationStatus = "VALID" },
            new() { Id = Guid.NewGuid(), TransactionDate = new DateTime(2026, 8, 3), Description = "TX3", Debit = 1000m, Amount = 1000m, Balance = 15000m, ValidationStatus = "VALID" }
        };

        var summary = _validationService.ComputeSummary(fileRecord.Id, 1, "HDFC Bank", "HDFC-v1", txs);
        var bytes = await _exportService.GenerateStatementWorkbookAsync(fileRecord, summary, txs);

        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);
        var ws = workbook.Worksheet("Summary");

        // Opening Balance: 20000 - 10000 = 10000
        Assert.Equal(10000, ws.Cell(3, 2).GetDouble(), precision: 2);
        // Total Credits: 10000
        Assert.Equal(10000, ws.Cell(4, 2).GetDouble(), precision: 2);
        // Total Debits: 5000
        Assert.Equal(5000, ws.Cell(5, 2).GetDouble(), precision: 2);
        // Net Movement: +5000
        Assert.Equal(5000, ws.Cell(6, 2).GetDouble(), precision: 2);
        // Closing Balance: 15000
        Assert.Equal(15000, ws.Cell(7, 2).GetDouble(), precision: 2);
    }

    [Fact]
    public async Task Test10_LargeStatementExport_700PlusTransactions_Benchmark()
    {
        var fileRecord = new FileRecord { Id = Guid.NewGuid(), OriginalFileName = "Large_HDFC_Statement.pdf", UploadedAt = DateTime.UtcNow };

        var txs = new List<TransactionReviewDto>(750);
        decimal runningBal = 1000000.00m;
        for (int i = 0; i < 750; i++)
        {
            decimal debitAmt = (i % 2 == 0) ? 250.00m + i : 0m;
            decimal creditAmt = (i % 2 != 0) ? 500.00m + i : 0m;
            decimal amt = debitAmt > 0 ? debitAmt : creditAmt;
            runningBal = runningBal - debitAmt + creditAmt;

            txs.Add(new TransactionReviewDto
            {
                Id = Guid.NewGuid(),
                TransactionDate = new DateTime(2026, 1, 1).AddDays(i % 365),
                ValueDate = new DateTime(2026, 1, 1).AddDays(i % 365),
                Description = $"BENCHMARK TRANSACTION RECORD #{i + 1} CLIENT BATCH REF",
                Debit = debitAmt > 0 ? debitAmt : null,
                Credit = creditAmt > 0 ? creditAmt : null,
                Amount = amt,
                Balance = runningBal,
                Reference = $"REF{i:D6}",
                Utr = $"UTR{i:D12}",
                TransactionType = debitAmt > 0 ? "Debit" : "Credit",
                BankName = "HDFC Bank",
                Account = "50200031189753",
                ValidationStatus = "VALID"
            });
        }

        var summary = _validationService.ComputeSummary(fileRecord.Id, 1, "HDFC Bank", "HDFC-v1", txs);

        var sw = Stopwatch.StartNew();
        var bytes = await _exportService.GenerateStatementWorkbookAsync(fileRecord, summary, txs);
        sw.Stop();

        _output.WriteLine($"[PERFORMANCE BENCHMARK] 750 Transactions Excel Generation Time: {sw.ElapsedMilliseconds} ms");

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 20000);

        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);
        var ws = workbook.Worksheet("Transactions");

        // Verify exact row count (1 header + 750 rows = 751 rows)
        Assert.Equal(751, ws.LastRowUsed()!.RowNumber());
        Assert.Equal(750, ws.Cell(751, 1).GetDouble());
    }

    [Fact]
    public async Task Test11_1000PlusTransactions_StressTest()
    {
        var fileRecord = new FileRecord { Id = Guid.NewGuid(), OriginalFileName = "Super_Large_Statement.pdf", UploadedAt = DateTime.UtcNow };

        var txs = new List<TransactionReviewDto>(1200);
        decimal balance = 500000m;
        for (int i = 0; i < 1200; i++)
        {
            balance += 10m;
            txs.Add(new TransactionReviewDto
            {
                Id = Guid.NewGuid(),
                TransactionDate = new DateTime(2026, 1, 1).AddDays(i % 365),
                Description = $"BULK TRANSACTION ENTRY #{i}",
                Amount = 10m,
                Credit = 10m,
                Balance = balance,
                TransactionType = "Credit",
                ValidationStatus = "VALID"
            });
        }

        var summary = _validationService.ComputeSummary(fileRecord.Id, 1, "HDFC Bank", "HDFC-v1", txs);

        var sw = Stopwatch.StartNew();
        var bytes = await _exportService.GenerateStatementWorkbookAsync(fileRecord, summary, txs);
        sw.Stop();

        _output.WriteLine($"[PERFORMANCE BENCHMARK] 1,200 Transactions Excel Generation Time: {sw.ElapsedMilliseconds} ms");

        Assert.NotNull(bytes);
        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);
        var ws = workbook.Worksheet("Transactions");

        Assert.Equal(1201, ws.LastRowUsed()!.RowNumber());
    }

    [Fact]
    public async Task Test12_EmptyStatement_GeneratesCleanWorkbookWithoutError()
    {
        var fileRecord = new FileRecord { Id = Guid.NewGuid(), OriginalFileName = "Empty.pdf", UploadedAt = DateTime.UtcNow };
        var txs = new List<TransactionReviewDto>();
        var summary = _validationService.ComputeSummary(fileRecord.Id, 1, "HDFC Bank", "HDFC-v1", txs);

        var bytes = await _exportService.GenerateStatementWorkbookAsync(fileRecord, summary, txs);

        Assert.NotNull(bytes);
        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);
        var ws = workbook.Worksheet("Transactions");

        Assert.Equal(1, ws.LastRowUsed()!.RowNumber()); // Only header row
    }

    [Fact]
    public async Task Test13_SpecialCharactersInDescription_HandledSafely()
    {
        var specialText = "TEST & CO <SPECIAL> \"QUOTES\" 'APOSTROPHE' \t TAB \\ SLASH / FORWARD € £ ¥ ₹ © ®";
        var fileRecord = new FileRecord { Id = Guid.NewGuid(), OriginalFileName = "Special.pdf", UploadedAt = DateTime.UtcNow };

        var txs = new List<TransactionReviewDto>
        {
            new()
            {
                Id = Guid.NewGuid(),
                TransactionDate = new DateTime(2026, 8, 1),
                Description = specialText,
                Amount = 100m,
                Credit = 100m,
                Balance = 100m,
                TransactionType = "Credit",
                ValidationStatus = "VALID"
            }
        };

        var summary = _validationService.ComputeSummary(fileRecord.Id, 1, "HDFC Bank", "HDFC-v1", txs);
        var bytes = await _exportService.GenerateStatementWorkbookAsync(fileRecord, summary, txs);

        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);
        var ws = workbook.Worksheet("Transactions");

        Assert.Equal(specialText, ws.Cell(2, 4).GetString());
    }

    [Fact]
    public void Test14_GenerateSafeExportFileName_SanitizesProperly()
    {
        var safeName = _exportService.GenerateSafeExportFileName("My/Unsafe:Statement\\File.pdf", "Axis Bank (Retail)", new DateTime(2026, 8, 15));
        Assert.DoesNotContain("/", safeName);
        Assert.DoesNotContain("\\", safeName);
        Assert.DoesNotContain(":", safeName);
        Assert.EndsWith(".xlsx", safeName);
        Assert.StartsWith("EasyFin_", safeName);
        Assert.Contains("20260815", safeName);
    }

    #endregion

    #region Integration Tests with StatementsController API & End-to-End Flow

    private static byte[] GenerateTestPdf()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        page.AddText("HDFC BANK LIMITED - Statement of account", 10, new PdfPoint(50, 800), font);
        page.AddText("Account No : 50200031189753", 10, new PdfPoint(50, 780), font);
        page.AddText("RTGS/NEFT IFSC : HDFC0004224", 10, new PdfPoint(50, 760), font);
        page.AddText("Statement From : 01/04/2024 To : 31/03/2025", 10, new PdfPoint(50, 740), font);

        // Header
        page.AddText("Date", 10, new PdfPoint(40, 690), font);
        page.AddText("Narration", 10, new PdfPoint(150, 690), font);
        page.AddText("Chq./Ref.No.", 10, new PdfPoint(290, 690), font);
        page.AddText("Value Dt", 10, new PdfPoint(365, 690), font);
        page.AddText("Withdrawal Amt.", 10, new PdfPoint(420, 690), font);
        page.AddText("Deposit Amt.", 10, new PdfPoint(490, 690), font);
        page.AddText("Closing Balance", 10, new PdfPoint(565, 690), font);

        // Row 1
        page.AddText("01/04/24", 10, new PdfPoint(35, 660), font);
        page.AddText("SALARY CREDIT", 10, new PdfPoint(150, 660), font);
        page.AddText("REF0001", 10, new PdfPoint(290, 660), font);
        page.AddText("01/04/24", 10, new PdfPoint(365, 660), font);
        page.AddText("50,000.00", 10, new PdfPoint(490, 660), font);
        page.AddText("100,000.00", 10, new PdfPoint(565, 660), font);

        // Row 2
        page.AddText("02/04/24", 10, new PdfPoint(35, 630), font);
        page.AddText("OFFICE SUPPLIES", 10, new PdfPoint(150, 630), font);
        page.AddText("REF0002", 10, new PdfPoint(290, 630), font);
        page.AddText("02/04/24", 10, new PdfPoint(365, 630), font);
        page.AddText("2,500.00", 10, new PdfPoint(420, 630), font);
        page.AddText("97,500.00", 10, new PdfPoint(565, 630), font);

        return builder.Build();
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

        var email = $"{prefix}_{Guid.NewGuid():N}@excel-test.com";
        var request = new RegisterRequest($"{prefix} Workspace", email, "SecurePass123!");
        var response = await client.PostAsJsonAsync("/api/auth/register", request, JsonOptions);
        Assert.True(response.IsSuccessStatusCode);

        var auth = await response.Content.ReadFromJsonAsync<AuthResponseDto>(JsonOptions);
        Assert.NotNull(auth);
        return (client, auth);
    }

    [Fact]
    public async Task Test15_ExportExcelEndpoint_Success_ReturnsSpreadsheet()
    {
        var (client, auth) = await RegisterUserAsync("ExportSuccess");

        // 1. Upload
        var pdfBytes = GenerateTestPdf();
        var uploadContent = CreateMultipartPdf("TestStatement.pdf", pdfBytes);
        var uploadResp = await client.PostAsync("/api/statements/upload", uploadContent);
        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>(JsonOptions);
        var fileId = uploadResult!.FileId;

        // 2. Parse
        var parseResp = await client.PostAsync($"/api/statements/{fileId}/parse", null);
        Assert.True(parseResp.IsSuccessStatusCode);

        // 3. Export Excel
        var exportResp = await client.GetAsync($"/api/statements/{fileId}/export/excel");
        Assert.Equal(HttpStatusCode.OK, exportResp.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", exportResp.Content.Headers.ContentType?.MediaType);

        var fileBytes = await exportResp.Content.ReadAsByteArrayAsync();
        Assert.NotNull(fileBytes);
        Assert.True(fileBytes.Length > 2000);

        // 4. Open and inspect with ClosedXML
        using var stream = new MemoryStream(fileBytes);
        using var workbook = new XLWorkbook(stream);

        var ws = workbook.Worksheet("Transactions");
        Assert.Equal(3, ws.LastRowUsed()!.RowNumber()); // 1 header + 2 transactions
        Assert.Equal("SALARY CREDIT", ws.Cell(2, 4).GetString());
        Assert.Equal("OFFICE SUPPLIES", ws.Cell(3, 4).GetString());
    }

    [Fact]
    public async Task Test16_ExportExcel_PreservesTransactionOrder()
    {
        var (client, auth) = await RegisterUserAsync("ExportOrder");

        var pdfBytes = GenerateTestPdf();
        var uploadContent = CreateMultipartPdf("TestStatement.pdf", pdfBytes);
        var uploadResp = await client.PostAsync("/api/statements/upload", uploadContent);
        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>(JsonOptions);
        var fileId = uploadResult!.FileId;

        await client.PostAsync($"/api/statements/{fileId}/parse", null);

        var exportResp = await client.GetAsync($"/api/statements/{fileId}/export/excel");
        var fileBytes = await exportResp.Content.ReadAsByteArrayAsync();

        using var stream = new MemoryStream(fileBytes);
        using var workbook = new XLWorkbook(stream);
        var ws = workbook.Worksheet("Transactions");

        // Row 1 should be SALARY CREDIT, Row 2 should be OFFICE SUPPLIES
        Assert.Equal("SALARY CREDIT", ws.Cell(2, 4).GetString());
        Assert.Equal("OFFICE SUPPLIES", ws.Cell(3, 4).GetString());
    }

    [Fact]
    public async Task Test17_ReExportAfterCorrection_ReflectsLatestCorrectedValues()
    {
        var (client, auth) = await RegisterUserAsync("ReExportCorrection");

        var pdfBytes = GenerateTestPdf();
        var uploadContent = CreateMultipartPdf("TestStatement.pdf", pdfBytes);
        var uploadResp = await client.PostAsync("/api/statements/upload", uploadContent);
        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>(JsonOptions);
        var fileId = uploadResult!.FileId;

        await client.PostAsync($"/api/statements/{fileId}/parse", null);

        // First export before correction
        var export1Resp = await client.GetAsync($"/api/statements/{fileId}/export/excel");
        var bytes1 = await export1Resp.Content.ReadAsByteArrayAsync();
        using (var wb1 = new XLWorkbook(new MemoryStream(bytes1)))
        {
            var ws1 = wb1.Worksheet("Transactions");
            Assert.Equal("SALARY CREDIT", ws1.Cell(2, 4).GetString());
            Assert.Equal("VALID", ws1.Cell(2, 14).GetString());
        }

        // Get transaction ID
        var txResp = await client.GetAsync($"/api/statements/{fileId}/transactions");
        var txList = await txResp.Content.ReadFromJsonAsync<StatementTransactionsResponse>(JsonOptions);
        var targetTx = txList!.Items.First(t => t.Description == "SALARY CREDIT");

        // Apply correction
        var req = new CorrectTransactionRequest
        {
            TransactionDate = targetTx.TransactionDate,
            ValueDate = targetTx.ValueDate,
            Description = "SALARY CREDIT - SENIOR CONSULTANT",
            Debit = targetTx.Debit,
            Credit = targetTx.Credit,
            Amount = targetTx.Amount,
            Balance = targetTx.Balance,
            Reference = "CHQ-REVISED-99",
            TransactionType = targetTx.TransactionType,
            Reason = "Updated client consultant title"
        };
        var putResp = await client.PutAsJsonAsync($"/api/statements/{fileId}/transactions/{targetTx.Id}", req);
        Assert.True(putResp.IsSuccessStatusCode);

        // Re-export after correction
        var export2Resp = await client.GetAsync($"/api/statements/{fileId}/export/excel");
        Assert.Equal(HttpStatusCode.OK, export2Resp.StatusCode);
        var bytes2 = await export2Resp.Content.ReadAsByteArrayAsync();

        using (var wb2 = new XLWorkbook(new MemoryStream(bytes2)))
        {
            var ws2 = wb2.Worksheet("Transactions");
            // Must contain corrected value
            Assert.Equal("SALARY CREDIT - SENIOR CONSULTANT", ws2.Cell(2, 4).GetString());
            Assert.Equal("CHQ-REVISED-99", ws2.Cell(2, 5).GetString());
            Assert.Equal("CORRECTED", ws2.Cell(2, 14).GetString());
        }
    }

    [Fact]
    public async Task Test18_ExportBlockedWhenStatementHasInvalidTransactions()
    {
        var (client, auth) = await RegisterUserAsync("InvalidExportBlock");

        var pdfBytes = GenerateTestPdf();
        var uploadContent = CreateMultipartPdf("TestStatement.pdf", pdfBytes);
        var uploadResp = await client.PostAsync("/api/statements/upload", uploadContent);
        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>(JsonOptions);
        var fileId = uploadResult!.FileId;

        await client.PostAsync($"/api/statements/{fileId}/parse", null);

        var txResp = await client.GetAsync($"/api/statements/{fileId}/transactions");
        var txList = await txResp.Content.ReadFromJsonAsync<StatementTransactionsResponse>(JsonOptions);
        var targetTx = txList!.Items.First();

        // Corrupt row into INVALID state by setting future date
        var futureReq = new CorrectTransactionRequest
        {
            TransactionDate = DateTime.UtcNow.AddDays(30), // Future date -> INVALID
            Description = "FUTURE INVALID DATE",
            Debit = targetTx.Debit,
            Credit = targetTx.Credit,
            Amount = targetTx.Amount,
            Balance = targetTx.Balance,
            Reference = targetTx.Reference,
            TransactionType = targetTx.TransactionType,
            Reason = "Testing invalid export blocking"
        };
        await client.PutAsJsonAsync($"/api/statements/{fileId}/transactions/{targetTx.Id}", futureReq);

        // Export should now be BLOCKED with 400 Bad Request
        var exportResp = await client.GetAsync($"/api/statements/{fileId}/export/excel");
        Assert.Equal(HttpStatusCode.BadRequest, exportResp.StatusCode);

        var problem = await exportResp.Content.ReadFromJsonAsync<ProblemDetails>(JsonOptions);
        Assert.NotNull(problem);
        Assert.Contains("invalid", problem.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Test19_CrossUserExport_DeniedWith404NotFound()
    {
        var (clientA, authA) = await RegisterUserAsync("UserOwnerA");
        var (clientB, authB) = await RegisterUserAsync("UserAttackerB");

        var pdfBytes = GenerateTestPdf();
        var uploadContent = CreateMultipartPdf("TestStatement.pdf", pdfBytes);
        var uploadResp = await clientA.PostAsync("/api/statements/upload", uploadContent);
        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>(JsonOptions);
        var fileIdA = uploadResult!.FileId;

        await clientA.PostAsync($"/api/statements/{fileIdA}/parse", null);

        // User B attempts to export statement belonging to User A
        var crossExportResp = await clientB.GetAsync($"/api/statements/{fileIdA}/export/excel");
        Assert.Equal(HttpStatusCode.NotFound, crossExportResp.StatusCode);
    }

    [Fact]
    public async Task Test20_IdManipulation_CannotExportAnotherUsersStatement()
    {
        var (client, auth) = await RegisterUserAsync("IdManipulator");

        var randomFileId = Guid.NewGuid();
        var resp = await client.GetAsync($"/api/statements/{randomFileId}/export/excel");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Test21_ExportIsReadOnly_DoesNotMutateDatabaseData()
    {
        var (client, auth) = await RegisterUserAsync("ReadOnlyExport");

        var pdfBytes = GenerateTestPdf();
        var uploadContent = CreateMultipartPdf("TestStatement.pdf", pdfBytes);
        var uploadResp = await client.PostAsync("/api/statements/upload", uploadContent);
        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>(JsonOptions);
        var fileId = uploadResult!.FileId;

        await client.PostAsync($"/api/statements/{fileId}/parse", null);

        var txRespBefore = await client.GetAsync($"/api/statements/{fileId}/transactions");
        var txListBefore = await txRespBefore.Content.ReadFromJsonAsync<StatementTransactionsResponse>(JsonOptions);

        // Export
        var exportResp = await client.GetAsync($"/api/statements/{fileId}/export/excel");
        Assert.Equal(HttpStatusCode.OK, exportResp.StatusCode);

        var txRespAfter = await client.GetAsync($"/api/statements/{fileId}/transactions");
        var txListAfter = await txRespAfter.Content.ReadFromJsonAsync<StatementTransactionsResponse>(JsonOptions);

        Assert.Equal(txListBefore!.TotalCount, txListAfter!.TotalCount);
        Assert.Equal(txListBefore.Items[0].Description, txListAfter.Items[0].Description);
        Assert.Equal(txListBefore.Items[0].Amount, txListAfter.Items[0].Amount);
    }

    [Fact]
    public async Task Test22_HdfcStatement_772Rows_ExportsSuccessfully_AndInspectsWorkbook()
    {
        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "HDFC_Production_Statement.pdf",
            UploadedAt = DateTime.UtcNow
        };

        var txs = new List<TransactionReviewDto>(772);
        decimal balance = 1500000m;
        for (int i = 0; i < 772; i++)
        {
            decimal debit = (i % 2 == 0) ? 1000m : 0m;
            decimal credit = (i % 2 != 0) ? 2500m : 0m;
            balance = balance - debit + credit;
            txs.Add(new TransactionReviewDto
            {
                Id = Guid.NewGuid(),
                TransactionDate = new DateTime(2024, 4, 1).AddDays(i % 365),
                ValueDate = new DateTime(2024, 4, 1).AddDays(i % 365),
                Description = $"HDFC TRANSACTION ROW #{i + 1}",
                Debit = debit > 0 ? debit : null,
                Credit = credit > 0 ? credit : null,
                Amount = debit > 0 ? debit : credit,
                Balance = balance,
                Reference = $"HDFC{i:D6}",
                Utr = $"HDFCR5{i:D10}",
                TransactionType = debit > 0 ? "Debit" : "Credit",
                BankName = "HDFC Bank",
                Account = "50200031189753",
                ValidationStatus = "VALID"
            });
        }

        var summary = _validationService.ComputeSummary(fileRecord.Id, 1, "HDFC Bank", "HDFC-v1", txs);

        var sw = Stopwatch.StartNew();
        var bytes = await _exportService.GenerateStatementWorkbookAsync(fileRecord, summary, txs);
        sw.Stop();

        _output.WriteLine($"[HDFC 772 Rows Benchmark] Generation Time: {sw.ElapsedMilliseconds} ms, File Size: {bytes.Length / 1024.0:F1} KB");

        Assert.NotNull(bytes);
        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);

        var ws = workbook.Worksheet("Transactions");
        Assert.Equal(773, ws.LastRowUsed()!.RowNumber()); // 1 header + 772 rows
        Assert.Equal("HDFC TRANSACTION ROW #1", ws.Cell(2, 4).GetString());
        Assert.Equal("HDFC TRANSACTION ROW #772", ws.Cell(773, 4).GetString());
    }

    [Fact]
    public async Task Test23_YesBankStatement_92Rows_ExportsSuccessfully_AndInspectsWorkbook()
    {
        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "YES_BANK_Iris.pdf",
            UploadedAt = DateTime.UtcNow
        };

        var txs = new List<TransactionReviewDto>(92);
        decimal balance = 250000m;
        for (int i = 0; i < 92; i++)
        {
            decimal amt = 100m + i;
            balance += amt;
            txs.Add(new TransactionReviewDto
            {
                Id = Guid.NewGuid(),
                TransactionDate = new DateTime(2024, 4, 1).AddDays(i),
                Description = $"YES BANK INWARD UPI #{i + 1}",
                Credit = amt,
                Amount = amt,
                Balance = balance,
                TransactionType = "Credit",
                BankName = "YES BANK",
                Account = "0014901000214",
                ValidationStatus = "VALID"
            });
        }

        var summary = _validationService.ComputeSummary(fileRecord.Id, 2, "YES BANK", "YES-v1", txs);

        var sw = Stopwatch.StartNew();
        var bytes = await _exportService.GenerateStatementWorkbookAsync(fileRecord, summary, txs);
        sw.Stop();

        _output.WriteLine($"[YES BANK 92 Rows Benchmark] Generation Time: {sw.ElapsedMilliseconds} ms");

        Assert.NotNull(bytes);
        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);

        var ws = workbook.Worksheet("Transactions");
        Assert.Equal(93, ws.LastRowUsed()!.RowNumber());
        Assert.Equal("YES BANK INWARD UPI #1", ws.Cell(2, 4).GetString());
        Assert.Equal("YES BANK INWARD UPI #92", ws.Cell(93, 4).GetString());
    }

    [Fact]
    public async Task Test24_AxisBankStatement_615Rows_ExportsSuccessfully_AndInspectsWorkbook()
    {
        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "AXIS_APRIL_TO_MARCH.pdf",
            UploadedAt = DateTime.UtcNow
        };

        var txs = new List<TransactionReviewDto>(615);
        decimal balance = 121547.69m;
        for (int i = 0; i < 615; i++)
        {
            decimal amt = 50m;
            balance -= amt;
            txs.Add(new TransactionReviewDto
            {
                Id = Guid.NewGuid(),
                TransactionDate = new DateTime(2024, 4, 1).AddDays(i % 365),
                Description = $"AXIS STATEMENT ROW #{i + 1}",
                Debit = amt,
                Amount = amt,
                Balance = balance,
                TransactionType = "Debit",
                BankName = "Axis Bank",
                Account = "918020014892014",
                ValidationStatus = "VALID"
            });
        }

        var summary = _validationService.ComputeSummary(fileRecord.Id, 3, "Axis Bank", "AXIS-v1", txs);

        var sw = Stopwatch.StartNew();
        var bytes = await _exportService.GenerateStatementWorkbookAsync(fileRecord, summary, txs);
        sw.Stop();

        _output.WriteLine($"[Axis Bank 615 Rows Benchmark] Generation Time: {sw.ElapsedMilliseconds} ms, Size: {bytes.Length / 1024.0:F1} KB");

        Assert.NotNull(bytes);
        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);

        var ws = workbook.Worksheet("Transactions");
        Assert.Equal(616, ws.LastRowUsed()!.RowNumber()); // 1 header + 615 rows
        Assert.Equal("AXIS STATEMENT ROW #1", ws.Cell(2, 4).GetString());
        Assert.Equal("AXIS STATEMENT ROW #615", ws.Cell(616, 4).GetString());

        var sumWs = workbook.Worksheet("Summary");
        Assert.Equal("BALANCED", sumWs.Cell(8, 2).GetString());
    }

    [Fact]
    public async Task Test25_CentralBankStatement_RealPdf_UploadParseAndExportExcel()
    {
        var (client, auth) = await RegisterUserAsync("CentralBankExport");

        var fixturePath = @"E:\Bank Statements\CENTRAL BANK OF INDIA-6804.pdf";
        Assert.True(File.Exists(fixturePath), $"Fixture must exist: {fixturePath}");
        var pdfBytes = await File.ReadAllBytesAsync(fixturePath);

        var uploadContent = CreateMultipartPdf("CENTRAL BANK OF INDIA-6804.pdf", pdfBytes);
        var uploadResp = await client.PostAsync("/api/statements/upload", uploadContent);
        Assert.Equal(HttpStatusCode.OK, uploadResp.StatusCode);

        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>(JsonOptions);
        Assert.NotNull(uploadResult);
        var fileId = uploadResult.FileId;

        // Parse
        var parseResp = await client.PostAsync($"/api/statements/{fileId}/parse", null);
        Assert.Equal(HttpStatusCode.OK, parseResp.StatusCode);

        // Fetch review / preview transactions
        var txResp = await client.GetAsync($"/api/statements/{fileId}/transactions");
        Assert.Equal(HttpStatusCode.OK, txResp.StatusCode);
        var txList = await txResp.Content.ReadFromJsonAsync<StatementTransactionsResponse>(JsonOptions);
        Assert.NotNull(txList);
        Assert.Equal(132, txList.TotalCount);

        // Confirm review rows exist (the zero-amount memo rows) and invalid count is 0
        var valSummaryResp = await client.GetAsync($"/api/statements/{fileId}/validation-summary");
        Assert.Equal(HttpStatusCode.OK, valSummaryResp.StatusCode);
        var valSummary = await valSummaryResp.Content.ReadFromJsonAsync<StatementValidationSummaryDto>(JsonOptions);
        Assert.NotNull(valSummary);
        Assert.Equal(0, valSummary.InvalidCount);
        Assert.True(valSummary.ReviewCount > 0, "Central Bank statement has memo rows flagged for review");

        // Export to Excel
        var exportResp = await client.GetAsync($"/api/statements/{fileId}/export/excel");
        Assert.Equal(HttpStatusCode.OK, exportResp.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", exportResp.Content.Headers.ContentType?.MediaType);

        var fileBytes = await exportResp.Content.ReadAsByteArrayAsync();
        Assert.NotNull(fileBytes);
        Assert.True(fileBytes.Length > 2000, "Exported file should be non-empty .xlsx");

        // Inspect workbook using ClosedXML
        using var stream = new MemoryStream(fileBytes);
        using var workbook = new XLWorkbook(stream);

        // Verify sheets
        Assert.NotNull(workbook.Worksheet("Transactions"));
        Assert.NotNull(workbook.Worksheet("Statement Info"));
        Assert.NotNull(workbook.Worksheet("Summary"));

        var txWs = workbook.Worksheet("Transactions");
        Assert.Equal(133, txWs.LastRowUsed()!.RowNumber()); // 1 header + 132 data rows
        Assert.Equal("Central Bank of India", txWs.Cell(2, 12).GetString());

        var infoWs = workbook.Worksheet("Statement Info");
        Assert.Equal("Central Bank of India", infoWs.Cell(2, 2).GetString());
        Assert.Equal("CENTRAL-v1", infoWs.Cell(3, 2).GetString());
        Assert.Equal("3552886804", infoWs.Cell(4, 2).GetString());

        var sumWs = workbook.Worksheet("Summary");
        Assert.Equal(2675592.11m, (decimal)sumWs.Cell(4, 2).GetDouble()); // Credits
        Assert.Equal(2734771.01m, (decimal)sumWs.Cell(5, 2).GetDouble()); // Debits
    }

    [Fact]
    public async Task Test26_HdfcStatement_RealPdf_UploadParseAndExportExcel()
    {
        var (client, auth) = await RegisterUserAsync("HdfcRealExport");

        var fixturePath = @"E:\Bank Statements\HDFC Pass-134173633_unlocked.pdf";
        Assert.True(File.Exists(fixturePath), $"Fixture must exist: {fixturePath}");
        var pdfBytes = await File.ReadAllBytesAsync(fixturePath);

        var uploadContent = CreateMultipartPdf("HDFC Pass-134173633_unlocked.pdf", pdfBytes);
        var uploadResp = await client.PostAsync("/api/statements/upload", uploadContent);
        Assert.Equal(HttpStatusCode.OK, uploadResp.StatusCode);

        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>(JsonOptions);
        var fileId = uploadResult!.FileId;

        // Parse
        var parseResp = await client.PostAsync($"/api/statements/{fileId}/parse", null);
        Assert.Equal(HttpStatusCode.OK, parseResp.StatusCode);

        // Fetch review / preview transactions
        var txResp = await client.GetAsync($"/api/statements/{fileId}/transactions");
        Assert.Equal(HttpStatusCode.OK, txResp.StatusCode);
        var txList = await txResp.Content.ReadFromJsonAsync<StatementTransactionsResponse>(JsonOptions);
        Assert.NotNull(txList);
        Assert.Equal(757, txList.TotalCount);

        // Export to Excel
        var exportResp = await client.GetAsync($"/api/statements/{fileId}/export/excel");
        Assert.Equal(HttpStatusCode.OK, exportResp.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", exportResp.Content.Headers.ContentType?.MediaType);

        var fileBytes = await exportResp.Content.ReadAsByteArrayAsync();
        Assert.NotNull(fileBytes);
        Assert.True(fileBytes.Length > 2000);

        // Inspect workbook using ClosedXML
        using var stream = new MemoryStream(fileBytes);
        using var workbook = new XLWorkbook(stream);

        Assert.NotNull(workbook.Worksheet("Transactions"));
        Assert.NotNull(workbook.Worksheet("Statement Info"));
        Assert.NotNull(workbook.Worksheet("Summary"));

        var txWs = workbook.Worksheet("Transactions");
        Assert.Equal(758, txWs.LastRowUsed()!.RowNumber()); // 1 header + 757 rows
        Assert.Equal("HDFC Bank", txWs.Cell(2, 12).GetString());

        var infoWs = workbook.Worksheet("Statement Info");
        Assert.Equal("HDFC Bank", infoWs.Cell(2, 2).GetString());
        Assert.Equal("HDFC-v1", infoWs.Cell(3, 2).GetString());

        var sumWs = workbook.Worksheet("Summary");
        Assert.Equal("BALANCED", sumWs.Cell(8, 2).GetString());
    }

    [Fact]
    public async Task Test27_YesBankStatement_RealPdf_UploadParseAndExportExcel()
    {
        var (client, auth) = await RegisterUserAsync("YesRealExport");

        var fixturePath = @"E:\Bank Statements\Iris-YesBank-09 Sat,2025 14-21-46 pm.pdf";
        Assert.True(File.Exists(fixturePath), $"Fixture must exist: {fixturePath}");
        var pdfBytes = await File.ReadAllBytesAsync(fixturePath);

        var uploadContent = CreateMultipartPdf("Iris-YesBank.pdf", pdfBytes);
        var uploadResp = await client.PostAsync("/api/statements/upload", uploadContent);
        Assert.Equal(HttpStatusCode.OK, uploadResp.StatusCode);

        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>(JsonOptions);
        var fileId = uploadResult!.FileId;

        // Parse
        var parseResp = await client.PostAsync($"/api/statements/{fileId}/parse", null);
        Assert.Equal(HttpStatusCode.OK, parseResp.StatusCode);

        // Fetch review / preview transactions
        var txResp = await client.GetAsync($"/api/statements/{fileId}/transactions");
        Assert.Equal(HttpStatusCode.OK, txResp.StatusCode);
        var txList = await txResp.Content.ReadFromJsonAsync<StatementTransactionsResponse>(JsonOptions);
        Assert.NotNull(txList);
        Assert.Equal(92, txList.TotalCount);

        // Export to Excel
        var exportResp = await client.GetAsync($"/api/statements/{fileId}/export/excel");
        Assert.Equal(HttpStatusCode.OK, exportResp.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", exportResp.Content.Headers.ContentType?.MediaType);

        var fileBytes = await exportResp.Content.ReadAsByteArrayAsync();
        Assert.NotNull(fileBytes);
        Assert.True(fileBytes.Length > 2000);

        // Inspect workbook using ClosedXML
        using var stream = new MemoryStream(fileBytes);
        using var workbook = new XLWorkbook(stream);

        Assert.NotNull(workbook.Worksheet("Transactions"));
        Assert.NotNull(workbook.Worksheet("Statement Info"));
        Assert.NotNull(workbook.Worksheet("Summary"));

        var txWs = workbook.Worksheet("Transactions");
        Assert.Equal(93, txWs.LastRowUsed()!.RowNumber()); // 1 header + 92 rows
        Assert.Equal("YES BANK", txWs.Cell(2, 12).GetString());

        var infoWs = workbook.Worksheet("Statement Info");
        Assert.Equal("YES BANK", infoWs.Cell(2, 2).GetString());
        Assert.Equal("YES-v1", infoWs.Cell(3, 2).GetString());
        Assert.Equal("113763300000920", infoWs.Cell(4, 2).GetString());

        var sumWs = workbook.Worksheet("Summary");
        Assert.Equal("MISMATCH DETECTED", sumWs.Cell(8, 2).GetString());
    }

    [Fact]
    public async Task Test28_AxisBankStatement_RealPdf_UploadParseAndExportExcel()
    {
        var (client, auth) = await RegisterUserAsync("AxisRealExport");

        var fixturePath = @"E:\Bank Statements\AXIS APRIL TO MARCH.PDF";
        Assert.True(File.Exists(fixturePath), $"Fixture must exist: {fixturePath}");
        var pdfBytes = await File.ReadAllBytesAsync(fixturePath);

        var uploadContent = CreateMultipartPdf("AXIS APRIL TO MARCH.PDF", pdfBytes);
        var uploadResp = await client.PostAsync("/api/statements/upload", uploadContent);
        Assert.Equal(HttpStatusCode.OK, uploadResp.StatusCode);

        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>(JsonOptions);
        var fileId = uploadResult!.FileId;

        // Parse
        var parseResp = await client.PostAsync($"/api/statements/{fileId}/parse", null);
        Assert.Equal(HttpStatusCode.OK, parseResp.StatusCode);

        // Fetch review / preview transactions
        var txResp = await client.GetAsync($"/api/statements/{fileId}/transactions");
        Assert.Equal(HttpStatusCode.OK, txResp.StatusCode);
        var txList = await txResp.Content.ReadFromJsonAsync<StatementTransactionsResponse>(JsonOptions);
        Assert.NotNull(txList);
        Assert.Equal(615, txList.TotalCount);

        // Export to Excel
        var exportResp = await client.GetAsync($"/api/statements/{fileId}/export/excel");
        Assert.Equal(HttpStatusCode.OK, exportResp.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", exportResp.Content.Headers.ContentType?.MediaType);

        var fileBytes = await exportResp.Content.ReadAsByteArrayAsync();
        Assert.NotNull(fileBytes);
        Assert.True(fileBytes.Length > 2000);

        // Inspect workbook using ClosedXML
        using var stream = new MemoryStream(fileBytes);
        using var workbook = new XLWorkbook(stream);

        Assert.NotNull(workbook.Worksheet("Transactions"));
        Assert.NotNull(workbook.Worksheet("Statement Info"));
        Assert.NotNull(workbook.Worksheet("Summary"));

        var txWs = workbook.Worksheet("Transactions");
        Assert.Equal(616, txWs.LastRowUsed()!.RowNumber()); // 1 header + 615 rows
        Assert.Equal("Axis Bank", txWs.Cell(2, 12).GetString());

        var infoWs = workbook.Worksheet("Statement Info");
        Assert.Equal("Axis Bank", infoWs.Cell(2, 2).GetString());
        Assert.Equal("AXIS-v1", infoWs.Cell(3, 2).GetString());
        Assert.Equal("924020025076740", infoWs.Cell(4, 2).GetString());

        var sumWs = workbook.Worksheet("Summary");
        Assert.Equal("BALANCED", sumWs.Cell(8, 2).GetString());
    }

    #endregion
}
