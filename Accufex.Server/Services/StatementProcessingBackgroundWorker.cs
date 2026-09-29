using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Accufex.Server.Data;
using Accufex.Server.Options;
using Accufex.Server.Parsing.Interfaces;
using Accufex.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Accufex.Server.Services;

public class StatementProcessingBackgroundWorker : BackgroundService
{
    private readonly IStatementProcessingQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundProcessingOptions _options;
    private readonly ILogger<StatementProcessingBackgroundWorker> _logger;

    public StatementProcessingBackgroundWorker(
        IStatementProcessingQueue queue,
        IServiceScopeFactory scopeFactory,
        IOptions<BackgroundProcessingOptions> options,
        ILogger<StatementProcessingBackgroundWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StatementProcessingBackgroundWorker started with max concurrency {MaxConcurrent}",
            _options.MaxConcurrentJobs);

        // 1. Startup Recovery: Recover interrupted or queued jobs from database
        await RecoverInterruptedJobsAsync(stoppingToken);

        // 2. Concurrency Throttle via SemaphoreSlim
        var maxConcurrency = _options.MaxConcurrentJobs > 0 ? _options.MaxConcurrentJobs : 2;
        var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await _queue.DequeueJobAsync(stoppingToken);
                await semaphore.WaitAsync(stoppingToken);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessJobAsync(job, stoppingToken);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, CancellationToken.None);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in background worker loop.");
            }
        }

        _logger.LogInformation("StatementProcessingBackgroundWorker stopping.");
    }

    private async Task ProcessJobAsync(StatementProcessingJob job, CancellationToken stoppingToken)
    {
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _queue.RegisterActiveCts(job.JobId, jobCts);
        var ct = jobCts.Token;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AccufexDbContext>();
            var extractionService = scope.ServiceProvider.GetRequiredService<IPdfExtractionService>();
            var bankDetector = scope.ServiceProvider.GetRequiredService<IBankDetector>();
            var bankParsingService = scope.ServiceProvider.GetRequiredService<IBankParsingService>();

            var fileRecord = await dbContext.FileRecords
                .Include(f => f.Client)
                .Include(f => f.FinancialYear)
                .FirstOrDefaultAsync(f => f.Id == job.FileRecordId && f.Client.UserId == job.UserId, ct);

            if (fileRecord == null)
            {
                _logger.LogWarning("Worker: FileRecord {FileId} not found or unauthorized for user {UserId}", job.FileRecordId, job.UserId);
                return;
            }

            // Idempotency: skip if already at a terminal state (e.g., parsed by explicit /parse call)
            if (fileRecord.ProcessingStatus is 2 or 3 or 4)
            {
                _logger.LogInformation("Worker: Skipping job {JobId} — statement already at terminal status {Status}.",
                    job.JobId, fileRecord.ProcessingStatus);
                _queue.UpdateStage(job.JobId,
                    fileRecord.ProcessingStatus == 2 ? "Completed" :
                    fileRecord.ProcessingStatus == 4 ? "Cancelled" : "Failed",
                    fileRecord.ProcessingStatus == 2 ? 100 : 0);
                return;
            }

            if (ct.IsCancellationRequested) return;

            // Mark status as Processing (1)
            fileRecord.ProcessingStatus = 1;
            fileRecord.UpdatedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(ct);
            _queue.UpdateStage(job.JobId, "Extracting", 30);

            // Step A: Digital PDF Text Extraction
            var (extractSuccess, extractError, extractResult) = await extractionService.ExtractDocumentAsync(
                job.FileRecordId,
                job.UserId,
                job.Password,
                ct);

            if (ct.IsCancellationRequested) return;

            if (!extractSuccess)
            {
                if (extractResult?.ExtractionStatus == "PasswordProtected")
                {
                    _logger.LogInformation("Job {JobId}: PDF statement requires password to unlock.", job.JobId);
                    fileRecord.ProcessingStatus = 5; // RequiresPassword
                    fileRecord.ProcessingError = "This PDF is password protected and requires a password to open.";
                    fileRecord.UpdatedAt = DateTime.UtcNow;
                    await dbContext.SaveChangesAsync(ct);
                    _queue.UpdateStage(job.JobId, "RequiresPassword", 25);
                    return;
                }

                _logger.LogWarning("Job {JobId}: Extraction failed: {Error}", job.JobId, extractError);
                fileRecord.ProcessingStatus = 3; // Failed
                fileRecord.ProcessingError = extractError ?? "Failed to extract text structure from statement PDF.";
                fileRecord.UpdatedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(ct);
                _queue.UpdateStage(job.JobId, "Failed", 0);
                return;
            }

            if (extractResult == null || !extractResult.HasUsableText)
            {
                _logger.LogWarning("Job {JobId}: No usable digital text found.", job.JobId);
                fileRecord.ProcessingStatus = 3; // Failed
                fileRecord.ProcessingError = "The statement contains no usable digital text. Scanned/rasterized PDFs cannot be parsed.";
                fileRecord.UpdatedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(ct);
                _queue.UpdateStage(job.JobId, "Failed", 0);
                return;
            }

            // Step B: Bank Detection
            _queue.UpdateStage(job.JobId, "DetectingBank", 60);
            var detection = bankDetector.DetectBank(extractResult);

            if (!detection.IsSupported)
            {
                _logger.LogWarning("Job {JobId}: Bank format not recognized.", job.JobId);
                fileRecord.ProcessingStatus = 3; // Failed
                fileRecord.ProcessingError = "The statement bank format was not recognized as a supported bank format.";
                fileRecord.UpdatedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(ct);
                _queue.UpdateStage(job.JobId, "Failed", 0);
                return;
            }

            // Step C: Bank Transaction Parsing & Validation Persistence
            _queue.UpdateStage(job.JobId, "ParsingTransactions", 80);
            var (parseSuccess, parseError, _) = await bankParsingService.ParseAndPersistStatementAsync(
                job.FileRecordId,
                job.UserId,
                ct);

            if (ct.IsCancellationRequested) return;

            if (!parseSuccess)
            {
                _logger.LogWarning("Job {JobId}: Parsing failed: {Error}", job.JobId, parseError);
                fileRecord.ProcessingStatus = 3; // Failed
                fileRecord.ProcessingError = parseError ?? "Statement transaction parsing failed.";
                fileRecord.UpdatedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(ct);
                _queue.UpdateStage(job.JobId, "Failed", 0);
                return;
            }

            // Success: Mark status as Completed (2)
            fileRecord.ProcessingStatus = 2; // Completed
            fileRecord.ProcessingError = null;
            fileRecord.UpdatedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(ct);
            _queue.UpdateStage(job.JobId, "Completed", 100);
            _logger.LogInformation("Job {JobId}: Successfully processed and persisted statement transactions.", job.JobId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Job {JobId} was cancelled by user.", job.JobId);
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AccufexDbContext>();
                var record = await dbContext.FileRecords.FirstOrDefaultAsync(f => f.Id == job.FileRecordId, CancellationToken.None);
                if (record != null)
                {
                    record.ProcessingStatus = 4; // Cancelled
                    record.ProcessingError = "Processing was cancelled by user.";
                    record.UpdatedAt = DateTime.UtcNow;
                    await dbContext.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update record status to Cancelled for job {JobId}", job.JobId);
            }
            _queue.UpdateStage(job.JobId, "Cancelled", 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId}: Unexpected exception during background statement processing.", job.JobId);
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AccufexDbContext>();
                var record = await dbContext.FileRecords.FirstOrDefaultAsync(f => f.Id == job.FileRecordId, CancellationToken.None);
                if (record != null)
                {
                    record.ProcessingStatus = 3; // Failed
                    record.ProcessingError = "An unexpected error occurred during statement processing.";
                    record.UpdatedAt = DateTime.UtcNow;
                    await dbContext.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception updateEx)
            {
                _logger.LogError(updateEx, "Failed to update record status to Failed for job {JobId}", job.JobId);
            }
            _queue.UpdateStage(job.JobId, "Failed", 0);
        }
        finally
        {
            _queue.CompleteJob(job.JobId);
        }
    }

    private async Task RecoverInterruptedJobsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AccufexDbContext>();

            // Clean up any jobs stuck in Processing (1) due to server restart
            var interruptedJobs = await dbContext.FileRecords
                .Where(f => f.ProcessingStatus == 1)
                .ToListAsync(cancellationToken);

            if (interruptedJobs.Count > 0)
            {
                _logger.LogInformation("Startup Recovery: Found {Count} interrupted jobs in Processing state. Marking as Failed with retry advisory.", interruptedJobs.Count);
                foreach (var job in interruptedJobs)
                {
                    job.ProcessingStatus = 3; // Failed
                    job.ProcessingError = "Processing was interrupted by server restart. Please click Retry to reprocess.";
                    job.UpdatedAt = DateTime.UtcNow;
                }
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            // Re-enqueue any jobs that were in Queued (0) state
            var queuedJobs = await dbContext.FileRecords
                .Include(f => f.Client)
                .Where(f => f.ProcessingStatus == 0)
                .ToListAsync(cancellationToken);

            if (queuedJobs.Count > 0)
            {
                _logger.LogInformation("Startup Recovery: Re-enqueuing {Count} jobs in Queued state.", queuedJobs.Count);
                foreach (var job in queuedJobs)
                {
                    await _queue.QueueJobAsync(job.Id, job.Client.UserId, null, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute startup recovery for interrupted statement processing jobs.");
        }
    }
}
