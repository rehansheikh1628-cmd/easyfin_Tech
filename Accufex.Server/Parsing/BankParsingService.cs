using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Accufex.Server.Data;
using Accufex.Server.DTOs;
using Accufex.Server.Models;
using Accufex.Server.Parsing.Interfaces;
using Accufex.Server.Parsing.Models;
using Accufex.Server.Parsing.Universal.Interfaces;
using Accufex.Server.Parsing.Universal.Models;
using Accufex.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Accufex.Server.Parsing;

public class BankParsingService : IBankParsingService
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _statementLocks = new();

    private readonly AccufexDbContext _dbContext;
    private readonly IPdfExtractionService _pdfExtractionService;
    private readonly IBankDetector _bankDetector;
    private readonly IBankParserRegistry _parserRegistry;
    private readonly IUniversalStatementEngine? _universalEngine;
    private readonly Accufex.Server.Parsing.Universal.Interfaces.IUniversalReviewService? _universalReviewService;
    private readonly Accufex.Server.Validation.Services.ITransactionCorrectionStore? _correctionStore;
    private readonly ILogger<BankParsingService> _logger;

    public BankParsingService(
        AccufexDbContext dbContext,
        IPdfExtractionService pdfExtractionService,
        IBankDetector bankDetector,
        IBankParserRegistry parserRegistry,
        IUniversalStatementEngine? universalEngine,
        Accufex.Server.Parsing.Universal.Interfaces.IUniversalReviewService? universalReviewService,
        Accufex.Server.Validation.Services.ITransactionCorrectionStore? correctionStore,
        ILogger<BankParsingService> logger)
    {
        _dbContext = dbContext;
        _pdfExtractionService = pdfExtractionService;
        _bankDetector = bankDetector;
        _parserRegistry = parserRegistry;
        _universalEngine = universalEngine;
        _universalReviewService = universalReviewService;
        _correctionStore = correctionStore;
        _logger = logger;
    }

    public BankParsingService(
        AccufexDbContext dbContext,
        IPdfExtractionService pdfExtractionService,
        IBankDetector bankDetector,
        IBankParserRegistry parserRegistry,
        IUniversalStatementEngine? universalEngine,
        Accufex.Server.Validation.Services.ITransactionCorrectionStore? correctionStore,
        ILogger<BankParsingService> logger)
        : this(dbContext, pdfExtractionService, bankDetector, parserRegistry, universalEngine, null, correctionStore, logger)
    {
    }

    public BankParsingService(
        AccufexDbContext dbContext,
        IPdfExtractionService pdfExtractionService,
        IBankDetector bankDetector,
        IBankParserRegistry parserRegistry,
        Accufex.Server.Validation.Services.ITransactionCorrectionStore? correctionStore,
        ILogger<BankParsingService> logger)
        : this(dbContext, pdfExtractionService, bankDetector, parserRegistry, null, null, correctionStore, logger)
    {
    }

    public BankParsingService(
        AccufexDbContext dbContext,
        IPdfExtractionService pdfExtractionService,
        IBankDetector bankDetector,
        IBankParserRegistry parserRegistry,
        ILogger<BankParsingService> logger)
        : this(dbContext, pdfExtractionService, bankDetector, parserRegistry, null, null, null, logger)
    {
    }

    public async Task<(bool Success, string? ErrorMessage, BankParsingResult? Result)> ParseAndPersistStatementAsync(
        Guid fileRecordId,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        var semaphore = _statementLocks.GetOrAdd(fileRecordId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);

        try
        {
            var startedAt = DateTime.UtcNow;

            // 1. Enforce strict authorization: file must belong to client owned by current authenticated user
            var fileRecord = await _dbContext.FileRecords
                .Include(f => f.Client)
                .Include(f => f.FinancialYear)
                .Include(f => f.TransactionImportResult)
                .Include(f => f.Transactions)
                .FirstOrDefaultAsync(f => f.Id == fileRecordId && f.Client.UserId == currentUserId, cancellationToken);

            if (fileRecord == null)
            {
                _logger.LogWarning("Unauthorized or non-existent statement parsing requested: {FileId} by user {UserId}",
                    fileRecordId, currentUserId);
                return (false, $"Statement with ID {fileRecordId} was not found or is not accessible.", null);
            }

            // 2. Resolve Phase 3 extraction result (or extract on demand if not yet extracted)
            var extraction = await _pdfExtractionService.GetExtractionResultAsync(fileRecordId, currentUserId, cancellationToken);
            if (extraction == null)
            {
                _logger.LogInformation("No extraction artifact found for file {FileId}. Triggering Phase 3 digital extraction.", fileRecordId);
                var (extractSuccess, extractError, newExtraction) = await _pdfExtractionService.ExtractDocumentAsync(
                    fileRecordId, currentUserId, password: null, cancellationToken);

                if (!extractSuccess || newExtraction == null)
                {
                    return (false, extractError ?? "Failed to extract text from statement PDF.", null);
                }

                extraction = newExtraction;
            }

            if (!extraction.HasUsableText)
            {
                return (false, "The statement contains no usable digital text. Scanned/rasterized PDFs cannot be parsed in this phase.", null);
            }

            // 3. Deterministic Bank Detection
            var detection = _bankDetector.DetectBank(extraction);
            if (!detection.IsSupported || detection.DetectedBank == BankType.Unknown)
            {
                _logger.LogInformation("Statement {FileId} is from an unknown/unsupported bank format. Routing to Universal Statement Engine fallback.", fileRecordId);

                if (_universalEngine == null)
                {
                    _logger.LogWarning("Statement {FileId} rejected: Universal Statement Engine is not registered and bank is unsupported.", fileRecordId);
                    return (false, "The statement bank format was not recognized as a supported bank format (Supported: HDFC Bank, YES BANK, Axis Bank, Central Bank of India, ICICI Bank, State Bank of India, Bank of India, Kotak Mahindra Bank, Punjab National Bank, Bank of Baroda).", null);
                }

                var universalResult = await _universalEngine.ProcessStatementAsync(extraction, detection, cancellationToken);
                var bankParsingResult = universalResult.ToBankParsingResult();

                // Phase 3 Review Policy:
                // Unknown statements require user review before conversion into trusted financial records.
                if (!universalResult.Success || universalResult.NeedsReview || universalResult.Confidence.Level != UniversalConfidenceLevel.High || universalResult.Transactions.Count == 0)
                {
                    _logger.LogInformation("Universal Engine determined statement {FileId} requires review before conversion. Confidence: {Level} ({Score:F1}%)",
                        fileRecordId, universalResult.Confidence.Level, universalResult.Confidence.OverallScore * 100);

                    if (_universalReviewService != null)
                    {
                        await _universalReviewService.CreateOrUpdateReviewSessionAsync(fileRecordId, universalResult, fileRecord.OriginalFileName, cancellationToken);
                    }

                    fileRecord.ProcessingStatus = 6; // ReviewRequired
                    fileRecord.ProcessingError = "Statement format requires review before conversion.";
                    fileRecord.UpdatedAt = DateTime.UtcNow;
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    var message = universalResult.StatusMessage ?? "This statement format requires review before conversion.";
                    return (false, message, bankParsingResult);
                }

                // If in future phases high confidence automatic extraction is supported:
                bool universalPersistenceSucceeded = false;
                string? universalPersistenceError = null;
                bool isUniversalInMemory = _dbContext.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true;

                if (isUniversalInMemory)
                {
                    try
                    {
                        await PersistValidatedTransactionsAsync(fileRecord, bankParsingResult, (int)BankType.Universal, detection.AccountNumber, startedAt, cancellationToken);
                        universalPersistenceSucceeded = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to persist universal transactions for statement {FileId} in in-memory database.", fileRecordId);
                        universalPersistenceError = "Failed to store validated transactions in the database.";
                    }
                }
                else
                {
                    var strategy = _dbContext.Database.CreateExecutionStrategy();
                    await strategy.ExecuteAsync(async () =>
                    {
                        await using var dbTransaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                        try
                        {
                            await PersistValidatedTransactionsAsync(fileRecord, bankParsingResult, (int)BankType.Universal, detection.AccountNumber, startedAt, cancellationToken);
                            await dbTransaction.CommitAsync(cancellationToken);
                            universalPersistenceSucceeded = true;
                        }
                        catch (Exception ex)
                        {
                            await dbTransaction.RollbackAsync(cancellationToken);
                            _logger.LogError(ex, "Failed to persist universal transactions for statement {FileId}. Database rolled back.", fileRecordId);
                            universalPersistenceError = "Failed to store validated transactions in the database.";
                        }
                    });
                }

                if (!universalPersistenceSucceeded)
                {
                    return (false, universalPersistenceError ?? "Database persistence failed.", bankParsingResult);
                }

                return (true, null, bankParsingResult);
            }

            // 4. Resolve Bank Parser
            var parser = _parserRegistry.ResolveParser(detection);
            if (parser == null)
            {
                _logger.LogError("No parser registered for detected bank {BankName} ({BankCode})", detection.BankName, (int)detection.DetectedBank);
                return (false, $"No parser implementation is available for {detection.BankName}.", null);
            }

            // 5. In-Memory Parsing & Running Balance Validation (COMPLETELY IN MEMORY)
            BankParsingResult parsingResult;
            try
            {
                parsingResult = parser.Parse(extraction, detection);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in {ParserName} while parsing file {FileId}", parser.BankName, fileRecordId);
                return (false, "An unexpected error occurred while parsing the statement transactions.", null);
            }

            // 6. Safe Validation: If parsing failed or produced 0 transactions, DO NOT TOUCH DATABASE
            if (!parsingResult.Success || parsingResult.Transactions.Count == 0)
            {
                _logger.LogWarning("Parsing produced 0 transactions for file {FileId}. Preserving any existing database import.", fileRecordId);
                return (false, parsingResult.ErrorMessage ?? "No valid transactions could be parsed from the statement.", parsingResult);
            }

            // 7. Atomic Idempotent Persistence:
            // Only upon validated parsing success, persist transactions and update TransactionImportResult.
            bool persistenceSucceeded = false;
            string? persistenceError = null;

            bool isInMemory = _dbContext.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true;

            if (isInMemory)
            {
                try
                {
                    await PersistValidatedTransactionsAsync(fileRecord, parsingResult, parser.BankCode, detection.AccountNumber, startedAt, cancellationToken);
                    persistenceSucceeded = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to persist parsed transactions for statement {FileId} in in-memory database.", fileRecordId);
                    persistenceError = "Failed to store validated transactions in the database.";
                }
            }
            else
            {
                var strategy = _dbContext.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    await using var dbTransaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                    try
                    {
                        await PersistValidatedTransactionsAsync(fileRecord, parsingResult, parser.BankCode, detection.AccountNumber, startedAt, cancellationToken);
                        await dbTransaction.CommitAsync(cancellationToken);
                        persistenceSucceeded = true;
                    }
                    catch (Exception ex)
                    {
                        await dbTransaction.RollbackAsync(cancellationToken);
                        _logger.LogError(ex, "Failed to persist parsed transactions for statement {FileId}. Database rolled back.", fileRecordId);
                        persistenceError = "Failed to store validated transactions in the database.";
                    }
                });
            }

            if (!persistenceSucceeded)
            {
                return (false, persistenceError ?? "Database persistence failed.", parsingResult);
            }

            return (true, null, parsingResult);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task PersistValidatedTransactionsAsync(
        FileRecord fileRecord,
        BankParsingResult parsingResult,
        int bankCode,
        string? accountNumber,
        DateTime startedAt,
        CancellationToken cancellationToken)
    {
        var fileRecordId = fileRecord.Id;

        // Delete existing transactions for this statement if re-processing
        var existingTransactions = await _dbContext.Transactions
            .Where(t => t.SourceFileId == fileRecordId)
            .ToListAsync(cancellationToken);

        if (existingTransactions.Count > 0)
        {
            _logger.LogInformation("Safe re-parsing: Removing {Count} previously imported transactions for statement {FileId}",
                existingTransactions.Count, fileRecordId);
            _dbContext.Transactions.RemoveRange(existingTransactions);
        }

        // Update or create TransactionImportResult
        var existingImport = await _dbContext.TransactionImportResults
            .FirstOrDefaultAsync(r => r.SourceFileId == fileRecordId, cancellationToken);

        if (existingImport != null)
        {
            existingImport.DetectedBank = bankCode;
            existingImport.TotalDetected = parsingResult.TotalDetected;
            existingImport.ProcessedCount = parsingResult.ProcessedCount;
            existingImport.RejectedCount = parsingResult.RejectedCount;
            existingImport.WarningsCount = parsingResult.WarningsCount;
            existingImport.Status = "Completed";
            existingImport.ErrorMessage = null;
            existingImport.CompletedAt = DateTime.UtcNow;
        }
        else
        {
            var newImport = new TransactionImportResult
            {
                Id = Guid.NewGuid(),
                SourceFileId = fileRecordId,
                ClientId = fileRecord.ClientId,
                FinancialYearId = fileRecord.FinancialYearId,
                DetectedBank = bankCode,
                TotalDetected = parsingResult.TotalDetected,
                ProcessedCount = parsingResult.ProcessedCount,
                RejectedCount = parsingResult.RejectedCount,
                WarningsCount = parsingResult.WarningsCount,
                Status = "Completed",
                ErrorMessage = null,
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow
            };
            _dbContext.TransactionImportResults.Add(newImport);
        }

        // Map ParsedTransactions to entity Transactions
        var entitiesToAdd = new List<Transaction>(parsingResult.Transactions.Count);
        foreach (var tx in parsingResult.Transactions)
        {
            string? warning = tx.ReviewWarnings.Count > 0
                ? string.Join("; ", tx.ReviewWarnings)
                : null;

            entitiesToAdd.Add(new Transaction
            {
                Id = Guid.NewGuid(),
                TransactionDate = tx.TransactionDate,
                Description = tx.Description,
                Debit = tx.Debit,
                Credit = tx.Credit,
                Amount = tx.Amount,
                Balance = tx.Balance,
                Reference = tx.Reference,
                Utr = tx.Utr,
                TransactionType = tx.TransactionType,
                BankCode = tx.BankCode,
                Account = accountNumber,
                SourceFileId = fileRecordId,
                ClientId = fileRecord.ClientId,
                FinancialYearId = fileRecord.FinancialYearId,
                CreatedAt = DateTime.UtcNow,
                ProcessingWarning = warning
            });
        }

        _dbContext.Transactions.AddRange(entitiesToAdd);

        // Update FileRecord processing status: 2 = Completed
        fileRecord.ProcessingStatus = 2;
        fileRecord.UpdatedAt = DateTime.UtcNow;
        fileRecord.ProcessingError = null;

        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Successfully persisted {Count} transactions for statement {FileId}.",
            entitiesToAdd.Count, fileRecordId);

        // Initialize pristine audit sidecar snapshot (Section 1 Safety Requirement)
        if (_correctionStore != null)
        {
            await _correctionStore.InitializeSnapshotAsync(
                fileRecordId,
                parsingResult,
                entitiesToAdd,
                fileRecord.ClientId,
                fileRecord.FinancialYearId,
                cancellationToken);
        }
    }

    public async Task<BankParsingResult?> GetParsedTransactionsAsync(
        Guid fileRecordId,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        // Enforce strict workspace ownership
        var fileRecord = await _dbContext.FileRecords
            .AsNoTracking()
            .Include(f => f.Client)
            .FirstOrDefaultAsync(f => f.Id == fileRecordId && f.Client.UserId == currentUserId, cancellationToken);

        if (fileRecord == null)
        {
            return null;
        }

        var importResult = await _dbContext.TransactionImportResults
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.SourceFileId == fileRecordId, cancellationToken);

        var transactions = await _dbContext.Transactions
            .AsNoTracking()
            .Where(t => t.SourceFileId == fileRecordId)
            .OrderBy(t => t.TransactionDate)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

        var bankCode = importResult?.DetectedBank ?? (transactions.FirstOrDefault()?.BankCode ?? (int)BankType.Hdfc);
        var bankName = bankCode switch
        {
            1 => "HDFC Bank",
            2 => "YES BANK",
            3 => "Axis Bank",
            4 => "Central Bank of India",
            5 => "ICICI Bank",
            6 => "State Bank of India",
            7 => "Bank of India",
            8 => "Kotak Mahindra Bank",
            9 => "Punjab National Bank",
            10 => "Bank of Baroda",
            _ => "Unknown"
        };
        var sidecar = _correctionStore != null ? await _correctionStore.GetAuditSidecarAsync(fileRecordId, cancellationToken) : null;
        var parserVersion = sidecar?.ParserVersion ?? (bankCode switch
        {
            1 => "HDFC-v1",
            2 => "YES-v1",
            3 => "AXIS-v1",
            4 => "CENTRAL-v1",
            5 => "ICICI-v1",
            6 => "SBI-v1",
            7 => "BOI-v1",
            8 => "KOTAK-v1",
            9 => "PNB-v1",
            10 => "BOB-v1",
            _ => "v1"
        });

        var parsedList = transactions.Select(t => new ParsedTransaction
        {
            Id = t.Id,
            TransactionDate = t.TransactionDate,
            Description = t.Description,
            Debit = t.Debit,
            Credit = t.Credit,
            Amount = t.Amount,
            Balance = t.Balance,
            Reference = t.Reference,
            Utr = t.Utr,
            TransactionType = t.TransactionType ?? (t.Debit.HasValue ? "Debit" : "Credit"),
            BankCode = t.BankCode,
            BankName = bankName,
            ParserVersion = parserVersion,
            NeedsReview = !string.IsNullOrEmpty(t.ProcessingWarning),
            ReviewWarnings = !string.IsNullOrEmpty(t.ProcessingWarning) ? [t.ProcessingWarning] : [],
            Confidence = string.IsNullOrEmpty(t.ProcessingWarning) ? 1.0 : 0.75
        }).ToList();

        return new BankParsingResult
        {
            Success = true,
            BankCode = bankCode,
            BankName = bankName,
            ParserVersion = parserVersion,
            TotalDetected = importResult?.TotalDetected ?? parsedList.Count,
            ProcessedCount = importResult?.ProcessedCount ?? parsedList.Count,
            RejectedCount = importResult?.RejectedCount ?? 0,
            WarningsCount = importResult?.WarningsCount ?? parsedList.Count(p => p.NeedsReview),
            OverallConfidence = parsedList.Count > 0 ? parsedList.Average(p => p.Confidence) : 1.0,
            Transactions = parsedList
        };
    }
}
