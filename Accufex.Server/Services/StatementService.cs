using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Accufex.Server.Data;
using Accufex.Server.DTOs;
using Accufex.Server.Models;
using Accufex.Server.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Accufex.Server.Services;

public class StatementService : IStatementService
{
    private readonly AccufexDbContext _dbContext;
    private readonly IFileStorageService _storageService;
    private readonly FileUploadOptions _options;
    private readonly ILogger<StatementService> _logger;

    // PDF Magic Bytes signature: %PDF- (0x25, 0x50, 0x44, 0x46, 0x2D)
    private static readonly byte[] PdfHeaderSignature = [0x25, 0x50, 0x44, 0x46, 0x2D];

    public StatementService(
        AccufexDbContext dbContext,
        IFileStorageService storageService,
        IOptions<FileUploadOptions> options,
        ILogger<StatementService> logger)
    {
        _dbContext = dbContext;
        _storageService = storageService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<(bool Success, string? ErrorMessage, StatementUploadResponse? Response)> UploadStatementAsync(
        IFormFile file,
        Guid currentUserId,
        Guid? clientId = null,
        Guid? financialYearId = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Validate File Existence
        if (file == null || file.Length == 0)
        {
            _logger.LogWarning("Upload rejected: file is empty or null.");
            return (false, "Please select a non-empty PDF file to upload.", null);
        }

        // 2. Validate File Size
        if (file.Length > _options.MaxPdfSizeBytes)
        {
            var maxMb = _options.MaxPdfSizeBytes / (1024.0 * 1024.0);
            _logger.LogWarning("Upload rejected: file size {Bytes} exceeds maximum {MaxBytes} bytes.", file.Length, _options.MaxPdfSizeBytes);
            return (false, $"File exceeds the maximum allowed upload size of {maxMb:F0} MB.", null);
        }

        // 3. Validate File Extension
        var extension = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(extension) || !extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Upload rejected: invalid extension '{Extension}'.", extension);
            return (false, "Only PDF files (.pdf) are supported.", null);
        }

        // 4. Validate PDF Magic Bytes (%PDF-)
        await using var stream = file.OpenReadStream();
        if (!IsValidPdfHeader(stream))
        {
            _logger.LogWarning("Upload rejected: file fails PDF signature validation (header mismatch).");
            return (false, "The file does not appear to be a valid PDF document.", null);
        }

        // 5. Compute SHA-256 Hash
        var fileHash = await _storageService.ComputeSha256HashAsync(stream, cancellationToken);

        // 6. Resolve Target Client and Financial Year scoped to authenticated user
        var (targetClient, targetYear) = await ResolveClientAndYearAsync(currentUserId, clientId, financialYearId, cancellationToken);
        if (targetClient == null || targetYear == null)
        {
            _logger.LogWarning("Upload rejected for user {UserId}: workspace or financial year unauthorized or unavailable.", currentUserId);
            return (false, "The requested workspace or financial year is not accessible.", null);
        }

        // 7. Check for Potential Duplicates
        var sanitizedOriginalFileName = Path.GetFileName(file.FileName);
        var existingDuplicate = await _dbContext.FileRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.ClientId == targetClient.Id &&
                                      f.OriginalFileName == sanitizedOriginalFileName &&
                                      f.SizeBytes == file.Length,
                                cancellationToken);

        var isDuplicate = existingDuplicate != null;
        string? duplicateNotice = null;
        if (isDuplicate)
        {
            duplicateNotice = $"A statement with this name and size was previously uploaded on {existingDuplicate!.UploadedAt:yyyy-MM-dd HH:mm:ss} UTC.";
            _logger.LogInformation("Duplicate detected for file {FileName}: previously recorded with ID {ExistingId}",
                sanitizedOriginalFileName, existingDuplicate.Id);
        }

        // 8. Generate Safe File ID and Internal Storage Name
        var fileId = Guid.NewGuid();
        var safeFileName = $"{fileId}.pdf";
        var yearFolder = targetYear.StartDate.Year.ToString();
        var subDirectory = Path.Combine("tenants", targetClient.UserId.ToString("N"), "clients", targetClient.Id.ToString("N"), "statements", yearFolder);

        // 9. Store File Safely Outside Public Web Root
        string savedPath;
        try
        {
            savedPath = await _storageService.SaveFileAsync(stream, subDirectory, safeFileName, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store statement file securely on disk.");
            await _storageService.DeleteFileAsync(subDirectory, safeFileName, CancellationToken.None);
            return (false, "An error occurred while saving the statement securely.", null);
        }

        // 10. Create and Persist Database Record (FileRecord)
        var fileRecord = new FileRecord
        {
            Id = fileId,
            ClientId = targetClient.Id,
            FinancialYearId = targetYear.Id,
            OriginalFileName = sanitizedOriginalFileName,
            StoredFileName = safeFileName,
            Extension = ".pdf",
            ContentType = "application/pdf",
            SizeBytes = file.Length,
            UploadedAt = DateTime.UtcNow,
            ProcessingStatus = 0, // 0 = Uploaded / ReadyForProcessing
            ProcessingError = null
        };

        try
        {
            _dbContext.FileRecords.Add(fileRecord);
            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Created FileRecord {FileId} for statement {FileName} in database.", fileId, sanitizedOriginalFileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database insert failed for FileRecord {FileId}. Performing storage rollback.", fileId);
            // Rollback stored physical file to prevent orphan files
            await _storageService.DeleteFileAsync(subDirectory, safeFileName, CancellationToken.None);
            return (false, "Failed to register statement in database.", null);
        }

        var response = new StatementUploadResponse
        {
            Success = true,
            Message = isDuplicate
                ? "Statement uploaded successfully (duplicate record noted)."
                : "Statement uploaded successfully and ready for extraction.",
            FileId = fileId,
            OriginalFileName = sanitizedOriginalFileName,
            StoredFileName = safeFileName,
            FileSizeBytes = file.Length,
            FileSizeFormatted = FormatBytes(file.Length),
            ContentType = "application/pdf",
            UploadedAt = fileRecord.UploadedAt,
            Status = "ReadyForProcessing",
            ProcessingStatus = fileRecord.ProcessingStatus,
            IsDuplicate = isDuplicate,
            DuplicateNotice = duplicateNotice,
            FileHash = fileHash,
            ClientId = targetClient.Id,
            FinancialYearId = targetYear.Id
        };

        return (true, null, response);
    }

    public async Task<List<StatementSummaryDto>> GetStatementsAsync(Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var records = await _dbContext.FileRecords
            .AsNoTracking()
            .Where(f => f.Client.UserId == currentUserId)
            .Include(f => f.Client)
            .Include(f => f.FinancialYear)
            .OrderByDescending(f => f.UploadedAt)
            .ToListAsync(cancellationToken);

        return records.Select(r => new StatementSummaryDto
        {
            Id = r.Id,
            OriginalFileName = r.OriginalFileName,
            FileSizeBytes = r.SizeBytes,
            FileSizeFormatted = FormatBytes(r.SizeBytes),
            UploadedAt = r.UploadedAt,
            Status = MapStatusName(r.ProcessingStatus),
            ProcessingStatus = r.ProcessingStatus,
            ProcessingError = r.ProcessingError,
            ClientId = r.ClientId,
            ClientName = r.Client?.Name,
            FinancialYearId = r.FinancialYearId,
            FinancialYearName = r.FinancialYear?.DisplayName
        }).ToList();
    }

    public async Task<StatementDetailDto?> GetStatementByIdAsync(Guid id, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.FileRecords
            .AsNoTracking()
            .Where(f => f.Id == id && f.Client.UserId == currentUserId)
            .Include(f => f.Client)
            .Include(f => f.FinancialYear)
            .FirstOrDefaultAsync(cancellationToken);

        if (record == null) return null;

        return new StatementDetailDto
        {
            Id = record.Id,
            OriginalFileName = record.OriginalFileName,
            StoredFileName = record.StoredFileName,
            Extension = record.Extension,
            ContentType = record.ContentType,
            FileSizeBytes = record.SizeBytes,
            FileSizeFormatted = FormatBytes(record.SizeBytes),
            UploadedAt = record.UploadedAt,
            UpdatedAt = record.UpdatedAt,
            Status = MapStatusName(record.ProcessingStatus),
            ProcessingStatus = record.ProcessingStatus,
            ProcessingError = record.ProcessingError,
            ClientId = record.ClientId,
            ClientName = record.Client?.Name,
            FinancialYearId = record.FinancialYearId,
            FinancialYearName = record.FinancialYear?.DisplayName
        };
    }

    public async Task<(Stream? Stream, string OriginalFileName, string ContentType)?> GetStatementFileStreamAsync(Guid id, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.FileRecords
            .AsNoTracking()
            .Where(f => f.Id == id && f.Client.UserId == currentUserId)
            .Include(f => f.FinancialYear)
            .FirstOrDefaultAsync(cancellationToken);

        if (record == null) return null;

        var yearFolder = (record.FinancialYear?.StartDate.Year ?? record.UploadedAt.Year).ToString();
        var tenantSubDir = Path.Combine("tenants", currentUserId.ToString("N"), "clients", record.ClientId.ToString("N"), "statements", yearFolder);
        var legacySubDir = Path.Combine(record.ClientId.ToString("N"), yearFolder);

        var stream = await _storageService.GetFileStreamAsync(tenantSubDir, record.StoredFileName, cancellationToken)
            ?? await _storageService.GetFileStreamAsync(legacySubDir, record.StoredFileName, cancellationToken);

        if (stream == null) return null;

        return (stream, record.OriginalFileName, record.ContentType);
    }

    public async Task<(bool Success, string? ErrorMessage)> DeleteStatementAsync(Guid id, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.FileRecords
            .Include(f => f.Client)
            .Include(f => f.FinancialYear)
            .FirstOrDefaultAsync(f => f.Id == id && f.Client.UserId == currentUserId, cancellationToken);

        if (record == null)
        {
            return (false, "Statement not found or you are not authorized to delete it.");
        }

        var yearFolder = (record.FinancialYear?.StartDate.Year ?? record.UploadedAt.Year).ToString();
        var tenantSubDir = Path.Combine("tenants", currentUserId.ToString("N"), "clients", record.ClientId.ToString("N"), "statements", yearFolder);
        var legacySubDir = Path.Combine(record.ClientId.ToString("N"), yearFolder);

        // 1. Delete physical storage files (tenant path, legacy path, extraction sidecar, audit sidecar)
        try
        {
            await _storageService.DeleteFileAsync(tenantSubDir, record.StoredFileName, cancellationToken);
            await _storageService.DeleteFileAsync(legacySubDir, record.StoredFileName, cancellationToken);

            var extractionDir = Path.Combine("Extractions", record.ClientId.ToString("N"));
            var extractionFileName = $"{record.Id}_extraction.json";
            await _storageService.DeleteFileAsync(extractionDir, extractionFileName, cancellationToken);

            var auditDir = "corrections";
            var auditFileName = $"statement_audit_{record.Id:N}.json";
            await _storageService.DeleteFileAsync(auditDir, auditFileName, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete one or more storage artifacts for FileRecord {FileId}", id);
        }

        // 2. Cascading database entity removal
        var transactions = await _dbContext.Transactions
            .Where(t => t.SourceFileId == id)
            .ToListAsync(cancellationToken);
        if (transactions.Count != 0)
        {
            _dbContext.Transactions.RemoveRange(transactions);
        }

        var importResults = await _dbContext.TransactionImportResults
            .Where(ir => ir.SourceFileId == id)
            .ToListAsync(cancellationToken);
        if (importResults.Count != 0)
        {
            _dbContext.TransactionImportResults.RemoveRange(importResults);
        }

        var processingResults = await _dbContext.PdfProcessingResults
            .Where(pr => pr.FileRecordId == id)
            .ToListAsync(cancellationToken);
        if (processingResults.Count != 0)
        {
            _dbContext.PdfProcessingResults.RemoveRange(processingResults);
        }

        _dbContext.FileRecords.Remove(record);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Statement {FileId} and its dependent records were deleted successfully by user {UserId}.", id, currentUserId);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database deletion failed for FileRecord {FileId}", id);
            return (false, "Failed to delete statement from database.");
        }
    }

    private static bool IsValidPdfHeader(Stream stream)
    {
        if (stream.Length < 5) return false;

        var buffer = new byte[5];
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        var read = stream.Read(buffer, 0, buffer.Length);
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        if (read < 5) return false;

        return buffer.SequenceEqual(PdfHeaderSignature);
    }

    private async Task<(Client? Client, FinancialYear? Year)> ResolveClientAndYearAsync(
        Guid currentUserId,
        Guid? clientId,
        Guid? financialYearId,
        CancellationToken cancellationToken)
    {
        Client? client = null;
        if (clientId.HasValue && clientId.Value != Guid.Empty)
        {
            // Strictly enforce workspace ownership: Client must belong to currentUserId!
            client = await _dbContext.Clients
                .FirstOrDefaultAsync(c => c.Id == clientId.Value && c.UserId == currentUserId, cancellationToken);

            if (client == null)
            {
                _logger.LogWarning("Security violation: User {UserId} attempted access to unauthorized Client {ClientId}", currentUserId, clientId.Value);
                return (null, null);
            }
        }
        else
        {
            // Resolve the user's workspace, or create a default one if none exists
            client = await _dbContext.Clients
                .FirstOrDefaultAsync(c => c.UserId == currentUserId, cancellationToken);

            if (client == null)
            {
                var user = await _dbContext.Users.FindAsync([currentUserId], cancellationToken);
                var emailPrefix = user?.Email.Split('@').FirstOrDefault() ?? "User";
                client = new Client
                {
                    Id = Guid.NewGuid(),
                    UserId = currentUserId,
                    Name = $"{emailPrefix}'s Workspace",
                    ContactPerson = emailPrefix,
                    Email = user?.Email ?? "user@accufex.local",
                    Phone = "0000000000",
                    BusinessName = $"{emailPrefix}'s Workspace",
                    BusinessType = "Corporate",
                    Address = "Default Address",
                    TaxId = "TAX0000",
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.Clients.Add(client);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        FinancialYear? year = null;
        if (financialYearId.HasValue && financialYearId.Value != Guid.Empty)
        {
            // Strictly enforce that FinancialYear belongs to the authorized client!
            year = await _dbContext.FinancialYears
                .FirstOrDefaultAsync(y => y.Id == financialYearId.Value && y.ClientId == client.Id, cancellationToken);

            if (year == null)
            {
                _logger.LogWarning("Security violation: FinancialYear {YearId} does not belong to authorized Client {ClientId}", financialYearId.Value, client.Id);
                return (null, null);
            }
        }
        else
        {
            year = await _dbContext.FinancialYears
                .FirstOrDefaultAsync(y => y.ClientId == client.Id, cancellationToken);

            if (year == null)
            {
                year = new FinancialYear
                {
                    Id = Guid.NewGuid(),
                    ClientId = client.Id,
                    DisplayName = "2026-27",
                    StartDate = new DateTime(2026, 4, 1),
                    EndDate = new DateTime(2027, 3, 31),
                    Status = 1,
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.FinancialYears.Add(year);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        return (client, year);
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{(bytes / 1024.0):F2} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{(bytes / (1024.0 * 1024.0)):F2} MB";
        return $"{(bytes / (1024.0 * 1024.0 * 1024.0)):F2} GB";
    }

    private static string MapStatusName(int status)
    {
        return status switch
        {
            0 => "ReadyForProcessing",
            1 => "Processing",
            2 => "Completed",
            3 => "Failed",
            _ => "Unknown"
        };
    }
}
