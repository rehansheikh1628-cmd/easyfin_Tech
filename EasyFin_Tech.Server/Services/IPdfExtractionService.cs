using System;
using System.Threading;
using System.Threading.Tasks;
using EasyFin_Tech.Server.DTOs;

namespace EasyFin_Tech.Server.Services;

public interface IPdfExtractionService
{
    Task<(bool Success, string? ErrorMessage, PdfExtractionResult? Result)> ExtractDocumentAsync(
        Guid fileRecordId,
        Guid currentUserId,
        string? password = null,
        CancellationToken cancellationToken = default);

    Task<PdfExtractionResult?> GetExtractionResultAsync(
        Guid fileRecordId,
        Guid currentUserId,
        CancellationToken cancellationToken = default);
}
