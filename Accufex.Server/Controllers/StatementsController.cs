using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Accufex.Server.Data;
using Accufex.Server.DTOs;
using Accufex.Server.Export.Interfaces;
using Accufex.Server.Models;
using Accufex.Server.Parsing;
using Accufex.Server.Services;
using Accufex.Server.Validation.Models;
using Accufex.Server.Validation.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Accufex.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class StatementsController : ControllerBase
{
    private readonly IStatementService _statementService;
    private readonly IPdfExtractionService _pdfExtractionService;
    private readonly Accufex.Server.Parsing.Interfaces.IBankParsingService _bankParsingService;
    private readonly AccufexDbContext _dbContext;
    private readonly ITransactionValidationService _validationService;
    private readonly ITransactionCorrectionStore _correctionStore;
    private readonly IExcelExportService? _excelExportService;
    private readonly ILogger<StatementsController> _logger;
    private readonly IStatementProcessingQueue? _processingQueue;

    public StatementsController(
        IStatementService statementService,
        IPdfExtractionService pdfExtractionService,
        Accufex.Server.Parsing.Interfaces.IBankParsingService bankParsingService,
        AccufexDbContext dbContext,
        ITransactionValidationService validationService,
        ITransactionCorrectionStore correctionStore,
        ILogger<StatementsController> logger,
        IExcelExportService? excelExportService = null,
        IStatementProcessingQueue? processingQueue = null)
    {
        _statementService = statementService;
        _pdfExtractionService = pdfExtractionService;
        _bankParsingService = bankParsingService;
        _dbContext = dbContext;
        _validationService = validationService;
        _correctionStore = correctionStore;
        _logger = logger;
        _excelExportService = excelExportService;
        _processingQueue = processingQueue;
    }

    private Guid GetCurrentUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier);
        if (claim != null && Guid.TryParse(claim.Value, out var userId))
        {
            return userId;
        }

        throw new UnauthorizedAccessException("Authenticated user identity is missing or invalid.");
    }

    /// <summary>
    /// Securely upload a bank statement PDF for ingestion and validation.
    /// </summary>
    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(StatementUploadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Upload(
        [FromForm] IFormFile? file,
        [FromForm] Guid? clientId = null,
        [FromForm] Guid? financialYearId = null,
        CancellationToken cancellationToken = default)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Validation Error",
                Detail = "Please select a PDF bank statement file to upload.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var currentUserId = GetCurrentUserId();

        var (success, errorMessage, response) = await _statementService.UploadStatementAsync(
            file,
            currentUserId,
            clientId,
            financialYearId,
            cancellationToken);

        if (!success)
        {
            if (errorMessage?.Contains("not accessible", StringComparison.OrdinalIgnoreCase) == true)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
                {
                    Title = "Forbidden",
                    Detail = errorMessage,
                    Status = StatusCodes.Status403Forbidden
                });
            }

            return BadRequest(new ProblemDetails
            {
                Title = "File Validation Error",
                Detail = errorMessage ?? "The uploaded statement file was rejected.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (response != null)
        {
            response.JobId = response.FileId;
            response.Status = "Queued";
            if (_processingQueue != null)
            {
                await _processingQueue.QueueJobAsync(response.FileId, currentUserId, null, cancellationToken);
            }
        }

        return Ok(response);
    }

    /// <summary>
    /// Retrieve metadata summary list of uploaded bank statements.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(StatementSummaryDto[]), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatements(CancellationToken cancellationToken = default)
    {
        var currentUserId = GetCurrentUserId();
        var statements = await _statementService.GetStatementsAsync(currentUserId, cancellationToken);
        return Ok(statements);
    }

    /// <summary>
    /// Retrieve single statement metadata by internal ID.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(StatementDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStatementById(Guid id, CancellationToken cancellationToken = default)
    {
        var currentUserId = GetCurrentUserId();
        var statement = await _statementService.GetStatementByIdAsync(id, currentUserId, cancellationToken);
        if (statement == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Not Found",
                Detail = $"Statement with ID {id} was not found.",
                Status = StatusCodes.Status404NotFound
            });
        }

        return Ok(statement);
    }

    /// <summary>
    /// Securely stream the uploaded bank statement PDF file for download.
    /// </summary>
    [HttpGet("{id:guid}/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadStatement(Guid id, CancellationToken cancellationToken = default)
    {
        var currentUserId = GetCurrentUserId();
        var result = await _statementService.GetStatementFileStreamAsync(id, currentUserId, cancellationToken);
        if (result == null || result.Value.Stream == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "File Not Found",
                Detail = "The requested statement file stream is not available.",
                Status = StatusCodes.Status404NotFound
            });
        }

        var (stream, originalFileName, contentType) = result.Value;
        return File(stream, contentType, originalFileName);
    }

    /// <summary>
    /// Securely delete a bank statement and its associated transactions, processing results, and physical storage files.
    /// Strictly verifies user/tenant ownership before deletion.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteStatement(Guid id, CancellationToken cancellationToken = default)
    {
        var currentUserId = GetCurrentUserId();
        _processingQueue?.RequestCancellation(id, currentUserId);
        var (success, errorMessage) = await _statementService.DeleteStatementAsync(id, currentUserId, cancellationToken);

        if (!success)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Statement Not Found",
                Detail = errorMessage ?? $"Statement with ID {id} was not found or is not accessible.",
                Status = StatusCodes.Status404NotFound
            });
        }

        return NoContent();
    }

    /// <summary>
    /// Retrieve real-time asynchronous background job processing status for a statement.
    /// Strictly verifies user/tenant ownership before disclosing status.
    /// </summary>
    [HttpGet("{id:guid}/job-status")]
    [ProducesResponseType(typeof(StatementJobStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJobStatus(Guid id, CancellationToken cancellationToken = default)
    {
        var currentUserId = GetCurrentUserId();

        var record = await _dbContext.FileRecords
            .AsNoTracking()
            .Include(f => f.Client)
            .FirstOrDefaultAsync(f => f.Id == id && f.Client.UserId == currentUserId, cancellationToken);

        if (record == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Job Not Found",
                Detail = $"Job with ID {id} was not found or is not accessible.",
                Status = StatusCodes.Status404NotFound
            });
        }

        var runtimeInfo = _processingQueue?.GetRuntimeInfo(id);

        string statusName;
        int progress;
        string stage;
        bool requiresPassword = false;

        switch (record.ProcessingStatus)
        {
            case 0:
                statusName = "Queued";
                stage = runtimeInfo?.Stage ?? "Queued";
                progress = runtimeInfo?.Progress ?? 10;
                break;
            case 1:
                statusName = "Processing";
                stage = runtimeInfo?.Stage ?? "Processing";
                progress = runtimeInfo?.Progress ?? 50;
                break;
            case 2:
                statusName = "Completed";
                stage = "Completed";
                progress = 100;
                break;
            case 3:
                statusName = "Failed";
                stage = "Failed";
                progress = 0;
                break;
            case 4:
                statusName = "Cancelled";
                stage = "Cancelled";
                progress = 0;
                break;
            case 5:
                statusName = "RequiresPassword";
                stage = "RequiresPassword";
                progress = 25;
                requiresPassword = true;
                break;
            default:
                statusName = "Unknown";
                stage = "Unknown";
                progress = 0;
                break;
        }

        var statusDto = new StatementJobStatusDto
        {
            JobId = record.Id,
            FileId = record.Id,
            FileName = record.OriginalFileName,
            Status = statusName,
            ProcessingStatus = record.ProcessingStatus,
            Progress = progress,
            Stage = stage,
            ErrorMessage = record.ProcessingError,
            RequiresPassword = requiresPassword,
            CreatedAt = record.UploadedAt,
            UpdatedAt = record.UpdatedAt
        };

        return Ok(statusDto);
    }

    /// <summary>
    /// Safely request cancellation of an active or queued statement processing job.
    /// Strictly verifies user/tenant ownership before requesting cancellation.
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CancelJob(Guid id, CancellationToken cancellationToken = default)
    {
        var currentUserId = GetCurrentUserId();

        var record = await _dbContext.FileRecords
            .Include(f => f.Client)
            .FirstOrDefaultAsync(f => f.Id == id && f.Client.UserId == currentUserId, cancellationToken);

        if (record == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Job Not Found",
                Detail = $"Job with ID {id} was not found or is not accessible.",
                Status = StatusCodes.Status404NotFound
            });
        }

        // Reject cancellation of terminal jobs (Completed, Failed, or already Cancelled)
        if (record.ProcessingStatus is 2 or 3 or 4)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid Operation",
                Detail = "This job has already reached a terminal state and cannot be cancelled.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        // Signal in-memory CTS if running
        _processingQueue?.RequestCancellation(id, currentUserId);

        record.ProcessingStatus = 4; // Cancelled
        record.ProcessingError = "Processing was cancelled by user.";
        record.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { success = true, message = "Job was cancelled successfully.", jobId = id });
    }

    /// <summary>
    /// Retry processing for a failed or cancelled statement job.
    /// Strictly verifies user/tenant ownership before re-enqueuing.
    /// </summary>
    [HttpPost("{id:guid}/retry")]
    [ProducesResponseType(typeof(StatementJobStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RetryJob(Guid id, CancellationToken cancellationToken = default)
    {
        var currentUserId = GetCurrentUserId();

        var record = await _dbContext.FileRecords
            .Include(f => f.Client)
            .FirstOrDefaultAsync(f => f.Id == id && f.Client.UserId == currentUserId, cancellationToken);

        if (record == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Job Not Found",
                Detail = $"Job with ID {id} was not found or is not accessible.",
                Status = StatusCodes.Status404NotFound
            });
        }

        if (record.ProcessingStatus == 1)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Job Already In Progress",
                Detail = "This job is currently being processed and cannot be retried until it finishes or is cancelled.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (record.ProcessingStatus == 2)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Job Already Completed",
                Detail = "This job has already completed successfully and does not need to be retried.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        // Reset status to Queued (0)
        record.ProcessingStatus = 0;
        record.ProcessingError = null;
        record.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (_processingQueue != null)
        {
            await _processingQueue.QueueJobAsync(id, currentUserId, null, cancellationToken);
        }

        return await GetJobStatus(id, cancellationToken);
    }

    /// <summary>
    /// Resume/re-enqueue a password-protected statement job with a user-supplied password.
    /// Password is never persisted to database or storage, and is held only in transient memory during processing.
    /// </summary>
    [HttpPost("{id:guid}/unlock")]
    [ProducesResponseType(typeof(StatementJobStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UnlockJob(
        Guid id,
        [FromBody] UnlockJobRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request?.Password))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Validation Error",
                Detail = "PDF password is required to unlock this statement.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var currentUserId = GetCurrentUserId();

        var record = await _dbContext.FileRecords
            .Include(f => f.Client)
            .FirstOrDefaultAsync(f => f.Id == id && f.Client.UserId == currentUserId, cancellationToken);

        if (record == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Job Not Found",
                Detail = $"Job with ID {id} was not found or is not accessible.",
                Status = StatusCodes.Status404NotFound
            });
        }

        // Test password validity against encrypted PDF before queuing
        var (extractSuccess, extractError, extractResult) = await _pdfExtractionService.ExtractDocumentAsync(
            id,
            currentUserId,
            request.Password,
            cancellationToken);

        if (!extractSuccess && extractResult?.ExtractionStatus == "PasswordProtected")
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid PDF Password",
                Detail = "Incorrect PDF password. Please try again.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        // Reset status to Queued (0)
        record.ProcessingStatus = 0;
        record.ProcessingError = null;
        record.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (_processingQueue != null)
        {
            await _processingQueue.QueueJobAsync(id, currentUserId, request.Password, cancellationToken);
        }

        return await GetJobStatus(id, cancellationToken);
    }

    /// <summary>
    /// Trigger generic digital PDF extraction for an ingested statement.
    /// </summary>
    [HttpPost("{id:guid}/extract")]
    [ProducesResponseType(typeof(PdfExtractionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExtractStatement(
        Guid id,
        [FromBody] ExtractStatementRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var currentUserId = GetCurrentUserId();
        var (success, errorMessage, result) = await _pdfExtractionService.ExtractDocumentAsync(
            id,
            currentUserId,
            request?.Password,
            cancellationToken);

        if (!success)
        {
            if (result == null && (errorMessage?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true))
            {
                return NotFound(new ProblemDetails
                {
                    Title = "Not Found",
                    Detail = errorMessage,
                    Status = StatusCodes.Status404NotFound
                });
            }

            if (result?.ExtractionStatus == "PasswordProtected")
            {
                var isInvalidPassword = !string.IsNullOrWhiteSpace(request?.Password);
                var problem = new ProblemDetails
                {
                    Title = isInvalidPassword ? "Invalid PDF Password" : "Password Protected PDF",
                    Detail = isInvalidPassword
                        ? "Incorrect PDF password. Please try again."
                        : (errorMessage ?? "This PDF is password protected and requires a password to open."),
                    Status = StatusCodes.Status400BadRequest
                };
                problem.Extensions["requiresPassword"] = true;
                problem.Extensions["extractionStatus"] = "PasswordProtected";
                problem.Extensions["isIncorrectPassword"] = isInvalidPassword;
                return BadRequest(problem);
            }

            return BadRequest(new ProblemDetails
            {
                Title = "Extraction Failed",
                Detail = errorMessage ?? "Unable to extract text structure from the requested statement.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        return Ok(result);
    }

    /// <summary>
    /// Retrieve stored generic extraction result for a statement.
    /// </summary>
    [HttpGet("{id:guid}/extraction")]
    [ProducesResponseType(typeof(PdfExtractionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetExtraction(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var currentUserId = GetCurrentUserId();
        var result = await _pdfExtractionService.GetExtractionResultAsync(id, currentUserId, cancellationToken);
        if (result == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Extraction Not Found",
                Detail = $"No extraction results found for statement ID {id}.",
                Status = StatusCodes.Status404NotFound
            });
        }

        return Ok(result);
    }

    /// <summary>
    /// Parse an extracted statement into structured bank transactions (HDFC Bank & YES BANK supported in Phase 4).
    /// </summary>
    [HttpPost("{id:guid}/parse")]
    [ProducesResponseType(typeof(Accufex.Server.Parsing.Models.BankParsingResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ParseStatement(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var currentUserId = GetCurrentUserId();
        var (success, errorMessage, result) = await _bankParsingService.ParseAndPersistStatementAsync(
            id,
            currentUserId,
            cancellationToken);

        if (!success)
        {
            if (errorMessage?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true ||
                errorMessage?.Contains("not accessible", StringComparison.OrdinalIgnoreCase) == true)
            {
                return NotFound(new ProblemDetails
                {
                    Title = "Not Found",
                    Detail = errorMessage,
                    Status = StatusCodes.Status404NotFound
                });
            }

            return BadRequest(new ProblemDetails
            {
                Title = "Parsing Failed",
                Detail = errorMessage ?? "Unable to parse bank transactions from the statement.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        return Ok(result);
    }

    /// <summary>
    /// Retrieve stored parsed transactions, review flags, and validation metadata for a statement.
    /// Supports pagination, status filtering, type filtering, and text search.
    /// </summary>
    [HttpGet("{id:guid}/transactions")]
    [ProducesResponseType(typeof(StatementTransactionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTransactions(
        Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? status = null,
        [FromQuery] string? type = null,
        [FromQuery] string? search = null,
        [FromQuery] string? sortBy = "date",
        [FromQuery] bool sortDesc = false,
        CancellationToken cancellationToken = default)
    {
        var currentUserId = GetCurrentUserId();

        // 1. Strict Tenant Authorization: Statement must belong to current user
        var fileRecord = await _dbContext.FileRecords
            .AsNoTracking()
            .Include(f => f.Client)
            .FirstOrDefaultAsync(f => f.Id == id && f.Client.UserId == currentUserId, cancellationToken);

        if (fileRecord == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Statement Not Found",
                Detail = $"Statement with ID {id} was not found or is not accessible.",
                Status = StatusCodes.Status404NotFound
            });
        }

        // 2. Load stored transactions
        var transactions = await _dbContext.Transactions
            .AsNoTracking()
            .Where(t => t.SourceFileId == id)
            .OrderBy(t => t.TransactionDate)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

        // 3. Load sidecar audit record (if exists)
        var sidecar = await _correctionStore.GetAuditSidecarAsync(id, cancellationToken);

        // 4. Resolve bank identification
        var importResult = await _dbContext.TransactionImportResults
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.SourceFileId == id, cancellationToken);

        var bankCode = importResult?.DetectedBank ?? (transactions.FirstOrDefault()?.BankCode ?? (int)BankType.Hdfc);
        var bankName = sidecar?.BankName ?? (bankCode switch
        {
            1 => "HDFC Bank",
            2 => "YES BANK",
            3 => "Axis Bank",
            4 => "Central Bank of India",
            5 => "ICICI Bank",
            6 => "State Bank of India",
            7 => "Bank of India",
            8 => "Kotak Mahindra Bank",
            _ => "Unknown Bank"
        });
        var parserVersion = sidecar?.ParserVersion ?? (bankCode switch
        {
            1 => "HDFC-v1",
            2 => "YES-v1",
            3 => "AXIS-v1",
            4 => "CENTRAL-v1",
            5 => "ICICI-v1",
            6 => "SBI-v1",
            7 => "BOI-v1",
            8 => "KOTAK-v1",
            _ => "v1"
        });

        // 5. Run deterministic validation & enrichment
        var enriched = _validationService.ValidateAndEnrichStatement(transactions, sidecar);
        var summary = _validationService.ComputeSummary(id, bankCode, bankName, parserVersion, enriched);

        // 6. Apply filters
        var query = enriched.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(status) && !status.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(t => t.ValidationStatus.Equals(status.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(type) && !type.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            if (type.Equals("debit", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(t => t.Debit.HasValue && t.Debit.Value > 0);
            }
            else if (type.Equals("credit", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(t => t.Credit.HasValue && t.Credit.Value > 0);
            }
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.Trim();
            query = query.Where(t =>
                (!string.IsNullOrEmpty(t.Description) && t.Description.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(t.Reference) && t.Reference.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(t.Utr) && t.Utr.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                t.Amount.ToString("F2", CultureInfo.InvariantCulture).Contains(q, StringComparison.OrdinalIgnoreCase) ||
                t.TransactionDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture).Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        // 7. Apply sorting
        query = (sortBy?.ToLowerInvariant()) switch
        {
            "description" => sortDesc ? query.OrderByDescending(t => t.Description) : query.OrderBy(t => t.Description),
            "amount" => sortDesc ? query.OrderByDescending(t => t.Amount) : query.OrderBy(t => t.Amount),
            "balance" => sortDesc ? query.OrderByDescending(t => t.Balance ?? 0) : query.OrderBy(t => t.Balance ?? 0),
            "status" => sortDesc ? query.OrderByDescending(t => t.ValidationStatus) : query.OrderBy(t => t.ValidationStatus),
            _ => sortDesc ? query.OrderByDescending(t => t.TransactionDate) : query.OrderBy(t => t.TransactionDate)
        };

        var filteredList = query.ToList();
        var totalCount = filteredList.Count;

        // 8. Paginate
        var safePage = page < 1 ? 1 : page;
        var safePageSize = pageSize < 1 ? 50 : (pageSize > 500 ? 500 : pageSize);
        var pagedItems = filteredList
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToList();

        var response = new StatementTransactionsResponse
        {
            Success = true,
            BankCode = bankCode,
            BankName = bankName,
            ParserVersion = parserVersion,
            TotalDetected = importResult?.TotalDetected ?? transactions.Count,
            ProcessedCount = importResult?.ProcessedCount ?? transactions.Count,
            TotalCount = totalCount,
            Page = safePage,
            PageSize = safePageSize,
            Items = pagedItems,
            Summary = summary
        };

        return Ok(response);
    }

    /// <summary>
    /// Retrieve detailed transaction review data including pristine original parsed values and audit history.
    /// </summary>
    [HttpGet("{id:guid}/transactions/{transactionId:guid}")]
    [ProducesResponseType(typeof(TransactionReviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTransactionById(
        Guid id,
        Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        var currentUserId = GetCurrentUserId();

        // 1. Strict Tenant Authorization: Statement must belong to current user
        var fileRecord = await _dbContext.FileRecords
            .AsNoTracking()
            .Include(f => f.Client)
            .FirstOrDefaultAsync(f => f.Id == id && f.Client.UserId == currentUserId, cancellationToken);

        if (fileRecord == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Statement Not Found",
                Detail = $"Statement with ID {id} was not found or is not accessible.",
                Status = StatusCodes.Status404NotFound
            });
        }

        // 2. Transaction must belong to statement
        var transaction = await _dbContext.Transactions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transactionId && t.SourceFileId == id, cancellationToken);

        if (transaction == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Transaction Not Found",
                Detail = $"Transaction with ID {transactionId} was not found in statement {id}.",
                Status = StatusCodes.Status404NotFound
            });
        }

        // 3. Load sidecar audit record
        var sidecar = await _correctionStore.GetAuditSidecarAsync(id, cancellationToken);
        TransactionAuditRecord? auditRecord = null;
        sidecar?.Records.TryGetValue(transactionId, out auditRecord);

        // 4. Resolve adjacent transaction for running balance check
        var previous = await _dbContext.Transactions
            .AsNoTracking()
            .Where(t => t.SourceFileId == id && (t.TransactionDate < transaction.TransactionDate || (t.TransactionDate == transaction.TransactionDate && t.CreatedAt < transaction.CreatedAt)))
            .OrderByDescending(t => t.TransactionDate)
            .ThenByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var enriched = _validationService.ValidateAndEnrichSingle(transaction, auditRecord, previous);
        return Ok(enriched);
    }

    /// <summary>
    /// Correct transaction fields with complete audit history tracking and running balance revalidation.
    /// Preserves original parsed values immutably and never touches parser ProcessingWarning.
    /// </summary>
    [HttpPut("{id:guid}/transactions/{transactionId:guid}")]
    [ProducesResponseType(typeof(TransactionReviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CorrectTransaction(
        Guid id,
        Guid transactionId,
        [FromBody] CorrectTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        var currentUserId = GetCurrentUserId();

        // 1. Strict Tenant Authorization: Statement must belong to current user
        var fileRecord = await _dbContext.FileRecords
            .Include(f => f.Client)
            .FirstOrDefaultAsync(f => f.Id == id && f.Client.UserId == currentUserId, cancellationToken);

        if (fileRecord == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Statement Not Found",
                Detail = $"Statement with ID {id} was not found or is not accessible.",
                Status = StatusCodes.Status404NotFound
            });
        }

        // 2. Transaction must belong to statement
        var transaction = await _dbContext.Transactions
            .FirstOrDefaultAsync(t => t.Id == transactionId && t.SourceFileId == id, cancellationToken);

        if (transaction == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Transaction Not Found",
                Detail = $"Transaction with ID {transactionId} was not found in statement {id}.",
                Status = StatusCodes.Status404NotFound
            });
        }

        // 3. Validate Correction Request Inputs
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Validation Error",
                Detail = "A reason for correction is required for audit traceability.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (string.IsNullOrWhiteSpace(request.Description))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Validation Error",
                Detail = "Description/narration cannot be empty.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (request.TransactionDate == default || request.TransactionDate == DateTime.MinValue)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Validation Error",
                Detail = "A valid transaction date is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (request.Amount <= 0.00m)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Validation Error",
                Detail = "Transaction amount must be greater than zero.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var debit = request.Debit;
        var credit = request.Credit;

        if (debit.HasValue && debit.Value > 0 && credit.HasValue && credit.Value > 0)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Monetary Error",
                Detail = "Both Debit and Credit cannot be populated simultaneously.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if ((!debit.HasValue || debit.Value <= 0) && (!credit.HasValue || credit.Value <= 0))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Monetary Error",
                Detail = "Either Debit or Credit must be populated with a positive amount.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if ((debit.HasValue && debit.Value < 0) || (credit.HasValue && credit.Value < 0))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Monetary Error",
                Detail = "Monetary values cannot be negative.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        // Normalize amount and transaction type based on non-zero debit/credit
        if (debit.HasValue && debit.Value > 0)
        {
            request.Amount = debit.Value;
            request.TransactionType = "Debit";
            request.Credit = null;
        }
        else if (credit.HasValue && credit.Value > 0)
        {
            request.Amount = credit.Value;
            request.TransactionType = "Credit";
            request.Debit = null;
        }

        // 4. Calculate Field Changes
        var changes = new List<FieldChange>();

        if (transaction.TransactionDate.Date != request.TransactionDate.Date)
        {
            changes.Add(new FieldChange
            {
                FieldName = nameof(Transaction.TransactionDate),
                OldValue = transaction.TransactionDate.ToString("yyyy-MM-dd"),
                NewValue = request.TransactionDate.ToString("yyyy-MM-dd")
            });
        }

        if ((transaction.Description ?? string.Empty).Trim() != request.Description.Trim())
        {
            changes.Add(new FieldChange
            {
                FieldName = nameof(Transaction.Description),
                OldValue = transaction.Description,
                NewValue = request.Description.Trim()
            });
        }

        if (transaction.Debit != request.Debit)
        {
            changes.Add(new FieldChange
            {
                FieldName = nameof(Transaction.Debit),
                OldValue = transaction.Debit?.ToString("F2"),
                NewValue = request.Debit?.ToString("F2")
            });
        }

        if (transaction.Credit != request.Credit)
        {
            changes.Add(new FieldChange
            {
                FieldName = nameof(Transaction.Credit),
                OldValue = transaction.Credit?.ToString("F2"),
                NewValue = request.Credit?.ToString("F2")
            });
        }

        if (transaction.Amount != request.Amount)
        {
            changes.Add(new FieldChange
            {
                FieldName = nameof(Transaction.Amount),
                OldValue = transaction.Amount.ToString("F2"),
                NewValue = request.Amount.ToString("F2")
            });
        }

        if (transaction.Balance != request.Balance)
        {
            changes.Add(new FieldChange
            {
                FieldName = nameof(Transaction.Balance),
                OldValue = transaction.Balance?.ToString("F2"),
                NewValue = request.Balance?.ToString("F2")
            });
        }

        if ((transaction.Reference ?? string.Empty).Trim() != (request.Reference ?? string.Empty).Trim())
        {
            changes.Add(new FieldChange
            {
                FieldName = nameof(Transaction.Reference),
                OldValue = transaction.Reference,
                NewValue = request.Reference?.Trim()
            });
        }

        if ((transaction.TransactionType ?? string.Empty).Trim() != (request.TransactionType ?? string.Empty).Trim())
        {
            changes.Add(new FieldChange
            {
                FieldName = nameof(Transaction.TransactionType),
                OldValue = transaction.TransactionType,
                NewValue = request.TransactionType
            });
        }

        // If no fields changed, return existing enriched transaction
        if (changes.Count == 0)
        {
            var sidecarExisting = await _correctionStore.GetAuditSidecarAsync(id, cancellationToken);
            TransactionAuditRecord? auditExisting = null;
            sidecarExisting?.Records.TryGetValue(transactionId, out auditExisting);
            var enrichedExisting = _validationService.ValidateAndEnrichSingle(transaction, auditExisting);
            return Ok(enrichedExisting);
        }

        // 5. Ensure Audit Sidecar is present and readable before modifying anything (Data Loss Protection)
        var hasSidecar = await _correctionStore.HasAuditSidecarAsync(id, cancellationToken);
        if (!hasSidecar)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Audit Baseline Missing",
                Detail = "The statement audit snapshot is missing or unreadable. Corrections cannot be applied without a pristine baseline snapshot.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        // 6. Atomic Persistence: Apply changes to DB and sidecar
        // Update database operational fields ONLY. ProcessingWarning is NOT touched!
        transaction.TransactionDate = request.TransactionDate;
        transaction.Description = request.Description.Trim();
        transaction.Debit = request.Debit;
        transaction.Credit = request.Credit;
        transaction.Amount = request.Amount;
        transaction.Balance = request.Balance;
        transaction.Reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim();
        transaction.TransactionType = request.TransactionType;

        TransactionAuditRecord updatedAuditRecord;
        bool isInMemory = _dbContext.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true;

        if (isInMemory)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            updatedAuditRecord = await _correctionStore.ApplyCorrectionAsync(
                id,
                transactionId,
                currentUserId,
                request.Reason.Trim(),
                changes,
                cancellationToken);
        }
        else
        {
            var strategy = _dbContext.Database.CreateExecutionStrategy();
            TransactionAuditRecord? auditResult = null;

            await strategy.ExecuteAsync(async () =>
            {
                await using var dbTx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                try
                {
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    auditResult = await _correctionStore.ApplyCorrectionAsync(
                        id,
                        transactionId,
                        currentUserId,
                        request.Reason.Trim(),
                        changes,
                        cancellationToken);

                    await dbTx.CommitAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    await dbTx.RollbackAsync(cancellationToken);
                    _logger.LogError(ex, "Atomic correction failed for transaction {TxId}. Database changes rolled back.", transactionId);
                    throw;
                }
            });

            updatedAuditRecord = auditResult ?? throw new InvalidOperationException("Correction application did not return an audit record.");
        }

        // 7. Resolve adjacent transaction and return enriched DTO
        var prevTx = await _dbContext.Transactions
            .AsNoTracking()
            .Where(t => t.SourceFileId == id && (t.TransactionDate < transaction.TransactionDate || (t.TransactionDate == transaction.TransactionDate && t.CreatedAt < transaction.CreatedAt)))
            .OrderByDescending(t => t.TransactionDate)
            .ThenByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var finalEnriched = _validationService.ValidateAndEnrichSingle(transaction, updatedAuditRecord, prevTx);
        return Ok(finalEnriched);
    }

    /// <summary>
    /// Retrieve statement-level validation statistics and reconciliation summary.
    /// </summary>
    [HttpGet("{id:guid}/validation-summary")]
    [ProducesResponseType(typeof(StatementValidationSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetValidationSummary(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var currentUserId = GetCurrentUserId();

        // Strict Tenant Authorization
        var fileRecord = await _dbContext.FileRecords
            .AsNoTracking()
            .Include(f => f.Client)
            .FirstOrDefaultAsync(f => f.Id == id && f.Client.UserId == currentUserId, cancellationToken);

        if (fileRecord == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Statement Not Found",
                Detail = $"Statement with ID {id} was not found or is not accessible.",
                Status = StatusCodes.Status404NotFound
            });
        }

        var transactions = await _dbContext.Transactions
            .AsNoTracking()
            .Where(t => t.SourceFileId == id)
            .OrderBy(t => t.TransactionDate)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

        var sidecar = await _correctionStore.GetAuditSidecarAsync(id, cancellationToken);
        var importResult = await _dbContext.TransactionImportResults
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.SourceFileId == id, cancellationToken);

        var bankCode = importResult?.DetectedBank ?? (transactions.FirstOrDefault()?.BankCode ?? 1);
        var bankName = sidecar?.BankName ?? (bankCode switch { 1 => "HDFC Bank", 2 => "YES BANK", 3 => "Axis Bank", 4 => "Central Bank of India", 5 => "ICICI Bank", 6 => "State Bank of India", 7 => "Bank of India", 8 => "Kotak Mahindra Bank", _ => "Unknown Bank" });
        var parserVersion = sidecar?.ParserVersion ?? (bankCode switch { 1 => "HDFC-v1", 2 => "YES-v1", 3 => "AXIS-v1", 4 => "CENTRAL-v1", 5 => "ICICI-v1", 6 => "SBI-v1", 7 => "BOI-v1", 8 => "KOTAK-v1", _ => "v1" });

        var enriched = _validationService.ValidateAndEnrichStatement(transactions, sidecar);
        var summary = _validationService.ComputeSummary(id, bankCode, bankName, parserVersion, enriched);

        return Ok(summary);
    }

    /// <summary>
    /// Trigger revalidation pass across all transactions in a statement.
    /// </summary>
    [HttpPost("{id:guid}/revalidate")]
    [ProducesResponseType(typeof(StatementValidationSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevalidateStatement(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await GetValidationSummary(id, cancellationToken);
    }

    /// <summary>
    /// Export statement transactions to an accounting-grade Excel workbook (.xlsx).
    /// Uses final current transaction values (reflecting user corrections).
    /// Enforces strict tenant ownership and export eligibility (blocks on invalid records).
    /// </summary>
    [HttpGet("{id:guid}/export/excel")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportExcel(
        Guid id,
        [FromServices] IExcelExportService? exportServiceFallback,
        CancellationToken cancellationToken = default)
    {
        var exportService = _excelExportService ?? exportServiceFallback;
        if (exportService == null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Title = "Export Service Unavailable",
                Detail = "Excel export service is not registered in the application container.",
                Status = StatusCodes.Status500InternalServerError
            });
        }

        var currentUserId = GetCurrentUserId();

        // 1. Strict Tenant Authorization: Statement must belong to current user
        var fileRecord = await _dbContext.FileRecords
            .AsNoTracking()
            .Include(f => f.Client)
            .FirstOrDefaultAsync(f => f.Id == id && f.Client.UserId == currentUserId, cancellationToken);

        if (fileRecord == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Statement Not Found",
                Detail = $"Statement with ID {id} was not found or is not accessible.",
                Status = StatusCodes.Status404NotFound
            });
        }

        // 2. Load stored transactions
        var transactions = await _dbContext.Transactions
            .AsNoTracking()
            .Where(t => t.SourceFileId == id)
            .OrderBy(t => t.TransactionDate)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

        // 3. Load sidecar audit record (if exists)
        var sidecar = await _correctionStore.GetAuditSidecarAsync(id, cancellationToken);

        // 4. Resolve bank identification
        var importResult = await _dbContext.TransactionImportResults
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.SourceFileId == id, cancellationToken);

        var bankCode = importResult?.DetectedBank ?? (transactions.FirstOrDefault()?.BankCode ?? 1);
        var bankName = sidecar?.BankName ?? (bankCode switch
        {
            1 => "HDFC Bank",
            2 => "YES BANK",
            3 => "Axis Bank",
            4 => "Central Bank of India",
            5 => "ICICI Bank",
            6 => "State Bank of India",
            7 => "Bank of India",
            8 => "Kotak Mahindra Bank",
            _ => "Unknown Bank"
        });
        var parserVersion = sidecar?.ParserVersion ?? (bankCode switch
        {
            1 => "HDFC-v1",
            2 => "YES-v1",
            3 => "AXIS-v1",
            4 => "CENTRAL-v1",
            5 => "ICICI-v1",
            6 => "SBI-v1",
            7 => "BOI-v1",
            8 => "KOTAK-v1",
            _ => "v1"
        });

        // 5. Run deterministic validation & enrichment (produces final current values post-correction)
        var enriched = _validationService.ValidateAndEnrichStatement(transactions, sidecar);
        var summary = _validationService.ComputeSummary(id, bankCode, bankName, parserVersion, enriched);

        // 6. Enforce Export Eligibility Policy:
        // VALID -> export, CORRECTED -> export, REVIEW -> export, INVALID -> block export with clear 400
        if (summary.InvalidCount > 0)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Export Blocked",
                Detail = $"Statement contains {summary.InvalidCount} invalid transaction(s). Please review and correct all invalid transactions before exporting.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        // 7. Generate Excel Workbook
        var bytes = await exportService.GenerateStatementWorkbookAsync(fileRecord, summary, enriched, cancellationToken);
        var safeFileName = exportService.GenerateSafeExportFileName(fileRecord.OriginalFileName, bankName, DateTime.UtcNow);

        // Expose Content-Disposition header for client download extraction
        Response.Headers.Append("Access-Control-Expose-Headers", "Content-Disposition");

        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", safeFileName);
    }
}
