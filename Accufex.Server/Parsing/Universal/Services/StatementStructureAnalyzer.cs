using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Accufex.Server.DTOs;
using Accufex.Server.Parsing.Universal.Interfaces;
using Accufex.Server.Parsing.Universal.Models;
using Microsoft.Extensions.Logging;

namespace Accufex.Server.Parsing.Universal.Services;

/// <summary>
/// Bank-agnostic statement structure discovery service.
/// Analyzes geometry, table delimiters, and structural boundaries.
/// </summary>
public class StatementStructureAnalyzer : IStatementStructureAnalyzer
{
    private readonly IUniversalColumnDetector _columnDetector;
    private readonly ILogger<StatementStructureAnalyzer> _logger;

    public StatementStructureAnalyzer(
        IUniversalColumnDetector columnDetector,
        ILogger<StatementStructureAnalyzer> logger)
    {
        _columnDetector = columnDetector;
        _logger = logger;
    }

    public StatementStructureAnalysis AnalyzeStructure(PdfExtractionResult extraction)
    {
        var analysis = new StatementStructureAnalysis
        {
            AnalyzedPageCount = extraction.Pages.Count
        };

        if (extraction.Pages.Count == 0)
        {
            analysis.StructuralWarnings.Add("Extraction result contains zero pages.");
            return analysis;
        }

        var firstPage = extraction.Pages[0];
        double pageWidth = firstPage.Width;
        double pageHeight = firstPage.Height;

        // 1. Identify Potential Bank Name and Account Number from Header Region (Top 30% of page)
        var topBlocks = firstPage.TextBlocks
            .Where(b => b.Y <= pageHeight * 0.35)
            .OrderBy(b => b.Y)
            .ThenBy(b => b.X)
            .ToList();

        var topText = string.Join(" ", topBlocks.Select(b => b.Text));

        var bankNameMatch = Regex.Match(topText, @"\b([A-Za-z\s.&'-]{3,35}\b(?:Bank|Gramin\s+Bank|Co-?operative\s+Bank|Sahakari\s+Bank|Financial\s+Services))\b", RegexOptions.IgnoreCase);
        if (bankNameMatch.Success)
        {
            analysis.PotentialBankName = bankNameMatch.Value.Trim();
        }

        var accMatch = Regex.Match(topText, @"(?:Account\s*(?:No|Number)|A/c\s*(?:No|Number))\s*[:.]?\s*(\d{9,18})", RegexOptions.IgnoreCase);
        if (accMatch.Success)
        {
            analysis.PotentialAccountNumber = accMatch.Groups[1].Value.Trim();
        }

        // 2. Discover Columns using Universal Column Detector
        var candidateRows = firstPage.CandidateRows ?? [];
        analysis.Columns = _columnDetector.DetectColumns(candidateRows, firstPage.TextBlocks, pageWidth);

        // 3. Discover Table Top (HeaderBottomY)
        if (analysis.Columns.Count > 0)
        {
            // Find row matching the detected columns
            var headerRow = candidateRows.FirstOrDefault(r =>
                analysis.Columns.Take(2).All(c => r.RawLineText.Contains(c.HeaderText.Split(' ')[0], StringComparison.OrdinalIgnoreCase)));

            if (headerRow != null)
            {
                analysis.HeaderTopY = headerRow.Y;
                analysis.HeaderBottomY = headerRow.Y + (headerRow.Height > 0 ? headerRow.Height : 15.0);
            }
            else
            {
                analysis.HeaderBottomY = 150.0;
            }
        }
        else
        {
            analysis.HeaderBottomY = 150.0;
            analysis.StructuralWarnings.Add("Could not reliably identify transaction column headers on page 1.");
        }

        // 4. Discover Footer Top Boundary (FooterTopY)
        double footerTopY = pageHeight > 0 ? pageHeight - 30.0 : 800.0;
        foreach (var row in candidateRows.OrderByDescending(r => r.Y))
        {
            if (row.Y <= analysis.HeaderBottomY) break;

            var lower = row.RawLineText.ToLowerInvariant();
            if (lower.Contains("page ") ||
                lower.Contains("computer generated") ||
                lower.Contains("grand total") ||
                lower.Contains("page total") ||
                lower.Contains("disclaimer") ||
                lower.Contains("toll free"))
            {
                footerTopY = row.Y - 5.0;
                break;
            }
        }
        analysis.FooterTopY = footerTopY;

        // 5. Count Candidate Table Rows inside Table Region
        int tableRows = candidateRows.Count(r => r.Y > analysis.HeaderBottomY && r.Y < analysis.FooterTopY);
        analysis.DetectedTableRowCount = tableRows;

        // 6. Inspect Sample Date Format
        var dateRegex = new Regex(@"\b(\d{2}[/-]\d{2}[/-]\d{2,4}|\d{1,2}\s+[A-Za-z]{3}\s+\d{2,4}|\d{1,2}-[A-Za-z]{3}-\d{2,4})\b");
        var sampleDateMatch = candidateRows
            .Where(r => r.Y > analysis.HeaderBottomY && r.Y < analysis.FooterTopY)
            .Select(r => dateRegex.Match(r.RawLineText))
            .FirstOrDefault(m => m.Success);

        if (sampleDateMatch != null)
        {
            analysis.DetectedDateFormat = sampleDateMatch.Value;
        }

        _logger.LogInformation("Structure analysis complete. PotentialBank: {Bank}, Columns: {ColCount}, Rows: {RowCount}, Sufficiency: {Sufficient}",
            analysis.PotentialBankName ?? "Unknown", analysis.Columns.Count, analysis.DetectedTableRowCount, analysis.HasSufficientColumnsForExtraction);

        return analysis;
    }
}
