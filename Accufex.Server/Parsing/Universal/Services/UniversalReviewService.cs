using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Accufex.Server.Data;
using Accufex.Server.DTOs;
using Accufex.Server.Models;
using Accufex.Server.Parsing;
using Accufex.Server.Parsing.Models;
using Accufex.Server.Parsing.Universal.Interfaces;
using Accufex.Server.Parsing.Universal.Models;
using Accufex.Server.Services;
using Accufex.Server.Validation.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Accufex.Server.Parsing.Universal.Services;

public class UniversalReviewService : IUniversalReviewService
{
    private const string ReviewDir = "universal-reviews";
    private readonly AccufexDbContext _dbContext;
    private readonly IFileStorageService _fileStorageService;
    private readonly IPdfExtractionService _pdfExtractionService;
    private readonly IFinancialValidator _financialValidator;
    private readonly IUniversalConfidenceCalculator _confidenceCalculator;
    private readonly IUniversalRowSegmenter _rowSegmenter;
    private readonly ITransactionCorrectionStore _correctionStore;
    private readonly IStatementFormatLearner _formatLearner;
    private readonly ILogger<UniversalReviewService> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _statementLocks = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public UniversalReviewService(
        AccufexDbContext dbContext,
        IFileStorageService fileStorageService,
        IPdfExtractionService pdfExtractionService,
        IFinancialValidator financialValidator,
        IUniversalConfidenceCalculator confidenceCalculator,
        IUniversalRowSegmenter rowSegmenter,
        ITransactionCorrectionStore correctionStore,
        IStatementFormatLearner formatLearner,
        ILogger<UniversalReviewService> logger)
    {
        _dbContext = dbContext;
        _fileStorageService = fileStorageService;
        _pdfExtractionService = pdfExtractionService;
        _financialValidator = financialValidator;
        _confidenceCalculator = confidenceCalculator;
        _rowSegmenter = rowSegmenter;
        _correctionStore = correctionStore;
        _formatLearner = formatLearner;
        _logger = logger;
    }

    private SemaphoreSlim GetLockForStatement(Guid statementFileId)
    {
        return _statementLocks.GetOrAdd(statementFileId, _ => new SemaphoreSlim(1, 1));
    }

    public async Task<UniversalReviewSession> CreateOrUpdateReviewSessionAsync(
        Guid fileRecordId,
        UniversalParseResult parseResult,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var sem = GetLockForStatement(fileRecordId);
        await sem.WaitAsync(cancellationToken);
        try
        {
            var candidateDtos = new List<UniversalCandidateTransactionDto>();
            int rowNum = 1;

            foreach (var tx in parseResult.Transactions)
            {
                var isDateAmbiguous = tx.TransactionDate == default;
                var isDirectionAmbiguous = !tx.Debit.HasValue && !tx.Credit.HasValue;
                var direction = tx.Debit.HasValue && tx.Debit.Value > 0
                    ? "Debit"
                    : (tx.Credit.HasValue && tx.Credit.Value > 0 ? "Credit" : "Unknown");

                var warnings = new List<string>(tx.ReviewWarnings);
                var errors = new List<string>();

                if (isDateAmbiguous) warnings.Add("Transaction date could not be confidently determined.");
                if (isDirectionAmbiguous) errors.Add("Debit/Credit direction is uncertain.");
                if (tx.Debit.HasValue && tx.Debit.Value > 0 && tx.Credit.HasValue && tx.Credit.Value > 0)
                {
                    errors.Add("Both Debit and Credit contain non-zero values.");
                }

                var origValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Date"] = tx.TransactionDate != default ? tx.TransactionDate.ToString("yyyy-MM-dd") : null,
                    ["ValueDate"] = tx.ValueDate.HasValue ? tx.ValueDate.Value.ToString("yyyy-MM-dd") : null,
                    ["Description"] = tx.Description,
                    ["Reference"] = tx.Reference,
                    ["Debit"] = tx.Debit?.ToString("F2"),
                    ["Credit"] = tx.Credit?.ToString("F2"),
                    ["Amount"] = tx.Amount.ToString("F2"),
                    ["Balance"] = tx.Balance?.ToString("F2")
                };

                candidateDtos.Add(new UniversalCandidateTransactionDto
                {
                    Id = Guid.NewGuid(),
                    RowNumber = rowNum++,
                    PageNumber = tx.SourcePageNumber,
                    Date = tx.TransactionDate != default ? tx.TransactionDate : null,
                    ValueDate = tx.ValueDate,
                    Description = tx.Description,
                    Reference = tx.Reference,
                    Debit = tx.Debit,
                    Credit = tx.Credit,
                    Amount = tx.Amount,
                    Balance = tx.Balance,
                    Direction = direction,
                    IsDateAmbiguous = isDateAmbiguous,
                    IsDirectionAmbiguous = isDirectionAmbiguous,
                    IsBalanceMismatch = false,
                    ValidationWarnings = warnings,
                    ValidationErrors = errors,
                    IsUserEdited = false,
                    OriginalValues = origValues
                });
            }

            var finValidation = parseResult.FinancialValidation ?? new FinancialValidationResult
            {
                TotalRowsChecked = candidateDtos.Count,
                MissingBalanceCount = candidateDtos.Count(c => !c.Balance.HasValue),
                MissingAmountCount = candidateDtos.Count(c => c.IsDirectionAmbiguous)
            };

            var rowsRequiringAttention = candidateDtos
                .Where(c => c.ValidationErrors.Count > 0 || c.ValidationWarnings.Count > 0 || c.IsBalanceMismatch)
                .Select(c => c.RowNumber)
                .ToList();

            var generalWarnings = new List<string>(parseResult.Warnings);
            if (finValidation.FailedRowsCount > 0)
            {
                generalWarnings.Add($"Running balance failed to reconcile on {finValidation.FailedRowsCount} row(s).");
            }
            if (finValidation.MissingAmountCount > 0)
            {
                generalWarnings.Add($"{finValidation.MissingAmountCount} transaction(s) have uncertain debit/credit classification.");
            }

            bool isConversionAllowed = candidateDtos.Count > 0 &&
                                       candidateDtos.All(c => c.ValidationErrors.Count == 0 && c.Date.HasValue && c.Amount > 0);

            var session = new UniversalReviewSession
            {
                FileRecordId = fileRecordId,
                FileName = fileName,
                Status = "ReviewRequired",
                DetectedBank = parseResult.DetectedBankName ?? "Unknown Bank",
                Structure = parseResult.Structure,
                Columns = parseResult.Structure?.Columns ?? [],
                Confidence = parseResult.Confidence,
                FinancialValidation = finValidation,
                CandidateTransactions = candidateDtos,
                Warnings = generalWarnings,
                RowsRequiringAttention = rowsRequiringAttention,
                IsApprovalRequired = true,
                IsConversionAllowed = isConversionAllowed,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            await SaveReviewSessionInternalAsync(session, cancellationToken);
            _logger.LogInformation("Initialized review session for statement {FileId} with {TxCount} candidate rows.", fileRecordId, candidateDtos.Count);
            return session;
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<UniversalReviewSession?> GetReviewSessionAsync(
        Guid fileRecordId,
        CancellationToken cancellationToken = default)
    {
        var fileName = $"{fileRecordId}.json";
        if (!await _fileStorageService.FileExistsAsync(ReviewDir, fileName, cancellationToken))
        {
            return null;
        }

        var stream = await _fileStorageService.GetFileStreamAsync(ReviewDir, fileName, cancellationToken);
        if (stream == null) return null;

        using var reader = new StreamReader(stream);
        var json = await reader.ReadToEndAsync(cancellationToken);
        return JsonSerializer.Deserialize<UniversalReviewSession>(json, JsonOptions);
    }

    public async Task<UniversalReviewSession> UpdateTransactionAsync(
        Guid fileRecordId,
        Guid candidateId,
        CorrectUniversalTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        var sem = GetLockForStatement(fileRecordId);
        await sem.WaitAsync(cancellationToken);
        try
        {
            var session = await GetReviewSessionAsync(fileRecordId, cancellationToken)
                ?? throw new KeyNotFoundException($"Review session for statement {fileRecordId} was not found.");

            var tx = session.CandidateTransactions.FirstOrDefault(c => c.Id == candidateId)
                ?? throw new KeyNotFoundException($"Candidate transaction {candidateId} was not found.");

            // 1. Validate correction inputs
            if (string.IsNullOrWhiteSpace(request.Description))
            {
                throw new ArgumentException("Description cannot be empty.", nameof(request.Description));
            }

            if (!request.TransactionDate.HasValue || request.TransactionDate.Value == default || request.TransactionDate.Value.Year < 1990 || request.TransactionDate.Value.Year > 2100)
            {
                throw new ArgumentException("A valid transaction date between 1990 and 2100 is required.", nameof(request.TransactionDate));
            }

            var debit = request.Debit;
            var credit = request.Credit;

            if (debit.HasValue && debit.Value > 0 && credit.HasValue && credit.Value > 0)
            {
                throw new ArgumentException("Both Debit and Credit cannot be populated simultaneously.", nameof(request.Debit));
            }

            if ((!debit.HasValue || debit.Value <= 0) && (!credit.HasValue || credit.Value <= 0))
            {
                throw new ArgumentException("Either Debit or Credit must be populated with a positive amount.", nameof(request.Debit));
            }

            if ((debit.HasValue && debit.Value < 0) || (credit.HasValue && credit.Value < 0))
            {
                throw new ArgumentException("Monetary values cannot be negative.", nameof(request.Debit));
            }

            decimal amount = debit.HasValue && debit.Value > 0 ? debit.Value : (credit ?? 0m);

            // 2. Apply edits to candidate transaction
            tx.Date = request.TransactionDate.Value;
            tx.ValueDate = request.ValueDate ?? request.TransactionDate.Value;
            tx.Description = request.Description.Trim();
            tx.Reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim();
            tx.Debit = debit.HasValue && debit.Value > 0 ? debit.Value : null;
            tx.Credit = credit.HasValue && credit.Value > 0 ? credit.Value : null;
            tx.Amount = amount;
            tx.Balance = request.Balance;
            tx.Direction = tx.Debit.HasValue ? "Debit" : "Credit";
            tx.IsDateAmbiguous = false;
            tx.IsDirectionAmbiguous = false;
            tx.IsUserEdited = true;

            // Clear previous errors for this transaction
            tx.ValidationErrors.Clear();
            tx.ValidationWarnings.Clear();

            // 3. Re-run financial balance validation across candidate rows
            var parsedTxList = session.CandidateTransactions.Select(c => new ParsedTransaction
            {
                Id = c.Id,
                TransactionDate = c.Date ?? default,
                ValueDate = c.ValueDate,
                Description = c.Description,
                Debit = c.Debit,
                Credit = c.Credit,
                Amount = c.Amount,
                Balance = c.Balance,
                Reference = c.Reference,
                TransactionType = c.Direction,
                SourcePageNumber = c.PageNumber
            }).ToList();

            var newFinValidation = _financialValidator.Validate(parsedTxList, null, null);
            session.FinancialValidation = newFinValidation;

            // 4. Update attention list and conversion allowance
            session.RowsRequiringAttention = session.CandidateTransactions
                .Where(c => c.ValidationErrors.Count > 0 || c.ValidationWarnings.Count > 0 || c.IsBalanceMismatch)
                .Select(c => c.RowNumber)
                .ToList();

            session.IsConversionAllowed = session.CandidateTransactions.Count > 0 &&
                                          session.CandidateTransactions.All(c => c.ValidationErrors.Count == 0 && c.Date.HasValue && c.Amount > 0);

            // Re-evaluate confidence
            if (session.Structure != null)
            {
                session.Confidence = _confidenceCalculator.CalculateConfidence(session.Structure, parsedTxList, null, null, newFinValidation);
            }

            session.UpdatedAtUtc = DateTime.UtcNow;
            await SaveReviewSessionInternalAsync(session, cancellationToken);

            _logger.LogInformation("Updated candidate transaction {CandidateId} in statement {FileId}.", candidateId, fileRecordId);
            return session;
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<UniversalReviewSession> UpdateColumnsAndReprocessAsync(
        Guid fileRecordId,
        List<DetectedColumnLayout> columns,
        CancellationToken cancellationToken = default)
    {
        var sem = GetLockForStatement(fileRecordId);
        await sem.WaitAsync(cancellationToken);
        try
        {
            var session = await GetReviewSessionAsync(fileRecordId, cancellationToken)
                ?? throw new KeyNotFoundException($"Review session for statement {fileRecordId} was not found.");

            // 1. Update column layout
            session.Columns = columns.OrderBy(c => c.LeftX).ToList();
            if (session.Structure != null)
            {
                session.Structure.Columns = session.Columns;
            }

            // 2. Retrieve extraction result to reprocess
            var fileRecord = await _dbContext.FileRecords
                .Include(f => f.Client)
                .FirstOrDefaultAsync(f => f.Id == fileRecordId, cancellationToken)
                ?? throw new KeyNotFoundException($"Statement file record {fileRecordId} was not found.");

            var extractionResult = await _pdfExtractionService.GetExtractionResultAsync(fileRecordId, fileRecord.Client.UserId, cancellationToken);
            if (extractionResult != null && session.Structure != null)
            {
                var candidateRows = _rowSegmenter.SegmentDocumentRows(extractionResult.Pages, session.Structure);
                var reprocessedCandidates = new List<UniversalCandidateTransactionDto>();
                int rowNum = 1;

                foreach (var row in candidateRows)
                {
                    string dateText = string.Empty;
                    string descText = string.Empty;
                    string refText = string.Empty;
                    string debitText = string.Empty;
                    string creditText = string.Empty;
                    string balanceText = string.Empty;

                    foreach (var fragment in row.Fragments)
                    {
                        var col = FindMatchingColumn(fragment.X, session.Columns);
                        if (col == null) continue;

                        switch (col.ColumnType)
                        {
                            case StatementColumnType.Date:
                                dateText += " " + fragment.Text;
                                break;
                            case StatementColumnType.Description:
                                descText += " " + fragment.Text;
                                break;
                            case StatementColumnType.Reference:
                                refText += " " + fragment.Text;
                                break;
                            case StatementColumnType.Debit:
                                debitText += " " + fragment.Text;
                                break;
                            case StatementColumnType.Credit:
                                creditText += " " + fragment.Text;
                                break;
                            case StatementColumnType.Balance:
                                balanceText += " " + fragment.Text;
                                break;
                        }
                    }

                    DateTime? parsedDate = DateTime.TryParse(dateText.Trim(), out var dt) ? dt : null;
                    decimal? parsedDebit = decimal.TryParse(debitText.Trim().Replace(",", ""), out var d) ? d : null;
                    decimal? parsedCredit = decimal.TryParse(creditText.Trim().Replace(",", ""), out var c) ? c : null;
                    decimal? parsedBalance = decimal.TryParse(balanceText.Trim().Replace(",", ""), out var b) ? b : null;
                    decimal amount = parsedDebit ?? parsedCredit ?? 0m;
                    string direction = parsedDebit.HasValue ? "Debit" : (parsedCredit.HasValue ? "Credit" : "Unknown");

                    var errors = new List<string>();
                    var warnings = new List<string>();
                    if (!parsedDate.HasValue) warnings.Add("Date could not be determined.");
                    if (!parsedDebit.HasValue && !parsedCredit.HasValue) errors.Add("Debit/Credit direction is uncertain.");

                    reprocessedCandidates.Add(new UniversalCandidateTransactionDto
                    {
                        Id = Guid.NewGuid(),
                        RowNumber = rowNum++,
                        PageNumber = row.PageNumber,
                        Date = parsedDate,
                        ValueDate = parsedDate,
                        Description = descText.Trim(),
                        Reference = string.IsNullOrWhiteSpace(refText) ? null : refText.Trim(),
                        Debit = parsedDebit,
                        Credit = parsedCredit,
                        Amount = amount,
                        Balance = parsedBalance,
                        Direction = direction,
                        IsDateAmbiguous = !parsedDate.HasValue,
                        IsDirectionAmbiguous = !parsedDebit.HasValue && !parsedCredit.HasValue,
                        ValidationErrors = errors,
                        ValidationWarnings = warnings,
                        IsUserEdited = false
                    });
                }

                if (reprocessedCandidates.Count > 0)
                {
                    session.CandidateTransactions = reprocessedCandidates;
                }
            }

            // Re-evaluate validation & confidence
            var parsedList = session.CandidateTransactions.Select(c => new ParsedTransaction
            {
                Id = c.Id,
                TransactionDate = c.Date ?? default,
                ValueDate = c.ValueDate,
                Description = c.Description,
                Debit = c.Debit,
                Credit = c.Credit,
                Amount = c.Amount,
                Balance = c.Balance,
                Reference = c.Reference,
                TransactionType = c.Direction,
                SourcePageNumber = c.PageNumber
            }).ToList();

            session.FinancialValidation = _financialValidator.Validate(parsedList, null, null);
            if (session.Structure != null)
            {
                session.Confidence = _confidenceCalculator.CalculateConfidence(session.Structure, parsedList, null, null, session.FinancialValidation);
            }

            session.RowsRequiringAttention = session.CandidateTransactions
                .Where(c => c.ValidationErrors.Count > 0 || c.ValidationWarnings.Count > 0 || c.IsBalanceMismatch)
                .Select(c => c.RowNumber)
                .ToList();

            session.IsConversionAllowed = session.CandidateTransactions.Count > 0 &&
                                          session.CandidateTransactions.All(c => c.ValidationErrors.Count == 0 && c.Date.HasValue && c.Amount > 0);

            session.UpdatedAtUtc = DateTime.UtcNow;
            await SaveReviewSessionInternalAsync(session, cancellationToken);

            _logger.LogInformation("Reprocessed columns for statement {FileId}. New candidate count: {Count}", fileRecordId, session.CandidateTransactions.Count);
            return session;
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<(bool Success, string? Error, List<Transaction> PersistedTransactions, string? FingerprintHash)> ApproveAndConvertAsync(
        Guid fileRecordId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var sem = GetLockForStatement(fileRecordId);
        await sem.WaitAsync(cancellationToken);
        try
        {
            // 1. Strict Tenant Authorization
            var fileRecord = await _dbContext.FileRecords
                .Include(f => f.Client)
                .FirstOrDefaultAsync(f => f.Id == fileRecordId && f.Client.UserId == userId, cancellationToken);

            if (fileRecord == null)
            {
                return (false, $"Statement {fileRecordId} was not found or is not accessible.", [], null);
            }

            // 2. Idempotency Check: If already converted, return existing persisted transactions without duplicates
            var existingTransactions = await _dbContext.Transactions
                .Where(t => t.SourceFileId == fileRecordId)
                .OrderBy(t => t.TransactionDate)
                .ThenBy(t => t.CreatedAt)
                .ToListAsync(cancellationToken);

            if (fileRecord.ProcessingStatus == 2 && existingTransactions.Count > 0)
            {
                _logger.LogInformation("Statement {FileId} was already approved and converted. Returning {Count} existing transactions (Idempotent).", fileRecordId, existingTransactions.Count);
                return (true, null, existingTransactions, null);
            }

            // 3. Load Review Session
            var session = await GetReviewSessionAsync(fileRecordId, cancellationToken);
            if (session == null || session.CandidateTransactions.Count == 0)
            {
                return (false, "No candidate transactions are available for approval.", [], null);
            }

            // 4. Final Validation: Verify all candidate rows
            var invalidRows = session.CandidateTransactions
                .Where(c => !c.Date.HasValue || c.Amount <= 0 || (c.Debit.HasValue && c.Credit.HasValue && c.Debit.Value > 0 && c.Credit.Value > 0) || c.ValidationErrors.Count > 0)
                .ToList();

            if (invalidRows.Count > 0)
            {
                var errorSummary = $"Cannot approve statement: {invalidRows.Count} transaction(s) have unresolved errors (Rows: {string.Join(", ", invalidRows.Take(5).Select(r => r.RowNumber))}). Please review and correct them.";
                return (false, errorSummary, [], null);
            }

            // 5. Atomic Conversion: Map candidate rows to canonical Transaction entities
            var now = DateTime.UtcNow;
            var canonicalTransactions = session.CandidateTransactions.Select(c => new Transaction
            {
                Id = Guid.NewGuid(),
                SourceFileId = fileRecordId,
                ClientId = fileRecord.ClientId,
                FinancialYearId = fileRecord.FinancialYearId,
                BankCode = (int)BankType.Universal,
                TransactionDate = c.Date!.Value,
                Description = c.Description,
                Debit = c.Debit,
                Credit = c.Credit,
                Amount = c.Amount,
                Balance = c.Balance,
                Reference = c.Reference,
                TransactionType = c.Direction,
                CreatedAt = now
            }).ToList();

            bool isInMemory = _dbContext.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true;

            if (isInMemory)
            {
                _dbContext.Transactions.AddRange(canonicalTransactions);

                var existingImport = await _dbContext.TransactionImportResults.FirstOrDefaultAsync(r => r.SourceFileId == fileRecordId, cancellationToken);
                if (existingImport == null)
                {
                    _dbContext.TransactionImportResults.Add(new TransactionImportResult
                    {
                        Id = Guid.NewGuid(),
                        SourceFileId = fileRecordId,
                        ClientId = fileRecord.ClientId,
                        FinancialYearId = fileRecord.FinancialYearId,
                        DetectedBank = (int)BankType.Universal,
                        TotalDetected = canonicalTransactions.Count,
                        ProcessedCount = canonicalTransactions.Count,
                        RejectedCount = 0,
                        WarningsCount = 0,
                        Status = "Completed",
                        StartedAt = session.CreatedAtUtc,
                        CompletedAt = now
                    });
                }
                else
                {
                    existingImport.TotalDetected = canonicalTransactions.Count;
                    existingImport.ProcessedCount = canonicalTransactions.Count;
                }

                fileRecord.ProcessingStatus = 2; // Completed
                fileRecord.ProcessingError = null;
                fileRecord.UpdatedAt = now;

                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            else
            {
                var strategy = _dbContext.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    await using var dbTx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                    try
                    {
                        _dbContext.Transactions.AddRange(canonicalTransactions);

                        var existingImport = await _dbContext.TransactionImportResults.FirstOrDefaultAsync(r => r.SourceFileId == fileRecordId, cancellationToken);
                        if (existingImport == null)
                        {
                            _dbContext.TransactionImportResults.Add(new TransactionImportResult
                            {
                                Id = Guid.NewGuid(),
                                SourceFileId = fileRecordId,
                                ClientId = fileRecord.ClientId,
                                FinancialYearId = fileRecord.FinancialYearId,
                                DetectedBank = (int)BankType.Universal,
                                TotalDetected = canonicalTransactions.Count,
                                ProcessedCount = canonicalTransactions.Count,
                                RejectedCount = 0,
                                WarningsCount = 0,
                                Status = "Completed",
                                StartedAt = session.CreatedAtUtc,
                                CompletedAt = now
                            });
                        }
                        else
                        {
                            existingImport.TotalDetected = canonicalTransactions.Count;
                            existingImport.ProcessedCount = canonicalTransactions.Count;
                        }

                        fileRecord.ProcessingStatus = 2; // Completed
                        fileRecord.ProcessingError = null;
                        fileRecord.UpdatedAt = now;

                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await dbTx.CommitAsync(cancellationToken);
                    }
                    catch
                    {
                        await dbTx.RollbackAsync(cancellationToken);
                        throw;
                    }
                });
            }

            // 6. Initialize Audit Sidecar Baseline for ACCUFEX Verification & Editing
            try
            {
                var bankParsingResult = new BankParsingResult
                {
                    Success = true,
                    BankCode = (int)BankType.Universal,
                    BankName = session.DetectedBank ?? "Unknown Bank",
                    ParserVersion = "Universal-v1",
                    Transactions = session.CandidateTransactions.Select(c => new ParsedTransaction
                    {
                        Id = c.Id,
                        TransactionDate = c.Date!.Value,
                        ValueDate = c.ValueDate ?? c.Date!.Value,
                        Description = c.Description,
                        Debit = c.Debit,
                        Credit = c.Credit,
                        Amount = c.Amount,
                        Balance = c.Balance,
                        Reference = c.Reference,
                        TransactionType = c.Direction,
                        SourcePageNumber = c.PageNumber
                    }).ToList()
                };

                await _correctionStore.InitializeSnapshotAsync(
                    fileRecordId,
                    bankParsingResult,
                    canonicalTransactions,
                    fileRecord.ClientId,
                    fileRecord.FinancialYearId,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to initialize audit sidecar for approved statement {FileId}.", fileRecordId);
            }

            // 7. Generate & Persist Layout Fingerprint for Future Template Learning
            string? fingerprintHash = null;
            try
            {
                var extraction = await _pdfExtractionService.GetExtractionResultAsync(fileRecordId, userId, cancellationToken);
                if (extraction != null)
                {
                    var parseResult = new UniversalParseResult
                    {
                        Success = true,
                        DetectedBankName = session.DetectedBank,
                        Transactions = session.CandidateTransactions.Select(c => new ParsedTransaction
                        {
                            Id = c.Id,
                            TransactionDate = c.Date ?? default,
                            Description = c.Description,
                            Debit = c.Debit,
                            Credit = c.Credit,
                            Amount = c.Amount,
                            Balance = c.Balance
                        }).ToList()
                    };

                    var fingerprint = _formatLearner.GenerateFingerprint(parseResult, session.Columns, extraction);
                    await _formatLearner.SaveFingerprintAsync(fingerprint, cancellationToken);
                    fingerprintHash = fingerprint.FingerprintHash;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate layout fingerprint for statement {FileId}.", fileRecordId);
            }

            // 8. Update Review Session Status to Converted
            session.Status = "Converted";
            session.UpdatedAtUtc = DateTime.UtcNow;
            await SaveReviewSessionInternalAsync(session, cancellationToken);

            _logger.LogInformation("Successfully approved and converted {Count} transactions for statement {FileId}.", canonicalTransactions.Count, fileRecordId);
            return (true, null, canonicalTransactions, fingerprintHash);
        }
        finally
        {
            sem.Release();
        }
    }

    private static DetectedColumnLayout? FindMatchingColumn(double x, List<DetectedColumnLayout> sortedColumns)
    {
        for (int i = 0; i < sortedColumns.Count; i++)
        {
            var col = sortedColumns[i];
            double leftBound = col.LeftX - 10.0;
            double rightBound = i < sortedColumns.Count - 1 ? sortedColumns[i + 1].LeftX - 5.0 : col.RightX + 25.0;

            if (x >= leftBound && x < rightBound)
            {
                return col;
            }
        }
        return null;
    }

    private async Task SaveReviewSessionInternalAsync(
        UniversalReviewSession session,
        CancellationToken cancellationToken)
    {
        var fileName = $"{session.FileRecordId}.json";
        var json = JsonSerializer.Serialize(session, JsonOptions);
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await _fileStorageService.SaveFileAsync(stream, ReviewDir, fileName, cancellationToken);
    }
}
