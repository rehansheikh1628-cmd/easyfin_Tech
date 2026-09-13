using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EasyFin_Tech.Server.Data;
using EasyFin_Tech.Server.DTOs;
using EasyFin_Tech.Server.Models;
using EasyFin_Tech.Server.Options;
using EasyFin_Tech.Server.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace EasyFin_Tech.Server.Tests;

public class SecureStorageTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public SecureStorageTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static byte[] GenerateSamplePdfBytes(string title)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText(title, 12, new PdfPoint(50, 750), font);
        page.AddText("01/04/2026", 10, new PdfPoint(50, 700), font);
        page.AddText("Secure Storage Test Entry", 10, new PdfPoint(150, 700), font);
        page.AddText("12500.00", 10, new PdfPoint(400, 700), font);
        return builder.Build();
    }

    private static MultipartFormDataContent CreateMultipartPdf(string fileName, byte[] bytes, Guid? clientId = null, Guid? financialYearId = null)
    {
        var multipart = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        multipart.Add(fileContent, "file", fileName);

        if (clientId.HasValue)
        {
            multipart.Add(new StringContent(clientId.Value.ToString()), "clientId");
        }
        if (financialYearId.HasValue)
        {
            multipart.Add(new StringContent(financialYearId.Value.ToString()), "financialYearId");
        }
        return multipart;
    }

    private async Task<(HttpClient Client, AuthResponseDto Auth)> RegisterUserAsync(string prefix)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            BaseAddress = new Uri("http://localhost")
        });

        var email = $"{prefix}_{Guid.NewGuid():N}@secstorage.test";
        var request = new RegisterRequest($"{prefix} Workspace", email, "SecurePassword123!");
        var response = await client.PostAsJsonAsync("/api/auth/register", request, JsonOptions);
        var respContent = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Register failed with {response.StatusCode}: {respContent}");

        var auth = await response.Content.ReadFromJsonAsync<AuthResponseDto>(JsonOptions);
        Assert.NotNull(auth);
        Assert.True(auth.Success);
        Assert.NotNull(auth.User);
        Assert.NotNull(auth.ActiveWorkspace);

        return (client, auth);
    }

    private async Task<StatementUploadResponse> UploadPdfAsync(HttpClient client, string fileName, string title, Guid? clientId = null, Guid? financialYearId = null)
    {
        var bytes = GenerateSamplePdfBytes(title);
        using var content = CreateMultipartPdf(fileName, bytes, clientId, financialYearId);
        var response = await client.PostAsync("/api/statements/upload", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var uploadResponse = await response.Content.ReadFromJsonAsync<StatementUploadResponse>(JsonOptions);
        Assert.NotNull(uploadResponse);
        Assert.True(uploadResponse.Success);
        return uploadResponse;
    }

    [Fact]
    public async Task Test01_UploadStatement_StoresFileInTenantDirectory_OutsideWebRoot()
    {
        var (clientA, authA) = await RegisterUserAsync("uA_path");
        var uploadResp = await UploadPdfAsync(clientA, "TenantIsolation_Doc.pdf", "TENANT STORAGE TEST");

        // Verify database entity is created
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyFinDbContext>();
        var record = await db.FileRecords.Include(f => f.FinancialYear).FirstOrDefaultAsync(f => f.Id == uploadResp.FileId);
        Assert.NotNull(record);

        // Verify physical file path in storage
        var yearFolder = (record.FinancialYear?.StartDate.Year ?? record.UploadedAt.Year).ToString();
        var expectedPath = Path.Combine(
            _factory.TempStoragePath,
            "tenants",
            authA.User!.Id.ToString("N"),
            "clients",
            record.ClientId.ToString("N"),
            "statements",
            yearFolder,
            record.StoredFileName);

        Assert.True(File.Exists(expectedPath), $"Physical file must exist at {expectedPath}");

        // Verify NO customer files are inside wwwroot
        var wwwrootFolder = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(wwwrootFolder))
        {
            var leaked = Directory.GetFiles(wwwrootFolder, record.StoredFileName, SearchOption.AllDirectories);
            Assert.Empty(leaked);
        }
    }

    [Fact]
    public async Task Test02_UserA_CanDownloadOwnStatementStream()
    {
        var (clientA, _) = await RegisterUserAsync("uA_dl");
        var uploadResp = await UploadPdfAsync(clientA, "UserA_Download.pdf", "USER A DOWNLOAD STREAM");

        var dlResp = await clientA.GetAsync($"/api/statements/{uploadResp.FileId}/download");
        Assert.Equal(HttpStatusCode.OK, dlResp.StatusCode);
        Assert.Equal("application/pdf", dlResp.Content.Headers.ContentType?.MediaType);

        var downloadedBytes = await dlResp.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(downloadedBytes);

        // Verify PDF Magic Bytes %PDF-
        Assert.Equal(0x25, downloadedBytes[0]);
        Assert.Equal(0x50, downloadedBytes[1]);
        Assert.Equal(0x44, downloadedBytes[2]);
        Assert.Equal(0x46, downloadedBytes[3]);
        Assert.Equal(0x2D, downloadedBytes[4]);
    }

    [Fact]
    public async Task Test03_UserB_CannotDownloadUserAStatementStream()
    {
        var (clientA, _) = await RegisterUserAsync("uA_sec");
        var (clientB, _) = await RegisterUserAsync("uB_sec");

        var uploadA = await UploadPdfAsync(clientA, "UserA_Confidential.pdf", "HIGHLY CONFIDENTIAL USER A");

        // User B tries to download User A's file
        var dlRespB = await clientB.GetAsync($"/api/statements/{uploadA.FileId}/download");

        // Must return 404 NotFound to prevent disclosing existence
        Assert.Equal(HttpStatusCode.NotFound, dlRespB.StatusCode);
    }

    [Fact]
    public async Task Test04_AnonymousUser_CannotDownloadStatement()
    {
        var (clientA, _) = await RegisterUserAsync("uA_anon");
        var uploadA = await UploadPdfAsync(clientA, "Statement_AnonCheck.pdf", "ANON CHECK");

        var anonClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var resp = await anonClient.GetAsync($"/api/statements/{uploadA.FileId}/download");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Test05_UserB_CannotDeleteUserAStatement()
    {
        var (clientA, authA) = await RegisterUserAsync("uA_delSec");
        var (clientB, _) = await RegisterUserAsync("uB_delSec");

        var uploadA = await UploadPdfAsync(clientA, "UserA_Preserved.pdf", "USER A PRESERVED FILE");

        // User B attempts to DELETE User A's statement
        var deleteRespB = await clientB.DeleteAsync($"/api/statements/{uploadA.FileId}");
        Assert.Equal(HttpStatusCode.NotFound, deleteRespB.StatusCode);

        // Verify file and DB record still exist
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyFinDbContext>();
        var record = await db.FileRecords.Include(f => f.FinancialYear).FirstOrDefaultAsync(f => f.Id == uploadA.FileId);
        Assert.NotNull(record);

        var yearFolder = (record.FinancialYear?.StartDate.Year ?? record.UploadedAt.Year).ToString();
        var physicalPath = Path.Combine(
            _factory.TempStoragePath,
            "tenants",
            authA.User!.Id.ToString("N"),
            "clients",
            record.ClientId.ToString("N"),
            "statements",
            yearFolder,
            record.StoredFileName);

        Assert.True(File.Exists(physicalPath), "File must remain on disk after unauthorized deletion attempt.");
    }

    [Fact]
    public async Task Test06_UserA_CanDeleteOwnStatement_CascadesDbAndStorage()
    {
        var (clientA, authA) = await RegisterUserAsync("uA_delOwn");
        var uploadA = await UploadPdfAsync(clientA, "UserA_ToDelete.pdf", "USER A TO DELETE");

        // Seed dependent entities: Transaction, TransactionImportResult, PdfProcessingResult
        using (var seedScope = _factory.Services.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<EasyFinDbContext>();
            var rec = await seedDb.FileRecords.Include(f => f.FinancialYear).FirstAsync(f => f.Id == uploadA.FileId);

            var tx = new Transaction
            {
                Id = Guid.NewGuid(),
                SourceFileId = rec.Id,
                ClientId = rec.ClientId,
                TransactionDate = DateTime.UtcNow,
                Description = "Sample TX for statement",
                Debit = 500m,
                Amount = 500m,
                CreatedAt = DateTime.UtcNow
            };
            seedDb.Transactions.Add(tx);

            var importRes = new TransactionImportResult
            {
                Id = Guid.NewGuid(),
                SourceFileId = rec.Id,
                ClientId = rec.ClientId,
                FinancialYearId = rec.FinancialYearId,
                DetectedBank = 1,
                TotalDetected = 1,
                ProcessedCount = 1,
                Status = "Completed",
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow
            };
            seedDb.TransactionImportResults.Add(importRes);

            var procRes = new PdfProcessingResult
            {
                Id = Guid.NewGuid(),
                FileRecordId = rec.Id,
                PdfType = 1,
                ExtractionMethod = 1,
                PageCount = 1,
                HasUsableText = true,
                ExtractedTextStoragePath = "Extractions/test.json",
                ProcessedAt = DateTime.UtcNow
            };
            seedDb.PdfProcessingResults.Add(procRes);

            await seedDb.SaveChangesAsync();
        }

        // Verify physical file exists before delete
        string physicalPath;
        using (var checkScope = _factory.Services.CreateScope())
        {
            var checkDb = checkScope.ServiceProvider.GetRequiredService<EasyFinDbContext>();
            var rec = await checkDb.FileRecords.Include(f => f.FinancialYear).FirstAsync(f => f.Id == uploadA.FileId);
            var yearFolder = (rec.FinancialYear?.StartDate.Year ?? rec.UploadedAt.Year).ToString();
            physicalPath = Path.Combine(
                _factory.TempStoragePath,
                "tenants",
                authA.User!.Id.ToString("N"),
                "clients",
                rec.ClientId.ToString("N"),
                "statements",
                yearFolder,
                rec.StoredFileName);
            Assert.True(File.Exists(physicalPath));
        }

        // Delete statement as User A
        var delResp = await clientA.DeleteAsync($"/api/statements/{uploadA.FileId}");
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        // Verify physical file is deleted
        Assert.False(File.Exists(physicalPath), "Physical file must be deleted from disk.");

        // Verify database cascade cleanup
        using (var verifyScope = _factory.Services.CreateScope())
        {
            var vDb = verifyScope.ServiceProvider.GetRequiredService<EasyFinDbContext>();
            var deletedRecord = await vDb.FileRecords.FirstOrDefaultAsync(f => f.Id == uploadA.FileId);
            Assert.Null(deletedRecord);

            var orphanTxs = await vDb.Transactions.Where(t => t.SourceFileId == uploadA.FileId).ToListAsync();
            Assert.Empty(orphanTxs);

            var orphanImports = await vDb.TransactionImportResults.Where(i => i.SourceFileId == uploadA.FileId).ToListAsync();
            Assert.Empty(orphanImports);

            var orphanProcs = await vDb.PdfProcessingResults.Where(p => p.FileRecordId == uploadA.FileId).ToListAsync();
            Assert.Empty(orphanProcs);
        }
    }

    [Fact]
    public async Task Test07_DeleteStatement_CleansUpSidecarsAndExtractions()
    {
        var (clientA, _) = await RegisterUserAsync("uA_sidecars");
        var uploadA = await UploadPdfAsync(clientA, "Sidecars_Doc.pdf", "SIDECARS DOC");

        var extractionDir = Path.Combine(_factory.TempStoragePath, "Extractions", uploadA.ClientId.ToString("N"));
        Directory.CreateDirectory(extractionDir);
        var extractionFile = Path.Combine(extractionDir, $"{uploadA.FileId}_extraction.json");
        await File.WriteAllTextAsync(extractionFile, "{\"mock\": true}");

        var auditDir = Path.Combine(_factory.TempStoragePath, "corrections");
        Directory.CreateDirectory(auditDir);
        var auditFile = Path.Combine(auditDir, $"statement_audit_{uploadA.FileId:N}.json");
        await File.WriteAllTextAsync(auditFile, "{\"audit\": true}");

        Assert.True(File.Exists(extractionFile));
        Assert.True(File.Exists(auditFile));

        // Delete statement
        var delResp = await clientA.DeleteAsync($"/api/statements/{uploadA.FileId}");
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        // Verify sidecars deleted
        Assert.False(File.Exists(extractionFile), "Extraction artifact should be removed on statement delete.");
        Assert.False(File.Exists(auditFile), "Audit sidecar should be removed on statement delete.");
    }

    [Fact]
    public async Task Test08_UserB_CannotOverwriteUserAFile()
    {
        var (clientA, authA) = await RegisterUserAsync("uA_overwrite");
        var (clientB, authB) = await RegisterUserAsync("uB_overwrite");

        var uploadA = await UploadPdfAsync(clientA, "SameBankStatement.pdf", "STATEMENT A IDENTICAL NAME");
        var uploadB = await UploadPdfAsync(clientB, "SameBankStatement.pdf", "STATEMENT B IDENTICAL NAME");

        // IDs must be distinct GUIDs
        Assert.NotEqual(uploadA.FileId, uploadB.FileId);
        Assert.NotEqual(uploadA.StoredFileName, uploadB.StoredFileName);

        // Stored under separate tenant paths
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyFinDbContext>();

        var recA = await db.FileRecords.Include(f => f.FinancialYear).FirstAsync(f => f.Id == uploadA.FileId);
        var recB = await db.FileRecords.Include(f => f.FinancialYear).FirstAsync(f => f.Id == uploadB.FileId);

        var yearFolderA = (recA.FinancialYear?.StartDate.Year ?? recA.UploadedAt.Year).ToString();
        var yearFolderB = (recB.FinancialYear?.StartDate.Year ?? recB.UploadedAt.Year).ToString();

        var pathA = Path.Combine(_factory.TempStoragePath, "tenants", authA.User!.Id.ToString("N"), "clients", recA.ClientId.ToString("N"), "statements", yearFolderA, recA.StoredFileName);
        var pathB = Path.Combine(_factory.TempStoragePath, "tenants", authB.User!.Id.ToString("N"), "clients", recB.ClientId.ToString("N"), "statements", yearFolderB, recB.StoredFileName);

        Assert.True(File.Exists(pathA));
        Assert.True(File.Exists(pathB));
        Assert.NotEqual(pathA, pathB);
    }

    [Fact]
    public async Task Test09_LocalFileStorageService_RejectsPathTraversal()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new FileUploadOptions
        {
            StorageRoot = Path.Combine(Path.GetTempPath(), "EasyFin_Traversal_Test_" + Guid.NewGuid().ToString("N"))
        });

        var mockEnv = new TestHostEnvironment { ContentRootPath = AppContext.BaseDirectory };
        var storage = new LocalFileStorageService(options, mockEnv, NullLogger<LocalFileStorageService>.Instance);

        using var ms = new MemoryStream([1, 2, 3, 4]);

        // Attempt path traversal via subDirectory escaping root
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            storage.SaveFileAsync(ms, "../../../etc", "malicious.pdf"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            storage.GetFileStreamAsync("..\\..\\secret", "malicious.pdf"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            storage.DeleteFileAsync("..\\..\\secret", "malicious.pdf"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            storage.FileExistsAsync("..\\..\\secret", "malicious.pdf"));

        // Cleanup
        try
        {
            if (Directory.Exists(options.Value.StorageRoot))
            {
                Directory.Delete(options.Value.StorageRoot, true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task Test10_GetStatementFileStream_MissingPhysicalFile_Returns404()
    {
        var (clientA, authA) = await RegisterUserAsync("uA_missingFile");
        var uploadA = await UploadPdfAsync(clientA, "MissingPhysical.pdf", "MISSING PHYSICAL FILE");

        // Manually delete the physical file from disk to simulate file loss / corruption
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyFinDbContext>();
            var rec = await db.FileRecords.Include(f => f.FinancialYear).FirstAsync(f => f.Id == uploadA.FileId);
            var yearFolder = (rec.FinancialYear?.StartDate.Year ?? rec.UploadedAt.Year).ToString();
            var path = Path.Combine(_factory.TempStoragePath, "tenants", authA.User!.Id.ToString("N"), "clients", rec.ClientId.ToString("N"), "statements", yearFolder, rec.StoredFileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        var dlResp = await clientA.GetAsync($"/api/statements/{uploadA.FileId}/download");
        Assert.Equal(HttpStatusCode.NotFound, dlResp.StatusCode);
    }

    [Fact]
    public async Task Test11_NonExistentStatementId_Returns404()
    {
        var (clientA, _) = await RegisterUserAsync("uA_404");
        var randomId = Guid.NewGuid();

        var dlResp = await clientA.GetAsync($"/api/statements/{randomId}/download");
        Assert.Equal(HttpStatusCode.NotFound, dlResp.StatusCode);

        var delResp = await clientA.DeleteAsync($"/api/statements/{randomId}");
        Assert.Equal(HttpStatusCode.NotFound, delResp.StatusCode);
    }

    [Fact]
    public async Task Test12_UserACanExportExcel_UserBCannot()
    {
        var (clientA, _) = await RegisterUserAsync("uA_excelSec");
        var (clientB, _) = await RegisterUserAsync("uB_excelSec");

        var uploadA = await UploadPdfAsync(clientA, "UserA_ExcelExport.pdf", "EXCEL EXPORT DOC");

        // User B attempts to export User A's statement to Excel
        var excelRespB = await clientB.GetAsync($"/api/statements/{uploadA.FileId}/export/excel");
        Assert.Equal(HttpStatusCode.NotFound, excelRespB.StatusCode);
    }

    [Fact]
    public void Test13_ConfigurationBinding_StorageOptions()
    {
        var options = new FileUploadOptions
        {
            StorageProvider = "Local",
            StorageRoot = "Storage/Statements",
            MaxPdfSizeBytes = 262144000,
            AzureBlob = new AzureBlobStorageOptions
            {
                ConnectionString = "DefaultEndpointsProtocol=https;AccountName=test;AccountKey=testkey;EndpointSuffix=core.windows.net",
                ContainerName = "production-statements"
            }
        };

        Assert.Equal("Local", options.StorageProvider);
        Assert.Equal("Storage/Statements", options.StorageRoot);
        Assert.Equal(262144000, options.MaxPdfSizeBytes);
        Assert.Equal("production-statements", options.AzureBlob.ContainerName);
        Assert.Contains("AccountName=test", options.AzureBlob.ConnectionString);
    }
}

internal class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "EasyFin_Tech.Server";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
}
