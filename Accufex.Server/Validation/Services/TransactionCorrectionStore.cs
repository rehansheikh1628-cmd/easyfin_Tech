using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Accufex.Server.Models;
using Accufex.Server.Parsing.Models;
using Accufex.Server.Services;
using Accufex.Server.Validation.Models;
using Microsoft.Extensions.Logging;

namespace Accufex.Server.Validation.Services;

public class TransactionCorrectionStore : ITransactionCorrectionStore
{
    private const string SubDirectory = "corrections";
    private readonly IFileStorageService _fileStorageService;
    private readonly ILogger<TransactionCorrectionStore> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _statementLocks = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public TransactionCorrectionStore(
        IFileStorageService fileStorageService,
        ILogger<TransactionCorrectionStore> logger)
    {
        _fileStorageService = fileStorageService;
        _logger = logger;
    }

    private SemaphoreSlim GetLockForStatement(Guid statementFileId)
    {
        return _statementLocks.GetOrAdd(statementFileId, _ => new SemaphoreSlim(1, 1));
    }

    public async Task<StatementAuditSidecar> InitializeSnapshotAsync(
        Guid fileRecordId,
        BankParsingResult parsingResult,
        List<Transaction> persistedEntities,
        Guid clientId,
        Guid financialYearId,
        CancellationToken cancellationToken = default)
    {
        var sem = GetLockForStatement(fileRecordId);
        await sem.WaitAsync(cancellationToken);
        try
        {
            var sidecar = new StatementAuditSidecar
            {
                StatementFileId = fileRecordId,
                BankCode = parsingResult.BankCode,
                BankName = parsingResult.BankName,
                ParserVersion = parsingResult.ParserVersion,
                CreatedAtUtc = DateTime.UtcNow,
                LastModifiedAtUtc = DateTime.UtcNow,
                Records = new Dictionary<Guid, TransactionAuditRecord>(persistedEntities.Count)
            };

            for (int i = 0; i < persistedEntities.Count; i++)
            {
                var entity = persistedEntities[i];
                var parsed = i < parsingResult.Transactions.Count ? parsingResult.Transactions[i] : null;

                var snapshot = new OriginalTransactionSnapshot
                {
                    TransactionId = entity.Id,
                    TransactionDate = entity.TransactionDate,
                    ValueDate = parsed?.ValueDate,
                    Description = entity.Description,
                    Debit = entity.Debit,
                    Credit = entity.Credit,
                    Amount = entity.Amount,
                    Balance = entity.Balance,
                    Reference = entity.Reference,
                    Utr = entity.Utr,
                    TransactionType = entity.TransactionType,
                    BankCode = entity.BankCode,
                    Account = entity.Account,
                    SourceFileId = fileRecordId,
                    ClientId = clientId,
                    FinancialYearId = financialYearId,
                    SourcePageNumber = parsed?.SourcePageNumber ?? 1,
                    SourceLineIndex = parsed?.SourceLineIndex ?? i,
                    ParserVersion = parsed?.ParserVersion ?? parsingResult.ParserVersion,
                    ProcessingWarning = entity.ProcessingWarning
                };

                sidecar.Records[entity.Id] = new TransactionAuditRecord
                {
                    TransactionId = entity.Id,
                    IsCorrected = false,
                    OriginalValues = snapshot,
                    History = []
                };
            }

            await SaveSidecarInternalAsync(fileRecordId, sidecar, cancellationToken);
            _logger.LogInformation("Pristine audit sidecar initialized for statement {FileId} with {Count} original snapshots.",
                fileRecordId, sidecar.Records.Count);

            return sidecar;
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<StatementAuditSidecar?> GetAuditSidecarAsync(
        Guid fileRecordId,
        CancellationToken cancellationToken = default)
    {
        var sem = GetLockForStatement(fileRecordId);
        await sem.WaitAsync(cancellationToken);
        try
        {
            var fileName = $"statement_audit_{fileRecordId}.json";
            var stream = await _fileStorageService.GetFileStreamAsync(SubDirectory, fileName, cancellationToken);
            if (stream == null)
            {
                return null;
            }

            await using (stream)
            {
                try
                {
                    var sidecar = await JsonSerializer.DeserializeAsync<StatementAuditSidecar>(stream, JsonOptions, cancellationToken);
                    return sidecar;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to deserialize statement audit sidecar for file {FileId}. Corrupt or malformed sidecar file.", fileRecordId);
                    throw new InvalidOperationException($"Audit sidecar file for statement {fileRecordId} is malformed or unreadable.", ex);
                }
            }
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<bool> HasAuditSidecarAsync(
        Guid fileRecordId,
        CancellationToken cancellationToken = default)
    {
        var fileName = $"statement_audit_{fileRecordId}.json";
        return await _fileStorageService.FileExistsAsync(SubDirectory, fileName, cancellationToken);
    }

    public async Task<TransactionAuditRecord> ApplyCorrectionAsync(
        Guid fileRecordId,
        Guid transactionId,
        Guid userId,
        string reason,
        List<FieldChange> changes,
        CancellationToken cancellationToken = default)
    {
        var sem = GetLockForStatement(fileRecordId);
        await sem.WaitAsync(cancellationToken);
        try
        {
            var fileName = $"statement_audit_{fileRecordId}.json";
            var stream = await _fileStorageService.GetFileStreamAsync(SubDirectory, fileName, cancellationToken);
            if (stream == null)
            {
                _logger.LogError("Audit sidecar missing for statement {FileId}. Cannot apply correction without pristine snapshot.", fileRecordId);
                throw new InvalidOperationException($"Statement audit sidecar for file {fileRecordId} is missing. Refusing to overwrite transaction without pristine baseline.");
            }

            StatementAuditSidecar sidecar;
            await using (stream)
            {
                sidecar = await JsonSerializer.DeserializeAsync<StatementAuditSidecar>(stream, JsonOptions, cancellationToken)
                    ?? throw new InvalidOperationException($"Statement audit sidecar for file {fileRecordId} was empty.");
            }

            if (!sidecar.Records.TryGetValue(transactionId, out var record))
            {
                _logger.LogError("Transaction {TxId} not found in audit sidecar for statement {FileId}.", transactionId, fileRecordId);
                throw new KeyNotFoundException($"Transaction {transactionId} was not found in statement audit sidecar.");
            }

            // Append correction history entry without overwriting OriginalValues
            var historyEntry = new CorrectionHistoryEntry
            {
                TimestampUtc = DateTime.UtcNow,
                UserId = userId,
                Reason = reason,
                Changes = changes
            };

            record.History.Add(historyEntry);
            record.IsCorrected = true;
            sidecar.LastModifiedAtUtc = DateTime.UtcNow;

            await SaveSidecarInternalAsync(fileRecordId, sidecar, cancellationToken);
            _logger.LogInformation("Successfully recorded correction #{CorrectionNumber} for transaction {TxId} by user {UserId}.",
                record.History.Count, transactionId, userId);

            return record;
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task SaveSidecarInternalAsync(
        Guid fileRecordId,
        StatementAuditSidecar sidecar,
        CancellationToken cancellationToken)
    {
        var fileName = $"statement_audit_{fileRecordId}.json";
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(sidecar, JsonOptions);
        using var memoryStream = new MemoryStream(jsonBytes);

        await _fileStorageService.SaveFileAsync(memoryStream, SubDirectory, fileName, cancellationToken);
    }
}
