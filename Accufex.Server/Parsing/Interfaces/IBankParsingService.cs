using System;
using System.Threading;
using System.Threading.Tasks;
using Accufex.Server.Parsing.Models;

namespace Accufex.Server.Parsing.Interfaces;

public interface IBankParsingService
{
    Task<(bool Success, string? ErrorMessage, BankParsingResult? Result)> ParseAndPersistStatementAsync(
        Guid fileRecordId,
        Guid currentUserId,
        CancellationToken cancellationToken = default);

    Task<BankParsingResult?> GetParsedTransactionsAsync(
        Guid fileRecordId,
        Guid currentUserId,
        CancellationToken cancellationToken = default);
}
