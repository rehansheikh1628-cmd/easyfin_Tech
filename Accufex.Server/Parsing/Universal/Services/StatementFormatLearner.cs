using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Accufex.Server.DTOs;
using Accufex.Server.Parsing.Universal.Interfaces;
using Accufex.Server.Parsing.Universal.Models;
using Accufex.Server.Services;
using Microsoft.Extensions.Logging;

namespace Accufex.Server.Parsing.Universal.Services;

public class StatementFormatLearner : IStatementFormatLearner
{
    private const string FingerprintDir = "universal-fingerprints";
    private readonly IFileStorageService _fileStorageService;
    private readonly ILogger<StatementFormatLearner> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public StatementFormatLearner(
        IFileStorageService fileStorageService,
        ILogger<StatementFormatLearner> logger)
    {
        _fileStorageService = fileStorageService;
        _logger = logger;
    }

    public StatementFormatFingerprint GenerateFingerprint(
        UniversalParseResult parseResult,
        List<DetectedColumnLayout> columns,
        PdfExtractionResult extraction)
    {
        var firstPage = extraction.Pages.FirstOrDefault();
        double pageWidth = firstPage != null && firstPage.Width > 0 ? firstPage.Width : 595.0;

        var orderedColumns = columns.OrderBy(c => c.LeftX).ToList();
        var columnTypes = orderedColumns.Select(c => c.ColumnType).ToList();
        var relativePositions = orderedColumns
            .Select(c => Math.Round(c.LeftX / pageWidth, 2))
            .ToList();

        var headerTokens = orderedColumns
            .Select(c => c.HeaderText.Trim().ToLowerInvariant())
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .ToList();

        bool hasDebit = orderedColumns.Any(c => c.ColumnType == StatementColumnType.Debit);
        bool hasCredit = orderedColumns.Any(c => c.ColumnType == StatementColumnType.Credit);
        bool hasExplicitDebitCredit = hasDebit && hasCredit;

        string detectedDateFormat = "dd/MM/yyyy";
        var firstValidDateTx = parseResult.Transactions.FirstOrDefault(t => t.TransactionDate != default);
        if (firstValidDateTx != null)
        {
            detectedDateFormat = "dd/MM/yyyy";
        }

        // Build deterministic signature
        var sigBuilder = new StringBuilder();
        sigBuilder.Append(string.Join(",", columnTypes));
        sigBuilder.Append('|');
        sigBuilder.Append(string.Join(",", relativePositions.Select(p => p.ToString("F2", CultureInfo.InvariantCulture))));
        sigBuilder.Append('|');
        sigBuilder.Append(string.Join(",", headerTokens));

        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(sigBuilder.ToString()));
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        return new StatementFormatFingerprint
        {
            FingerprintHash = hash,
            BankName = parseResult.DetectedBankName ?? "Unknown Bank",
            ColumnCount = orderedColumns.Count,
            ColumnOrder = columnTypes,
            RelativeColumnPositions = relativePositions,
            HeaderKeywords = headerTokens,
            DateFormat = detectedDateFormat,
            HasExplicitDebitCredit = hasExplicitDebitCredit,
            CreatedAtUtc = DateTime.UtcNow,
            UsageCount = 1
        };
    }

    public async Task SaveFingerprintAsync(
        StatementFormatFingerprint fingerprint,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var fileName = $"{fingerprint.FingerprintHash}.json";
            var json = JsonSerializer.Serialize(fingerprint, JsonOptions);
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            await _fileStorageService.SaveFileAsync(stream, fileName, FingerprintDir, cancellationToken);
            _logger.LogInformation("Saved statement format fingerprint {Hash} for {BankName}.", fingerprint.FingerprintHash, fingerprint.BankName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist statement format fingerprint {Hash}.", fingerprint.FingerprintHash);
        }
    }

    public async Task<StatementFormatFingerprint?> FindMatchingFingerprintAsync(
        PdfExtractionResult extraction,
        List<DetectedColumnLayout> columns,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var firstPage = extraction.Pages.FirstOrDefault();
            double pageWidth = firstPage != null && firstPage.Width > 0 ? firstPage.Width : 595.0;

            var orderedColumns = columns.OrderBy(c => c.LeftX).ToList();
            var columnTypes = orderedColumns.Select(c => c.ColumnType).ToList();
            var relativePositions = orderedColumns
                .Select(c => Math.Round(c.LeftX / pageWidth, 2))
                .ToList();

            var headerTokens = orderedColumns
                .Select(c => c.HeaderText.Trim().ToLowerInvariant())
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .ToList();

            var sigBuilder = new StringBuilder();
            sigBuilder.Append(string.Join(",", columnTypes));
            sigBuilder.Append('|');
            sigBuilder.Append(string.Join(",", relativePositions.Select(p => p.ToString("F2", CultureInfo.InvariantCulture))));
            sigBuilder.Append('|');
            sigBuilder.Append(string.Join(",", headerTokens));

            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(sigBuilder.ToString()));
            var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();

            var fileName = $"{hash}.json";
            if (!await _fileStorageService.FileExistsAsync(fileName, FingerprintDir, cancellationToken))
            {
                return null;
            }

            var stream = await _fileStorageService.GetFileStreamAsync(fileName, FingerprintDir, cancellationToken);
            if (stream == null) return null;

            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync(cancellationToken);
            var matched = JsonSerializer.Deserialize<StatementFormatFingerprint>(json, JsonOptions);
            if (matched != null)
            {
                matched.UsageCount++;
                _ = SaveFingerprintAsync(matched, cancellationToken);
            }
            return matched;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while matching format fingerprint.");
            return null;
        }
    }
}
