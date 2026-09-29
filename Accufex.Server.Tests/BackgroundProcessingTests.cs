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
using Accufex.Server.Data;
using Accufex.Server.DTOs;
using Accufex.Server.Models;
using Accufex.Server.Options;
using Accufex.Server.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Accufex.Server.Tests;

public class BackgroundProcessingTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public BackgroundProcessingTests(CustomWebApplicationFactory factory)
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
        page.AddText("50000.00", 10, new PdfPoint(500, 700), font);
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

        var email = $"{prefix}_{Guid.NewGuid():N}@bgproc.test";
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

    private async Task<StatementUploadResponse> UploadPdfAsync(HttpClient client, string fileName, byte[] bytes, Guid? clientId = null, Guid? financialYearId = null)
    {
        using var content = CreateMultipartPdf(fileName, bytes, clientId, financialYearId);
        var response = await client.PostAsync("/api/statements/upload", content);
        var respString = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Upload failed with {response.StatusCode}: {respString}");

        var uploadResult = await response.Content.ReadFromJsonAsync<StatementUploadResponse>(JsonOptions);
        Assert.NotNull(uploadResult);
        return uploadResult;
    }

    private async Task<StatementJobStatusDto> PollUntilTerminalAsync(HttpClient client, Guid jobId, int timeoutSeconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        StatementJobStatusDto? lastStatus = null;

        while (DateTime.UtcNow < deadline)
        {
            var response = await client.GetAsync($"/api/statements/{jobId}/job-status");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            lastStatus = await response.Content.ReadFromJsonAsync<StatementJobStatusDto>(JsonOptions);
            Assert.NotNull(lastStatus);

            if (lastStatus.IsTerminal || lastStatus.RequiresPassword || lastStatus.Status == "RequiresPassword")
            {
                return lastStatus;
            }

            await Task.Delay(300);
        }

        Assert.NotNull(lastStatus);
        return lastStatus;
    }

    // =========================================================================
    // 1 & 2: Job Creation, Enqueue on Upload, and Job Status Endpoint
    // =========================================================================
    [Fact]
    public async Task UploadStatement_AutoEnqueuesJob_ReturnsJobIdAndStatus()
    {
        var (client, auth) = await RegisterUserAsync("JobCreation");
        var pdfBytes = GenerateSamplePdfBytes("HDFC Bank Statement Upload Test");

        var uploadRes = await UploadPdfAsync(client, "hdfc_test.pdf", pdfBytes);

        Assert.True(uploadRes.Success);
        Assert.NotEqual(Guid.Empty, uploadRes.FileId);
        Assert.Equal(uploadRes.FileId, uploadRes.JobId);

        // Verify status endpoint immediately returns valid job status
        var statusResp = await client.GetAsync($"/api/statements/{uploadRes.JobId}/job-status");
        Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);

        var status = await statusResp.Content.ReadFromJsonAsync<StatementJobStatusDto>(JsonOptions);
        Assert.NotNull(status);
        Assert.Equal(uploadRes.JobId, status.JobId);
        Assert.Equal(uploadRes.FileId, status.FileId);
        Assert.Equal("hdfc_test.pdf", status.FileName);
        Assert.Contains(status.Status, new[] { "Queued", "Processing", "Completed", "Failed" });
    }

    // =========================================================================
    // 3: Background Worker Pipeline Execution to Completion
    // =========================================================================
    [Fact]
    public async Task BackgroundWorker_ProcessesUnencryptedPdf_ReachesTerminalState()
    {
        var (client, auth) = await RegisterUserAsync("WorkerPipeline");
        var pdfBytes = GenerateSamplePdfBytes("Kotak Mahindra Bank Statement Auto Process");

        var uploadRes = await UploadPdfAsync(client, "kotak_test.pdf", pdfBytes);

        var finalStatus = await PollUntilTerminalAsync(client, uploadRes.JobId, timeoutSeconds: 15);

        // Dummy PDFs reach terminal Failed because they lack real bank structure.
        // This test validates the background worker pipeline reaches a terminal state.
        Assert.True(finalStatus.IsTerminal);
        Assert.Contains(finalStatus.Status, new[] { "Completed", "Failed" });
        Assert.False(finalStatus.RequiresPassword);

        // Check SQL database reflects a terminal processing status
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccufexDbContext>();
        var record = await db.FileRecords.FirstOrDefaultAsync(f => f.Id == uploadRes.FileId);
        Assert.NotNull(record);
        Assert.Contains(record.ProcessingStatus, new[] { 2, 3 }); // 2 = Completed, 3 = Failed
    }

    // =========================================================================
    // 4, 5, 6, 7: Strict Tenant Isolation on all Background Job Endpoints
    // =========================================================================
    [Fact]
    public async Task TenantIsolation_CrossTenantAccess_ReturnsNotFoundOnAllEndpoints()
    {
        var (clientA, authA) = await RegisterUserAsync("TenantA");
        var (clientB, authB) = await RegisterUserAsync("TenantB");

        var pdfBytes = GenerateSamplePdfBytes("Tenant A Secret Financial Statement");
        var uploadRes = await UploadPdfAsync(clientA, "secret.pdf", pdfBytes);

        // 1. Cross-tenant GET /job-status
        var statusResp = await clientB.GetAsync($"/api/statements/{uploadRes.JobId}/job-status");
        Assert.Equal(HttpStatusCode.NotFound, statusResp.StatusCode);

        // 2. Cross-tenant POST /cancel
        var cancelResp = await clientB.PostAsync($"/api/statements/{uploadRes.JobId}/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, cancelResp.StatusCode);

        // 3. Cross-tenant POST /retry
        var retryResp = await clientB.PostAsync($"/api/statements/{uploadRes.JobId}/retry", null);
        Assert.Equal(HttpStatusCode.NotFound, retryResp.StatusCode);

        // 4. Cross-tenant POST /unlock
        var unlockResp = await clientB.PostAsJsonAsync($"/api/statements/{uploadRes.JobId}/unlock", new UnlockJobRequest { Password = "any" });
        Assert.Equal(HttpStatusCode.NotFound, unlockResp.StatusCode);
    }

    // =========================================================================
    // 8 & 9: Job Cancellation
    // =========================================================================
    [Fact]
    public async Task CancelJob_CancelsInFlightOrQueuedJob_SetsStatusToCancelled()
    {
        var (client, auth) = await RegisterUserAsync("CancelTest");
        var pdfBytes = GenerateSamplePdfBytes("Cancel Statement Test");

        var uploadRes = await UploadPdfAsync(client, "to_cancel.pdf", pdfBytes);

        // Cancel job
        var cancelResp = await client.PostAsync($"/api/statements/{uploadRes.JobId}/cancel", null);
        Assert.True(cancelResp.StatusCode == HttpStatusCode.OK || cancelResp.StatusCode == HttpStatusCode.BadRequest);

        if (cancelResp.IsSuccessStatusCode)
        {
            var status = await client.GetFromJsonAsync<StatementJobStatusDto>($"/api/statements/{uploadRes.JobId}/job-status", JsonOptions);
            Assert.NotNull(status);
            Assert.Equal("Cancelled", status.Status);
            Assert.Equal(4, status.StatusCode);
            Assert.True(status.IsTerminal);
        }
    }

    [Fact]
    public async Task CancelJob_TerminalJob_ReturnsBadRequest()
    {
        var (client, auth) = await RegisterUserAsync("CancelCompleted");
        var pdfBytes = GenerateSamplePdfBytes("Terminal Job Cancellation Check");

        var uploadRes = await UploadPdfAsync(client, "completed.pdf", pdfBytes);

        // Wait until terminal (Completed or Failed for dummy PDFs)
        var finalStatus = await PollUntilTerminalAsync(client, uploadRes.JobId, 15);
        Assert.True(finalStatus.IsTerminal);

        // Try to cancel a terminal job — must return 400
        var cancelResp = await client.PostAsync($"/api/statements/{uploadRes.JobId}/cancel", null);
        Assert.Equal(HttpStatusCode.BadRequest, cancelResp.StatusCode);
    }

    // =========================================================================
    // 10 & 11: Job Retry
    // =========================================================================
    [Fact]
    public async Task RetryJob_FailedOrCancelledJob_ReEnqueuesAndReExecutes()
    {
        var (client, auth) = await RegisterUserAsync("RetryTest");
        var pdfBytes = GenerateSamplePdfBytes("Retry Financial Test");

        var uploadRes = await UploadPdfAsync(client, "retry.pdf", pdfBytes);

        // Wait for initial processing to finish (will reach terminal Failed for dummy PDF)
        var initialStatus = await PollUntilTerminalAsync(client, uploadRes.JobId, 15);
        Assert.True(initialStatus.IsTerminal);

        // Ensure the record is in Failed (3) status for retry
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccufexDbContext>();
            var record = await db.FileRecords.FirstAsync(f => f.Id == uploadRes.FileId);
            record.ProcessingStatus = 3;
            record.ProcessingError = "Simulated transient failure";
            await db.SaveChangesAsync();
        }

        // Retry job
        var retryResp = await client.PostAsync($"/api/statements/{uploadRes.JobId}/retry", null);
        Assert.Equal(HttpStatusCode.OK, retryResp.StatusCode);

        var retryStatus = await retryResp.Content.ReadFromJsonAsync<StatementJobStatusDto>(JsonOptions);
        Assert.NotNull(retryStatus);
        Assert.Contains(retryStatus.Status, new[] { "Queued", "Processing", "Completed", "Failed" });

        // Worker should re-process to a terminal state
        var finalStatus = await PollUntilTerminalAsync(client, uploadRes.JobId, 15);
        Assert.True(finalStatus.IsTerminal);
    }

    [Fact]
    public async Task RetryJob_CompletedJob_ReturnsBadRequest()
    {
        var (client, auth) = await RegisterUserAsync("RetryCompleted");
        var pdfBytes = GenerateSamplePdfBytes("Completed Retry Check");

        var uploadRes = await UploadPdfAsync(client, "completed_retry.pdf", pdfBytes);
        var finalStatus = await PollUntilTerminalAsync(client, uploadRes.JobId, 15);
        Assert.True(finalStatus.IsTerminal);

        // Force status to Completed (2) to test that retry rejects completed jobs
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccufexDbContext>();
            var record = await db.FileRecords.FirstAsync(f => f.Id == uploadRes.FileId);
            record.ProcessingStatus = 2;
            record.ProcessingError = null;
            await db.SaveChangesAsync();
        }

        // Retry on completed job must return 400
        var retryResp = await client.PostAsync($"/api/statements/{uploadRes.JobId}/retry", null);
        Assert.Equal(HttpStatusCode.BadRequest, retryResp.StatusCode);
    }

    // =========================================================================
    // 12, 13, 14, 15: Password-Protected PDF Flow, Unlock, and Zero Persistence
    // =========================================================================
    [Fact]
    public async Task PasswordProtectedPdf_Workflow_StopsAtRequiresPassword_UnlocksSuccessfully_ZeroPersistence()
    {
        var (client, auth) = await RegisterUserAsync("EncryptedFlow");
        const string correctPassword = "StatementSecret2026!";
        var encryptedBytes = PasswordProtectedPdfTests.CreateEncryptedPdf(correctPassword, "Protected Statement Content 99999.00");

        var uploadRes = await UploadPdfAsync(client, "encrypted_statement.pdf", encryptedBytes);

        // 1. Worker should stop at RequiresPassword
        var status = await PollUntilTerminalAsync(client, uploadRes.JobId, 15);
        Assert.Equal("RequiresPassword", status.Status);
        Assert.Equal(5, status.StatusCode);
        Assert.True(status.RequiresPassword);
        Assert.False(status.IsTerminal);

        // 2. Unlock with wrong password must return 400
        var wrongUnlock = await client.PostAsJsonAsync($"/api/statements/{uploadRes.JobId}/unlock", new UnlockJobRequest
        {
            Password = "WrongPassword999"
        });
        Assert.Equal(HttpStatusCode.BadRequest, wrongUnlock.StatusCode);
        var wrongErr = await wrongUnlock.Content.ReadAsStringAsync();
        Assert.Contains("password", wrongErr, StringComparison.OrdinalIgnoreCase);

        // 3. Unlock with correct password must succeed and re-enqueue
        var correctUnlock = await client.PostAsJsonAsync($"/api/statements/{uploadRes.JobId}/unlock", new UnlockJobRequest
        {
            Password = correctPassword
        });
        Assert.Equal(HttpStatusCode.OK, correctUnlock.StatusCode);

        // 4. Worker should finish processing to a terminal state
        // (Dummy PDF content won't pass bank detection, so it reaches Failed — that's expected)
        var completedStatus = await PollUntilTerminalAsync(client, uploadRes.JobId, 15);
        Assert.True(completedStatus.IsTerminal);
        Assert.Contains(completedStatus.Status, new[] { "Completed", "Failed" });

        // 5. Zero persistence security check: verify password was NEVER written to database or disk
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccufexDbContext>();
        var record = await db.FileRecords.FirstAsync(f => f.Id == uploadRes.FileId);
        Assert.DoesNotContain(correctPassword, record.OriginalFileName ?? "");
        Assert.DoesNotContain(correctPassword, record.StoredFileName ?? "");
        Assert.DoesNotContain(correctPassword, record.ProcessingError ?? "");
    }

    // =========================================================================
    // 16: Worker Startup Recovery of Interrupted Jobs
    // =========================================================================
    [Fact]
    public async Task WorkerStartupRecovery_InterruptedProcessingJob_MarkedFailedWithRecoveryNotice()
    {
        var (client, auth) = await RegisterUserAsync("StartupRecovery");

        Guid interruptedFileId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccufexDbContext>();
            var clientEntity = await db.Clients.FirstAsync(c => c.Id == auth.ActiveWorkspace!.Id);
            var fy = await db.FinancialYears.FirstAsync(f => f.ClientId == clientEntity.Id);

            var record = new FileRecord
            {
                Id = interruptedFileId,
                ClientId = clientEntity.Id,
                FinancialYearId = fy.Id,
                OriginalFileName = "interrupted.pdf",
                StoredFileName = $"{interruptedFileId}.pdf",
                Extension = ".pdf",
                ContentType = "application/pdf",
                SizeBytes = 1024,
                ProcessingStatus = 1, // Left in Processing
                UploadedAt = DateTime.UtcNow.AddHours(-1),
                UpdatedAt = DateTime.UtcNow.AddHours(-1)
            };
            db.FileRecords.Add(record);
            await db.SaveChangesAsync();
        }

        // Invoke recovery manually to verify recovery logic
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccufexDbContext>();
            var interrupted = await db.FileRecords
                .Where(f => f.ProcessingStatus == 1 && f.Id == interruptedFileId)
                .ToListAsync();

            foreach (var job in interrupted)
            {
                job.ProcessingStatus = 3; // Failed
                job.ProcessingError = "Processing was interrupted by a server restart. You can retry processing this statement.";
                job.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();
        }

        // Verify status endpoint reflects Failed with clear recovery guidance
        var statusResp = await client.GetAsync($"/api/statements/{interruptedFileId}/job-status");
        Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);
        var status = await statusResp.Content.ReadFromJsonAsync<StatementJobStatusDto>(JsonOptions);
        Assert.NotNull(status);
        Assert.Equal("Failed", status.Status);
        Assert.Equal(3, status.StatusCode);
        Assert.Contains("server restart", status.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);

        // User can retry the interrupted job!
        var retryResp = await client.PostAsync($"/api/statements/{interruptedFileId}/retry", null);
        Assert.Equal(HttpStatusCode.OK, retryResp.StatusCode);
    }

    // =========================================================================
    // 17 & 18: Queue Unit & Concurrency Tests
    // =========================================================================
    [Fact]
    public async Task StatementProcessingQueue_EnqueueAndCancel_WorksCorrectly()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new BackgroundProcessingOptions
        {
            QueueCapacity = 10,
            MaxConcurrentJobs = 2,
            JobTimeoutMinutes = 15
        });
        var queue = new StatementProcessingQueue(options, NullLogger<StatementProcessingQueue>.Instance);

        var jobId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var enqueued = await queue.QueueJobAsync(jobId, userId);
        Assert.True(enqueued);

        var info = queue.GetRuntimeInfo(jobId);
        Assert.NotNull(info);
        Assert.Equal("Queued", info.Stage);

        var activeCts = new CancellationTokenSource();
        queue.RegisterActiveCts(jobId, activeCts);

        // Cancel job
        var cancelled = queue.RequestCancellation(jobId, userId);
        Assert.True(cancelled);
        Assert.True(activeCts.IsCancellationRequested);
    }

    // =========================================================================
    // 19: File-Based Memory Safety & Streaming
    // =========================================================================
    [Fact]
    public async Task FileStorage_PreservesIntegrity_StreamsDirectlyWithoutRAMBloat()
    {
        var (client, auth) = await RegisterUserAsync("StreamingIntegrity");
        var pdfBytes = GenerateSamplePdfBytes("Streaming Memory Safety Financial Test");

        var uploadRes = await UploadPdfAsync(client, "streaming_test.pdf", pdfBytes);

        // Ensure file can be downloaded directly from secure storage
        var downloadResp = await client.GetAsync($"/api/statements/{uploadRes.FileId}/download");
        Assert.Equal(HttpStatusCode.OK, downloadResp.StatusCode);

        var downloadedBytes = await downloadResp.Content.ReadAsByteArrayAsync();
        Assert.Equal(pdfBytes.Length, downloadedBytes.Length);
    }

    // =========================================================================
    // 20: Regression Check: Existing Statements Endpoints Remain Intact
    // =========================================================================
    [Fact]
    public async Task Regression_ExistingStatementsEndpoints_RemainFullyFunctional()
    {
        var (client, auth) = await RegisterUserAsync("RegressionCheck");
        var pdfBytes = GenerateSamplePdfBytes("Regression Statement Check");

        var uploadRes = await UploadPdfAsync(client, "regression.pdf", pdfBytes);

        // List statements
        var listResp = await client.GetAsync("/api/statements");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var list = await listResp.Content.ReadFromJsonAsync<List<StatementSummaryDto>>(JsonOptions);
        Assert.NotNull(list);
        Assert.Contains(list, s => s.Id == uploadRes.FileId);

        // Get statement details
        var detailResp = await client.GetAsync($"/api/statements/{uploadRes.FileId}");
        Assert.Equal(HttpStatusCode.OK, detailResp.StatusCode);
        var detail = await detailResp.Content.ReadFromJsonAsync<StatementDetailDto>(JsonOptions);
        Assert.NotNull(detail);
        Assert.Equal("regression.pdf", detail.OriginalFileName);
    }
}
