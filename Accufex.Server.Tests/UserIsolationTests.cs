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
using Accufex.Server.Data;
using Accufex.Server.DTOs;
using Accufex.Server.Models;
using Accufex.Server.Options;
using Accufex.Server.Parsing;
using Accufex.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Accufex.Server.Tests;

public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = "Accufex_TestDb_" + Guid.NewGuid();
    public string TempStoragePath { get; } = Path.Combine(Path.GetTempPath(), "Accufex_Tests_" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            services.AddDbContext<AccufexDbContext>(options =>
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

    private async Task<(StatementUploadResponse Statement, Guid TransactionId)> SeedStatementWithTransactionsAsync(HttpClient client, string prefix)
    {
        var file = await UploadPdfAsync(client, $"{prefix}_stmt.pdf", $"{prefix} STATEMENT");
        var txId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccufexDbContext>();
            var fileRecord = await db.FileRecords.IgnoreQueryFilters().FirstAsync(f => f.Id == file.FileId);

            var importResult = new TransactionImportResult
            {
                Id = Guid.NewGuid(),
                SourceFileId = file.FileId,
                ClientId = fileRecord.ClientId,
                FinancialYearId = fileRecord.FinancialYearId,
                TotalDetected = 1,
                ProcessedCount = 1,
                DetectedBank = (int)BankType.Hdfc,
                Status = "Success",
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow
            };
            db.TransactionImportResults.Add(importResult);

            var tx = new Transaction
            {
                Id = txId,
                SourceFileId = file.FileId,
                ClientId = fileRecord.ClientId,
                FinancialYearId = fileRecord.FinancialYearId,
                TransactionDate = new DateTime(2026, 4, 1),
                Description = $"{prefix} SALARY DEPOSIT",
                Debit = null,
                Credit = 50000.00m,
                Amount = 50000.00m,
                Balance = 50000.00m,
                BankCode = (int)BankType.Hdfc,
                TransactionType = "Credit",
                Reference = "DEP-12345",
                CreatedAt = DateTime.UtcNow
            };
            db.Transactions.Add(tx);
            await db.SaveChangesAsync();

            var correctionStore = scope.ServiceProvider.GetRequiredService<Accufex.Server.Validation.Services.ITransactionCorrectionStore>();
            var fakeParsingResult = new Accufex.Server.Parsing.Models.BankParsingResult
            {
                Success = true,
                BankCode = 1,
                BankName = "HDFC Bank",
                ParserVersion = "HDFC-v1",
                Transactions = [new Accufex.Server.Parsing.Models.ParsedTransaction { Id = txId, TransactionDate = tx.TransactionDate, Description = tx.Description, Credit = tx.Credit, Amount = tx.Amount, Balance = tx.Balance }]
            };
            await correctionStore.InitializeSnapshotAsync(file.FileId, fakeParsingResult, [tx], fileRecord.ClientId, fileRecord.FinancialYearId);
        }

        return (file, txId);
    }

    [Fact]
    public async Task Test13_CrossUser_GetTransactions_DeniedWith404()
    {
        var (client1, _) = await RegisterUserAsync("u1_t13");
        var (client2, _) = await RegisterUserAsync("u2_t13");

        var (fileA, _) = await SeedStatementWithTransactionsAsync(client1, "U1_TX_LIST");

        // User 2 attempts to retrieve User 1's transactions
        var crossTxResp = await client2.GetAsync($"/api/statements/{fileA.FileId}/transactions");
        Assert.Equal(HttpStatusCode.NotFound, crossTxResp.StatusCode);

        // User 1 retrieves own transactions successfully
        var ownTxResp = await client1.GetAsync($"/api/statements/{fileA.FileId}/transactions");
        Assert.Equal(HttpStatusCode.OK, ownTxResp.StatusCode);
        var respData = await ownTxResp.Content.ReadFromJsonAsync<StatementTransactionsResponse>(JsonOptions);
        Assert.NotNull(respData);
        Assert.True(respData.TotalCount >= 1);
    }

    [Fact]
    public async Task Test14_CrossUser_GetTransactionById_DeniedWith404()
    {
        var (client1, _) = await RegisterUserAsync("u1_t14");
        var (client2, _) = await RegisterUserAsync("u2_t14");

        var (fileA, txId) = await SeedStatementWithTransactionsAsync(client1, "U1_TX_SINGLE");

        // User 2 attempts to access single transaction belonging to User 1
        var crossResp = await client2.GetAsync($"/api/statements/{fileA.FileId}/transactions/{txId}");
        Assert.Equal(HttpStatusCode.NotFound, crossResp.StatusCode);

        // User 1 accesses own transaction successfully
        var ownResp = await client1.GetAsync($"/api/statements/{fileA.FileId}/transactions/{txId}");
        Assert.Equal(HttpStatusCode.OK, ownResp.StatusCode);
        var txData = await ownResp.Content.ReadFromJsonAsync<TransactionReviewDto>(JsonOptions);
        Assert.NotNull(txData);
        Assert.Equal(txId, txData.Id);
    }

    [Fact]
    public async Task Test15_CrossUser_CorrectTransaction_DeniedWith404()
    {
        var (client1, _) = await RegisterUserAsync("u1_t15");
        var (client2, _) = await RegisterUserAsync("u2_t15");

        var (fileA, txId) = await SeedStatementWithTransactionsAsync(client1, "U1_TX_MOD");

        var maliciousRequest = new CorrectTransactionRequest
        {
            TransactionDate = new DateTime(2026, 4, 1),
            Description = "HACKED TRANSACTION DESCRIPTION",
            Credit = 999999.00m,
            Amount = 999999.00m,
            Reason = "Malicious unauthorized modification"
        };

        // User 2 attempts to modify User 1's transaction
        var crossModResp = await client2.PutAsJsonAsync($"/api/statements/{fileA.FileId}/transactions/{txId}", maliciousRequest, JsonOptions);
        Assert.Equal(HttpStatusCode.NotFound, crossModResp.StatusCode);

        // Verify User 1's transaction in database was NOT altered
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccufexDbContext>();
        var originalTx = await db.Transactions.IgnoreQueryFilters().FirstAsync(t => t.Id == txId);
        Assert.NotEqual("HACKED TRANSACTION DESCRIPTION", originalTx.Description);
        Assert.Equal(50000.00m, originalTx.Amount);
    }

    [Fact]
    public async Task Test16_CrossUser_ValidationSummaryAndRevalidate_DeniedWith404()
    {
        var (client1, _) = await RegisterUserAsync("u1_t16");
        var (client2, _) = await RegisterUserAsync("u2_t16");

        var (fileA, _) = await SeedStatementWithTransactionsAsync(client1, "U1_VAL");

        // User 2 attempts to access validation summary
        var crossSummaryResp = await client2.GetAsync($"/api/statements/{fileA.FileId}/validation-summary");
        Assert.Equal(HttpStatusCode.NotFound, crossSummaryResp.StatusCode);

        // User 2 attempts to trigger revalidation
        var crossRevalResp = await client2.PostAsync($"/api/statements/{fileA.FileId}/revalidate", null);
        Assert.Equal(HttpStatusCode.NotFound, crossRevalResp.StatusCode);

        // User 1 accesses both successfully
        var ownSummaryResp = await client1.GetAsync($"/api/statements/{fileA.FileId}/validation-summary");
        Assert.Equal(HttpStatusCode.OK, ownSummaryResp.StatusCode);
    }

    [Fact]
    public async Task Test17_CrossUser_ExcelExport_DeniedWith404()
    {
        var (client1, _) = await RegisterUserAsync("u1_t17");
        var (client2, _) = await RegisterUserAsync("u2_t17");

        var (fileA, _) = await SeedStatementWithTransactionsAsync(client1, "U1_EXP");

        // User 2 attempts to export User 1's statement
        var crossExportResp = await client2.GetAsync($"/api/statements/{fileA.FileId}/export/excel");
        Assert.Equal(HttpStatusCode.NotFound, crossExportResp.StatusCode);

        // User 1 exports own statement successfully
        var ownExportResp = await client1.GetAsync($"/api/statements/{fileA.FileId}/export/excel");
        Assert.Equal(HttpStatusCode.OK, ownExportResp.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ownExportResp.Content.Headers.ContentType?.MediaType);
        var bytes = await ownExportResp.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 1000);
    }

    [Fact]
    public async Task Test18_CrossUser_ParseStatement_DeniedWith404()
    {
        var (client1, _) = await RegisterUserAsync("u1_t18");
        var (client2, _) = await RegisterUserAsync("u2_t18");

        var fileA = await UploadPdfAsync(client1, "U1_ParseTarget.pdf", "USER 1 PARSE TARGET");

        // User 2 attempts to trigger parse on User 1's statement
        var crossParseResp = await client2.PostAsync($"/api/statements/{fileA.FileId}/parse", null);
        Assert.Equal(HttpStatusCode.NotFound, crossParseResp.StatusCode);
    }

    [Fact]
    public async Task Test19_CrossUser_DeleteStatement_Blocked()
    {
        var (client1, _) = await RegisterUserAsync("u1_t19");
        var (client2, _) = await RegisterUserAsync("u2_t19");

        var fileA = await UploadPdfAsync(client1, "U1_DeleteTarget.pdf", "USER 1 DELETE TARGET");

        // User 2 attempts DELETE on User 1's statement (endpoint either doesn't exist or returns 404/405)
        var crossDeleteResp = await client2.DeleteAsync($"/api/statements/{fileA.FileId}");
        Assert.True(
            crossDeleteResp.StatusCode == HttpStatusCode.NotFound ||
            crossDeleteResp.StatusCode == HttpStatusCode.MethodNotAllowed,
            $"Expected 404 or 405 for cross-user delete, got {crossDeleteResp.StatusCode}");

        // Verify statement still safely exists in database
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccufexDbContext>();
        var exists = await db.FileRecords.IgnoreQueryFilters().AnyAsync(f => f.Id == fileA.FileId);
        Assert.True(exists, "User 1 statement must remain intact in database");
    }

    [Fact]
    public async Task Test20_CrossUser_ClientWorkspaces_StrictlyIsolated()
    {
        var (client1, auth1) = await RegisterUserAsync("u1_t20");
        var (client2, auth2) = await RegisterUserAsync("u2_t20");

        var me1 = await client1.GetFromJsonAsync<AuthResponseDto>("/api/auth/me", JsonOptions);
        var me2 = await client2.GetFromJsonAsync<AuthResponseDto>("/api/auth/me", JsonOptions);

        Assert.NotNull(me1);
        Assert.NotNull(me2);
        Assert.NotNull(me1.Workspaces);
        Assert.NotNull(me2.Workspaces);

        // User 1 workspaces contain ONLY User 1 workspace ID
        Assert.All(me1.Workspaces, w => Assert.NotEqual(auth2.ActiveWorkspace!.Id, w.Id));
        // User 2 workspaces contain ONLY User 2 workspace ID
        Assert.All(me2.Workspaces, w => Assert.NotEqual(auth1.ActiveWorkspace!.Id, w.Id));
    }

    [Fact]
    public async Task Test21_GlobalQueryFilter_DirectDbAccess_EnforcesStrictTenantIsolation()
    {
        var (client1, auth1) = await RegisterUserAsync("u1_t21");
        var (client2, auth2) = await RegisterUserAsync("u2_t21");

        var (file1, tx1Id) = await SeedStatementWithTransactionsAsync(client1, "U1_DB_TEST");
        var (file2, tx2Id) = await SeedStatementWithTransactionsAsync(client2, "U2_DB_TEST");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccufexDbContext>();

        // Set tenant context explicitly to User 2
        var user2Id = auth2.User!.Id;
        db.SetTenantUserId(user2Id);

        // Querying Clients returns ONLY User 2's clients
        var clients = await db.Clients.ToListAsync();
        Assert.Contains(clients, c => c.UserId == user2Id);
        Assert.DoesNotContain(clients, c => c.UserId == auth1.User!.Id);

        // Querying FileRecords returns ONLY User 2's files
        var files = await db.FileRecords.ToListAsync();
        Assert.Contains(files, f => f.Id == file2.FileId);
        Assert.DoesNotContain(files, f => f.Id == file1.FileId);

        // Querying Transactions returns ONLY User 2's transactions
        var transactions = await db.Transactions.ToListAsync();
        Assert.Contains(transactions, t => t.Id == tx2Id);
        Assert.DoesNotContain(transactions, t => t.Id == tx1Id);
    }

    [Fact]
    public async Task Test22_DashboardStats_ZeroLeakage_ForUnrelatedUser()
    {
        var (client1, _) = await RegisterUserAsync("u1_t22");
        var (client2, _) = await RegisterUserAsync("u2_t22");

        // User 1 uploads multiple statements with transactions
        await SeedStatementWithTransactionsAsync(client1, "U1_DASH_A");
        await SeedStatementWithTransactionsAsync(client1, "U1_DASH_B");

        // User 2 has uploaded 0 files
        var statsResp2 = await client2.GetAsync("/api/dashboard/stats");
        Assert.Equal(HttpStatusCode.OK, statsResp2.StatusCode);

        using var jsonDoc = JsonDocument.Parse(await statsResp2.Content.ReadAsStringAsync());
        var root = jsonDoc.RootElement;

        var totalStatements = root.GetProperty("totalStatements").GetInt32();
        var totalTransactions = root.GetProperty("totalTransactions").GetInt32();
        var recentStatements = root.GetProperty("recentStatements").EnumerateArray().ToList();

        Assert.Equal(0, totalStatements);
        Assert.Equal(0, totalTransactions);
        Assert.Empty(recentStatements);
    }

    [Fact]
    public async Task Test23_SameUser_FullWorkflow_Continuity()
    {
        var (client1, _) = await RegisterUserAsync("u1_t23");

        var fileA = await UploadPdfAsync(client1, "FullContinuity.pdf", "LEGITIMATE FULL FLOW");

        // Extract
        var extractResp = await client1.PostAsJsonAsync($"/api/statements/{fileA.FileId}/extract", new ExtractStatementRequest());
        Assert.Equal(HttpStatusCode.OK, extractResp.StatusCode);

        // Get Extraction
        var getExtractResp = await client1.GetAsync($"/api/statements/{fileA.FileId}/extraction");
        Assert.Equal(HttpStatusCode.OK, getExtractResp.StatusCode);

        // Download
        var downloadResp = await client1.GetAsync($"/api/statements/{fileA.FileId}/download");
        Assert.Equal(HttpStatusCode.OK, downloadResp.StatusCode);
        var bytes = await downloadResp.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 0);

        // Details
        var detailsResp = await client1.GetAsync($"/api/statements/{fileA.FileId}");
        Assert.Equal(HttpStatusCode.OK, detailsResp.StatusCode);
    }

    [Fact]
    public async Task Test24_SameUser_MultipleWorkspaces_MaintainsDistinctResources()
    {
        var (client1, auth1) = await RegisterUserAsync("u1_t24");
        var (client2, _) = await RegisterUserAsync("u2_t24");

        // Create a second workspace for User 1 in DB
        Guid secondClientId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccufexDbContext>();
            var secondClient = new Client
            {
                Id = Guid.NewGuid(),
                UserId = auth1.User!.Id,
                Name = "User1 Subsidiary Workspace",
                ContactPerson = "User 1 Manager",
                Email = "subsidiary@test.local",
                Phone = "1234567890",
                BusinessName = "User 1 Subsidiary",
                BusinessType = "Corporate",
                Address = "Branch Address",
                TaxId = "TAX9999",
                CreatedAt = DateTime.UtcNow
            };
            var secondFy = new FinancialYear
            {
                Id = Guid.NewGuid(),
                ClientId = secondClient.Id,
                DisplayName = "2026-27",
                StartDate = new DateTime(2026, 4, 1),
                EndDate = new DateTime(2027, 3, 31),
                Status = 1,
                CreatedAt = DateTime.UtcNow
            };
            db.Clients.Add(secondClient);
            db.FinancialYears.Add(secondFy);
            await db.SaveChangesAsync();
            secondClientId = secondClient.Id;
        }

        // User 1 uploads to second workspace
        var bytes = GenerateSamplePdfBytes("SUBSIDIARY STATEMENT");
        using var content = CreateMultipartPdf("sub_stmt.pdf", bytes, clientId: secondClientId);
        var uploadResp = await client1.PostAsync("/api/statements/upload", content);
        Assert.Equal(HttpStatusCode.OK, uploadResp.StatusCode);

        var uploadData = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>(JsonOptions);
        Assert.NotNull(uploadData);
        Assert.Equal(secondClientId, uploadData.ClientId);

        // User 2 cannot access this statement from second workspace
        var crossResp = await client2.GetAsync($"/api/statements/{uploadData.FileId}");
        Assert.Equal(HttpStatusCode.NotFound, crossResp.StatusCode);
    }
}
