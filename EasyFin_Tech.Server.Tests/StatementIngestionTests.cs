using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EasyFin_Tech.Server.Data;
using EasyFin_Tech.Server.Models;
using EasyFin_Tech.Server.Options;
using EasyFin_Tech.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace EasyFin_Tech.Server.Tests;

public class StatementIngestionTests
{
    private readonly DbContextOptions<EasyFinDbContext> _dbOptions;
    private readonly Mock<IFileStorageService> _mockStorageService;
    private readonly Mock<ILogger<StatementService>> _mockLogger;
    private readonly IOptions<FileUploadOptions> _options;

    public StatementIngestionTests()
    {
        _dbOptions = new DbContextOptionsBuilder<EasyFinDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _mockStorageService = new Mock<IFileStorageService>();
        _mockLogger = new Mock<ILogger<StatementService>>();

        _options = Microsoft.Extensions.Options.Options.Create(new FileUploadOptions
        {
            MaxPdfSizeBytes = 10 * 1024 * 1024, // 10MB limit for test
            StorageRoot = "Storage/Statements",
            AllowedExtensions = [".pdf"],
            AllowedContentTypes = ["application/pdf"]
        });

        _mockStorageService
            .Setup(s => s.ComputeSha256HashAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");

        _mockStorageService
            .Setup(s => s.SaveFileAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/safe/path/test.pdf");
    }

    private static IFormFile CreateMockPdf(string fileName, byte[] content)
    {
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, content.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf"
        };
    }

    private static byte[] CreateValidPdfBytes()
    {
        // %PDF-1.4 header
        return Encoding.UTF8.GetBytes("%PDF-1.4\n%âãÏÓ\n1 0 obj<</Type/Catalog>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF");
    }

    private async Task<(Guid UserId, Guid ClientId, Guid YearId)> SeedClientAndYearAsync(EasyFinDbContext context)
    {
        var userId = Guid.NewGuid();
        var client = new Client
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "Test Client",
            ContactPerson = "Contact",
            Email = "client@test.com",
            Phone = "1234567890",
            BusinessName = "Test Business",
            BusinessType = "Corporate",
            Address = "123 Test St",
            TaxId = "TAX123",
            CreatedAt = DateTime.UtcNow
        };

        var year = new FinancialYear
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            DisplayName = "2026-27",
            StartDate = new DateTime(2026, 4, 1),
            EndDate = new DateTime(2027, 3, 31),
            Status = 1,
            CreatedAt = DateTime.UtcNow
        };

        context.Clients.Add(client);
        context.FinancialYears.Add(year);
        await context.SaveChangesAsync();

        return (userId, client.Id, year.Id);
    }

    [Fact]
    public async Task UploadStatementAsync_ValidPdf_SucceedsAndCreatesFileRecord()
    {
        await using var context = new EasyFinDbContext(_dbOptions);
        var (userId, clientId, _) = await SeedClientAndYearAsync(context);

        var service = new StatementService(context, _mockStorageService.Object, _options, _mockLogger.Object);
        var file = CreateMockPdf("bank_statement.pdf", CreateValidPdfBytes());

        var (success, error, response) = await service.UploadStatementAsync(file, userId, clientId);

        Assert.True(success);
        Assert.Null(error);
        Assert.NotNull(response);
        Assert.Equal("bank_statement.pdf", response.OriginalFileName);
        Assert.Equal("ReadyForProcessing", response.Status);
        Assert.False(response.IsDuplicate);

        var recordInDb = await context.FileRecords.FindAsync(response.FileId);
        Assert.NotNull(recordInDb);
        Assert.Equal("bank_statement.pdf", recordInDb.OriginalFileName);
        Assert.Equal(0, recordInDb.ProcessingStatus); // 0 = Ready
    }

    [Fact]
    public async Task UploadStatementAsync_EmptyFile_ReturnsValidationError()
    {
        await using var context = new EasyFinDbContext(_dbOptions);
        var (userId, _, _) = await SeedClientAndYearAsync(context);
        var service = new StatementService(context, _mockStorageService.Object, _options, _mockLogger.Object);
        var file = CreateMockPdf("empty.pdf", []);

        var (success, error, response) = await service.UploadStatementAsync(file, userId);

        Assert.False(success);
        Assert.Contains("non-empty", error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(response);
    }

    [Fact]
    public async Task UploadStatementAsync_WrongExtension_ReturnsValidationError()
    {
        await using var context = new EasyFinDbContext(_dbOptions);
        var (userId, _, _) = await SeedClientAndYearAsync(context);
        var service = new StatementService(context, _mockStorageService.Object, _options, _mockLogger.Object);
        var file = CreateMockPdf("statement.xlsx", CreateValidPdfBytes());

        var (success, error, response) = await service.UploadStatementAsync(file, userId);

        Assert.False(success);
        Assert.Contains("Only PDF files", error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(response);
    }

    [Fact]
    public async Task UploadStatementAsync_InvalidPdfSignature_ReturnsValidationError()
    {
        await using var context = new EasyFinDbContext(_dbOptions);
        var (userId, _, _) = await SeedClientAndYearAsync(context);
        var service = new StatementService(context, _mockStorageService.Object, _options, _mockLogger.Object);
        // File named .pdf but containing plain text instead of %PDF- signature
        var invalidBytes = Encoding.UTF8.GetBytes("This is not a real PDF file, just text.");
        var file = CreateMockPdf("fake.pdf", invalidBytes);

        var (success, error, response) = await service.UploadStatementAsync(file, userId);

        Assert.False(success);
        Assert.Contains("valid PDF", error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(response);
    }

    [Fact]
    public async Task UploadStatementAsync_OversizedFile_ReturnsValidationError()
    {
        await using var context = new EasyFinDbContext(_dbOptions);
        var (userId, _, _) = await SeedClientAndYearAsync(context);
        // Configure a small 100-byte max size limit
        var smallOptions = Microsoft.Extensions.Options.Options.Create(new FileUploadOptions
        {
            MaxPdfSizeBytes = 100
        });

        var service = new StatementService(context, _mockStorageService.Object, smallOptions, _mockLogger.Object);
        var largeContent = new byte[200];
        Array.Copy(CreateValidPdfBytes(), largeContent, 10);
        var file = CreateMockPdf("large.pdf", largeContent);

        var (success, error, response) = await service.UploadStatementAsync(file, userId);

        Assert.False(success);
        Assert.Contains("exceeds the maximum", error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(response);
    }

    [Fact]
    public async Task UploadStatementAsync_DuplicateFile_NotesDuplicateFlag()
    {
        await using var context = new EasyFinDbContext(_dbOptions);
        var (userId, clientId, _) = await SeedClientAndYearAsync(context);

        var service = new StatementService(context, _mockStorageService.Object, _options, _mockLogger.Object);
        var content = CreateValidPdfBytes();
        var file1 = CreateMockPdf("duplicate_test.pdf", content);
        var file2 = CreateMockPdf("duplicate_test.pdf", content);

        // Upload first time
        var (s1, e1, r1) = await service.UploadStatementAsync(file1, userId, clientId);
        Assert.True(s1);
        Assert.False(r1!.IsDuplicate);

        // Upload second time
        var (s2, e2, r2) = await service.UploadStatementAsync(file2, userId, clientId);
        Assert.True(s2);
        Assert.True(r2!.IsDuplicate);
        Assert.NotNull(r2.DuplicateNotice);
    }
}
