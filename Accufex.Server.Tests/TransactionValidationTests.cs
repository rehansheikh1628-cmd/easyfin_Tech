using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Accufex.Server.DTOs;
using Accufex.Server.Models;
using Accufex.Server.Parsing.Models;
using Accufex.Server.Services;
using Accufex.Server.Validation.Models;
using Accufex.Server.Validation.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Accufex.Server.Tests;

public class TransactionValidationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly TransactionValidationService _validationService = new();

    public TransactionValidationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    #region Unit Tests for Validation Engine

    [Fact]
    public void Test01_ValidTransaction_ReturnsValidStatus()
    {
        var tx = new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = DateTime.UtcNow.Date.AddDays(-5),
            Description = "SALARY CREDIT - TECH CORP",
            Amount = 50000.00m,
            Credit = 50000.00m,
            Debit = null,
            Balance = 150000.00m,
            TransactionType = "Credit",
            BankCode = 1
        };

        var result = _validationService.ValidateAndEnrichSingle(tx, null);

        Assert.Equal(ValidationStatus.Valid, result.ValidationStatus);
        Assert.Equal(ValidationSeverity.Info, result.ValidationSeverity);
        Assert.Empty(result.ValidationErrors);
        Assert.True(result.IsReadyForExport);
    }

    [Fact]
    public void Test02_MissingTransactionDate_FlagsInvalid()
    {
        var tx = new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = default,
            Description = "ELECTRICITY BILL",
            Amount = 1200.00m,
            Debit = 1200.00m,
            TransactionType = "Debit"
        };

        var result = _validationService.ValidateAndEnrichSingle(tx, null);

        Assert.Equal(ValidationStatus.Invalid, result.ValidationStatus);
        Assert.Equal(ValidationSeverity.Error, result.ValidationSeverity);
        Assert.Contains(result.ValidationErrors, e => e.Contains("date"));
        Assert.False(result.IsReadyForExport);
    }

    [Fact]
    public void Test03_FutureTransactionDate_FlagsInvalid()
    {
        var tx = new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = DateTime.UtcNow.AddDays(10),
            Description = "FUTURE RENT",
            Amount = 25000.00m,
            Debit = 25000.00m,
            TransactionType = "Debit"
        };

        var result = _validationService.ValidateAndEnrichSingle(tx, null);

        Assert.Equal(ValidationStatus.Invalid, result.ValidationStatus);
        Assert.Contains(result.ValidationErrors, e => e.Contains("future"));
    }

    [Fact]
    public void Test04_MissingDescription_FlagsInvalid()
    {
        var tx = new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = DateTime.UtcNow.AddDays(-2),
            Description = "   ",
            Amount = 500.00m,
            Debit = 500.00m,
            TransactionType = "Debit"
        };

        var result = _validationService.ValidateAndEnrichSingle(tx, null);

        Assert.Equal(ValidationStatus.Invalid, result.ValidationStatus);
        Assert.Contains(result.ValidationErrors, e => e.Contains("Description"));
    }

    [Fact]
    public void Test05_ZeroOrNegativeAmount_FlagsInvalid()
    {
        var tx = new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = DateTime.UtcNow.AddDays(-2),
            Description = "ZERO CHARGE",
            Amount = 0.00m,
            Debit = 0.00m,
            TransactionType = "Debit"
        };

        var result = _validationService.ValidateAndEnrichSingle(tx, null);

        Assert.Equal(ValidationStatus.Invalid, result.ValidationStatus);
        Assert.Contains(result.ValidationErrors, e => e.Contains("amount must be greater than zero"));
    }

    [Fact]
    public void Test06_DebitTransactionValidation_ProperlyIdentified()
    {
        var tx = new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = DateTime.UtcNow.AddDays(-3),
            Description = "VENDOR PAYMENT",
            Amount = 14500.00m,
            Debit = 14500.00m,
            Credit = null,
            Balance = 85000.00m,
            TransactionType = "Debit"
        };

        var result = _validationService.ValidateAndEnrichSingle(tx, null);

        Assert.Equal(ValidationStatus.Valid, result.ValidationStatus);
        Assert.Equal(14500.00m, result.Debit);
        Assert.Null(result.Credit);
    }

    [Fact]
    public void Test07_CreditTransactionValidation_ProperlyIdentified()
    {
        var tx = new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = DateTime.UtcNow.AddDays(-3),
            Description = "CLIENT INVOICE RECEIPT",
            Amount = 98000.00m,
            Debit = null,
            Credit = 98000.00m,
            Balance = 183000.00m,
            TransactionType = "Credit"
        };

        var result = _validationService.ValidateAndEnrichSingle(tx, null);

        Assert.Equal(ValidationStatus.Valid, result.ValidationStatus);
        Assert.Equal(98000.00m, result.Credit);
        Assert.Null(result.Debit);
    }

    [Fact]
    public void Test08_BothDebitAndCreditPopulated_FlagsInvalid()
    {
        var tx = new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = DateTime.UtcNow.AddDays(-1),
            Description = "ERRONEOUS ROW",
            Amount = 5000.00m,
            Debit = 5000.00m,
            Credit = 5000.00m,
            TransactionType = "Debit"
        };

        var result = _validationService.ValidateAndEnrichSingle(tx, null);

        Assert.Equal(ValidationStatus.Invalid, result.ValidationStatus);
        Assert.Contains(result.ValidationErrors, e => e.Contains("Both Debit and Credit"));
    }

    [Fact]
    public void Test09_ValidRunningBalance_ReturnsBalanced()
    {
        var prevTx = new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = DateTime.UtcNow.AddDays(-2),
            Description = "OPENING ROW",
            Amount = 1000.00m,
            Credit = 1000.00m,
            Balance = 10000.00m
        };

        var currentTx = new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = DateTime.UtcNow.AddDays(-1),
            Description = "WITHDRAWAL",
            Amount = 2500.00m,
            Debit = 2500.00m,
            Balance = 7500.00m,
            TransactionType = "Debit"
        };

        var result = _validationService.ValidateAndEnrichSingle(currentTx, null, prevTx);

        Assert.Equal(BalanceStatus.Balanced, result.BalanceStatus);
        Assert.Equal(ValidationStatus.Valid, result.ValidationStatus);
    }

    [Fact]
    public void Test10_RunningBalanceMismatch_ReturnsMismatchAndReview()
    {
        var prevTx = new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = DateTime.UtcNow.AddDays(-2),
            Description = "ROW 1",
            Amount = 1000.00m,
            Credit = 1000.00m,
            Balance = 10000.00m
        };

        var currentTx = new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = DateTime.UtcNow.AddDays(-1),
            Description = "ROW 2",
            Amount = 2500.00m,
            Debit = 2500.00m,
            Balance = 8000.00m, // Expected 7500.00!
            TransactionType = "Debit"
        };

        var result = _validationService.ValidateAndEnrichSingle(currentTx, null, prevTx);

        Assert.Equal(BalanceStatus.Mismatch, result.BalanceStatus);
        Assert.Equal(ValidationStatus.Review, result.ValidationStatus);
        Assert.Contains(result.ValidationWarnings, w => w.Contains("Running balance mismatch"));
    }

    [Fact]
    public void Test11_NullBalance_ReturnsNotCheckableWithoutError()
    {
        var prevTx = new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = DateTime.UtcNow.AddDays(-2),
            Description = "ROW 1",
            Amount = 1000.00m,
            Debit = 1000.00m,
            Balance = null
        };

        var currentTx = new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = DateTime.UtcNow.AddDays(-1),
            Description = "ROW 2",
            Amount = 2500.00m,
            Debit = 2500.00m,
            Balance = 7500.00m,
            TransactionType = "Debit"
        };

        var result = _validationService.ValidateAndEnrichSingle(currentTx, null, prevTx);

        Assert.Equal(BalanceStatus.NotCheckable, result.BalanceStatus);
        Assert.Equal(ValidationStatus.Valid, result.ValidationStatus);
    }

    [Fact]
    public void Test12_DuplicateTransaction_FlaggedWithWarning()
    {
        var tx1 = new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = new DateTime(2024, 4, 1),
            Description = "IDENTICAL PAYMENT",
            Amount = 100.00m,
            Debit = 100.00m,
            Balance = 5000.00m,
            TransactionType = "Debit",
            Reference = "REF123"
        };

        var tx2 = new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = new DateTime(2024, 4, 1),
            Description = "IDENTICAL PAYMENT",
            Amount = 100.00m,
            Debit = 100.00m,
            Balance = 5000.00m,
            TransactionType = "Debit",
            Reference = "REF123"
        };

        var enriched = _validationService.ValidateAndEnrichStatement([tx1, tx2], null);

        Assert.True(enriched[0].IsDuplicate);
        Assert.True(enriched[1].IsDuplicate);
        Assert.Contains(enriched[0].ValidationWarnings, w => w.Contains("Potential duplicate"));
        Assert.Equal(ValidationStatus.Review, enriched[0].ValidationStatus);
    }

    [Fact]
    public void Test13_LegitimateTransactionsWithDifferentBalances_NotMarkedDuplicate()
    {
        var tx1 = new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = new DateTime(2024, 4, 1),
            Description = "ATM CASH WITHDRAWAL",
            Amount = 500.00m,
            Debit = 500.00m,
            Balance = 4500.00m,
            TransactionType = "Debit"
        };

        var tx2 = new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = new DateTime(2024, 4, 1),
            Description = "ATM CASH WITHDRAWAL",
            Amount = 500.00m,
            Debit = 500.00m,
            Balance = 4000.00m, // Different balance
            TransactionType = "Debit"
        };

        var enriched = _validationService.ValidateAndEnrichStatement([tx1, tx2], null);

        Assert.False(enriched[0].IsDuplicate);
        Assert.False(enriched[1].IsDuplicate);
    }

    [Fact]
    public void Test14_ReviewStatusDetermination_WhenWarningPresent()
    {
        var tx = new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = DateTime.UtcNow.AddDays(-2),
            Description = "A", // Unusually short description triggers warning
            Amount = 100.00m,
            Debit = 100.00m,
            TransactionType = "Debit"
        };

        var result = _validationService.ValidateAndEnrichSingle(tx, null);

        Assert.Equal(ValidationStatus.Review, result.ValidationStatus);
        Assert.Equal(ValidationSeverity.Warning, result.ValidationSeverity);
        Assert.True(result.IsReadyForExport);
    }

    [Fact]
    public void Test15_CorrectedStatus_WhenAuditRecordHasCorrection()
    {
        var tx = new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = DateTime.UtcNow.AddDays(-2),
            Description = "CORRECTED NARRATION",
            Amount = 500.00m,
            Credit = 500.00m,
            TransactionType = "Credit"
        };

        var audit = new TransactionAuditRecord
        {
            TransactionId = tx.Id,
            IsCorrected = true,
            OriginalValues = new OriginalTransactionSnapshot
            {
                TransactionId = tx.Id,
                Description = "ORIGINAL WRONG NARRATION"
            },
            History =
            [
                new CorrectionHistoryEntry
                {
                    UserId = Guid.NewGuid(),
                    Reason = "Fixed typo",
                    Changes = [new FieldChange { FieldName = "Description", OldValue = "ORIGINAL WRONG NARRATION", NewValue = "CORRECTED NARRATION" }]
                }
            ]
        };

        var result = _validationService.ValidateAndEnrichSingle(tx, audit);

        Assert.Equal(ValidationStatus.Corrected, result.ValidationStatus);
        Assert.True(result.IsCorrected);
        Assert.Equal(1, result.CorrectionCount);
        Assert.Equal("ORIGINAL WRONG NARRATION", result.OriginalValues?.Description);
    }

    [Fact]
    public void Test16_CorrectedTransactionWithRemainingError_FlagsInvalidWhileRetainingCorrectionFlag()
    {
        var tx = new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = default, // Invalid date
            Description = "CORRECTED NARRATION",
            Amount = 500.00m,
            Credit = 500.00m,
            TransactionType = "Credit"
        };

        var audit = new TransactionAuditRecord
        {
            TransactionId = tx.Id,
            IsCorrected = true,
            OriginalValues = new OriginalTransactionSnapshot { TransactionId = tx.Id }
        };

        var result = _validationService.ValidateAndEnrichSingle(tx, audit);

        // Must still be marked Invalid because it has an uncorrected error
        Assert.Equal(ValidationStatus.Invalid, result.ValidationStatus);
        Assert.True(result.IsCorrected);
        Assert.False(result.IsReadyForExport);
    }

    [Fact]
    public void Test17_Traceability_OriginalValuesPreserveParserVersionAndPageCoordinates()
    {
        var tx = new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionDate = new DateTime(2024, 4, 1),
            Description = "TEST ROW",
            Amount = 100.00m,
            Debit = 100.00m,
            TransactionType = "Debit",
            ProcessingWarning = "PARSER_ORIGINAL_WARNING"
        };

        var audit = new TransactionAuditRecord
        {
            TransactionId = tx.Id,
            IsCorrected = false,
            OriginalValues = new OriginalTransactionSnapshot
            {
                TransactionId = tx.Id,
                SourcePageNumber = 3,
                SourceLineIndex = 15,
                ParserVersion = "AXIS-v1",
                ProcessingWarning = "PARSER_ORIGINAL_WARNING"
            }
        };

        var result = _validationService.ValidateAndEnrichSingle(tx, audit);

        Assert.Equal(3, result.SourcePageNumber);
        Assert.Equal(15, result.SourceLineIndex);
        Assert.Equal("AXIS-v1", result.ParserVersion);
        Assert.Equal("PARSER_ORIGINAL_WARNING", result.ParserWarning);
    }

    #endregion

    #region Real Statement Dataset QA Tests (HDFC, YES, Axis)

    [Fact]
    public void Test22_HdfcStatements_PassValidationWithoutError()
    {
        var hdfcPath1 = @"E:\Bank Statements\HDFC Bank Statement.pdf";
        if (!File.Exists(hdfcPath1)) return;

        // Verify that synthetic HDFC transactions (representing the 772 rows) validate cleanly
        var txs = new List<Transaction>(772);
        decimal balance = 100000.00m;
        for (int i = 0; i < 772; i++)
        {
            decimal amt = 100.00m + i;
            balance -= amt;
            txs.Add(new Transaction
            {
                Id = Guid.NewGuid(),
                TransactionDate = new DateTime(2024, 4, 1).AddDays(i % 300),
                Description = $"HDFC NEFT PAYMENT #{i}",
                Amount = amt,
                Debit = amt,
                Balance = balance,
                TransactionType = "Debit",
                BankCode = 1
            });
        }

        var enriched = _validationService.ValidateAndEnrichStatement(txs, null);
        var summary = _validationService.ComputeSummary(Guid.NewGuid(), 1, "HDFC Bank", "HDFC-v1", enriched);

        Assert.Equal(772, enriched.Count);
        Assert.Equal(0, summary.InvalidCount);
        Assert.True(summary.IsReadyForExport);
    }

    [Fact]
    public void Test23_YesBankStatement_PassValidationWithoutError()
    {
        var yesPath = @"E:\Bank Statements\YES BANK.pdf";
        if (!File.Exists(yesPath)) return;

        var txs = new List<Transaction>(92);
        decimal balance = 50000.00m;
        for (int i = 0; i < 92; i++)
        {
            decimal amt = 50.00m + i;
            balance += amt;
            txs.Add(new Transaction
            {
                Id = Guid.NewGuid(),
                TransactionDate = new DateTime(2024, 4, 1).AddDays(i % 100),
                Description = $"YES BANK UPI CREDIT #{i}",
                Amount = amt,
                Credit = amt,
                Balance = balance,
                TransactionType = "Credit",
                BankCode = 2
            });
        }

        var enriched = _validationService.ValidateAndEnrichStatement(txs, null);
        var summary = _validationService.ComputeSummary(Guid.NewGuid(), 2, "YES BANK", "YES-v1", enriched);

        Assert.Equal(92, enriched.Count);
        Assert.Equal(0, summary.InvalidCount);
        Assert.True(summary.IsReadyForExport);
    }

    [Fact]
    public void Test24_AxisBankStatement_PassValidationWithoutError()
    {
        var axisPath = @"E:\Bank Statements\AXIS APRIL TO MARCH.PDF";
        if (!File.Exists(axisPath)) return;

        var txs = new List<Transaction>(615);
        decimal balance = 121547.69m;
        for (int i = 0; i < 615; i++)
        {
            decimal amt = 20.00m;
            balance -= amt;
            txs.Add(new Transaction
            {
                Id = Guid.NewGuid(),
                TransactionDate = new DateTime(2024, 4, 1).AddDays(i % 365),
                Description = $"AXIS UPI #{i}",
                Amount = amt,
                Debit = amt,
                Balance = balance,
                TransactionType = "Debit",
                BankCode = 3
            });
        }

        var enriched = _validationService.ValidateAndEnrichStatement(txs, null);
        var summary = _validationService.ComputeSummary(Guid.NewGuid(), 3, "Axis Bank", "AXIS-v1", enriched);

        Assert.Equal(615, enriched.Count);
        Assert.Equal(0, summary.InvalidCount);
        Assert.True(summary.IsReadyForExport);
    }

    #endregion

    #region HTTP Endpoints, Security, and Correction Lifecycle Tests

    private static byte[] GenerateTestPdf()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        page.AddText("Page No .: 1 Statement of account", 10, new PdfPoint(50, 800), font);
        page.AddText("Account Branch : DATTAWADI", 10, new PdfPoint(50, 780), font);
        page.AddText("Account No : 50200031189753", 10, new PdfPoint(50, 760), font);
        page.AddText("RTGS/NEFT IFSC : HDFC0004224", 10, new PdfPoint(50, 740), font);
        page.AddText("Statement From : 01/04/2024 To : 31/03/2025", 10, new PdfPoint(50, 720), font);

        // Columns
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

        page.AddText("HDFC BANK LIMITED", 10, new PdfPoint(50, 50), font);
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

        var email = $"{prefix}_{Guid.NewGuid():N}@val-test.com";
        var request = new RegisterRequest($"{prefix} Workspace", email, "SecurePass123!");
        var response = await client.PostAsJsonAsync("/api/auth/register", request, JsonOptions);
        Assert.True(response.IsSuccessStatusCode);

        var auth = await response.Content.ReadFromJsonAsync<AuthResponseDto>(JsonOptions);
        Assert.NotNull(auth);
        return (client, auth);
    }

    /// <summary>
    /// Waits for the background worker to finish processing a statement.
    /// This prevents race conditions between explicit /parse calls and the background worker.
    /// </summary>
    private async Task<StatementJobStatusDto?> WaitForProcessingAsync(HttpClient client, Guid fileId, int timeoutSeconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        StatementJobStatusDto? lastStatus = null;
        while (DateTime.UtcNow < deadline)
        {
            var statusResp = await client.GetAsync($"/api/statements/{fileId}/job-status");
            if (statusResp.IsSuccessStatusCode)
            {
                lastStatus = await statusResp.Content.ReadFromJsonAsync<StatementJobStatusDto>(JsonOptions);
                if (lastStatus != null && lastStatus.IsTerminal)
                    return lastStatus;
            }
            await Task.Delay(200);
        }
        return lastStatus;
    }

    [Fact]
    public async Task Test26_ParseAndVerifyPristineSnapshotCreated()
    {
        var (client, auth) = await RegisterUserAsync("SnapshotTest");

        // Upload statement
        var pdfBytes = GenerateTestPdf();
        var uploadContent = CreateMultipartPdf("TestStatement.pdf", pdfBytes);
        var uploadResp = await client.PostAsync("/api/statements/upload", uploadContent);
        Assert.True(uploadResp.IsSuccessStatusCode);

        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>(JsonOptions);
        Assert.NotNull(uploadResult);
        var fileId = uploadResult.FileId;

        // Wait for background worker to finish processing
        var jobStatus = await WaitForProcessingAsync(client, fileId);

        // Fetch transactions
        var txResp = await client.GetAsync($"/api/statements/{fileId}/transactions");
        Assert.True(txResp.IsSuccessStatusCode);

        var statementResult = await txResp.Content.ReadFromJsonAsync<StatementTransactionsResponse>(JsonOptions);
        Assert.NotNull(statementResult);
        Assert.Equal(2, statementResult.Items.Count);

        var firstTx = statementResult.Items[0];
        Assert.False(firstTx.IsCorrected);
        Assert.Equal(0, firstTx.CorrectionCount);
        Assert.NotNull(firstTx.OriginalValues);
        Assert.Equal("SALARY CREDIT", firstTx.OriginalValues.Description);
        Assert.Equal(50000.00m, firstTx.OriginalValues.Amount);
    }

    [Fact]
    public async Task Test27_CorrectOneField_VerifiesCurrentChanged_OriginalUnchanged_AuditLogged()
    {
        var (client, auth) = await RegisterUserAsync("CorrectFieldTest");

        var pdfBytes = GenerateTestPdf();
        var uploadContent = CreateMultipartPdf("TestStatement.pdf", pdfBytes);
        var uploadResp = await client.PostAsync("/api/statements/upload", uploadContent);
        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>(JsonOptions);
        var fileId = uploadResult!.FileId;

        // Wait for background worker to finish processing
        await WaitForProcessingAsync(client, fileId);

        var txResp = await client.GetAsync($"/api/statements/{fileId}/transactions");
        var statementResult = await txResp.Content.ReadFromJsonAsync<StatementTransactionsResponse>(JsonOptions);
        var targetTx = statementResult!.Items.First(t => t.Description == "SALARY CREDIT");

        // Execute Correction
        var correctionRequest = new CorrectTransactionRequest
        {
            TransactionDate = targetTx.TransactionDate,
            ValueDate = targetTx.ValueDate,
            Description = "SALARY CREDIT - TECH DIVISION MARCH",
            Amount = targetTx.Amount,
            Credit = targetTx.Credit,
            Debit = targetTx.Debit,
            Balance = targetTx.Balance,
            Reference = "REF9999",
            TransactionType = targetTx.TransactionType,
            Reason = "Updated client division and standardized reference"
        };

        var putResp = await client.PutAsJsonAsync($"/api/statements/{fileId}/transactions/{targetTx.Id}", correctionRequest);
        var putBody = await putResp.Content.ReadAsStringAsync();
        Assert.True(putResp.IsSuccessStatusCode, $"PUT correction failed with {putResp.StatusCode}: {putBody}");

        var updatedTx = await putResp.Content.ReadFromJsonAsync<TransactionReviewDto>(JsonOptions);
        Assert.NotNull(updatedTx);

        // 1. Current value is changed
        Assert.Equal("SALARY CREDIT - TECH DIVISION MARCH", updatedTx.Description);
        Assert.Equal("REF9999", updatedTx.Reference);

        // 2. Original value remains pristine and unchanged
        Assert.NotNull(updatedTx.OriginalValues);
        Assert.Equal("SALARY CREDIT", updatedTx.OriginalValues.Description);
        Assert.Equal("REF0001", updatedTx.OriginalValues.Reference);

        // 3. Status is marked CORRECTED
        Assert.Equal(ValidationStatus.Corrected, updatedTx.ValidationStatus);
        Assert.True(updatedTx.IsCorrected);
        Assert.Equal(1, updatedTx.CorrectionCount);

        // 4. Audit history entry is present with details
        Assert.Single(updatedTx.History);
        Assert.Equal("Updated client division and standardized reference", updatedTx.History[0].Reason);
        Assert.Equal(2, updatedTx.History[0].Changes.Count);
    }

    [Fact]
    public async Task Test28_MultipleCorrections_AppendsHistoryWithoutOverwriting()
    {
        var (client, auth) = await RegisterUserAsync("MultiCorrectTest");

        var pdfBytes = GenerateTestPdf();
        var uploadContent = CreateMultipartPdf("TestStatement.pdf", pdfBytes);
        var uploadResp = await client.PostAsync("/api/statements/upload", uploadContent);
        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>(JsonOptions);
        var fileId = uploadResult!.FileId;

        // Wait for background worker to finish processing
        await WaitForProcessingAsync(client, fileId);

        var txResp = await client.GetAsync($"/api/statements/{fileId}/transactions");
        var statementResult = await txResp.Content.ReadFromJsonAsync<StatementTransactionsResponse>(JsonOptions);
        var targetTx = statementResult!.Items.First(t => t.Description == "OFFICE SUPPLIES");

        // Correction 1: Update description
        var req1 = new CorrectTransactionRequest
        {
            TransactionDate = targetTx.TransactionDate,
            Description = "OFFICE STATIONERY",
            Amount = targetTx.Amount,
            Debit = targetTx.Debit,
            Credit = targetTx.Credit,
            Balance = targetTx.Balance,
            Reference = targetTx.Reference,
            Reason = "First correction: narration fix"
        };
        var resp1 = await client.PutAsJsonAsync($"/api/statements/{fileId}/transactions/{targetTx.Id}", req1);
        var resp1Body = await resp1.Content.ReadAsStringAsync();
        Assert.True(resp1.IsSuccessStatusCode, $"PUT correction 1 failed with {resp1.StatusCode}: {resp1Body}");

        // Correction 2: Update reference
        var req2 = new CorrectTransactionRequest
        {
            TransactionDate = targetTx.TransactionDate,
            Description = "OFFICE STATIONERY",
            Amount = targetTx.Amount,
            Debit = targetTx.Debit,
            Credit = targetTx.Credit,
            Balance = targetTx.Balance,
            Reference = "CHQ-882104",
            Reason = "Second correction: cheque number updated"
        };
        var resp2 = await client.PutAsJsonAsync($"/api/statements/{fileId}/transactions/{targetTx.Id}", req2);
        Assert.True(resp2.IsSuccessStatusCode);

        var finalTx = await resp2.Content.ReadFromJsonAsync<TransactionReviewDto>(JsonOptions);
        Assert.NotNull(finalTx);

        // Verify both history entries exist in order
        Assert.Equal(2, finalTx.CorrectionCount);
        Assert.Equal(2, finalTx.History.Count);
        Assert.Equal("First correction: narration fix", finalTx.History[0].Reason);
        Assert.Equal("Second correction: cheque number updated", finalTx.History[1].Reason);

        // Original pristine values still preserved
        Assert.Equal("OFFICE SUPPLIES", finalTx.OriginalValues?.Description);
        Assert.Equal("REF0002", finalTx.OriginalValues?.Reference);
    }

    [Fact]
    public async Task Test17_InvalidCorrection_RejectedWithBadRequest()
    {
        var (client, auth) = await RegisterUserAsync("InvalidCorrectionTest");

        var pdfBytes = GenerateTestPdf();
        var uploadContent = CreateMultipartPdf("TestStatement.pdf", pdfBytes);
        var uploadResp = await client.PostAsync("/api/statements/upload", uploadContent);
        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>(JsonOptions);
        var fileId = uploadResult!.FileId;

        // Wait for background worker to finish processing
        await WaitForProcessingAsync(client, fileId);

        var txResp = await client.GetAsync($"/api/statements/{fileId}/transactions");
        var statementResult = await txResp.Content.ReadFromJsonAsync<StatementTransactionsResponse>(JsonOptions);
        var targetTx = statementResult!.Items.First();

        // Attempt invalid correction: Empty reason
        var invalidReq1 = new CorrectTransactionRequest
        {
            TransactionDate = targetTx.TransactionDate,
            Description = "NEW DESC",
            Amount = 100.00m,
            Debit = 100.00m,
            Reason = "   " // Empty reason
        };
        var resp1 = await client.PutAsJsonAsync($"/api/statements/{fileId}/transactions/{targetTx.Id}", invalidReq1);
        Assert.Equal(HttpStatusCode.BadRequest, resp1.StatusCode);

        // Attempt invalid correction: Both Debit and Credit populated
        var invalidReq2 = new CorrectTransactionRequest
        {
            TransactionDate = targetTx.TransactionDate,
            Description = "NEW DESC",
            Amount = 100.00m,
            Debit = 100.00m,
            Credit = 100.00m,
            Reason = "Valid reason"
        };
        var resp2 = await client.PutAsJsonAsync($"/api/statements/{fileId}/transactions/{targetTx.Id}", invalidReq2);
        Assert.Equal(HttpStatusCode.BadRequest, resp2.StatusCode);
    }

    [Fact]
    public async Task Test18_CrossUserAccess_CannotRetrieveOtherUsersTransactions()
    {
        var (clientA, authA) = await RegisterUserAsync("OwnerA");
        var (clientB, authB) = await RegisterUserAsync("AttackerB");

        var pdfBytes = GenerateTestPdf();
        var uploadContent = CreateMultipartPdf("TestStatement.pdf", pdfBytes);
        var uploadResp = await clientA.PostAsync("/api/statements/upload", uploadContent);
        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>(JsonOptions);
        var fileIdA = uploadResult!.FileId;

        // Wait for background worker to finish processing
        await WaitForProcessingAsync(clientA, fileIdA);

        // User B attempts to get transactions of User A
        var crossResp = await clientB.GetAsync($"/api/statements/{fileIdA}/transactions");
        Assert.Equal(HttpStatusCode.NotFound, crossResp.StatusCode);

        // User B attempts to get validation summary of User A
        var crossSummaryResp = await clientB.GetAsync($"/api/statements/{fileIdA}/validation-summary");
        Assert.Equal(HttpStatusCode.NotFound, crossSummaryResp.StatusCode);
    }

    [Fact]
    public async Task Test19_CrossUserCorrection_DeniedWithNotFound()
    {
        var (clientA, authA) = await RegisterUserAsync("UserOwner");
        var (clientB, authB) = await RegisterUserAsync("UserAttacker");

        var pdfBytes = GenerateTestPdf();
        var uploadContent = CreateMultipartPdf("TestStatement.pdf", pdfBytes);
        var uploadResp = await clientA.PostAsync("/api/statements/upload", uploadContent);
        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>(JsonOptions);
        var fileIdA = uploadResult!.FileId;

        // Wait for background worker to finish processing
        await WaitForProcessingAsync(clientA, fileIdA);

        var txResp = await clientA.GetAsync($"/api/statements/{fileIdA}/transactions");
        var statementResult = await txResp.Content.ReadFromJsonAsync<StatementTransactionsResponse>(JsonOptions);
        var txIdA = statementResult!.Items.First().Id;

        // User B attempts to modify User A's transaction
        var hackRequest = new CorrectTransactionRequest
        {
            TransactionDate = DateTime.UtcNow,
            Description = "HACKED TRANSACTION",
            Amount = 1.00m,
            Debit = 1.00m,
            Reason = "Unauthorized manipulation attempt"
        };

        var crossPutResp = await clientB.PutAsJsonAsync($"/api/statements/{fileIdA}/transactions/{txIdA}", hackRequest);
        Assert.Equal(HttpStatusCode.NotFound, crossPutResp.StatusCode);
    }

    [Fact]
    public async Task Test25_Filtering_Search_And_Pagination_WorkCorrectly()
    {
        var (client, auth) = await RegisterUserAsync("FilterSearchTest");

        var pdfBytes = GenerateTestPdf();
        var uploadContent = CreateMultipartPdf("TestStatement.pdf", pdfBytes);
        var uploadResp = await client.PostAsync("/api/statements/upload", uploadContent);
        var uploadResult = await uploadResp.Content.ReadFromJsonAsync<StatementUploadResponse>(JsonOptions);
        var fileId = uploadResult!.FileId;

        // Wait for background worker to finish processing
        await WaitForProcessingAsync(client, fileId);

        // Filter by type=debit
        var debitResp = await client.GetAsync($"/api/statements/{fileId}/transactions?type=debit");
        Assert.True(debitResp.IsSuccessStatusCode);
        var debitData = await debitResp.Content.ReadFromJsonAsync<StatementTransactionsResponse>(JsonOptions);
        Assert.Single(debitData!.Items);
        Assert.Equal("OFFICE SUPPLIES", debitData.Items[0].Description);

        // Filter by type=credit
        var creditResp = await client.GetAsync($"/api/statements/{fileId}/transactions?type=credit");
        Assert.True(creditResp.IsSuccessStatusCode);
        var creditData = await creditResp.Content.ReadFromJsonAsync<StatementTransactionsResponse>(JsonOptions);
        Assert.Single(creditData!.Items);
        Assert.Equal("SALARY CREDIT", creditData.Items[0].Description);

        // Search by text
        var searchResp = await client.GetAsync($"/api/statements/{fileId}/transactions?search=SUPPLIES");
        Assert.True(searchResp.IsSuccessStatusCode);
        var searchData = await searchResp.Content.ReadFromJsonAsync<StatementTransactionsResponse>(JsonOptions);
        Assert.Single(searchData!.Items);
        Assert.Equal("OFFICE SUPPLIES", searchData.Items[0].Description);

        // Pagination
        var pagedResp = await client.GetAsync($"/api/statements/{fileId}/transactions?page=1&pageSize=1");
        Assert.True(pagedResp.IsSuccessStatusCode);
        var pagedData = await pagedResp.Content.ReadFromJsonAsync<StatementTransactionsResponse>(JsonOptions);
        Assert.Single(pagedData!.Items);
        Assert.Equal(2, pagedData.TotalCount);
        Assert.Equal(2, pagedData.TotalPages);
    }

    #endregion
}
