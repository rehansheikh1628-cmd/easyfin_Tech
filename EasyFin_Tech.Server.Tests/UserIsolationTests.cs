using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EasyFin_Tech.Server.Data;
using EasyFin_Tech.Server.DTOs;
using EasyFin_Tech.Server.Models;
using EasyFin_Tech.Server.Options;
using EasyFin_Tech.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace EasyFin_Tech.Server.Tests;

public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = "EasyFin_TestDb_" + Guid.NewGuid();
    public string TempStoragePath { get; } = Path.Combine(Path.GetTempPath(), "EasyFin_Tests_" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            services.AddDbContext<EasyFinDbContext>(options =>
            {
                options.UseInMemoryDatabase(_dbName);
            });

            services.Configure<FileUploadOptions>(options =>
            {
                options.StorageRoot = TempStoragePath;
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            if (Directory.Exists(TempStoragePath))
            {
                Directory.Delete(TempStoragePath, recursive: true);
            }
        }
        catch { }
    }
}

public class UserIsolationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public UserIsolationTests(CustomWebApplicationFactory factory)
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
        page.AddText("Opening Balance Deposit", 10, new PdfPoint(150, 700), font);
        page.AddText("50000.00", 10, new PdfPoint(400, 700), font);
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

        var email = $"{prefix}_{Guid.NewGuid():N}@isolation.test";
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
    public async Task Test00_UnauthenticatedRequests_AreBlockedWith401()
    {
        var anonClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var respGet = await anonClient.GetAsync("/api/statements");
        Assert.Equal(HttpStatusCode.Unauthorized, respGet.StatusCode);

        var respStats = await anonClient.GetAsync("/api/dashboard/stats");
        Assert.Equal(HttpStatusCode.Unauthorized, respStats.StatusCode);

        using var content = CreateMultipartPdf("anon.pdf", GenerateSamplePdfBytes("Anon"));
        var respUpload = await anonClient.PostAsync("/api/statements/upload", content);
        Assert.Equal(HttpStatusCode.Unauthorized, respUpload.StatusCode);
    }

    [Fact]
    public async Task Test01_User1_UploadsFileA_CanListFileA()
    {
        var (client1, _) = await RegisterUserAsync("u1_t1");

        var fileA = await UploadPdfAsync(client1, "User1_StatementA.pdf", "USER 1 STATEMENT A");

        var listResp = await client1.GetAsync("/api/statements");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);

        var statements = await listResp.Content.ReadFromJsonAsync<List<StatementSummaryDto>>(JsonOptions);
        Assert.NotNull(statements);
        Assert.Contains(statements, s => s.Id == fileA.FileId && s.OriginalFileName == "User1_StatementA.pdf");
    }

    [Fact]
    public async Task Test02_User2_UploadsFileB_CanListFileB()
    {
        var (client2, _) = await RegisterUserAsync("u2_t2");

        var fileB = await UploadPdfAsync(client2, "User2_StatementB.pdf", "USER 2 STATEMENT B");

        var listResp = await client2.GetAsync("/api/statements");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);

        var statements = await listResp.Content.ReadFromJsonAsync<List<StatementSummaryDto>>(JsonOptions);
        Assert.NotNull(statements);
        Assert.Contains(statements, s => s.Id == fileB.FileId && s.OriginalFileName == "User2_StatementB.pdf");
    }

    [Fact]
    public async Task Test03_User2_ListsStatements_FileA_MustNotAppear()
    {
        var (client1, _) = await RegisterUserAsync("u1_t3");
        var (client2, _) = await RegisterUserAsync("u2_t3");

        var fileA = await UploadPdfAsync(client1, "User1_SecretFileA.pdf", "USER 1 CONFIDENTIAL");
        var fileB = await UploadPdfAsync(client2, "User2_NormalFileB.pdf", "USER 2 DOCUMENT");

        var listResp2 = await client2.GetAsync("/api/statements");
        Assert.Equal(HttpStatusCode.OK, listResp2.StatusCode);

        var statements2 = await listResp2.Content.ReadFromJsonAsync<List<StatementSummaryDto>>(JsonOptions);
        Assert.NotNull(statements2);

        Assert.Contains(statements2, s => s.Id == fileB.FileId);
        Assert.DoesNotContain(statements2, s => s.Id == fileA.FileId);
        Assert.DoesNotContain(statements2, s => s.OriginalFileName == "User1_SecretFileA.pdf");
    }

    [Fact]
    public async Task Test04_User2_DirectlyRequests_User1_FileA_ById_Denied()
    {
        var (client1, _) = await RegisterUserAsync("u1_t4");
        var (client2, _) = await RegisterUserAsync("u2_t4");

        var fileA = await UploadPdfAsync(client1, "User1_TargetA.pdf", "TARGET A");

        var crossResp = await client2.GetAsync($"/api/statements/{fileA.FileId}");
        Assert.Equal(HttpStatusCode.NotFound, crossResp.StatusCode);
    }

    [Fact]
    public async Task Test05_User2_AttemptsDownload_User1_FileA_Denied()
    {
        var (client1, _) = await RegisterUserAsync("u1_t5");
        var (client2, _) = await RegisterUserAsync("u2_t5");

        var fileA = await UploadPdfAsync(client1, "User1_DownloadA.pdf", "DOWNLOAD TEST A");

        var crossDownloadResp = await client2.GetAsync($"/api/statements/{fileA.FileId}/download");
        Assert.Equal(HttpStatusCode.NotFound, crossDownloadResp.StatusCode);
    }

    [Fact]
    public async Task Test06_User2_AttemptsExtract_User1_FileA_Denied()
    {
        var (client1, _) = await RegisterUserAsync("u1_t6");
        var (client2, _) = await RegisterUserAsync("u2_t6");

        var fileA = await UploadPdfAsync(client1, "User1_ExtractA.pdf", "EXTRACTION TARGET A");

        var crossExtractResp = await client2.PostAsJsonAsync($"/api/statements/{fileA.FileId}/extract", new ExtractStatementRequest());
        Assert.Equal(HttpStatusCode.NotFound, crossExtractResp.StatusCode);
    }

    [Fact]
    public async Task Test07_User2_AttemptsRead_User1_ExtractionResult_Denied()
    {
        var (client1, _) = await RegisterUserAsync("u1_t7");
        var (client2, _) = await RegisterUserAsync("u2_t7");

        var fileA = await UploadPdfAsync(client1, "User1_ExtractResultA.pdf", "EXTRACT AND SAVE A");

        var extractResp1 = await client1.PostAsJsonAsync($"/api/statements/{fileA.FileId}/extract", new ExtractStatementRequest());
        Assert.Equal(HttpStatusCode.OK, extractResp1.StatusCode);

        var crossGetExtractionResp = await client2.GetAsync($"/api/statements/{fileA.FileId}/extraction");
        Assert.Equal(HttpStatusCode.NotFound, crossGetExtractionResp.StatusCode);
    }

    [Fact]
    public async Task Test08_User2_AttemptsUpload_Using_User1_ClientId_Denied()
    {
        var (_, auth1) = await RegisterUserAsync("u1_t8");
        var (client2, _) = await RegisterUserAsync("u2_t8");

        var user1ClientId = auth1.ActiveWorkspace!.Id;
        var bytes = GenerateSamplePdfBytes("INJECTION ATTEMPT");
        using var maliciousContent = CreateMultipartPdf("malicious.pdf", bytes, clientId: user1ClientId);

        var injectResp = await client2.PostAsync("/api/statements/upload", maliciousContent);
        Assert.Equal(HttpStatusCode.Forbidden, injectResp.StatusCode);
    }

    [Fact]
    public async Task Test09_User1_CanStillAccessOwnFile_Normally()
    {
        var (client1, _) = await RegisterUserAsync("u1_t9");
        var (client2, _) = await RegisterUserAsync("u2_t9");

        var fileA = await UploadPdfAsync(client1, "User1_LegitimateA.pdf", "USER 1 NORMAL FLOW");

        // User 2 attacks
        var crossResp = await client2.GetAsync($"/api/statements/{fileA.FileId}");
        Assert.Equal(HttpStatusCode.NotFound, crossResp.StatusCode);

        // User 1 accesses normally
        var ownResp = await client1.GetAsync($"/api/statements/{fileA.FileId}");
        Assert.Equal(HttpStatusCode.OK, ownResp.StatusCode);
        var detail = await ownResp.Content.ReadFromJsonAsync<StatementDetailDto>(JsonOptions);
        Assert.NotNull(detail);
        Assert.Equal(fileA.FileId, detail.Id);

        var downloadResp = await client1.GetAsync($"/api/statements/{fileA.FileId}/download");
        Assert.Equal(HttpStatusCode.OK, downloadResp.StatusCode);
        var bytes = await downloadResp.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 0);
    }

    [Fact]
    public async Task Test10_ExistingLegitimateSingleUserFlows_ContinueToPass()
    {
        var (client1, _) = await RegisterUserAsync("u1_t10");

        var fileA = await UploadPdfAsync(client1, "LegitimateFullFlow.pdf", "FULL FLOW STATEMENT");

        var extractResp = await client1.PostAsJsonAsync($"/api/statements/{fileA.FileId}/extract", new ExtractStatementRequest());
        Assert.Equal(HttpStatusCode.OK, extractResp.StatusCode);
        var result = await extractResp.Content.ReadFromJsonAsync<PdfExtractionResult>(JsonOptions);
        Assert.NotNull(result);
        Assert.Equal("DigitalTextExtracted", result.ExtractionStatus);
        Assert.True(result.HasUsableText);
        Assert.True(result.PageCount >= 1);

        var getResultResp = await client1.GetAsync($"/api/statements/{fileA.FileId}/extraction");
        Assert.Equal(HttpStatusCode.OK, getResultResp.StatusCode);
        var cached = await getResultResp.Content.ReadFromJsonAsync<PdfExtractionResult>(JsonOptions);
        Assert.NotNull(cached);
        Assert.Equal(result.FileId, cached.FileId);
    }

    [Fact]
    public async Task Test11_ParameterTampering_ChangingOnlyIds_CannotBypassAuthorization()
    {
        var (client1, _) = await RegisterUserAsync("u1_t11");
        var (client2, _) = await RegisterUserAsync("u2_t11");

        var fileA = await UploadPdfAsync(client1, "User1_TamperA.pdf", "TAMPER TEST A");
        var user1YearId = fileA.FinancialYearId;

        // User 2 attempts to upload pointing to User 1's FinancialYearId
        var bytes = GenerateSamplePdfBytes("YEAR TAMPERING");
        using var tamperContent = CreateMultipartPdf("tamper_year.pdf", bytes, financialYearId: user1YearId);
        var tamperResp = await client2.PostAsync("/api/statements/upload", tamperContent);
        Assert.Equal(HttpStatusCode.Forbidden, tamperResp.StatusCode);

        // User 2 attempts random GUID
        var randomResp = await client2.GetAsync($"/api/statements/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, randomResp.StatusCode);
    }

    [Fact]
    public async Task Test12_NoEndpointLeaksMetadata_BetweenUsers_DashboardStatsIsolated()
    {
        var (client1, _) = await RegisterUserAsync("u1_t12");
        var (client2, _) = await RegisterUserAsync("u2_t12");

        // User 1 uploads 2 files
        await UploadPdfAsync(client1, "User1_Doc1.pdf", "USER 1 DOC 1");
        await UploadPdfAsync(client1, "User1_Doc2.pdf", "USER 1 DOC 2");

        // User 2 uploads 1 file
        await UploadPdfAsync(client2, "User2_Doc1.pdf", "USER 2 DOC 1");

        // User 2 requests dashboard stats
        var statsResp2 = await client2.GetAsync("/api/dashboard/stats");
        Assert.Equal(HttpStatusCode.OK, statsResp2.StatusCode);

        using var jsonDoc = JsonDocument.Parse(await statsResp2.Content.ReadAsStringAsync());
        var root = jsonDoc.RootElement;

        var totalStatements = root.GetProperty("totalStatements").GetInt32();
        var clientProfiles = root.GetProperty("clientProfiles").GetInt32();

        Assert.Equal(1, totalStatements);
        Assert.Equal(1, clientProfiles);

        if (root.TryGetProperty("tables", out var tables) && tables.TryGetProperty("fileRecords", out var fileRecordsCount))
        {
            Assert.Equal(1, fileRecordsCount.GetInt32());
        }
    }

    [Fact]
    public void PasswordHasher_SaltedPbkdf2_GeneratesSecureHashes_AndVerifiesCorrectly()
    {
        var hasher = new PasswordHasher();
        var rawPassword = "CorrectPassword123#";

        var hash = hasher.HashPassword(rawPassword);
        Assert.StartsWith("$pbkdf2$100000$", hash);

        Assert.True(hasher.VerifyHashedPassword(hash, rawPassword, out _));
        Assert.False(hasher.VerifyHashedPassword(hash, "WrongPassword!", out _));
    }
}
