using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EasyFin_Tech.Server.Models;
using EasyFin_Tech.Server.Parsing.Models;
using EasyFin_Tech.Server.Validation.Models;

namespace EasyFin_Tech.Server.Validation.Services;

public interface ITransactionCorrectionStore
{
    Task<StatementAuditSidecar> InitializeSnapshotAsync(
        Guid fileRecordId,
        BankParsingResult parsingResult,
        List<Transaction> persistedEntities,
        Guid clientId,
        Guid financialYearId,
        CancellationToken cancellationToken = default);

    Task<StatementAuditSidecar?> GetAuditSidecarAsync(
        Guid fileRecordId,
        CancellationToken cancellationToken = default);

    Task<bool> HasAuditSidecarAsync(
        Guid fileRecordId,
        CancellationToken cancellationToken = default);

    Task<TransactionAuditRecord> ApplyCorrectionAsync(
        Guid fileRecordId,
        Guid transactionId,
        Guid userId,
        string reason,
        List<FieldChange> changes,
        CancellationToken cancellationToken = default);
}
