using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EasyFin_Tech.Server.Data;
using EasyFin_Tech.Server.DTOs;
using EasyFin_Tech.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EasyFin_Tech.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DashboardController : ControllerBase
{
    private readonly EasyFinDbContext _dbContext;
    private readonly ICurrentUserService _currentUserService;

    public DashboardController(EasyFinDbContext dbContext, ICurrentUserService currentUserService)
    {
        _dbContext = dbContext;
        _currentUserService = currentUserService;
    }

    private Guid? GetEffectiveUserId()
    {
        if (_currentUserService.UserId.HasValue)
        {
            return _currentUserService.UserId.Value;
        }

        var claim = User.FindFirst(ClaimTypes.NameIdentifier);
        if (claim != null && Guid.TryParse(claim.Value, out var guid))
        {
            return guid;
        }

        return null;
    }

    [HttpGet("stats")]
    [HttpGet("summary")]
    [Authorize]
    public async Task<IActionResult> GetStats(CancellationToken cancellationToken)
    {
        var currentUserId = GetEffectiveUserId();
        if (!currentUserId.HasValue) return Unauthorized();

        var userId = currentUserId.Value;

        var clientCount = await _dbContext.Clients
            .CountAsync(c => c.UserId == userId, cancellationToken);

        var fileRecordsQuery = _dbContext.FileRecords
            .Where(f => f.Client.UserId == userId);

        var totalStatements = await fileRecordsQuery.CountAsync(cancellationToken);

        var completedStatements = await fileRecordsQuery
            .CountAsync(f => f.ProcessingStatus == 2, cancellationToken);

        var processingStatements = await fileRecordsQuery
            .CountAsync(f => f.ProcessingStatus == 1 || f.ProcessingStatus == 0, cancellationToken);

        var failedStatements = await fileRecordsQuery
            .CountAsync(f => f.ProcessingStatus == 3 || f.ProcessingStatus < 0, cancellationToken);

        var totalTransactions = await _dbContext.Transactions
            .CountAsync(t => t.Client.UserId == userId, cancellationToken);

        var recentFiles = await fileRecordsQuery
            .AsNoTracking()
            .Include(f => f.TransactionImportResult)
            .Include(f => f.Transactions)
            .OrderByDescending(f => f.UploadedAt)
            .Take(10)
            .ToListAsync(cancellationToken);

        var recentStatements = recentFiles.Select(f =>
        {
            string bankName = f.TransactionImportResult?.DetectedBank switch
            {
                1 => "HDFC Bank",
                2 => "YES BANK",
                3 => "Axis Bank",
                4 => "Central Bank of India",
                5 => "ICICI Bank",
                6 => "State Bank of India",
                7 => "Bank of India",
                8 => "Kotak Mahindra Bank",
                _ => f.ProcessingStatus == 2 ? "Detected" : "Pending"
            };

            int txCount = f.TransactionImportResult?.ProcessedCount ?? f.Transactions.Count;

            return new DashboardRecentStatementDto
            {
                Id = f.Id,
                OriginalFileName = f.OriginalFileName,
                BankName = bankName,
                UploadedAt = f.UploadedAt,
                Status = MapStatusName(f.ProcessingStatus),
                ProcessingStatus = f.ProcessingStatus,
                TransactionCount = txCount,
                FileSizeBytes = f.SizeBytes,
                FileSizeFormatted = FormatBytes(f.SizeBytes)
            };
        }).ToList();

        var canConnect = await _dbContext.Database.CanConnectAsync(cancellationToken);

        var result = new DashboardSummaryDto
        {
            Status = canConnect ? "Online / Connected" : "Degraded",
            Database = "EasyFin_Tech",
            Server = @"localhost\SQLEXPRESS",
            CanConnect = canConnect,
            TotalStatements = totalStatements,
            CompletedStatements = completedStatements,
            StatementsProcessed = completedStatements,
            ProcessingStatements = processingStatements,
            FailedStatements = failedStatements,
            TotalTransactions = totalTransactions,
            ClientProfiles = Math.Max(clientCount, 1),
            RecentStatements = recentStatements,
            Tables = new
            {
                clients = Math.Max(clientCount, 1),
                fileRecords = totalStatements,
                transactions = totalTransactions,
                transactionImportResults = completedStatements,
                pdfProcessingResults = completedStatements
            }
        };

        return Ok(result);
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

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{(bytes / 1024.0):F2} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{(bytes / (1024.0 * 1024.0)):F2} MB";
        return $"{(bytes / (1024.0 * 1024.0 * 1024.0)):F2} GB";
    }
}
