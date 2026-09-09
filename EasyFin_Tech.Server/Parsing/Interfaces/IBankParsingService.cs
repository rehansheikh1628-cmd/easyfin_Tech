using System;
using System.Threading;
using System.Threading.Tasks;
using EasyFin_Tech.Server.Parsing.Models;

namespace EasyFin_Tech.Server.Parsing.Interfaces;

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
