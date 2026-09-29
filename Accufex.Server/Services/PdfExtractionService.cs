using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Accufex.Server.Data;
using Accufex.Server.DTOs;
using Accufex.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;

namespace Accufex.Server.Services;

public class PdfExtractionService : IPdfExtractionService
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _extractionLocks = new();

    private readonly AccufexDbContext _dbContext;
    private readonly IFileStorageService _storageService;
    private readonly ILogger<PdfExtractionService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public PdfExtractionService(
        AccufexDbContext dbContext,
        IFileStorageService storageService,
        ILogger<PdfExtractionService> logger)
    {
        _dbContext = dbContext;
        _storageService = storageService;
        _logger = logger;
    }

    public async Task<(bool Success, string? ErrorMessage, PdfExtractionResult? Result)> ExtractDocumentAsync(
        Guid fileRecordId,
        Guid currentUserId,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        var semaphore = _extractionLocks.GetOrAdd(fileRecordId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);

        try
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                var existingExtraction = await GetExtractionResultAsync(fileRecordId, currentUserId, cancellationToken);
                if (existingExtraction != null && existingExtraction.ExtractionStatus != "PasswordProtected")
                {
                    return (true, null, existingExtraction);
                }
            }

            var stopwatch = Stopwatch.StartNew();
            var startedAt = DateTime.UtcNow;

            // 1. Resolve FileRecord scoped strictly to the authenticated user's workspace
            var fileRecord = await _dbContext.FileRecords
                .Include(f => f.FinancialYear)
                .Include(f => f.Client)
                .FirstOrDefaultAsync(f => f.Id == fileRecordId && f.Client.UserId == currentUserId, cancellationToken);

        if (fileRecord == null)
        {
            _logger.LogWarning("Extraction requested for inaccessible or non-existent statement: {FileRecordId} by user {UserId}", fileRecordId, currentUserId);
            return (false, $"Statement with ID {fileRecordId} was not found.", null);
        }

        // 2. Resolve internal storage location
        var yearFolder = (fileRecord.FinancialYear?.StartDate.Year ?? fileRecord.UploadedAt.Year).ToString();
        var tenantSubDir = Path.Combine("tenants", currentUserId.ToString("N"), "clients", fileRecord.ClientId.ToString("N"), "statements", yearFolder);
        var legacySubDir = Path.Combine(fileRecord.ClientId.ToString("N"), yearFolder);

        var pdfStream = await _storageService.GetFileStreamAsync(tenantSubDir, fileRecord.StoredFileName, cancellationToken)
            ?? await _storageService.GetFileStreamAsync(legacySubDir, fileRecord.StoredFileName, cancellationToken);

        if (pdfStream == null)
        {
            _logger.LogError("Stored statement file stream was not found: {StoredFileName} in {TenantSubDir} or {LegacySubDir}",
                fileRecord.StoredFileName, tenantSubDir, legacySubDir);
            return (false, "The stored statement file could not be located on the server.", null);
        }

        // Stream directly into PdfPig using seekable storage stream without full-file in-memory buffering
        if (pdfStream.CanSeek)
        {
            pdfStream.Position = 0;
        }

        // 3. Open PDF Document with PdfPig directly from seekable file/storage stream
        ParsingOptions parsingOptions = new()
        {
            ClipPaths = false
        };

        if (!string.IsNullOrWhiteSpace(password))
        {
            parsingOptions.Password = password;
        }

        PdfDocument pdfDocument;
        try
        {
            pdfDocument = PdfDocument.Open(pdfStream, parsingOptions);
        }
        catch (PdfDocumentEncryptedException)
        {
            _logger.LogWarning("PDF extraction halted: File {FileRecordId} is password-protected or password was invalid.", fileRecordId);

            var isInvalidPassword = !string.IsNullOrWhiteSpace(password);
            var encryptedResult = new PdfExtractionResult
            {
                FileId = fileRecord.Id,
                OriginalFileName = fileRecord.OriginalFileName,
                PageCount = 0,
                ExtractionStatus = "PasswordProtected",
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow,
                DurationMs = stopwatch.ElapsedMilliseconds,
                PdfType = "Encrypted",
                HasUsableText = false,
                Warnings = [isInvalidPassword ? "Incorrect PDF password. Please try again." : "The PDF is password-protected. Please provide the document password to unlock extraction."]
            };

            return (false, isInvalidPassword ? "Incorrect PDF password. Please try again." : "The PDF statement is password-protected and requires a password to open.", encryptedResult);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "PdfPig failed to open PDF document: {FileRecordId}", fileRecordId);

            var failedResult = new PdfExtractionResult
            {
                FileId = fileRecord.Id,
                OriginalFileName = fileRecord.OriginalFileName,
                PageCount = 0,
                ExtractionStatus = "Failed",
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow,
                DurationMs = stopwatch.ElapsedMilliseconds,
                PdfType = "Unknown",
                HasUsableText = false,
                Errors = ["The PDF document structure could not be parsed or is corrupted."]
            };

            return (false, "The PDF document could not be read. The file may be damaged or in an unsupported format.", failedResult);
        }

        using (pdfDocument)
        {
            // 4. Extract pages and structural geometry
            var extractionResult = ProcessPdfDocument(pdfDocument, fileRecord, startedAt, stopwatch);

            // 5. Save detailed extraction artifact as JSON outside web root
            var artifactSubDir = Path.Combine("Extractions", fileRecord.ClientId.ToString("N"));
            var artifactFileName = $"{fileRecord.Id}_extraction.json";
            string relativeArtifactPath;

            try
            {
                var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(extractionResult, JsonOptions);
                using var jsonStream = new MemoryStream(jsonBytes);
                relativeArtifactPath = await _storageService.SaveFileAsync(
                    jsonStream,
                    artifactSubDir,
                    artifactFileName,
                    cancellationToken);

                extractionResult.StorageArtifactPath = Path.Combine(artifactSubDir, artifactFileName).Replace('\\', '/');
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist extraction artifact JSON for file {FileRecordId}", fileRecordId);
                return (false, "An error occurred while saving the extraction results artifact.", null);
            }

            // 6. Persist metadata into existing PdfProcessingResults table in SQL Server
            try
            {
                var fileRecordExists = await _dbContext.FileRecords
                    .AnyAsync(f => f.Id == fileRecord.Id, cancellationToken);
                if (!fileRecordExists)
                {
                    _logger.LogWarning("FileRecord {FileRecordId} was deleted during extraction. Aborting metadata persistence.", fileRecordId);
                    return (false, "Statement was deleted during processing.", null);
                }

                var existingPdfResult = await _dbContext.PdfProcessingResults
                    .FirstOrDefaultAsync(p => p.FileRecordId == fileRecord.Id, cancellationToken);

                if (existingPdfResult != null)
                {
                    existingPdfResult.PageCount = extractionResult.PageCount;
                    existingPdfResult.HasUsableText = extractionResult.HasUsableText;
                    existingPdfResult.PdfType = extractionResult.HasUsableText ? 1 : 2; // 1 = DigitalWithText, 2 = ScannedNoText
                    existingPdfResult.ExtractionMethod = 1; // 1 = NativeTextExtraction (PdfPig)
                    existingPdfResult.ExtractedTextStoragePath = extractionResult.StorageArtifactPath;
                    existingPdfResult.ProcessedAt = DateTime.UtcNow;
                }
                else
                {
                    var newPdfResult = new PdfProcessingResult
                    {
                        Id = Guid.NewGuid(),
                        FileRecordId = fileRecord.Id,
                        PageCount = extractionResult.PageCount,
                        HasUsableText = extractionResult.HasUsableText,
                        PdfType = extractionResult.HasUsableText ? 1 : 2,
                        ExtractionMethod = 1,
                        ExtractedTextStoragePath = extractionResult.StorageArtifactPath,
                        ProcessedAt = DateTime.UtcNow
                    };
                    _dbContext.PdfProcessingResults.Add(newPdfResult);
                }

                // Update FileRecord processing status:
                // ProcessingStatus 2 = Completed / Extracted only if not currently actively in background worker processing (1)
                if (fileRecord.ProcessingStatus != 1)
                {
                    fileRecord.ProcessingStatus = 2;
                }
                fileRecord.UpdatedAt = DateTime.UtcNow;
                fileRecord.ProcessingError = extractionResult.HasUsableText ? null : "No digital text detected (scanned or image-based PDF).";

                await _dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Successfully persisted PdfProcessingResult and updated FileRecord for statement {FileRecordId}", fileRecordId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database update failed for extraction metadata of statement {FileRecordId}", fileRecordId);
                return (false, "Extraction succeeded but failed to update database status.", extractionResult);
            }

            return (true, null, extractionResult);
        }
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<PdfExtractionResult?> GetExtractionResultAsync(
        Guid fileRecordId,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        var fileRecord = await _dbContext.FileRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == fileRecordId && f.Client.UserId == currentUserId, cancellationToken);

        if (fileRecord == null) return null;

        var artifactSubDir = Path.Combine("Extractions", fileRecord.ClientId.ToString("N"));
        var artifactFileName = $"{fileRecord.Id}_extraction.json";

        await using var artifactStream = await _storageService.GetFileStreamAsync(
            artifactSubDir,
            artifactFileName,
            cancellationToken);

        if (artifactStream == null) return null;

        return await JsonSerializer.DeserializeAsync<PdfExtractionResult>(
            artifactStream,
            JsonOptions,
            cancellationToken);
    }

    private static PdfExtractionResult ProcessPdfDocument(
        PdfDocument document,
        FileRecord fileRecord,
        DateTime startedAt,
        Stopwatch stopwatch)
    {
        var pageCount = document.NumberOfPages;
        var pageResults = new List<PdfPageResult>(pageCount);

        int totalChars = 0;
        int totalWords = 0;
        int totalTextBlocks = 0;
        int totalCandidateRows = 0;
        int totalCandidateTables = 0;

        // Page-by-page independent extraction preserving boundaries
        for (int pageNum = 1; pageNum <= pageCount; pageNum++)
        {
            var page = document.GetPage(pageNum);
            var pageResult = ProcessSinglePage(page);
            pageResults.Add(pageResult);

            totalChars += pageResult.CharacterCount;
            totalWords += pageResult.WordCount;
            totalTextBlocks += pageResult.TextBlocks.Count;
            totalCandidateRows += pageResult.CandidateRows.Count;
            totalCandidateTables += pageResult.CandidateTables.Count;
        }

        // Cross-page conservative header/footer heuristic analysis
        DetectRepeatedHeadersAndFooters(pageResults);

        // Digital text availability check
        // If total alphanumeric character count is virtually zero, document is scanned/rasterized
        var hasUsableText = totalChars >= 10 && totalWords >= 2;
        var extractionStatus = hasUsableText ? "DigitalTextExtracted" : "NoDigitalTextDetected";
        var pdfType = hasUsableText ? "DigitalWithText" : "ScannedOrRasterizedNoText";

        var warnings = new List<string>();
        if (!hasUsableText)
        {
            warnings.Add("No usable digital text was detected in this PDF. It appears to be a scanned image or rasterized document; OCR processing will be supported in a future phase.");
        }

        stopwatch.Stop();
        var completedAt = DateTime.UtcNow;

        return new PdfExtractionResult
        {
            FileId = fileRecord.Id,
            OriginalFileName = fileRecord.OriginalFileName,
            PageCount = pageCount,
            ExtractionStatus = extractionStatus,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            DurationMs = stopwatch.ElapsedMilliseconds,
            PdfType = pdfType,
            HasUsableText = hasUsableText,
            CharacterCount = totalChars,
            WordCount = totalWords,
            TextBlockCount = totalTextBlocks,
            CandidateRowCount = totalCandidateRows,
            CandidateTableCount = totalCandidateTables,
            Pages = pageResults,
            Warnings = warnings,
            Errors = []
        };
    }

    private static PdfPageResult ProcessSinglePage(Page page)
    {
        var pageWidth = Math.Round(page.Width, 2);
        var pageHeight = Math.Round(page.Height, 2);

        // Extract raw words using PdfPig (immutable source of truth)
        var words = page.GetWords()
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .ToList();

        // 1. Geometry Normalization & Visual Line Grouping (Deterministic Reading Order)
        // Coordinate Convention:
        // Top-Left origin:
        // X = Left
        // Y = page.Height - BoundingBox.Top (distance from top edge of page)
        // Width = BoundingBox.Width
        // Height = BoundingBox.Height
        var visualLines = GroupWordsIntoLines(words, pageHeight);

        // 2. Assign reading order indices and flatten text blocks
        var textBlocks = new List<PdfTextBlock>(words.Count);
        int readingOrderIndex = 1;

        var candidateRows = new List<PdfCandidateRow>(visualLines.Count);
        int rowIndex = 1;
        var rawTextLines = new List<string>(visualLines.Count);

        foreach (var line in visualLines)
        {
            var lineBlocks = new List<PdfTextBlock>(line.Words.Count);

            foreach (var word in line.Words)
            {
                var block = new PdfTextBlock
                {
                    Text = word.Text,
                    X = Math.Round(word.BoundingBox.Left, 2),
                    Y = Math.Round(pageHeight - word.BoundingBox.Top, 2),
                    Width = Math.Round(word.BoundingBox.Width, 2),
                    Height = Math.Round(word.BoundingBox.Height, 2),
                    PageNumber = page.Number,
                    ReadingOrderIndex = readingOrderIndex++
                };

                lineBlocks.Add(block);
                textBlocks.Add(block);
            }

            var lineTop = lineBlocks.Min(b => b.Y);
            var lineBottom = lineBlocks.Max(b => b.Y + b.Height);
            
            // Reconstruct visual line text respecting horizontal word gaps and ordering
            var rawLineText = ReconstructLineText(line.Words);
            rawTextLines.Add(rawLineText);

            candidateRows.Add(new PdfCandidateRow
            {
                RowIndex = rowIndex++,
                PageNumber = page.Number,
                Y = Math.Round(lineTop, 2),
                Height = Math.Round(lineBottom - lineTop, 2),
                RawLineText = rawLineText,
                Cells = [],
                IsHeader = false,
                IsFooter = false,
                LineFragments = lineBlocks
            });
        }

        // Reconstruct visual RawText from visual lines with preserved horizontal spacing
        // Raw PDF word extraction = original extracted tokens + coordinates (source of truth)
        // RawText = deterministic human-readable visual reconstruction of those tokens
        var rawText = string.Join("\n", rawTextLines);

        // 3. Best-Effort Candidate Table & Column Detection
        var candidateTables = DetectCandidateTables(candidateRows, page.Number, pageWidth, pageHeight);

        var charCount = words.Sum(w => w.Text.Length);
        var wordCount = words.Count;
        var hasUsableText = charCount >= 5 && wordCount >= 1;

        return new PdfPageResult
        {
            PageNumber = page.Number,
            Width = pageWidth,
            Height = pageHeight,
            RawText = rawText,
            WordCount = wordCount,
            CharacterCount = charCount,
            HasUsableText = hasUsableText,
            TextBlocks = textBlocks,
            CandidateRows = candidateRows,
            CandidateTables = candidateTables,
            RepeatedHeaders = [],
            RepeatedFooters = []
        };
    }

    private static List<VisualLine> GroupWordsIntoLines(List<Word> words, double pageHeight)
    {
        if (words.Count == 0) return [];

        // Project words into top-normalized coordinates
        var projectedWords = words.Select(w => new
        {
            Word = w,
            Left = w.BoundingBox.Left,
            Top = pageHeight - w.BoundingBox.Top,
            Bottom = pageHeight - w.BoundingBox.Bottom,
            Height = Math.Max(w.BoundingBox.Height, 6.0),
            CenterY = pageHeight - (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2.0
        })
        .OrderBy(w => w.CenterY)
        .ThenBy(w => w.Left)
        .ToList();

        var lines = new List<VisualLine>();

        foreach (var pw in projectedWords)
        {
            // Find an existing line whose vertical bounds overlap with this word
            VisualLine? matchingLine = null;
            foreach (var line in lines)
            {
                // Vertical proximity criteria:
                // Word center falls within line vertical bounds expanded by 30% of font height
                var verticalTolerance = Math.Max(line.AverageHeight, pw.Height) * 0.45;
                if (Math.Abs(pw.CenterY - line.CenterY) <= verticalTolerance)
                {
                    matchingLine = line;
                    break;
                }
            }

            if (matchingLine != null)
            {
                matchingLine.Add(pw.Word, pw.Left, pw.Top, pw.Bottom, pw.Height);
            }
            else
            {
                var newLine = new VisualLine();
                newLine.Add(pw.Word, pw.Left, pw.Top, pw.Bottom, pw.Height);
                lines.Add(newLine);
            }
        }

        // Sort lines strictly from top to bottom (Y ascending)
        // Inside each line, sort words horizontally from left to right (X ascending)
        return lines
            .OrderBy(l => l.Top)
            .Select(l =>
            {
                l.SortWordsLeftToRight();
                return l;
            })
            .ToList();
    }

    private static List<PdfCandidateTable> DetectCandidateTables(
        List<PdfCandidateRow> rows,
        int pageNumber,
        double pageWidth,
        double pageHeight)
    {
        // Require at least 3 rows to consider tabular structure
        if (rows.Count < 3) return [];

        // Identify rows that have multiple spaced columns (>= 3 words with noticeable horizontal gaps)
        var multiColumnRows = rows
            .Where(r => r.LineFragments.Count >= 3)
            .ToList();

        if (multiColumnRows.Count < 3) return [];

        // Collect all fragment X coordinates across multi-column rows
        var leftPositions = multiColumnRows
            .SelectMany(r => r.LineFragments)
            .Select(f => f.X)
            .OrderBy(x => x)
            .ToList();

        // Cluster X positions to find distinct column boundaries
        var columnClusters = ClusterValues(leftPositions, tolerance: 18.0);
        if (columnClusters.Count < 3) return [];

        var columns = new List<PdfCandidateColumn>(columnClusters.Count);
        for (int i = 0; i < columnClusters.Count; i++)
        {
            var left = columnClusters[i].Min;
            var right = (i < columnClusters.Count - 1)
                ? columnClusters[i + 1].Min - 4.0
                : pageWidth - 10.0;

            columns.Add(new PdfCandidateColumn
            {
                ColumnIndex = i,
                LeftX = Math.Round(left, 2),
                RightX = Math.Round(right, 2),
                HeaderText = null
            });
        }

        // Populate cells for rows matching these columns
        var tableRows = new List<PdfCandidateRow>();
        foreach (var row in multiColumnRows)
        {
            var cells = new List<PdfCandidateCell>();

            foreach (var col in columns)
            {
                // Find fragments falling within this column interval [LeftX - tolerance, RightX]
                var colFragments = row.LineFragments
                    .Where(f => f.X >= col.LeftX - 8.0 && f.X < col.RightX)
                    .OrderBy(f => f.X)
                    .ToList();

                if (colFragments.Count > 0)
                {
                    var cellText = string.Join(" ", colFragments.Select(f => f.Text));
                    var minX = colFragments.Min(f => f.X);
                    var maxX = colFragments.Max(f => f.X + f.Width);
                    var minY = colFragments.Min(f => f.Y);
                    var maxY = colFragments.Max(f => f.Y + f.Height);

                    cells.Add(new PdfCandidateCell
                    {
                        ColumnIndex = col.ColumnIndex,
                        Text = cellText,
                        BoundingBox = new PdfBoundingBox(minX, minY, Math.Round(maxX - minX, 2), Math.Round(maxY - minY, 2))
                    });
                }
            }

            if (cells.Count >= 2)
            {
                row.Cells = cells;
                tableRows.Add(row);
            }
        }

        if (tableRows.Count < 3) return [];

        var tableTop = tableRows.Min(r => r.Y);
        var tableBottom = tableRows.Max(r => r.Y + r.Height);
        var tableLeft = columns.Min(c => c.LeftX);
        var tableRight = columns.Max(c => c.RightX);

        var confidence = Math.Min(1.0, Math.Round(tableRows.Count / (double)rows.Count, 2) + 0.3);

        return
        [
            new PdfCandidateTable
            {
                TableIndex = 1,
                PageNumber = pageNumber,
                BoundingBox = new PdfBoundingBox(
                    tableLeft,
                    tableTop,
                    Math.Round(tableRight - tableLeft, 2),
                    Math.Round(tableBottom - tableTop, 2)),
                ColumnCount = columns.Count,
                RowCount = tableRows.Count,
                Columns = columns,
                Rows = tableRows,
                Confidence = confidence
            }
        ];
    }

    private static void DetectRepeatedHeadersAndFooters(List<PdfPageResult> pages)
    {
        if (pages.Count <= 1) return;

        var headerSignatures = new Dictionary<string, (int Count, List<double> YPositions)>(StringComparer.OrdinalIgnoreCase);
        var footerSignatures = new Dictionary<string, (int Count, List<double> YPositions)>(StringComparer.OrdinalIgnoreCase);

        var amountRegex = new Regex(@"\b\d{1,3}(?:,\d{2,3})*\.\d{2}\b", RegexOptions.Compiled);
        var dateRegex = new Regex(@"^\d{2}[/-]\d{2}[/-]\d{2,4}", RegexOptions.Compiled);
        var upiOrPaymentRegex = new Regex(@"PAYMENT\s+FROM|UPI-|UPI/|IMPS-|NEFT\s+|RTGS\s+|@|\bATM\b|\bPOS\b|\bTRANSFER\b|\bINVOICE\b|\bBILL\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        foreach (var page in pages)
        {
            var headerThresholdY = page.Height * 0.12;
            var footerThresholdY = page.Height * 0.90;

            foreach (var row in page.CandidateRows)
            {
                var text = Regex.Replace(row.RawLineText.Trim(), @"\s+", " ");
                if (string.IsNullOrWhiteSpace(text)) continue;

                // Headers
                if (row.Y <= headerThresholdY && text.Length >= 4)
                {
                    if (!headerSignatures.TryGetValue(text, out var entry))
                    {
                        entry = (0, new List<double>());
                    }
                    entry.Count++;
                    entry.YPositions.Add(row.Y);
                    headerSignatures[text] = entry;
                }
                // Footers: Must meet length/substance criteria
                else if (row.Y >= footerThresholdY)
                {
                    // Filter out transaction rows: lines with currency amounts or starting with a date or containing transaction narration keywords
                    if (amountRegex.IsMatch(text) || dateRegex.IsMatch(text) || upiOrPaymentRegex.IsMatch(text))
                    {
                        continue;
                    }

                    // A genuine document footer line must have substance:
                    // Require at least 15 characters, or match a page number format "Page # of #"
                    bool isPageNumber = Regex.IsMatch(text, @"^Page\s+\d+(\s+of\s+\d+)?$", RegexOptions.IgnoreCase);
                    if (text.Length < 15 && !isPageNumber)
                    {
                        continue;
                    }

                    // Strip numeric page numbers for fuzzy footer matching (e.g., "Page 1 of 5" vs "Page 2 of 5")
                    var maskedFooter = Regex.Replace(text, @"\b\d+\b", "#");
                    if (!footerSignatures.TryGetValue(maskedFooter, out var entry))
                    {
                        entry = (0, new List<double>());
                    }
                    entry.Count++;
                    entry.YPositions.Add(row.Y);
                    footerSignatures[maskedFooter] = entry;
                }
            }
        }

        // Recurrence criteria:
        // Must appear on at least 2 pages (for 2-3 page docs) or 20% of pages (capped at 5)
        var minRecurrence = Math.Max(2, Math.Min(5, (int)Math.Ceiling(pages.Count * 0.20)));

        var repeatedHeaders = headerSignatures
            .Where(kv => kv.Value.Count >= minRecurrence && (kv.Value.YPositions.Max() - kv.Value.YPositions.Min()) <= 20.0)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var repeatedFooters = footerSignatures
            .Where(kv => kv.Value.Count >= minRecurrence && (kv.Value.YPositions.Max() - kv.Value.YPositions.Min()) <= 20.0)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var page in pages)
        {
            var headerThresholdY = page.Height * 0.12;
            var footerThresholdY = page.Height * 0.90;

            foreach (var row in page.CandidateRows)
            {
                var text = Regex.Replace(row.RawLineText.Trim(), @"\s+", " ");
                var maskedText = Regex.Replace(text, @"\b\d+\b", "#");

                if (row.Y <= headerThresholdY && repeatedHeaders.Contains(text))
                {
                    row.IsHeader = true;
                    if (!page.RepeatedHeaders.Contains(text)) page.RepeatedHeaders.Add(text);
                }
                else if (row.Y >= footerThresholdY && (repeatedFooters.Contains(maskedText) || repeatedFooters.Contains(text)))
                {
                    row.IsFooter = true;
                    if (!page.RepeatedFooters.Contains(text)) page.RepeatedFooters.Add(text);
                }
            }
        }
    }

    private static List<ClusterRange> ClusterValues(List<double> values, double tolerance)
    {
        var clusters = new List<ClusterRange>();
        foreach (var val in values)
        {
            var match = clusters.FirstOrDefault(c => Math.Abs(c.Average - val) <= tolerance);
            if (match != null)
            {
                match.Add(val);
            }
            else
            {
                clusters.Add(new ClusterRange(val));
            }
        }

        return clusters.OrderBy(c => c.Min).ToList();
    }

    private sealed class ClusterRange
    {
        private double _sum;
        private int _count;

        public double Min { get; private set; }
        public double Max { get; private set; }
        public double Average => _count > 0 ? _sum / _count : 0;

        public ClusterRange(double initial)
        {
            Min = initial;
            Max = initial;
            _sum = initial;
            _count = 1;
        }

        public void Add(double val)
        {
            if (val < Min) Min = val;
            if (val > Max) Max = val;
            _sum += val;
            _count++;
        }
    }

    private sealed class VisualLine
    {
        public List<Word> Words { get; } = [];
        public double Top { get; private set; } = double.MaxValue;
        public double Bottom { get; private set; } = double.MinValue;
        private double _heightSum;

        public double CenterY => (Top + Bottom) / 2.0;
        public double AverageHeight => Words.Count > 0 ? _heightSum / Words.Count : 10.0;

        public void Add(Word word, double left, double top, double bottom, double height)
        {
            Words.Add(word);
            if (top < Top) Top = top;
            if (bottom > Bottom) Bottom = bottom;
            _heightSum += height;
        }

        public void SortWordsLeftToRight()
        {
            Words.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));
        }
    }

    /// <summary>
    /// Reconstructs a single visual line of text from its constituent word tokens.
    /// Preserves normal single-space word gaps and applies proportional spacing (2 to 8 spaces)
    /// when distinct horizontal gaps exist (such as column gutters or separated label/value fields),
    /// without destroying raw word geometries or attempting pixel-perfect rendering.
    /// </summary>
    private static string ReconstructLineText(IReadOnlyList<Word> words)
    {
        if (words == null || words.Count == 0) return string.Empty;
        if (words.Count == 1) return words[0].Text;

        var sb = new StringBuilder();
        sb.Append(words[0].Text);

        for (int i = 1; i < words.Count; i++)
        {
            var prevWord = words[i - 1];
            var currWord = words[i];

            // Horizontal gap between current word Left and previous word Right in points
            var gap = currWord.BoundingBox.Left - prevWord.BoundingBox.Right;

            if (gap <= 0)
            {
                // Words touch or slightly overlap due to font metrics/kerning;
                // separate distinct tokens with a single space
                sb.Append(' ');
            }
            else
            {
                // Character width estimate based on font height (defaulting to minimum ~3.5pt)
                var refHeight = Math.Max(Math.Min(prevWord.BoundingBox.Height, currWord.BoundingBox.Height), 6.0);
                var charWidth = Math.Max(refHeight * 0.38, 3.5);

                if (gap <= charWidth * 1.8)
                {
                    // Standard inter-word spacing within a phrase or sentence
                    sb.Append(' ');
                }
                else
                {
                    // Meaningful horizontal gap (e.g. column separation, distinct metadata fields).
                    // Scale spaces proportionally to the gap, bounded between 2 and 8 spaces.
                    int spaces = (int)Math.Clamp(Math.Round(gap / charWidth), 2, 8);
                    sb.Append(new string(' ', spaces));
                }
            }

            sb.Append(currWord.Text);
        }

        return sb.ToString();
    }
}
