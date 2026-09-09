using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EasyFin_Tech.Server.Data;
using EasyFin_Tech.Server.DTOs;
using EasyFin_Tech.Server.Models;
using EasyFin_Tech.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace EasyFin_Tech.Server.Tests;

public class PdfExtractionTests
{
    private readonly DbContextOptions<EasyFinDbContext> _dbOptions;
    private readonly Mock<IFileStorageService> _mockStorageService;
    private readonly Mock<ILogger<PdfExtractionService>> _mockLogger;
    private readonly Dictionary<string, byte[]> _storageFiles = new();

    public PdfExtractionTests()
    {
        _dbOptions = new DbContextOptionsBuilder<EasyFinDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _mockStorageService = new Mock<IFileStorageService>();
        _mockLogger = new Mock<ILogger<PdfExtractionService>>();

        // Configure mock storage service to store and retrieve files in-memory
        _mockStorageService
            .Setup(s => s.SaveFileAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Stream, string, string, CancellationToken>((stream, subDir, fileName, _) =>
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var key = Path.Combine(subDir, fileName).Replace('\\', '/');
                _storageFiles[key] = ms.ToArray();
                return Task.FromResult(key);
            });

        _mockStorageService
            .Setup(s => s.GetFileStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, CancellationToken>((subDir, fileName, _) =>
            {
                var key = Path.Combine(subDir, fileName).Replace('\\', '/');
                if (_storageFiles.TryGetValue(key, out var bytes))
                {
                    Stream ms = new MemoryStream(bytes);
                    return Task.FromResult<Stream?>(ms);
                }
                return Task.FromResult<Stream?>(null);
            });
    }

    private static byte[] CreateDigitalPdfWithColumns()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        // Header / Bank Information
        page.AddText("ACME BANK STATEMENT", 14, new PdfPoint(50, 780), font);
        page.AddText("Account: 1234567890", 10, new PdfPoint(50, 760), font);

        // Column Titles
        page.AddText("Date", 10, new PdfPoint(50, 720), font);
        page.AddText("Description", 10, new PdfPoint(150, 720), font);
        page.AddText("Debit", 10, new PdfPoint(350, 720), font);
        page.AddText("Credit", 10, new PdfPoint(420, 720), font);
        page.AddText("Balance", 10, new PdfPoint(490, 720), font);

        // Rows
        page.AddText("01/08/2026", 10, new PdfPoint(50, 690), font);
        page.AddText("SALARY CREDIT", 10, new PdfPoint(150, 690), font);
        page.AddText("150000.00", 10, new PdfPoint(420, 690), font);
        page.AddText("342850.00", 10, new PdfPoint(490, 690), font);

        page.AddText("05/08/2026", 10, new PdfPoint(50, 660), font);
        page.AddText("OFFICE LEASE ACH", 10, new PdfPoint(150, 660), font);
        page.AddText("45000.00", 10, new PdfPoint(350, 660), font);
        page.AddText("297850.00", 10, new PdfPoint(490, 660), font);

        page.AddText("10/08/2026", 10, new PdfPoint(50, 630), font);
        page.AddText("CLOUD HOSTING BILL", 10, new PdfPoint(150, 630), font);
        page.AddText("14890.00", 10, new PdfPoint(350, 630), font);
        page.AddText("282960.00", 10, new PdfPoint(490, 630), font);

        return builder.Build();
    }

    private static byte[] CreateMultiPageDigitalPdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        // Page 1
        var page1 = builder.AddPage(PageSize.A4);
        page1.AddText("CONFIDENTIAL FINANCIAL STATEMENT", 12, new PdfPoint(150, 800), font);
        page1.AddText("First Page Transaction Row 1", 10, new PdfPoint(50, 700), font);
        page1.AddText("Page 1 of 2", 10, new PdfPoint(250, 50), font);

        // Page 2
        var page2 = builder.AddPage(PageSize.A4);
        page2.AddText("CONFIDENTIAL FINANCIAL STATEMENT", 12, new PdfPoint(150, 800), font);
        page2.AddText("Second Page Transaction Row 2", 10, new PdfPoint(50, 700), font);
        page2.AddText("Page 2 of 2", 10, new PdfPoint(250, 50), font);

        return builder.Build();
    }

    private static byte[] CreateEmptyRasterizedPdf()
    {
        // PDF with an empty page (no text tokens)
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4);
        return builder.Build();
    }

    private async Task<(FileRecord Record, Guid UserId)> SeedDatabaseFileRecordAsync(EasyFinDbContext context, byte[] pdfBytes)
    {
        var clientId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var client = new Client
        {
            Id = clientId,
            UserId = userId,
            Name = "Extraction Test Client",
            ContactPerson = "Auditor",
            Email = "audit@test.com",
            Phone = "1112223333",
            BusinessName = "Test Org",
            BusinessType = "Corporate",
            Address = "Finance Street",
            TaxId = "TAX999",
            CreatedAt = DateTime.UtcNow
        };

        var yearId = Guid.NewGuid();
        var year = new FinancialYear
        {
            Id = yearId,
            ClientId = clientId,
            DisplayName = "2026-27",
            StartDate = new DateTime(2026, 4, 1),
            EndDate = new DateTime(2027, 3, 31),
            Status = 1,
            CreatedAt = DateTime.UtcNow
        };

        var fileRecordId = Guid.NewGuid();
        var storedFileName = $"{fileRecordId}.pdf";
        var storageSubDir = Path.Combine(clientId.ToString("N"), "2026");

        var fileRecord = new FileRecord
        {
            Id = fileRecordId,
            ClientId = clientId,
            FinancialYearId = yearId,
            OriginalFileName = "statement_test.pdf",
            StoredFileName = storedFileName,
            Extension = ".pdf",
            ContentType = "application/pdf",
            SizeBytes = pdfBytes.Length,
            UploadedAt = DateTime.UtcNow,
            ProcessingStatus = 0
        };

        context.Clients.Add(client);
        context.FinancialYears.Add(year);
        context.FileRecords.Add(fileRecord);
        await context.SaveChangesAsync();

        // Seed file in mock storage
        var key = Path.Combine(storageSubDir, storedFileName).Replace('\\', '/');
        _storageFiles[key] = pdfBytes;

        return (fileRecord, userId);
    }

    [Fact]
    public async Task ExtractDocumentAsync_WithValidDigitalPdf_ExtractsPagesWordsAndStructure()
    {
        using var context = new EasyFinDbContext(_dbOptions);
        var pdfBytes = CreateDigitalPdfWithColumns();
        var (fileRecord, userId) = await SeedDatabaseFileRecordAsync(context, pdfBytes);

        var service = new PdfExtractionService(context, _mockStorageService.Object, _mockLogger.Object);

        var (success, errorMessage, result) = await service.ExtractDocumentAsync(fileRecord.Id, userId);

        Assert.True(success);
        Assert.Null(errorMessage);
        Assert.NotNull(result);

        Assert.Equal(fileRecord.Id, result.FileId);
        Assert.Equal(1, result.PageCount);
        Assert.Equal("DigitalTextExtracted", result.ExtractionStatus);
        Assert.Equal("DigitalWithText", result.PdfType);
        Assert.True(result.HasUsableText);
        Assert.True(result.CharacterCount > 50);
        Assert.True(result.WordCount > 10);
        Assert.True(result.TextBlockCount > 10);
        Assert.True(result.CandidateRowCount >= 4);

        // Inspect Page 1
        var page1 = result.Pages.First();
        Assert.Equal(1, page1.PageNumber);
        Assert.Contains("ACME BANK STATEMENT", page1.RawText);
        Assert.Contains("SALARY CREDIT", page1.RawText);
        Assert.True(page1.Width > 0);
        Assert.True(page1.Height > 0);
    }

    [Fact]
    public async Task ExtractDocumentAsync_PreservesPageBoundariesAndGeometry()
    {
        using var context = new EasyFinDbContext(_dbOptions);
        var pdfBytes = CreateMultiPageDigitalPdf();
        var (fileRecord, userId) = await SeedDatabaseFileRecordAsync(context, pdfBytes);

        var service = new PdfExtractionService(context, _mockStorageService.Object, _mockLogger.Object);

        var (success, errorMessage, result) = await service.ExtractDocumentAsync(fileRecord.Id, userId);

        Assert.True(success);
        Assert.NotNull(result);
        Assert.Equal(2, result.PageCount);
        Assert.Equal(2, result.Pages.Count);

        var p1 = result.Pages[0];
        var p2 = result.Pages[1];

        Assert.Equal(1, p1.PageNumber);
        Assert.Equal(2, p2.PageNumber);

        Assert.Contains("First Page Transaction Row 1", p1.RawText);
        Assert.DoesNotContain("Second Page Transaction Row 2", p1.RawText);

        Assert.Contains("Second Page Transaction Row 2", p2.RawText);
        Assert.DoesNotContain("First Page Transaction Row 1", p2.RawText);

        // Coordinates check: top-left origin convention
        var firstBlock = p1.TextBlocks.First();
        Assert.True(firstBlock.X >= 0);
        Assert.True(firstBlock.Y >= 0);
        Assert.True(firstBlock.Width > 0);
        Assert.True(firstBlock.Height > 0);
    }

    [Fact]
    public async Task ExtractDocumentAsync_PreservesDeterministicReadingOrder()
    {
        using var context = new EasyFinDbContext(_dbOptions);
        var pdfBytes = CreateDigitalPdfWithColumns();
        var (fileRecord, userId) = await SeedDatabaseFileRecordAsync(context, pdfBytes);

        var service = new PdfExtractionService(context, _mockStorageService.Object, _mockLogger.Object);

        var (_, _, result) = await service.ExtractDocumentAsync(fileRecord.Id, userId);
        Assert.NotNull(result);

        var page = result.Pages.First();
        // Reading order indices must be strictly sequential (1, 2, 3...)
        for (int i = 0; i < page.TextBlocks.Count; i++)
        {
            Assert.Equal(i + 1, page.TextBlocks[i].ReadingOrderIndex);
        }

        // Within candidate rows, Y positions must increase monotonically (top to bottom)
        for (int i = 0; i < page.CandidateRows.Count - 1; i++)
        {
            Assert.True(page.CandidateRows[i].Y <= page.CandidateRows[i + 1].Y,
                $"Row {i} Y ({page.CandidateRows[i].Y}) should be <= Row {i + 1} Y ({page.CandidateRows[i + 1].Y})");
        }
    }

    [Fact]
    public async Task ExtractDocumentAsync_WithTabularData_DetectsCandidateTableAndColumns()
    {
        using var context = new EasyFinDbContext(_dbOptions);
        var pdfBytes = CreateDigitalPdfWithColumns();
        var (fileRecord, userId) = await SeedDatabaseFileRecordAsync(context, pdfBytes);

        var service = new PdfExtractionService(context, _mockStorageService.Object, _mockLogger.Object);

        var (success, _, result) = await service.ExtractDocumentAsync(fileRecord.Id, userId);

        Assert.True(success);
        Assert.NotNull(result);

        var page = result.Pages.First();
        Assert.NotEmpty(page.CandidateTables);

        var table = page.CandidateTables.First();
        Assert.True(table.ColumnCount >= 3);
        Assert.True(table.RowCount >= 3);
        Assert.True(table.Confidence > 0);
        Assert.NotNull(table.BoundingBox);
    }

    [Fact]
    public async Task ExtractDocumentAsync_WhenNoTablePresent_StillExtractsSuccessfully()
    {
        // Document with simple scattered sentences, no tabular alignment
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText("This is just a simple paragraph of terms and conditions.", 11, new PdfPoint(50, 700), font);
        page.AddText("It does not contain any tabular bank transaction tables.", 11, new PdfPoint(50, 670), font);
        byte[] pdfBytes = builder.Build();

        using var context = new EasyFinDbContext(_dbOptions);
        var (fileRecord, userId) = await SeedDatabaseFileRecordAsync(context, pdfBytes);

        var service = new PdfExtractionService(context, _mockStorageService.Object, _mockLogger.Object);

        var (success, _, result) = await service.ExtractDocumentAsync(fileRecord.Id, userId);

        Assert.True(success);
        Assert.NotNull(result);
        Assert.Equal("DigitalTextExtracted", result.ExtractionStatus);
        Assert.True(result.HasUsableText);
        // Candidate table count can legitimately be 0, and extraction is still 100% successful!
        Assert.Equal(0, result.CandidateTableCount);
    }

    [Fact]
    public async Task ExtractDocumentAsync_WithRepeatedHeadersAndFooters_ClassifiesConservativelyWithoutDeletingRawText()
    {
        using var context = new EasyFinDbContext(_dbOptions);
        var pdfBytes = CreateMultiPageDigitalPdf();
        var (fileRecord, userId) = await SeedDatabaseFileRecordAsync(context, pdfBytes);

        var service = new PdfExtractionService(context, _mockStorageService.Object, _mockLogger.Object);

        var (success, _, result) = await service.ExtractDocumentAsync(fileRecord.Id, userId);
        Assert.True(success);
        Assert.NotNull(result);

        // Header pattern repeated on both pages
        Assert.Contains(result.Pages[0].RepeatedHeaders, h => h.Contains("CONFIDENTIAL FINANCIAL STATEMENT"));
        Assert.Contains(result.Pages[1].RepeatedHeaders, h => h.Contains("CONFIDENTIAL FINANCIAL STATEMENT"));

        // Raw text MUST still retain the header and footer
        Assert.Contains("CONFIDENTIAL FINANCIAL STATEMENT", result.Pages[0].RawText);
        Assert.Contains("CONFIDENTIAL FINANCIAL STATEMENT", result.Pages[1].RawText);
        Assert.Contains("Page 1 of 2", result.Pages[0].RawText);
        Assert.Contains("Page 2 of 2", result.Pages[1].RawText);
    }

    [Fact]
    public async Task ExtractDocumentAsync_WithNoTextPdf_ReturnsNoDigitalTextDetected()
    {
        using var context = new EasyFinDbContext(_dbOptions);
        var pdfBytes = CreateEmptyRasterizedPdf();
        var (fileRecord, userId) = await SeedDatabaseFileRecordAsync(context, pdfBytes);

        var service = new PdfExtractionService(context, _mockStorageService.Object, _mockLogger.Object);

        var (success, errorMessage, result) = await service.ExtractDocumentAsync(fileRecord.Id, userId);

        Assert.True(success);
        Assert.Null(errorMessage);
        Assert.NotNull(result);

        Assert.Equal("NoDigitalTextDetected", result.ExtractionStatus);
        Assert.Equal("ScannedOrRasterizedNoText", result.PdfType);
        Assert.False(result.HasUsableText);
        Assert.Equal(0, result.WordCount);
        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("OCR"));
    }

    [Fact]
    public async Task ExtractDocumentAsync_WithMalformedPdf_ReturnsSafeError()
    {
        using var context = new EasyFinDbContext(_dbOptions);
        var malformedBytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x99, 0x88, 0x77, 0x00, 0xFF };
        var (fileRecord, userId) = await SeedDatabaseFileRecordAsync(context, malformedBytes);

        var service = new PdfExtractionService(context, _mockStorageService.Object, _mockLogger.Object);

        var (success, errorMessage, result) = await service.ExtractDocumentAsync(fileRecord.Id, userId);

        Assert.False(success);
        Assert.NotNull(errorMessage);
        Assert.NotNull(result);
        Assert.Equal("Failed", result.ExtractionStatus);
        Assert.False(result.HasUsableText);
    }

    [Fact]
    public async Task ExtractDocumentAsync_PersistsExtractionArtifactAndUpdatesDatabase()
    {
        using var context = new EasyFinDbContext(_dbOptions);
        var pdfBytes = CreateDigitalPdfWithColumns();
        var (fileRecord, userId) = await SeedDatabaseFileRecordAsync(context, pdfBytes);

        var service = new PdfExtractionService(context, _mockStorageService.Object, _mockLogger.Object);

        var (success, _, result) = await service.ExtractDocumentAsync(fileRecord.Id, userId);
        Assert.True(success);

        // Verify physical JSON artifact stored
        var expectedKey = Path.Combine("Extractions", fileRecord.ClientId.ToString("N"), $"{fileRecord.Id}_extraction.json").Replace('\\', '/');
        Assert.True(_storageFiles.ContainsKey(expectedKey));

        // Verify relational metadata in SQL Server InMemory DbContext
        var pdfProcessingResult = await context.PdfProcessingResults
            .FirstOrDefaultAsync(p => p.FileRecordId == fileRecord.Id);

        Assert.NotNull(pdfProcessingResult);
        Assert.Equal(result!.PageCount, pdfProcessingResult.PageCount);
        Assert.True(pdfProcessingResult.HasUsableText);
        Assert.Equal(1, pdfProcessingResult.PdfType); // 1 = DigitalWithText
        Assert.Equal(1, pdfProcessingResult.ExtractionMethod); // 1 = NativePdfPig

        // Verify FileRecord ProcessingStatus updated to 2 (Completed / Extracted)
        var updatedRecord = await context.FileRecords.FindAsync(fileRecord.Id);
        Assert.NotNull(updatedRecord);
        Assert.Equal(2, updatedRecord.ProcessingStatus);
    }

    [Fact]
    public async Task GetExtractionResultAsync_ReturnsSavedArtifact()
    {
        using var context = new EasyFinDbContext(_dbOptions);
        var pdfBytes = CreateDigitalPdfWithColumns();
        var (fileRecord, userId) = await SeedDatabaseFileRecordAsync(context, pdfBytes);

        var service = new PdfExtractionService(context, _mockStorageService.Object, _mockLogger.Object);

        await service.ExtractDocumentAsync(fileRecord.Id, userId);

        var fetchedResult = await service.GetExtractionResultAsync(fileRecord.Id, userId);

        Assert.NotNull(fetchedResult);
        Assert.Equal(fileRecord.Id, fetchedResult.FileId);
        Assert.Equal("DigitalTextExtracted", fetchedResult.ExtractionStatus);
        Assert.True(fetchedResult.Pages.Count > 0);
    }

    [Fact]
    public async Task ExtractDocumentAsync_RepeatedExtraction_ProducesDeterministicStructure()
    {
        using var context = new EasyFinDbContext(_dbOptions);
        var pdfBytes = CreateDigitalPdfWithColumns();
        var (fileRecord, userId) = await SeedDatabaseFileRecordAsync(context, pdfBytes);

        var service = new PdfExtractionService(context, _mockStorageService.Object, _mockLogger.Object);

        var (_, _, run1) = await service.ExtractDocumentAsync(fileRecord.Id, userId);
        var (_, _, run2) = await service.ExtractDocumentAsync(fileRecord.Id, userId);

        Assert.NotNull(run1);
        Assert.NotNull(run2);

        // Compare structural outputs (excluding runtime timestamps and durations)
        Assert.Equal(run1.PageCount, run2.PageCount);
        Assert.Equal(run1.WordCount, run2.WordCount);
        Assert.Equal(run1.CharacterCount, run2.CharacterCount);
        Assert.Equal(run1.CandidateRowCount, run2.CandidateRowCount);
        Assert.Equal(run1.CandidateTableCount, run2.CandidateTableCount);

        var p1 = run1.Pages.First();
        var p2 = run2.Pages.First();

        Assert.Equal(p1.RawText, p2.RawText);
        Assert.Equal(p1.TextBlocks.Count, p2.TextBlocks.Count);

        for (int i = 0; i < p1.TextBlocks.Count; i++)
        {
            var b1 = p1.TextBlocks[i];
            var b2 = p2.TextBlocks[i];
            Assert.Equal(b1.Text, b2.Text);
            Assert.Equal(b1.X, b2.X);
            Assert.Equal(b1.Y, b2.Y);
            Assert.Equal(b1.Width, b2.Width);
            Assert.Equal(b1.Height, b2.Height);
            Assert.Equal(b1.ReadingOrderIndex, b2.ReadingOrderIndex);
        }

        for (int i = 0; i < p1.CandidateRows.Count; i++)
        {
            Assert.Equal(p1.CandidateRows[i].RawLineText, p2.CandidateRows[i].RawLineText);
            Assert.Equal(p1.CandidateRows[i].Y, p2.CandidateRows[i].Y);
        }
    }

    [Fact]
    public async Task ExtractDocumentAsync_RawTextReconstruction_PreservesVisualLinesAndSpacing()
    {
        using var context = new EasyFinDbContext(_dbOptions);

        // Build a PDF with explicit multi-line wrapped narration and multiple columns
        var builder = new PdfDocumentBuilder();
        var page1 = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        // Visual Line 1: Header
        page1.AddText("Statement of Account", 12, new PdfPoint(50, 800), font);

        // Visual Line 2: Date interval with distinct horizontal gap
        page1.AddText("01-04-2025", 10, new PdfPoint(50, 770), font);
        page1.AddText("TO", 10, new PdfPoint(150, 770), font);
        page1.AddText("31-03-2026", 10, new PdfPoint(200, 770), font);

        // Visual Line 3: Column headers with column gaps
        page1.AddText("DATE", 10, new PdfPoint(50, 740), font);
        page1.AddText("DESCRIPTION", 10, new PdfPoint(150, 740), font);
        page1.AddText("DEBITS", 10, new PdfPoint(350, 740), font);
        page1.AddText("CREDITS", 10, new PdfPoint(430, 740), font);
        page1.AddText("BALANCE", 10, new PdfPoint(500, 740), font);

        // Visual Line 4: First line of transaction (wrapped narration line 1)
        page1.AddText("01-04-2025", 10, new PdfPoint(50, 710), font);
        page1.AddText("NEFT CR-HDFC0001-", 10, new PdfPoint(150, 710), font);
        page1.AddText("15000.00", 10, new PdfPoint(430, 710), font);
        page1.AddText("85000.00", 10, new PdfPoint(500, 710), font);

        // Visual Line 5: Wrapped narration line 2 (subsequent line)
        page1.AddText("ALPHA VENTURES PRIVATE LIMITED", 10, new PdfPoint(150, 695), font);

        // Visual Line 6: Another transaction
        page1.AddText("05-04-2025", 10, new PdfPoint(50, 670), font);
        page1.AddText("UPI-CLOUD-SERVICES", 10, new PdfPoint(150, 670), font);
        page1.AddText("2400.00", 10, new PdfPoint(350, 670), font);
        page1.AddText("82600.00", 10, new PdfPoint(500, 670), font);

        // Page 2
        var page2 = builder.AddPage(PageSize.A4);
        page2.AddText("PAGE TWO SUMMARY", 12, new PdfPoint(50, 800), font);
        page2.AddText("CLOSING BALANCE: 82600.00", 10, new PdfPoint(50, 760), font);

        var pdfBytes = builder.Build();
        var (fileRecord, userId) = await SeedDatabaseFileRecordAsync(context, pdfBytes);

        var service = new PdfExtractionService(context, _mockStorageService.Object, _mockLogger.Object);

        // Run 1
        var (success, _, result) = await service.ExtractDocumentAsync(fileRecord.Id, userId);

        Assert.True(success);
        Assert.NotNull(result);
        Assert.Equal(2, result.Pages.Count);

        var p1 = result.Pages[0];
        var p2 = result.Pages[1];

        // 1. A page with multiple visual lines produces multiple newline-separated RawText lines
        var p1Lines = p1.RawText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(p1Lines.Length >= 6, $"Expected at least 6 lines, but got {p1Lines.Length}");

        // 2. Lines are ordered top-to-bottom
        Assert.StartsWith("Statement of Account", p1Lines[0].Trim());
        Assert.Contains("01-04-2025", p1Lines[1]);
        Assert.Contains("31-03-2026", p1Lines[1]);
        Assert.Contains("DATE", p1Lines[2]);
        Assert.Contains("BALANCE", p1Lines[2]);

        // 3. Words on the same visual line are ordered left-to-right with sensible horizontal spacing
        var headerLine = p1Lines[2];
        var dateIdx = headerLine.IndexOf("DATE");
        var descIdx = headerLine.IndexOf("DESCRIPTION");
        var debIdx = headerLine.IndexOf("DEBITS");
        var credIdx = headerLine.IndexOf("CREDITS");
        var balIdx = headerLine.IndexOf("BALANCE");

        Assert.True(dateIdx < descIdx, "DATE should precede DESCRIPTION");
        Assert.True(descIdx < debIdx, "DESCRIPTION should precede DEBITS");
        Assert.True(debIdx < credIdx, "DEBITS should precede CREDITS");
        Assert.True(credIdx < balIdx, "CREDITS should precede BALANCE");

        // Verify that meaningful gaps contain more than a single space (column separation preserved)
        Assert.True(descIdx - dateIdx > 5, "Column gap between DATE and DESCRIPTION should be preserved");

        // 4. Multi-line narration remains on separate lines
        Assert.Contains("NEFT CR-HDFC0001-", p1Lines[3]);
        Assert.Contains("ALPHA VENTURES PRIVATE LIMITED", p1Lines[4]);
        Assert.DoesNotContain("ALPHA VENTURES PRIVATE LIMITED", p1Lines[3]);

        // 5. Page 1 and Page 2 RawText remain strictly separate
        Assert.Contains("Statement of Account", p1.RawText);
        Assert.DoesNotContain("PAGE TWO SUMMARY", p1.RawText);
        Assert.Contains("PAGE TWO SUMMARY", p2.RawText);
        Assert.DoesNotContain("Statement of Account", p2.RawText);

        // 6. Text block coordinates and underlying tokens are unchanged and preserved
        Assert.True(p1.TextBlocks.Count >= 18);
        Assert.All(p1.TextBlocks, b =>
        {
            Assert.False(string.IsNullOrWhiteSpace(b.Text));
            Assert.True(b.X >= 0);
            Assert.True(b.Y >= 0);
            Assert.True(b.Width > 0);
            Assert.True(b.Height > 0);
        });

        // 7. Candidate row / table detection still functions properly
        Assert.True(p1.CandidateRows.Count >= 6);

        // 8. Repeated extraction produces identical deterministic RawText
        var (_, _, run2) = await service.ExtractDocumentAsync(fileRecord.Id, userId);
        Assert.NotNull(run2);
        Assert.Equal(p1.RawText, run2.Pages[0].RawText);
        Assert.Equal(p2.RawText, run2.Pages[1].RawText);
    }

    [Fact]
    public async Task ExtractDocumentAsync_RealHdfcStatement_RawTextIsReadableAndLineStructured()
    {
        var filePath = @"E:\BrandNew Day\EasyFin Tech\EasyFin_Tech\EasyFin_Tech.Server\Storage\Statements\9a7e688c841d4c649d0c8b988ccd63f8\2026\5b0ae3b9-faf4-4549-b447-9b583d09a410.pdf";
        if (!File.Exists(filePath)) return;

        using var context = new EasyFinDbContext(_dbOptions);
        var pdfBytes = await File.ReadAllBytesAsync(filePath);
        var (fileRecord, userId) = await SeedDatabaseFileRecordAsync(context, pdfBytes);

        var service = new PdfExtractionService(context, _mockStorageService.Object, _mockLogger.Object);

        var (success, _, result) = await service.ExtractDocumentAsync(fileRecord.Id, userId);

        Assert.True(success);
        Assert.NotNull(result);
        Assert.Equal(65, result.PageCount);

        // Check Page 1
        var page1 = result.Pages[0];
        Assert.True(page1.RawText.Contains('\n'), "Page 1 RawText must contain line breaks");
        var p1Lines = page1.RawText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(p1Lines.Length >= 20, $"Expected >= 20 lines on Page 1, but got {p1Lines.Length}");

        // Verify column headers exist as a readable line
        var colHeaderLine = p1Lines.FirstOrDefault(l => l.Contains("Date") && l.Contains("Narration"));
        Assert.NotNull(colHeaderLine);
        Assert.Contains("Date", colHeaderLine);
        Assert.Contains("Narration", colHeaderLine);

        // Check Page 5 (a transaction page with multi-line content)
        var page5 = result.Pages[4];
        Assert.True(page5.RawText.Contains('\n'));
        var p5Lines = page5.RawText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(p5Lines.Length >= 20);

        // Check Page 15 (later page with repeated header structure)
        var page15 = result.Pages[14];
        Assert.True(page15.RawText.Contains('\n'));
        var p15Lines = page15.RawText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(p15Lines.Length >= 20);
    }
}
