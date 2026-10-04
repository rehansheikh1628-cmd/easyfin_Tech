using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Accufex.Server.DTOs;
using Accufex.Server.Models;
using Accufex.Server.Parsing.Universal.Models;

namespace Accufex.Server.Parsing.Universal.Interfaces;

public interface IUniversalReviewService
{
    Task<UniversalReviewSession> CreateOrUpdateReviewSessionAsync(
        Guid fileRecordId,
        UniversalParseResult parseResult,
        string fileName,
        CancellationToken cancellationToken = default);

    Task<UniversalReviewSession?> GetReviewSessionAsync(
        Guid fileRecordId,
        CancellationToken cancellationToken = default);

    Task<UniversalReviewSession> UpdateTransactionAsync(
        Guid fileRecordId,
        Guid candidateId,
        CorrectUniversalTransactionRequest request,
        CancellationToken cancellationToken = default);

    Task<UniversalReviewSession> UpdateColumnsAndReprocessAsync(
        Guid fileRecordId,
        List<DetectedColumnLayout> columns,
        CancellationToken cancellationToken = default);

    Task<(bool Success, string? Error, List<Transaction> PersistedTransactions, string? FingerprintHash)> ApproveAndConvertAsync(
        Guid fileRecordId,
        Guid userId,
        CancellationToken cancellationToken = default);
}
