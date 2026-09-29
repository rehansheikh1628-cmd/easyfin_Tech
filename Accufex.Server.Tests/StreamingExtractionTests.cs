using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Accufex.Server.Data;
using Accufex.Server.DTOs;
using Accufex.Server.Models;
using Accufex.Server.Options;
using Accufex.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Accufex.Server.Tests;

public class StreamingExtractionTests : IDisposable
{
    private readonly DbContextOptions<AccufexDbContext> _dbOptions;
    private readonly Mock<IFileStorageService> _mockStorageService;
    private readonly Mock<ILogger<PdfExtractionService>> _mockExtractionLogger;
    private readonly Mock<ILogger<StatementService>> _mockStatementLogger;
    private readonly Mock<ILogger<LocalFileStorageService>> _mockStorageLogger;
    private readonly Mock<IHostEnvironment> _mockEnvironment;
    private readonly string _testTempDir;
    private readonly Dictionary<string, byte[]> _inMemoryStorage = new();

    public StreamingExtractionTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AccufexDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _mockStorageService = new Mock<IFileStorageService>();
        _mockExtractionLogger = new Mock<ILogger<PdfExtractionService>>();
        _mockStatementLogger = new Mock<ILogger<StatementService>>();
        _mockStorageLogger = new Mock<ILogger<LocalFileStorageService>>();
        _mockEnvironment = new Mock<IHostEnvironment>();

        _testTempDir = Path.Combine(Path.GetTempPath(), "Accufex_Phase3_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testTempDir);
        _mockEnvironment.Setup(e => e.ContentRootPath).Returns(_testTempDir);

        _mockStorageService
            .Setup(s => s.SaveFileAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Stream, string, string, CancellationToken>((stream, subDir, fileName, _) =>
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var key = Path.Combine(subDir, fileName).Replace('\\', '/');
                _inMemoryStorage[key] = ms.ToArray();
                return Task.FromResult(key);
            });

        _mockStorageService
            .Setup(s => s.GetFileStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, CancellationToken>((subDir, fileName, _) =>
            {
                var key = Path.Combine(subDir, fileName).Replace('\\', '/');
                if (_inMemoryStorage.TryGetValue(key, out var bytes))
                {
                    Stream ms = new MemoryStream(bytes);
                    return Task.FromResult<Stream?>(ms);
                }
                return Task.FromResult<Stream?>(null);
            });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testTempDir))
            {
                Directory.Delete(_testTempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore test cleanup exceptions
        }
    }

    [Fact]
    public async Task PdfExtractionService_StreamsDirectly_FromSeekableStream_WithoutMemoryStreamDuplication()
    {
        // Arrange: Generate valid digital PDF
        var pdfBytes = CreateMultiPageDigitalPdf(3);
        await using var context = new AccufexDbContext(_dbOptions);

        var userId = Guid.NewGuid();
        var client = new Client
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "Streaming Corp",
            ContactPerson = "Auditor",
            Email = "audit@streaming.corp",
            Phone = "1234567890",
            BusinessName = "Streaming Corp",
            BusinessType = "Corporate",
            Address = "Finance Ave",
            TaxId = "TAX999",
            CreatedAt = DateTime.UtcNow
        };
        context.Clients.Add(client);

        var yearId = Guid.NewGuid();
        var year = new FinancialYear
        {
            Id = yearId,
            ClientId = client.Id,
            DisplayName = "2026-27",
            StartDate = new DateTime(2026, 4, 1),
            EndDate = new DateTime(2027, 3, 31),
            Status = 1,
            CreatedAt = DateTime.UtcNow
        };
        context.FinancialYears.Add(year);

        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            FinancialYearId = yearId,
            OriginalFileName = "multi_page_statement.pdf",
            StoredFileName = "multi_page_statement.pdf",
            Extension = ".pdf",
            ContentType = "application/pdf",
            SizeBytes = pdfBytes.Length,
            UploadedAt = DateTime.UtcNow,
            ProcessingStatus = 0
        };
        context.FileRecords.Add(fileRecord);
        await context.SaveChangesAsync();

        var yearFolder = "2026";
        var key = Path.Combine(client.Id.ToString("N"), yearFolder, fileRecord.StoredFileName).Replace('\\', '/');
        _inMemoryStorage[key] = pdfBytes;

        var extractionService = new PdfExtractionService(context, _mockStorageService.Object, _mockExtractionLogger.Object);

        // Act: Execute extraction
        var (success, error, result) = await extractionService.ExtractDocumentAsync(fileRecord.Id, userId);

        // Assert
        Assert.True(success, error);
        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(3, result.PageCount);
        Assert.True(result.HasUsableText);
        Assert.Equal("DigitalTextExtracted", result.ExtractionStatus);
        Assert.Equal(3, result.Pages.Count);
        Assert.Contains("First Page Transaction", result.Pages[0].RawText);
        Assert.Contains("Page 2 Transaction", result.Pages[1].RawText);
        Assert.Contains("Page 3 Transaction", result.Pages[2].RawText);
    }

    [Fact]
    public async Task StatementService_AcceptsLargeUpload_UpTo250MbLimit()
    {
        // Arrange
        await using var context = new AccufexDbContext(_dbOptions);
        var options = Microsoft.Extensions.Options.Options.Create(new FileUploadOptions
        {
            MaxPdfSizeBytes = 262144000, // 250MB
            StorageRoot = "Storage/Statements",
            AllowedExtensions = [".pdf"],
            AllowedContentTypes = ["application/pdf"]
        });

        _mockStorageService
            .Setup(s => s.ComputeSha256HashAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

        var statementService = new StatementService(context, _mockStorageService.Object, options, _mockStatementLogger.Object);

        var userId = Guid.NewGuid();
        var client = new Client
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "Large File Workspace",
            ContactPerson = "Finance Lead",
            Email = "lead@large.corp",
            Phone = "1234567890",
            BusinessName = "Large Corp",
            BusinessType = "Corporate",
            Address = "Enterprise Way",
            TaxId = "TAX888",
            CreatedAt = DateTime.UtcNow
        };
        context.Clients.Add(client);
        await context.SaveChangesAsync();

        // 105MB synthetic PDF stream with valid PDF magic bytes header
        const long simulated105Mb = 105L * 1024 * 1024;
        var header = Encoding.ASCII.GetBytes("%PDF-1.7 large statement test fixture\n");
        using var largeStream = new VirtualLargePdfStream(header, simulated105Mb);

        var formFile = new FormFile(largeStream, 0, simulated105Mb, "file", "annual_statement_105mb.pdf")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf"
        };

        // Act
        var (success, errorMessage, response) = await statementService.UploadStatementAsync(
            formFile,
            userId,
            client.Id);

        // Assert
        Assert.True(success);
        Assert.Null(errorMessage);
        Assert.NotNull(response);
        Assert.Equal(simulated105Mb, response.FileSizeBytes);
        Assert.Equal("105.00 MB", response.FileSizeFormatted);
    }

    [Fact]
    public async Task StatementService_RejectsUpload_ExceedingConfiguredLimit()
    {
        // Arrange
        await using var context = new AccufexDbContext(_dbOptions);
        var options = Microsoft.Extensions.Options.Options.Create(new FileUploadOptions
        {
            MaxPdfSizeBytes = 262144000, // 250MB
            StorageRoot = "Storage/Statements",
            AllowedExtensions = [".pdf"],
            AllowedContentTypes = ["application/pdf"]
        });

        var statementService = new StatementService(context, _mockStorageService.Object, options, _mockStatementLogger.Object);

        // 260MB stream exceeding 250MB limit
        const long simulated260Mb = 260L * 1024 * 1024;
        var header = Encoding.ASCII.GetBytes("%PDF-1.7 oversize test\n");
        using var oversizeStream = new VirtualLargePdfStream(header, simulated260Mb);

        var formFile = new FormFile(oversizeStream, 0, simulated260Mb, "file", "oversize_statement_260mb.pdf")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf"
        };

        // Act
        var (success, errorMessage, response) = await statementService.UploadStatementAsync(
            formFile,
            Guid.NewGuid(),
            Guid.NewGuid());

        // Assert
        Assert.False(success);
        Assert.NotNull(errorMessage);
        Assert.Contains("maximum allowed upload size of 250 MB", errorMessage);
        Assert.Null(response);
    }

    [Fact]
    public async Task LocalFileStorageService_CleansUpPartialFile_WhenStreamFailsMidway()
    {
        // Arrange: Real LocalFileStorageService instance
        var options = Microsoft.Extensions.Options.Options.Create(new FileUploadOptions
        {
            MaxPdfSizeBytes = 262144000,
            StorageRoot = _testTempDir,
            AllowedExtensions = [".pdf"],
            AllowedContentTypes = ["application/pdf"]
        });

        var localStorage = new LocalFileStorageService(options, _mockEnvironment.Object, _mockStorageLogger.Object);

        var subDir = "Client_PartialTest/2026";
        var safeFileName = "interrupted_upload.pdf";

        // Faulty stream that throws midway
        using var faultyStream = new FaultyStream(failAfterBytes: 1024);

        // Act & Assert
        await Assert.ThrowsAsync<IOException>(() =>
            localStorage.SaveFileAsync(faultyStream, subDir, safeFileName));

        // Verify that the destination file was deleted and does not exist as an orphaned partial file
        var targetFile = Path.Combine(_testTempDir, subDir, safeFileName);
        Assert.False(File.Exists(targetFile), "Partial file must be cleaned up on failure.");
    }

    [Fact]
    public async Task LocalFileStorageService_SavesAndRetrieves_SeekableStreamDirectly_ForPdfPig()
    {
        // Arrange: Real LocalFileStorageService instance
        var options = Microsoft.Extensions.Options.Options.Create(new FileUploadOptions
        {
            MaxPdfSizeBytes = 262144000,
            StorageRoot = _testTempDir,
            AllowedExtensions = [".pdf"],
            AllowedContentTypes = ["application/pdf"]
        });

        var localStorage = new LocalFileStorageService(options, _mockEnvironment.Object, _mockStorageLogger.Object);

        var subDir = "Client_DirectStream/2026";
        var safeFileName = "valid_statement.pdf";
        var pdfBytes = CreateMultiPageDigitalPdf(2);

        using var uploadStream = new MemoryStream(pdfBytes);

        // Act: Save file to controlled storage
        var savedPath = await localStorage.SaveFileAsync(uploadStream, subDir, safeFileName);
        Assert.True(File.Exists(savedPath));

        // Act: Retrieve seekable stream
        await using var retrievedStream = await localStorage.GetFileStreamAsync(subDir, safeFileName);
        Assert.NotNull(retrievedStream);
        Assert.True(retrievedStream.CanSeek, "Retrieved stream from storage must be seekable for direct PdfPig consumption.");

        // Assert: PdfPig opens directly from seekable file stream
        using var doc = PdfDocument.Open(retrievedStream);
        Assert.Equal(2, doc.NumberOfPages);
        Assert.Contains("First Page Transaction", doc.GetPage(1).Text);
        Assert.Contains("Page 2 Transaction", doc.GetPage(2).Text);
    }

    [Fact]
    public async Task PdfExtractionService_EnforcesTenantIsolation_DuringStreamingExtraction()
    {
        // Arrange
        await using var context = new AccufexDbContext(_dbOptions);
        var tenantAUser = Guid.NewGuid();
        var tenantBUser = Guid.NewGuid();

        var clientA = new Client
        {
            Id = Guid.NewGuid(),
            UserId = tenantAUser,
            Name = "Tenant A Corp",
            ContactPerson = "Alice",
            Email = "alice@tenanta.com",
            Phone = "1111111111",
            BusinessName = "Tenant A Corp",
            BusinessType = "Corporate",
            Address = "Tenant A Street",
            TaxId = "TAX-A",
            CreatedAt = DateTime.UtcNow
        };
        context.Clients.Add(clientA);

        var fileRecordA = new FileRecord
        {
            Id = Guid.NewGuid(),
            ClientId = clientA.Id,
            OriginalFileName = "tenant_a_statement.pdf",
            StoredFileName = "tenant_a_statement.pdf",
            Extension = ".pdf",
            ContentType = "application/pdf",
            SizeBytes = 1024,
            UploadedAt = DateTime.UtcNow,
            ProcessingStatus = 0
        };
        context.FileRecords.Add(fileRecordA);
        await context.SaveChangesAsync();

        var extractionService = new PdfExtractionService(context, _mockStorageService.Object, _mockExtractionLogger.Object);

        // Act: Tenant B attempts to extract Tenant A's statement
        var (success, error, result) = await extractionService.ExtractDocumentAsync(fileRecordA.Id, tenantBUser);

        // Assert
        Assert.False(success);
        Assert.NotNull(error);
        Assert.Contains("was not found", error);
        Assert.Null(result);
    }

    private static byte[] CreateMultiPageDigitalPdf(int pageCount)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        for (var i = 1; i <= pageCount; i++)
        {
            var page = builder.AddPage(PageSize.A4);
            page.AddText($"CONFIDENTIAL STATEMENT - PAGE {i}", 12, new PdfPoint(100, 800), font);
            if (i == 1)
            {
                page.AddText("First Page Transaction Row: Salary Credit 50000.00", 10, new PdfPoint(50, 700), font);
            }
            else
            {
                page.AddText($"Page {i} Transaction Row: Vendor Payment 1250.00", 10, new PdfPoint(50, 700), font);
            }
            page.AddText($"Page {i} of {pageCount}", 10, new PdfPoint(250, 50), font);
        }

        return builder.Build();
    }

    /// <summary>
    /// Virtual seekable stream representing a large synthetic PDF without allocating 100+ MB of RAM.
    /// </summary>
    private sealed class VirtualLargePdfStream : Stream
    {
        private readonly byte[] _header;
        private readonly long _totalLength;
        private long _position;

        public VirtualLargePdfStream(byte[] header, long totalLength)
        {
            _header = header;
            _totalLength = totalLength;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _totalLength;
        public override long Position
        {
            get => _position;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _position = Math.Min(value, _totalLength);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _totalLength) return 0;

            var bytesAvailable = _totalLength - _position;
            var toRead = (int)Math.Min(count, bytesAvailable);

            for (var i = 0; i < toRead; i++)
            {
                var currentPos = _position + i;
                if (currentPos < _header.Length)
                {
                    buffer[offset + i] = _header[currentPos];
                }
                else
                {
                    buffer[offset + i] = 0x20; // fill with space
                }
            }

            _position += toRead;
            return toRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    Position = offset;
                    break;
                case SeekOrigin.Current:
                    Position += offset;
                    break;
                case SeekOrigin.End:
                    Position = _totalLength + offset;
                    break;
            }
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Stream that simulates mid-flight network/hardware failure.
    /// </summary>
    private sealed class FaultyStream : Stream
    {
        private readonly int _failAfterBytes;
        private int _bytesRead;

        public FaultyStream(int failAfterBytes)
        {
            _failAfterBytes = failAfterBytes;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => 100000;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_bytesRead >= _failAfterBytes)
            {
                throw new IOException("Simulated network stream termination during upload.");
            }

            var toRead = Math.Min(count, _failAfterBytes - _bytesRead);
            for (var i = 0; i < toRead; i++)
            {
                buffer[offset + i] = 0x25;
            }
            _bytesRead += toRead;
            return toRead;
        }

        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
