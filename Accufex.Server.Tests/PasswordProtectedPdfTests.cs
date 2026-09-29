using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Accufex.Server.Controllers;
using Accufex.Server.Data;
using Accufex.Server.DTOs;
using Accufex.Server.Models;
using Accufex.Server.Options;
using Accufex.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Accufex.Server.Tests;

public class PasswordProtectedPdfTests : IClassFixture<CustomWebApplicationFactory>, IDisposable
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly DbContextOptions<AccufexDbContext> _dbOptions;
    private readonly Mock<IFileStorageService> _mockStorageService;
    private readonly Mock<ILogger<PdfExtractionService>> _mockExtractionLogger;
    private readonly Dictionary<string, byte[]> _inMemoryStorage = new();
    private readonly string _testTempDir;

    private static readonly byte[] PdfPadding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41,
        0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80,
        0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A
    ];

    private static readonly byte[] DocIdBytes =
    [
        0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef,
        0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef
    ];

    public PasswordProtectedPdfTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _dbOptions = new DbContextOptionsBuilder<AccufexDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _mockStorageService = new Mock<IFileStorageService>();
        _mockExtractionLogger = new Mock<ILogger<PdfExtractionService>>();

        _testTempDir = Path.Combine(Path.GetTempPath(), "Accufex_PwdTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testTempDir);

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
                var fallbackMatch = _inMemoryStorage.FirstOrDefault(kvp => kvp.Key.EndsWith(fileName));
                if (fallbackMatch.Value != null)
                {
                    Stream ms = new MemoryStream(fallbackMatch.Value);
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
            // Ignore cleanup errors
        }
    }

    #region Helper Methods for Synthetic PDFs

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        byte[] s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;
        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 255;
            (s[i], s[j]) = (s[j], s[i]);
        }
        int x = 0, y = 0;
        byte[] result = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            x = (x + 1) & 255;
            y = (y + s[x]) & 255;
            (s[x], s[y]) = (s[y], s[x]);
            result[i] = (byte)(data[i] ^ s[(s[x] + s[y]) & 255]);
        }
        return result;
    }

    public static byte[] CreateEncryptedPdf(string password, string text = "Encrypted Statement Secret Text")
    {
        // 1. Prepare user password padding
        byte[] userPad = new byte[32];
        int uLen = Math.Min(password.Length, 32);
        Array.Copy(Encoding.ASCII.GetBytes(password), userPad, uLen);
        Array.Copy(PdfPadding, 0, userPad, uLen, 32 - uLen);

        // 2. Compute Owner value (using same password for owner)
        byte[] ownerHash = MD5.HashData(userPad);
        byte[] ownerKey = ownerHash.AsSpan(0, 5).ToArray();
        byte[] oValue = Rc4(ownerKey, userPad);

        // 3. Compute Encryption Key
        int p = -44;
        byte[] pBytes = BitConverter.GetBytes(p);

        List<byte> keyMaterial = new(84);
        keyMaterial.AddRange(userPad);
        keyMaterial.AddRange(oValue);
        keyMaterial.AddRange(pBytes);
        keyMaterial.AddRange(DocIdBytes);

        byte[] keyHash = MD5.HashData(keyMaterial.ToArray());
        byte[] encKey = keyHash.AsSpan(0, 5).ToArray();

        // 4. Compute User value
        byte[] uValue = Rc4(encKey, PdfPadding);

        // 5. Encrypt stream for Obj 4
        byte[] rawStream = Encoding.ASCII.GetBytes($"BT\n/F1 12 Tf\n50 700 Td\n({text}) Tj\nET\n");
        byte[] objKeyMaterial = [encKey[0], encKey[1], encKey[2], encKey[3], encKey[4], 4, 0, 0, 0, 0];
        byte[] objKeyHash = MD5.HashData(objKeyMaterial);
        byte[] objKey = objKeyHash.AsSpan(0, 10).ToArray();
        byte[] encryptedStream = Rc4(objKey, rawStream);

        // 6. Build PDF File
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true);

        List<long> offsets = [0];

        writer.Write("%PDF-1.4\n");
        writer.Flush();

        // Obj 1: Catalog
        offsets.Add(ms.Position);
        writer.Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        writer.Flush();

        // Obj 2: Pages
        offsets.Add(ms.Position);
        writer.Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        writer.Flush();

        // Obj 3: Page
        offsets.Add(ms.Position);
        writer.Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 6 0 R >> >> >>\nendobj\n");
        writer.Flush();

        // Obj 4: Encrypted Stream
        offsets.Add(ms.Position);
        writer.Write($"4 0 obj\n<< /Length {encryptedStream.Length} >>\nstream\n");
        writer.Flush();
        ms.Write(encryptedStream, 0, encryptedStream.Length);
        writer.Write("\nendstream\nendobj\n");
        writer.Flush();

        // Obj 5: Encrypt dictionary
        offsets.Add(ms.Position);
        string oHex = Convert.ToHexString(oValue);
        string uHex = Convert.ToHexString(uValue);
        writer.Write($"5 0 obj\n<< /Filter /Standard /V 1 /R 2 /O <{oHex}> /U <{uHex}> /P {p} >>\nendobj\n");
        writer.Flush();

        // Obj 6: Font
        offsets.Add(ms.Position);
        writer.Write("6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        writer.Flush();

        // Xref
        long xrefPos = ms.Position;
        writer.Write($"xref\n0 {offsets.Count}\n");
        writer.Write("0000000000 65535 f \n");
        for (int i = 1; i < offsets.Count; i++)
        {
            writer.Write($"{offsets[i]:D10} 00000 n \n");
        }

        string docIdHex = Convert.ToHexString(DocIdBytes);
        writer.Write($"trailer\n<< /Size {offsets.Count} /Root 1 0 R /Encrypt 5 0 R /ID [<{docIdHex}> <{docIdHex}>] >>\n");
        writer.Write($"startxref\n{xrefPos}\n%%EOF\n");
        writer.Flush();

        return ms.ToArray();
    }

    public static byte[] CreateUnprotectedPdf(string text = "Unprotected Statement Text")
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText(text, 12, new PdfPoint(50, 750), font);
        return builder.Build();
    }

    #endregion

    #region Direct PdfExtractionService & PdfPig Tests

    [Fact]
    public void SyntheticEncryptedPdf_ThrowsPdfDocumentEncryptedException_WhenOpenedWithoutPassword()
    {
        var pdfBytes = CreateEncryptedPdf("Secret123!");
        using var ms = new MemoryStream(pdfBytes);

        var ex = Assert.Throws<PdfDocumentEncryptedException>(() =>
        {
            PdfDocument.Open(ms);
        });

        Assert.NotNull(ex);
    }

    [Fact]
    public void SyntheticEncryptedPdf_ThrowsPdfDocumentEncryptedException_WhenOpenedWithWrongPassword()
    {
        var pdfBytes = CreateEncryptedPdf("Secret123!");
        using var ms = new MemoryStream(pdfBytes);

        var ex = Assert.Throws<PdfDocumentEncryptedException>(() =>
        {
            PdfDocument.Open(ms, new ParsingOptions { Password = "WrongPassword!" });
        });

        Assert.NotNull(ex);
    }

    [Fact]
    public void SyntheticEncryptedPdf_OpensSuccessfully_WhenProvidedCorrectPassword()
    {
        var pdfBytes = CreateEncryptedPdf("Secret123!", "Decrypted Statement Content 100%");
        using var ms = new MemoryStream(pdfBytes);

        using var doc = PdfDocument.Open(ms, new ParsingOptions { Password = "Secret123!" });
        Assert.NotNull(doc);
        Assert.Equal(1, doc.NumberOfPages);
        var page = doc.GetPage(1);
        Assert.Contains("Decrypted Statement Content 100%", page.Text);
    }

    [Fact]
    public async Task ExtractDocumentAsync_UnprotectedPdf_ExtractsSuccessfullyWithoutPassword()
    {
        await using var context = new AccufexDbContext(_dbOptions);
        var userId = Guid.NewGuid();
        var client = new Client
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "Unprotected Client",
            ContactPerson = "Contact",
            Email = "unprotected@test.corp",
            Phone = "1234567890",
            BusinessName = "Biz",
            BusinessType = "Corporate",
            Address = "Street",
            TaxId = "TAX1",
            CreatedAt = DateTime.UtcNow
        };
        context.Clients.Add(client);

        var year = new FinancialYear
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            DisplayName = "2025-2026",
            StartDate = new DateTime(2025, 4, 1),
            EndDate = new DateTime(2026, 3, 31),
            Status = 1,
            CreatedAt = DateTime.UtcNow
        };
        context.FinancialYears.Add(year);

        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            FinancialYearId = year.Id,
            OriginalFileName = "normal_statement.pdf",
            StoredFileName = "normal_statement.pdf",
            Extension = ".pdf",
            ContentType = "application/pdf",
            SizeBytes = 1024,
            UploadedAt = DateTime.UtcNow,
            ProcessingStatus = 0
        };
        context.FileRecords.Add(fileRecord);
        await context.SaveChangesAsync();

        var pdfBytes = CreateUnprotectedPdf("Normal Transaction Deposit 5000");
        var storageKey = $"{client.Id:N}/{fileRecord.UploadedAt.Year}/{fileRecord.StoredFileName}".Replace('\\', '/');
        _inMemoryStorage[storageKey] = pdfBytes;

        var extractionService = new PdfExtractionService(context, _mockStorageService.Object, _mockExtractionLogger.Object);
        var (success, errorMessage, result) = await extractionService.ExtractDocumentAsync(fileRecord.Id, userId);

        Assert.True(success);
        Assert.Null(errorMessage);
        Assert.NotNull(result);
        Assert.Equal("DigitalTextExtracted", result.ExtractionStatus);
        Assert.True(result.HasUsableText);
        Assert.NotEqual("PasswordProtected", result.ExtractionStatus);
    }

    [Fact]
    public async Task ExtractDocumentAsync_PasswordProtectedPdf_WithoutPassword_ReturnsPasswordProtectedStatus()
    {
        await using var context = new AccufexDbContext(_dbOptions);
        var userId = Guid.NewGuid();
        var client = new Client
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "Encrypted Client",
            ContactPerson = "Contact",
            Email = "encrypted@test.corp",
            Phone = "1234567890",
            BusinessName = "Biz",
            BusinessType = "Corporate",
            Address = "Street",
            TaxId = "TAX2",
            CreatedAt = DateTime.UtcNow
        };
        context.Clients.Add(client);

        var year = new FinancialYear
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            DisplayName = "2025-2026",
            StartDate = new DateTime(2025, 4, 1),
            EndDate = new DateTime(2026, 3, 31),
            Status = 1,
            CreatedAt = DateTime.UtcNow
        };
        context.FinancialYears.Add(year);

        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            FinancialYearId = year.Id,
            OriginalFileName = "encrypted_statement.pdf",
            StoredFileName = "encrypted_statement.pdf",
            Extension = ".pdf",
            ContentType = "application/pdf",
            SizeBytes = 2048,
            UploadedAt = DateTime.UtcNow,
            ProcessingStatus = 0
        };
        context.FileRecords.Add(fileRecord);
        await context.SaveChangesAsync();

        var pdfBytes = CreateEncryptedPdf("Password456!");
        var storageKey = $"{client.Id:N}/{fileRecord.UploadedAt.Year}/{fileRecord.StoredFileName}".Replace('\\', '/');
        _inMemoryStorage[storageKey] = pdfBytes;

        var extractionService = new PdfExtractionService(context, _mockStorageService.Object, _mockExtractionLogger.Object);
        var (success, errorMessage, result) = await extractionService.ExtractDocumentAsync(fileRecord.Id, userId, password: null);

        Assert.False(success);
        Assert.NotNull(errorMessage);
        Assert.Contains("password", errorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result);
        Assert.Equal("PasswordProtected", result.ExtractionStatus);
        Assert.Equal("Encrypted", result.PdfType);
        Assert.False(result.HasUsableText);
        Assert.Contains("password-protected", result.Warnings[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractDocumentAsync_PasswordProtectedPdf_WithIncorrectPassword_ReturnsSafeIncorrectPasswordMessage()
    {
        await using var context = new AccufexDbContext(_dbOptions);
        var userId = Guid.NewGuid();
        var client = new Client
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "Encrypted Client 2",
            ContactPerson = "Contact",
            Email = "encrypted2@test.corp",
            Phone = "1234567890",
            BusinessName = "Biz",
            BusinessType = "Corporate",
            Address = "Street",
            TaxId = "TAX3",
            CreatedAt = DateTime.UtcNow
        };
        context.Clients.Add(client);

        var year = new FinancialYear
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            DisplayName = "2025-2026",
            StartDate = new DateTime(2025, 4, 1),
            EndDate = new DateTime(2026, 3, 31),
            Status = 1,
            CreatedAt = DateTime.UtcNow
        };
        context.FinancialYears.Add(year);

        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            FinancialYearId = year.Id,
            OriginalFileName = "encrypted_statement_2.pdf",
            StoredFileName = "encrypted_statement_2.pdf",
            Extension = ".pdf",
            ContentType = "application/pdf",
            SizeBytes = 2048,
            UploadedAt = DateTime.UtcNow,
            ProcessingStatus = 0
        };
        context.FileRecords.Add(fileRecord);
        await context.SaveChangesAsync();

        var pdfBytes = CreateEncryptedPdf("CorrectSecret123!");
        var storageKey = $"{client.Id:N}/{fileRecord.UploadedAt.Year}/{fileRecord.StoredFileName}".Replace('\\', '/');
        _inMemoryStorage[storageKey] = pdfBytes;

        var extractionService = new PdfExtractionService(context, _mockStorageService.Object, _mockExtractionLogger.Object);
        var (success, errorMessage, result) = await extractionService.ExtractDocumentAsync(fileRecord.Id, userId, password: "WrongPassword999!");

        Assert.False(success);
        Assert.Equal("Incorrect PDF password. Please try again.", errorMessage);
        Assert.NotNull(result);
        Assert.Equal("PasswordProtected", result.ExtractionStatus);
        Assert.Contains("Incorrect PDF password. Please try again.", result.Warnings[0]);

        // Verify password is NOT logged
        _mockExtractionLogger.Verify(
            x => x.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("WrongPassword999!")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never,
            "Security violation: Submitted password must NEVER be logged!");
    }

    [Fact]
    public async Task ExtractDocumentAsync_PasswordProtectedPdf_WithCorrectPassword_ExtractsSuccessfully()
    {
        await using var context = new AccufexDbContext(_dbOptions);
        var userId = Guid.NewGuid();
        var client = new Client
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "Encrypted Client 3",
            ContactPerson = "Contact",
            Email = "encrypted3@test.corp",
            Phone = "1234567890",
            BusinessName = "Biz",
            BusinessType = "Corporate",
            Address = "Street",
            TaxId = "TAX4",
            CreatedAt = DateTime.UtcNow
        };
        context.Clients.Add(client);

        var year = new FinancialYear
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            DisplayName = "2025-2026",
            StartDate = new DateTime(2025, 4, 1),
            EndDate = new DateTime(2026, 3, 31),
            Status = 1,
            CreatedAt = DateTime.UtcNow
        };
        context.FinancialYears.Add(year);

        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            FinancialYearId = year.Id,
            OriginalFileName = "encrypted_statement_3.pdf",
            StoredFileName = "encrypted_statement_3.pdf",
            Extension = ".pdf",
            ContentType = "application/pdf",
            SizeBytes = 2048,
            UploadedAt = DateTime.UtcNow,
            ProcessingStatus = 0
        };
        context.FileRecords.Add(fileRecord);
        await context.SaveChangesAsync();

        var pdfBytes = CreateEncryptedPdf("SuperSecretKey!", "Confidential Bank Statement Transactions 2026");
        var storageKey = $"{client.Id:N}/{fileRecord.UploadedAt.Year}/{fileRecord.StoredFileName}".Replace('\\', '/');
        _inMemoryStorage[storageKey] = pdfBytes;

        var extractionService = new PdfExtractionService(context, _mockStorageService.Object, _mockExtractionLogger.Object);
        var (success, errorMessage, result) = await extractionService.ExtractDocumentAsync(fileRecord.Id, userId, password: "SuperSecretKey!");

        Assert.True(success);
        Assert.Null(errorMessage);
        Assert.NotNull(result);
        Assert.Equal("DigitalTextExtracted", result.ExtractionStatus);
        Assert.True(result.HasUsableText);
        Assert.Single(result.Pages);
        Assert.Contains("Confidential Bank Statement Transactions 2026", result.Pages[0].RawText);

        // Verify password is NOT persisted in DB
        var dbRecord = await context.FileRecords.FindAsync(fileRecord.Id);
        Assert.NotNull(dbRecord);
        var anyStoredPwd = await context.FileRecords.AnyAsync(f => f.StoredFileName.Contains("SuperSecretKey!"));
        Assert.False(anyStoredPwd);
    }

    [Fact]
    public async Task ExtractDocumentAsync_CorruptedPdf_ReturnsFailed_NotClassifiedAsPasswordProtected()
    {
        await using var context = new AccufexDbContext(_dbOptions);
        var userId = Guid.NewGuid();
        var client = new Client
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "Corrupt Client",
            ContactPerson = "Contact",
            Email = "corrupt@test.corp",
            Phone = "1234567890",
            BusinessName = "Biz",
            BusinessType = "Corporate",
            Address = "Street",
            TaxId = "TAX5",
            CreatedAt = DateTime.UtcNow
        };
        context.Clients.Add(client);

        var year = new FinancialYear
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            DisplayName = "2025-2026",
            StartDate = new DateTime(2025, 4, 1),
            EndDate = new DateTime(2026, 3, 31),
            Status = 1,
            CreatedAt = DateTime.UtcNow
        };
        context.FinancialYears.Add(year);

        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            FinancialYearId = year.Id,
            OriginalFileName = "corrupt.pdf",
            StoredFileName = "corrupt.pdf",
            Extension = ".pdf",
            ContentType = "application/pdf",
            SizeBytes = 128,
            UploadedAt = DateTime.UtcNow,
            ProcessingStatus = 0
        };
        context.FileRecords.Add(fileRecord);
        await context.SaveChangesAsync();

        // Corrupted byte stream
        var corruptBytes = Encoding.ASCII.GetBytes("%PDF-1.4\nCorrupted binary garbage that cannot be parsed by any PDF engine\n%%EOF");
        var storageKey = $"{client.Id:N}/{fileRecord.UploadedAt.Year}/{fileRecord.StoredFileName}".Replace('\\', '/');
        _inMemoryStorage[storageKey] = corruptBytes;

        var extractionService = new PdfExtractionService(context, _mockStorageService.Object, _mockExtractionLogger.Object);
        var (success, errorMessage, result) = await extractionService.ExtractDocumentAsync(fileRecord.Id, userId);

        Assert.False(success);
        Assert.NotNull(errorMessage);
        Assert.True(errorMessage.Contains("could not be read", StringComparison.OrdinalIgnoreCase) || errorMessage.Contains("damaged", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(result);
        Assert.Equal("Failed", result.ExtractionStatus);
        Assert.Contains("corrupted", result.Errors[0], StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("PasswordProtected", result.ExtractionStatus);
    }

    [Fact]
    public async Task ExtractDocumentAsync_TenantIsolation_UserCannotUnlockOtherUsersPdf()
    {
        await using var context = new AccufexDbContext(_dbOptions);
        var userAId = Guid.NewGuid();
        var userBId = Guid.NewGuid();

        var clientA = new Client
        {
            Id = Guid.NewGuid(),
            UserId = userAId,
            Name = "User A Corp",
            ContactPerson = "User A",
            Email = "usera@test.corp",
            Phone = "1111111111",
            BusinessName = "User A",
            BusinessType = "Corporate",
            Address = "Address A",
            TaxId = "TAXA",
            CreatedAt = DateTime.UtcNow
        };
        context.Clients.Add(clientA);

        var yearA = new FinancialYear
        {
            Id = Guid.NewGuid(),
            ClientId = clientA.Id,
            DisplayName = "2025-2026",
            StartDate = new DateTime(2025, 4, 1),
            EndDate = new DateTime(2026, 3, 31),
            Status = 1,
            CreatedAt = DateTime.UtcNow
        };
        context.FinancialYears.Add(yearA);

        var fileRecordA = new FileRecord
        {
            Id = Guid.NewGuid(),
            ClientId = clientA.Id,
            FinancialYearId = yearA.Id,
            OriginalFileName = "user_a_encrypted.pdf",
            StoredFileName = "user_a_encrypted.pdf",
            Extension = ".pdf",
            ContentType = "application/pdf",
            SizeBytes = 2048,
            UploadedAt = DateTime.UtcNow,
            ProcessingStatus = 0
        };
        context.FileRecords.Add(fileRecordA);
        await context.SaveChangesAsync();

        var pdfBytes = CreateEncryptedPdf("UserAPassword!");
        var storageKey = $"{clientA.Id:N}/{fileRecordA.UploadedAt.Year}/{fileRecordA.StoredFileName}".Replace('\\', '/');
        _inMemoryStorage[storageKey] = pdfBytes;

        var extractionService = new PdfExtractionService(context, _mockStorageService.Object, _mockExtractionLogger.Object);

        // User B attempts to extract User A's file with correct password
        var (success, errorMessage, result) = await extractionService.ExtractDocumentAsync(fileRecordA.Id, userBId, password: "UserAPassword!");

        Assert.False(success);
        Assert.Contains("was not found", errorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result);
    }

    #endregion

    #region End-to-End API Controller Tests via WebApplicationFactory

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
            AllowAutoRedirect = false
        });

        var email = $"{prefix}_{Guid.NewGuid():N}@accufex.test";
        var regRequest = new RegisterRequest($"{prefix} User", email, "Password123!");

        var regResp = await client.PostAsJsonAsync("/api/auth/register", regRequest);
        Assert.Equal(HttpStatusCode.OK, regResp.StatusCode);
        var auth = await regResp.Content.ReadFromJsonAsync<AuthResponseDto>();
        Assert.NotNull(auth);

        return (client, auth);
    }

    [Fact]
    public async Task EndToEnd_EncryptedPdf_Upload_ThenExtractWithoutPassword_ReturnsRequiresPasswordExtension()
    {
        var (client, _) = await RegisterUserAsync("e2e_enc");

        // 1. Upload encrypted PDF
        var encryptedPdf = CreateEncryptedPdf("StatementPass2026!");
        using var content = CreateMultipartPdf("enc_stmt.pdf", encryptedPdf);

        var uploadResp = await client.PostAsync("/api/statements/upload", content);
        Assert.Equal(HttpStatusCode.OK, uploadResp.StatusCode);
        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>();
        Assert.NotNull(uploadResult);
        var fileId = uploadResult.FileId;

        // 2. Call Extract without password
        var extractResp = await client.PostAsJsonAsync($"/api/statements/{fileId}/extract", new ExtractStatementRequest());
        Assert.Equal(HttpStatusCode.BadRequest, extractResp.StatusCode);

        var errorDoc = await extractResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(errorDoc.TryGetProperty("requiresPassword", out var requiresPasswordProp));
        Assert.True(requiresPasswordProp.GetBoolean());

        Assert.True(errorDoc.TryGetProperty("extractionStatus", out var extractionStatusProp));
        Assert.Equal("PasswordProtected", extractionStatusProp.GetString());

        Assert.True(errorDoc.TryGetProperty("isIncorrectPassword", out var isIncorrectPasswordProp));
        Assert.False(isIncorrectPasswordProp.GetBoolean());

        Assert.True(errorDoc.TryGetProperty("title", out var titleProp));
        Assert.Equal("Password Protected PDF", titleProp.GetString());
    }

    [Fact]
    public async Task EndToEnd_EncryptedPdf_ExtractWithIncorrectPassword_ReturnsIsIncorrectPasswordExtension()
    {
        var (client, _) = await RegisterUserAsync("e2e_wrong_pwd");

        // 1. Upload encrypted PDF
        var encryptedPdf = CreateEncryptedPdf("TargetSecret789!");
        using var content = CreateMultipartPdf("enc_stmt_retry.pdf", encryptedPdf);

        var uploadResp = await client.PostAsync("/api/statements/upload", content);
        Assert.Equal(HttpStatusCode.OK, uploadResp.StatusCode);
        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>();
        Assert.NotNull(uploadResult);
        var fileId = uploadResult.FileId;

        // 2. Call Extract with WRONG password
        var extractResp = await client.PostAsJsonAsync($"/api/statements/{fileId}/extract", new ExtractStatementRequest
        {
            Password = "BadPassword123"
        });
        Assert.Equal(HttpStatusCode.BadRequest, extractResp.StatusCode);

        var errorDoc = await extractResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(errorDoc.TryGetProperty("requiresPassword", out var requiresPasswordProp));
        Assert.True(requiresPasswordProp.GetBoolean());

        Assert.True(errorDoc.TryGetProperty("isIncorrectPassword", out var isIncorrectPasswordProp));
        Assert.True(isIncorrectPasswordProp.GetBoolean());

        Assert.True(errorDoc.TryGetProperty("detail", out var detailProp));
        Assert.Equal("Incorrect PDF password. Please try again.", detailProp.GetString());

        Assert.True(errorDoc.TryGetProperty("title", out var titleProp));
        Assert.Equal("Invalid PDF Password", titleProp.GetString());
    }

    [Fact]
    public async Task EndToEnd_EncryptedPdf_ExtractWithCorrectPassword_ExtractsAndUnlocksPipeline()
    {
        var (client, _) = await RegisterUserAsync("e2e_correct_pwd");

        // 1. Upload encrypted PDF
        var encryptedPdf = CreateEncryptedPdf("UnlockMe2026!", "Acme Corporation Opening Balance 50000");
        using var content = CreateMultipartPdf("enc_stmt_success.pdf", encryptedPdf);

        var uploadResp = await client.PostAsync("/api/statements/upload", content);
        Assert.Equal(HttpStatusCode.OK, uploadResp.StatusCode);
        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>();
        Assert.NotNull(uploadResult);
        var fileId = uploadResult.FileId;

        // 2. Call Extract with CORRECT password
        var extractResp = await client.PostAsJsonAsync($"/api/statements/{fileId}/extract", new ExtractStatementRequest
        {
            Password = "UnlockMe2026!"
        });
        Assert.Equal(HttpStatusCode.OK, extractResp.StatusCode);

        var extractResult = await extractResp.Content.ReadFromJsonAsync<PdfExtractionResult>();
        Assert.NotNull(extractResult);
        Assert.Equal("DigitalTextExtracted", extractResult.ExtractionStatus);
        Assert.True(extractResult.HasUsableText);
        Assert.Single(extractResult.Pages);
        Assert.Contains("Acme Corporation", extractResult.Pages[0].RawText);
    }

    [Fact]
    public async Task EndToEnd_TenantIsolation_UserBCannotSubmitPasswordForUserAFiles()
    {
        var (clientA, _) = await RegisterUserAsync("tenant_a");
        var (clientB, _) = await RegisterUserAsync("tenant_b");

        // User A uploads encrypted PDF
        var encryptedPdf = CreateEncryptedPdf("PrivateA123!");
        using var content = CreateMultipartPdf("user_a_vault.pdf", encryptedPdf);

        var uploadResp = await clientA.PostAsync("/api/statements/upload", content);
        Assert.Equal(HttpStatusCode.OK, uploadResp.StatusCode);
        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>();
        Assert.NotNull(uploadResult);
        var fileIdA = uploadResult.FileId;

        // User B attempts to extract / unlock User A's file
        var extractRespB = await clientB.PostAsJsonAsync($"/api/statements/{fileIdA}/extract", new ExtractStatementRequest
        {
            Password = "PrivateA123!"
        });

        Assert.Equal(HttpStatusCode.NotFound, extractRespB.StatusCode);
    }

    [Fact]
    public async Task EndToEnd_CorruptedPdf_ExtractReturnsFailed_WithoutRequiresPassword()
    {
        var (client, _) = await RegisterUserAsync("e2e_corrupt");

        // 1. Upload corrupted PDF
        var corruptPdf = Encoding.ASCII.GetBytes("%PDF-1.4\nCorrupted content with no valid cross-reference\n%%EOF");
        using var content = CreateMultipartPdf("corrupted.pdf", corruptPdf);

        var uploadResp = await client.PostAsync("/api/statements/upload", content);
        Assert.Equal(HttpStatusCode.OK, uploadResp.StatusCode);
        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>();
        Assert.NotNull(uploadResult);
        var fileId = uploadResult.FileId;

        // 2. Call Extract
        var extractResp = await client.PostAsJsonAsync($"/api/statements/{fileId}/extract", new ExtractStatementRequest());
        Assert.Equal(HttpStatusCode.BadRequest, extractResp.StatusCode);

        var errorDoc = await extractResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(errorDoc.TryGetProperty("title", out var titleProp));
        Assert.Equal("Extraction Failed", titleProp.GetString());

        // Must NOT have requiresPassword set to true
        var hasRequiresPassword = errorDoc.TryGetProperty("requiresPassword", out var requiresPasswordProp) && requiresPasswordProp.GetBoolean();
        Assert.False(hasRequiresPassword, "Corrupted PDF must not be classified as requiring a password!");
    }

    #endregion
}
