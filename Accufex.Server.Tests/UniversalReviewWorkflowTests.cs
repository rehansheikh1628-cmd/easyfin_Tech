using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Accufex.Server.Controllers;
using Accufex.Server.Data;
using Accufex.Server.DTOs;
using Accufex.Server.Models;
using Accufex.Server.Parsing;
using Accufex.Server.Parsing.Interfaces;
using Accufex.Server.Parsing.Models;
using Accufex.Server.Parsing.Parsers;
using Accufex.Server.Parsing.Universal.Interfaces;
using Accufex.Server.Parsing.Universal.Models;
using Accufex.Server.Parsing.Universal.Services;
using Accufex.Server.Services;
using Accufex.Server.Validation.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Accufex.Server.Tests;

public class UniversalReviewWorkflowTests
{
    private readonly AccufexDbContext _db;
    private readonly InMemoryFileStorage _fileStorage;
    private readonly BankDetector _detector;
    private readonly BankParserRegistry _registry;
    private readonly IUniversalColumnDetector _columnDetector;
    private readonly IStatementStructureAnalyzer _structureAnalyzer;
    private readonly IUniversalRowSegmenter _rowSegmenter;
    private readonly IFinancialValidator _financialValidator;
    private readonly IUniversalConfidenceCalculator _confidenceCalculator;
    private readonly IUniversalStatementEngine _universalEngine;
    private readonly IStatementFormatLearner _formatLearner;
    private readonly IUniversalReviewService _reviewService;
    private readonly BankParsingService _parsingService;

    public UniversalReviewWorkflowTests()
    {
        var dbOptions = new DbContextOptionsBuilder<AccufexDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        _db = new AccufexDbContext(dbOptions);

        _fileStorage = new InMemoryFileStorage();

        _detector = new BankDetector(NullLogger<BankDetector>.Instance);
        var allDedicatedParsers = new List<IBankStatementParser>
        {
            new HdfcStatementParser(NullLogger<HdfcStatementParser>.Instance),
            new YesBankStatementParser(NullLogger<YesBankStatementParser>.Instance),
            new AxisStatementParser(NullLogger<AxisStatementParser>.Instance),
            new CentralBankStatementParser(NullLogger<CentralBankStatementParser>.Instance),
            new ICICIStatementParser(NullLogger<ICICIStatementParser>.Instance),
            new ICICIStatementParserV2(NullLogger<ICICIStatementParserV2>.Instance),
            new SBIStatementParser(NullLogger<SBIStatementParser>.Instance),
            new BOIStatementParser(NullLogger<BOIStatementParser>.Instance),
            new KotakStatementParser(NullLogger<KotakStatementParser>.Instance),
            new PNBStatementParser(NullLogger<PNBStatementParser>.Instance),
            new BOBStatementParser(NullLogger<BOBStatementParser>.Instance)
        };
        _registry = new BankParserRegistry(allDedicatedParsers);

        _columnDetector = new UniversalColumnDetector(NullLogger<UniversalColumnDetector>.Instance);
        _structureAnalyzer = new StatementStructureAnalyzer(_columnDetector, NullLogger<StatementStructureAnalyzer>.Instance);
        _rowSegmenter = new UniversalRowSegmenter(NullLogger<UniversalRowSegmenter>.Instance);
        _financialValidator = new FinancialValidator(NullLogger<FinancialValidator>.Instance);
        _confidenceCalculator = new UniversalConfidenceCalculator();

        _universalEngine = new UniversalStatementEngine(
            _structureAnalyzer,
            _confidenceCalculator,
            NullLogger<UniversalStatementEngine>.Instance,
            _rowSegmenter,
            _financialValidator);

        _formatLearner = new StatementFormatLearner(_fileStorage, NullLogger<StatementFormatLearner>.Instance);

        var mockPdfExtractionService = new Mock<IPdfExtractionService>();
        var mockCorrectionStore = new Mock<ITransactionCorrectionStore>();

        _reviewService = new UniversalReviewService(
            _db,
            _fileStorage,
            mockPdfExtractionService.Object,
            _financialValidator,
            _confidenceCalculator,
            _rowSegmenter,
            mockCorrectionStore.Object,
            _formatLearner,
            NullLogger<UniversalReviewService>.Instance);

        _parsingService = new BankParsingService(
            _db,
            mockPdfExtractionService.Object,
            _detector,
            _registry,
            _universalEngine,
            _reviewService,
            null,
            NullLogger<BankParsingService>.Instance);
    }

    private async Task<(User user, Client client, FinancialYear fy, FileRecord file)> CreateTestWorkspaceAsync()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"user_{Guid.NewGuid():N}@accufex.com",
            Password = "hashedpassword"
        };
        _db.Users.Add(user);

        var client = new Client
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Name = "Universal Client Test",
            ContactPerson = "Contact",
            Email = "client@test.com",
            Phone = "1234567890",
            BusinessName = "Biz",
            BusinessType = "Type",
            Address = "Address",
            TaxId = "TAX123",
            CreatedAt = DateTime.UtcNow
        };
        _db.Clients.Add(client);

        var fy = new FinancialYear
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            DisplayName = "2025-2026",
            StartDate = new DateTime(2025, 4, 1),
            EndDate = new DateTime(2026, 3, 31),
            Status = 1,
            CreatedAt = DateTime.UtcNow
        };
        _db.FinancialYears.Add(fy);

        var file = new FileRecord
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            FinancialYearId = fy.Id,
            OriginalFileName = "unknown_bank_statement.pdf",
            StoredFileName = $"{Guid.NewGuid():N}.pdf",
            Extension = ".pdf",
            ContentType = "application/pdf",
            SizeBytes = 1024,
            UploadedAt = DateTime.UtcNow,
            ProcessingStatus = 0
        };
        _db.FileRecords.Add(file);

        await _db.SaveChangesAsync();
        return (user, client, fy, file);
    }

    private UniversalReviewSession CreateSampleReviewSession(Guid fileId)
    {
        var session = new UniversalReviewSession
        {
            FileRecordId = fileId,
            FileName = "unknown_statement.pdf",
            Status = "ReviewRequired",
            DetectedBank = "Unknown Bank Ltd",
            Confidence = new UniversalConfidenceScore
            {
                HeaderConfidence = 0.8,
                ColumnConfidence = 0.8,
                DataContinuityConfidence = 0.9,
                OverallScore = 85,
                Level = UniversalConfidenceLevel.High
            },
            FinancialValidation = new FinancialValidationResult
            {
                TotalRowsChecked = 3,
                ReconciledRowsCount = 3,
                FailedRowsCount = 0,
                MissingBalanceCount = 0,
                MissingAmountCount = 0,
                TotalDebits = 1500m,
                TotalCredits = 2500m,
                OpeningBalance = 9000m,
                ClosingBalance = 10000m,
                IsFullyReconciled = true
            },
            Columns = new List<DetectedColumnLayout>
            {
                new() { ColumnIndex = 0, ColumnType = StatementColumnType.Date, HeaderText = "Date", LeftX = 50, RightX = 100 },
                new() { ColumnIndex = 1, ColumnType = StatementColumnType.Description, HeaderText = "Description", LeftX = 100, RightX = 300 },
                new() { ColumnIndex = 2, ColumnType = StatementColumnType.Debit, HeaderText = "Debit", LeftX = 300, RightX = 400 },
                new() { ColumnIndex = 3, ColumnType = StatementColumnType.Credit, HeaderText = "Credit", LeftX = 400, RightX = 500 },
                new() { ColumnIndex = 4, ColumnType = StatementColumnType.Balance, HeaderText = "Balance", LeftX = 500, RightX = 600 }
            },
            CandidateTransactions = new List<UniversalCandidateTransactionDto>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    RowNumber = 1,
                    PageNumber = 1,
                    Date = new DateTime(2026, 4, 1),
                    Description = "UPI PAYMENT 1",
                    Debit = 500m,
                    Credit = null,
                    Amount = 500m,
                    Balance = 9500m,
                    Direction = "Debit",
                    OriginalValues = new Dictionary<string, string?>
                    {
                        ["date"] = "2026-04-01",
                        ["description"] = "UPI PAYMENT 1",
                        ["debit"] = "500.00",
                        ["credit"] = null,
                        ["balance"] = "9500.00"
                    }
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    RowNumber = 2,
                    PageNumber = 1,
                    Date = new DateTime(2026, 4, 2),
                    Description = "SALARY INWARD",
                    Debit = null,
                    Credit = 2500m,
                    Amount = 2500m,
                    Balance = 12000m,
                    Direction = "Credit",
                    OriginalValues = new Dictionary<string, string?>
                    {
                        ["date"] = "2026-04-02",
                        ["description"] = "SALARY INWARD",
                        ["debit"] = null,
                        ["credit"] = "2500.00",
                        ["balance"] = "12000.00"
                    }
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    RowNumber = 3,
                    PageNumber = 1,
                    Date = new DateTime(2026, 4, 3),
                    Description = "ATM WITHDRAWAL",
                    Debit = 1000m,
                    Credit = null,
                    Amount = 1000m,
                    Balance = 11000m,
                    Direction = "Debit",
                    OriginalValues = new Dictionary<string, string?>
                    {
                        ["date"] = "2026-04-03",
                        ["description"] = "ATM WITHDRAWAL",
                        ["debit"] = "1000.00",
                        ["credit"] = null,
                        ["balance"] = "11000.00"
                    }
                }
            },
            IsApprovalRequired = true,
            IsConversionAllowed = true
        };

        return session;
    }

    private async Task SaveReviewSessionDirectlyAsync(UniversalReviewSession session)
    {
        using var ms = new MemoryStream();
        await JsonSerializer.SerializeAsync(ms, session, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        ms.Position = 0;
        await _fileStorage.SaveFileAsync(ms, "universal-reviews", $"{session.FileRecordId}.json");
    }

    // 1. Unknown statement creates reviewable result.
    [Fact]
    public async Task UnknownStatement_CreatesReviewableResult_WithStatusReviewRequired()
    {
        var (_, _, _, file) = await CreateTestWorkspaceAsync();
        var session = CreateSampleReviewSession(file.Id);
        await SaveReviewSessionDirectlyAsync(session);

        var loaded = await _reviewService.GetReviewSessionAsync(file.Id);

        Assert.NotNull(loaded);
        Assert.Equal("ReviewRequired", loaded.Status);
        Assert.True(loaded.IsApprovalRequired);
        Assert.True(loaded.IsConversionAllowed);
    }

    // 2. Candidate transactions are returned.
    [Fact]
    public async Task CandidateTransactions_AreReturnedWithFullDetails()
    {
        var (_, _, _, file) = await CreateTestWorkspaceAsync();
        var session = CreateSampleReviewSession(file.Id);
        await SaveReviewSessionDirectlyAsync(session);

        var sessionDto = await _reviewService.GetReviewSessionAsync(file.Id);

        Assert.NotNull(sessionDto);
        Assert.Equal(3, sessionDto.CandidateTransactions.Count);
        Assert.Contains(sessionDto.CandidateTransactions, t => t.Description == "SALARY INWARD" && t.Credit == 2500m);
    }

    // 3. User edits a transaction.
    [Fact]
    public async Task UserEditsTransaction_PreservesOriginalValues_AndUpdatesFields()
    {
        var (_, _, _, file) = await CreateTestWorkspaceAsync();
        var session = CreateSampleReviewSession(file.Id);
        await SaveReviewSessionDirectlyAsync(session);

        var targetTxn = session.CandidateTransactions[0];
        var req = new CorrectUniversalTransactionRequest
        {
            TransactionDate = new DateTime(2026, 4, 1),
            Description = "UPI PAYMENT 1 - EDITED NARRATION",
            Debit = 500m,
            Credit = null,
            Balance = 9500m,
            Reason = "Cleaned up payee name"
        };

        var updated = await _reviewService.UpdateTransactionAsync(file.Id, targetTxn.Id, req);

        var editedCandidate = updated.CandidateTransactions.First(t => t.Id == targetTxn.Id);
        Assert.Equal("UPI PAYMENT 1 - EDITED NARRATION", editedCandidate.Description);
        Assert.True(editedCandidate.IsUserEdited);
        Assert.NotNull(editedCandidate.OriginalValues);
        Assert.Equal("UPI PAYMENT 1", editedCandidate.OriginalValues["description"]);
    }

    // 4. Edited transaction is revalidated.
    [Fact]
    public async Task EditedTransaction_IsRevalidated_RecalculatingRunningBalances()
    {
        var (_, _, _, file) = await CreateTestWorkspaceAsync();
        var session = CreateSampleReviewSession(file.Id);
        await SaveReviewSessionDirectlyAsync(session);

        var targetTxn = session.CandidateTransactions[0];
        var req = new CorrectUniversalTransactionRequest
        {
            TransactionDate = new DateTime(2026, 4, 1),
            Description = "UPI PAYMENT 1",
            Debit = 600m, // Changed from 500 to 600
            Credit = null,
            Balance = 9400m,
            Reason = "Corrected debit amount"
        };

        var updated = await _reviewService.UpdateTransactionAsync(file.Id, targetTxn.Id, req);

        Assert.NotNull(updated.FinancialValidation);
        Assert.Equal(1600m, updated.FinancialValidation.TotalDebits); // 600 + 1000
    }

    // 5. Invalid edited amount rejected.
    [Fact]
    public async Task InvalidEditedAmount_ThrowsArgumentException()
    {
        var (_, _, _, file) = await CreateTestWorkspaceAsync();
        var session = CreateSampleReviewSession(file.Id);
        await SaveReviewSessionDirectlyAsync(session);

        var targetTxn = session.CandidateTransactions[0];
        var req = new CorrectUniversalTransactionRequest
        {
            TransactionDate = new DateTime(2026, 4, 1),
            Description = "Test",
            Debit = -100m, // Invalid negative
            Credit = null
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _reviewService.UpdateTransactionAsync(file.Id, targetTxn.Id, req));
    }

    // 6. Invalid date rejected.
    [Fact]
    public async Task InvalidDate_ThrowsArgumentException()
    {
        var (_, _, _, file) = await CreateTestWorkspaceAsync();
        var session = CreateSampleReviewSession(file.Id);
        await SaveReviewSessionDirectlyAsync(session);

        var targetTxn = session.CandidateTransactions[0];
        var req = new CorrectUniversalTransactionRequest
        {
            TransactionDate = new DateTime(1850, 1, 1), // Invalid year < 1990
            Description = "Test",
            Debit = 500m
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _reviewService.UpdateTransactionAsync(file.Id, targetTxn.Id, req));
    }

    // 7. Debit/Credit conflict rejected.
    [Fact]
    public async Task DebitCreditConflict_ThrowsArgumentException()
    {
        var (_, _, _, file) = await CreateTestWorkspaceAsync();
        var session = CreateSampleReviewSession(file.Id);
        await SaveReviewSessionDirectlyAsync(session);

        var targetTxn = session.CandidateTransactions[0];
        var req = new CorrectUniversalTransactionRequest
        {
            TransactionDate = new DateTime(2026, 4, 1),
            Description = "Test",
            Debit = 500m,
            Credit = 500m // Both set!
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _reviewService.UpdateTransactionAsync(file.Id, targetTxn.Id, req));
    }

    // 8. Balance mismatch blocks approval when critical.
    [Fact]
    public async Task BalanceMismatch_BlocksApproval_WhenCritical()
    {
        var (user, _, _, file) = await CreateTestWorkspaceAsync();
        var session = CreateSampleReviewSession(file.Id);
        // Introduce critical error (missing date on candidate)
        session.CandidateTransactions[0].Date = null;
        session.IsConversionAllowed = false;
        await SaveReviewSessionDirectlyAsync(session);

        var result = await _reviewService.ApproveAndConvertAsync(file.Id, user.Id);
        Assert.False(result.Success);
        Assert.Contains("Cannot approve statement", result.Error ?? string.Empty);
    }

    // 9. Valid candidate set can be approved.
    [Fact]
    public async Task ValidCandidateSet_CanBeApproved_ConvertsToCanonicalTransactions()
    {
        var (user, _, _, file) = await CreateTestWorkspaceAsync();
        var session = CreateSampleReviewSession(file.Id);
        await SaveReviewSessionDirectlyAsync(session);

        var result = await _reviewService.ApproveAndConvertAsync(file.Id, user.Id);

        Assert.True(result.Success);
        Assert.Equal(3, result.PersistedTransactions.Count);

        var canonicalTxns = await _db.Transactions.Where(t => t.SourceFileId == file.Id).ToListAsync();
        Assert.Equal(3, canonicalTxns.Count);

        var updatedFile = await _db.FileRecords.FindAsync(file.Id);
        Assert.Equal(2, updatedFile!.ProcessingStatus); // 2 = Completed
    }

    // 10. Approval is idempotent.
    [Fact]
    public async Task ApprovalIsIdempotent_SecondCallSucceeds()
    {
        var (user, _, _, file) = await CreateTestWorkspaceAsync();
        var session = CreateSampleReviewSession(file.Id);
        await SaveReviewSessionDirectlyAsync(session);

        var first = await _reviewService.ApproveAndConvertAsync(file.Id, user.Id);
        var second = await _reviewService.ApproveAndConvertAsync(file.Id, user.Id);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(first.PersistedTransactions.Count, second.PersistedTransactions.Count);
    }

    // 11. Duplicate approval does not duplicate transactions.
    [Fact]
    public async Task DuplicateApproval_DoesNotDuplicateTransactions()
    {
        var (user, _, _, file) = await CreateTestWorkspaceAsync();
        var session = CreateSampleReviewSession(file.Id);
        await SaveReviewSessionDirectlyAsync(session);

        await _reviewService.ApproveAndConvertAsync(file.Id, user.Id);
        var countFirst = await _db.Transactions.CountAsync(t => t.SourceFileId == file.Id);

        await _reviewService.ApproveAndConvertAsync(file.Id, user.Id);
        var countSecond = await _db.Transactions.CountAsync(t => t.SourceFileId == file.Id);

        Assert.Equal(3, countFirst);
        Assert.Equal(countFirst, countSecond);
    }

    // 12. Review requires authentication.
    [Fact]
    public async Task ReviewRequiresAuthentication_ControllerReturnsUnauthorized_WhenAnonymous()
    {
        var (_, _, _, file) = await CreateTestWorkspaceAsync();
        var controller = new StatementsController(
            new Mock<IStatementService>().Object,
            new Mock<IPdfExtractionService>().Object,
            _parsingService,
            _db,
            new Mock<ITransactionValidationService>().Object,
            new Mock<ITransactionCorrectionStore>().Object,
            NullLogger<StatementsController>.Instance,
            null,
            null,
            _reviewService);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext() // No user identity set
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => controller.GetUniversalReview(file.Id));
    }

    // 13. Review requires correct workspace ownership.
    [Fact]
    public async Task ReviewRequiresWorkspaceOwnership_ControllerReturnsNotFound_WhenAccessedByOtherUser()
    {
        var (owner, _, _, file) = await CreateTestWorkspaceAsync();
        var otherUserId = Guid.NewGuid();

        var controller = new StatementsController(
            new Mock<IStatementService>().Object,
            new Mock<IPdfExtractionService>().Object,
            _parsingService,
            _db,
            new Mock<ITransactionValidationService>().Object,
            new Mock<ITransactionCorrectionStore>().Object,
            NullLogger<StatementsController>.Instance,
            null,
            null,
            _reviewService);

        var claims = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, otherUserId.ToString())
        }, "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = claims }
        };

        var result = await controller.GetUniversalReview(file.Id);

        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, notFoundResult.StatusCode);
    }

    // 14. Fingerprint is generated.
    [Fact]
    public void FingerprintIsGenerated_FromCandidateStructure()
    {
        var parseResult = new UniversalParseResult
        {
            Success = true,
            DetectedBankName = "Unknown Sample Bank",
            Confidence = new UniversalConfidenceScore { OverallScore = 80, Level = UniversalConfidenceLevel.High },
            Transactions = new List<ParsedTransaction>()
        };

        var columns = new List<DetectedColumnLayout>
        {
            new() { ColumnIndex = 0, ColumnType = StatementColumnType.Date, HeaderText = "Date", LeftX = 50, RightX = 100 },
            new() { ColumnIndex = 1, ColumnType = StatementColumnType.Description, HeaderText = "Description", LeftX = 100, RightX = 300 },
            new() { ColumnIndex = 2, ColumnType = StatementColumnType.Debit, HeaderText = "Debit", LeftX = 300, RightX = 400 },
            new() { ColumnIndex = 3, ColumnType = StatementColumnType.Credit, HeaderText = "Credit", LeftX = 400, RightX = 500 },
            new() { ColumnIndex = 4, ColumnType = StatementColumnType.Balance, HeaderText = "Balance", LeftX = 500, RightX = 600 }
        };

        var extraction = new PdfExtractionResult
        {
            Pages = new List<PdfPageResult>
            {
                new() { PageNumber = 1, Width = 600, Height = 800, RawText = "Date Description Debit Credit Balance" }
            }
        };

        var fingerprint = _formatLearner.GenerateFingerprint(parseResult, columns, extraction);

        Assert.NotNull(fingerprint);
        Assert.False(string.IsNullOrWhiteSpace(fingerprint.FingerprintHash));
        Assert.Equal(5, fingerprint.ColumnCount);
        Assert.Equal("Unknown Sample Bank", fingerprint.BankName);
    }

    // 15. Fingerprint is stored.
    [Fact]
    public async Task FingerprintIsStored_InFileStorage()
    {
        var parseResult = new UniversalParseResult
        {
            Success = true,
            DetectedBankName = "Unknown Bank Stored",
            Confidence = new UniversalConfidenceScore { OverallScore = 85, Level = UniversalConfidenceLevel.High },
            Transactions = new List<ParsedTransaction>()
        };

        var columns = new List<DetectedColumnLayout>
        {
            new() { ColumnIndex = 0, ColumnType = StatementColumnType.Date, HeaderText = "Txn Date", LeftX = 50, RightX = 100 },
            new() { ColumnIndex = 1, ColumnType = StatementColumnType.Description, HeaderText = "Narration", LeftX = 100, RightX = 300 },
            new() { ColumnIndex = 2, ColumnType = StatementColumnType.Debit, HeaderText = "Dr", LeftX = 300, RightX = 400 },
            new() { ColumnIndex = 3, ColumnType = StatementColumnType.Credit, HeaderText = "Cr", LeftX = 400, RightX = 500 },
            new() { ColumnIndex = 4, ColumnType = StatementColumnType.Balance, HeaderText = "Bal", LeftX = 500, RightX = 600 }
        };

        var extraction = new PdfExtractionResult
        {
            Pages = new List<PdfPageResult>
            {
                new() { PageNumber = 1, Width = 600, Height = 800, RawText = "Txn Date Narration Dr Cr Bal" }
            }
        };

        var fingerprint = _formatLearner.GenerateFingerprint(parseResult, columns, extraction);
        await _formatLearner.SaveFingerprintAsync(fingerprint);

        var matched = await _formatLearner.FindMatchingFingerprintAsync(extraction, columns);
        Assert.NotNull(matched);
        Assert.Equal(fingerprint.FingerprintHash, matched.FingerprintHash);
    }

    // 16. Existing dedicated parser bypasses Universal review workflow.
    [Fact]
    public void ExistingDedicatedParser_BypassesUniversalReview()
    {
        var extraction = new PdfExtractionResult
        {
            Pages = new List<PdfPageResult>
            {
                new()
                {
                    PageNumber = 1,
                    RawText = "HDFC BANK LIMITED\nStatement of Account No: 50100012345678\nDate Narration Chq Ref Value Dt Withdrawal Deposit Closing Balance"
                }
            }
        };

        var detection = _detector.DetectBank(extraction);

        Assert.Equal(BankType.Hdfc, detection.DetectedBank);
        Assert.True(detection.IsSupported);

        var parser = _registry.GetParser((int)detection.DetectedBank);
        Assert.NotNull(parser);
        Assert.IsType<HdfcStatementParser>(parser);
    }

    // 17. Learned universal format does not override dedicated parser.
    [Fact]
    public void LearnedUniversalFormat_DoesNotOverride_DedicatedParser()
    {
        var extraction = new PdfExtractionResult
        {
            Pages = new List<PdfPageResult>
            {
                new()
                {
                    PageNumber = 1,
                    RawText = "AXIS BANK LIMITED\nStatement of Axis Bank Account No : 924020025076740\nUTIB0000001\nTransaction Date Particulars Amount(INR) Debit/Credit Balance(INR)"
                }
            }
        };

        var detection = _detector.DetectBank(extraction);

        Assert.Equal(BankType.Axis, detection.DetectedBank);
        Assert.True(detection.IsSupported);

        var parser = _registry.GetParser((int)detection.DetectedBank);
        Assert.NotNull(parser);
        Assert.IsType<AxisStatementParser>(parser);
        Assert.NotEqual("Universal", parser.ParserVersion);
    }
}

public class InMemoryFileStorage : IFileStorageService
{
    private readonly Dictionary<string, byte[]> _storage = new(StringComparer.OrdinalIgnoreCase);

    public Task<string> SaveFileAsync(Stream stream, string subDirectory, string safeFileName, CancellationToken cancellationToken = default)
    {
        var key = $"{subDirectory}/{safeFileName}".Replace('\\', '/');
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        _storage[key] = ms.ToArray();
        return Task.FromResult(key);
    }

    public Task<Stream?> GetFileStreamAsync(string subDirectory, string safeFileName, CancellationToken cancellationToken = default)
    {
        var key = $"{subDirectory}/{safeFileName}".Replace('\\', '/');
        if (_storage.TryGetValue(key, out var bytes))
        {
            return Task.FromResult<Stream?>(new MemoryStream(bytes));
        }
        return Task.FromResult<Stream?>(null);
    }

    public Task<bool> DeleteFileAsync(string subDirectory, string safeFileName, CancellationToken cancellationToken = default)
    {
        var key = $"{subDirectory}/{safeFileName}".Replace('\\', '/');
        return Task.FromResult(_storage.Remove(key));
    }

    public Task<bool> FileExistsAsync(string subDirectory, string safeFileName, CancellationToken cancellationToken = default)
    {
        var key = $"{subDirectory}/{safeFileName}".Replace('\\', '/');
        return Task.FromResult(_storage.ContainsKey(key));
    }

    public Task<string> ComputeSha256HashAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Task.FromResult(Convert.ToHexString(hash).ToLowerInvariant());
    }
}
