using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EasyFin_Tech.Server.DTOs;
using Microsoft.AspNetCore.Http;

namespace EasyFin_Tech.Server.Services;

public interface IStatementService
{
    Task<(bool Success, string? ErrorMessage, StatementUploadResponse? Response)> UploadStatementAsync(
        IFormFile file,
        Guid currentUserId,
        Guid? clientId = null,
        Guid? financialYearId = null,
        CancellationToken cancellationToken = default);

    Task<List<StatementSummaryDto>> GetStatementsAsync(Guid currentUserId, CancellationToken cancellationToken = default);

    Task<StatementDetailDto?> GetStatementByIdAsync(Guid id, Guid currentUserId, CancellationToken cancellationToken = default);

    Task<(Stream? Stream, string OriginalFileName, string ContentType)?> GetStatementFileStreamAsync(Guid id, Guid currentUserId, CancellationToken cancellationToken = default);

    Task<(bool Success, string? ErrorMessage)> DeleteStatementAsync(Guid id, Guid currentUserId, CancellationToken cancellationToken = default);
}
