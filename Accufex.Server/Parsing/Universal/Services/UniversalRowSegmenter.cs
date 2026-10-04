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
/// Dynamic transaction row boundary and multi-line narration detector.
/// Clusters PDF text fragments by spatial coordinates, eliminates headers/footers/branding,
/// and aggregates wrapped transaction lines into unified candidate transaction units.
/// </summary>
public class UniversalRowSegmenter : IUniversalRowSegmenter
{
    private readonly ILogger<UniversalRowSegmenter> _logger;

    private static readonly Regex DateTokenRegex = new(
        @"\b(?:\d{1,2}[/-]\d{1,2}[/-]\d{2,4}|\d{1,2}\s+[A-Za-z]{3}\s+\d{2,4}|\d{1,2}-[A-Za-z]{3}-\d{2,4}|\d{1,2}\.[A-Za-z]{3}\.\d{2,4}|\d{4}-\d{2}-\d{2}|\d{1,2}\.\d{1,2}\.\d{2,4})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public UniversalRowSegmenter(ILogger<UniversalRowSegmenter> logger)
    {
        _logger = logger;
    }

    public List<CandidateTransactionRow> SegmentDocumentRows(
        List<PdfPageResult> pages,
        StatementStructureAnalysis structure)
    {
        var allCandidateRows = new List<CandidateTransactionRow>();
        if (pages == null || pages.Count == 0) return allCandidateRows;

        int globalRowIndex = 1;
        foreach (var page in pages.OrderBy(p => p.PageNumber))
        {
            var pageRows = SegmentPageRows(page, structure);
            foreach (var r in pageRows)
            {
                r.RowIndex = globalRowIndex++;
                allCandidateRows.Add(r);
            }
        }

        _logger.LogInformation("SegmentDocumentRows completed: Extracted {Count} logical transaction rows across {PageCount} pages.",
            allCandidateRows.Count, pages.Count);

        return allCandidateRows;
    }

    public List<CandidateTransactionRow> SegmentPageRows(
        PdfPageResult page,
        StatementStructureAnalysis structure)
    {
        var result = new List<CandidateTransactionRow>();
        var physicalRows = page.CandidateRows ?? [];
        if (physicalRows.Count == 0) return result;

        // 1. Determine Page-Specific Table Boundaries
        double topBoundY = structure.HeaderBottomY;
        double bottomBoundY = structure.FooterTopY;

        // On multi-page statements (page > 1), detect repeated header row or adjust top bound
        if (page.PageNumber > 1)
        {
            var repeatedHeader = DetectRepeatedHeaderRow(physicalRows, structure);
            if (repeatedHeader != null)
            {
                topBoundY = repeatedHeader.Y + (repeatedHeader.Height > 0 ? repeatedHeader.Height : 15.0);
            }
            else
            {
                // Fallback top boundary for continuation page (ignore top 5% or 40 points of branding/page title)
                topBoundY = Math.Max(40.0, page.Height * 0.06);
            }
        }

        // Adjust footer bound on each page if explicit footer text appears
        double detectedFooterY = DetectPageFooterBound(physicalRows, page.Height);
        if (detectedFooterY > topBoundY)
        {
            bottomBoundY = Math.Min(bottomBoundY, detectedFooterY);
        }

        // 2. Identify Date Column Bounds for Primary Row Recognition
        var dateCol = structure.Columns.FirstOrDefault(c => c.ColumnType == StatementColumnType.Date);
        double dateColLeft = dateCol?.LeftX ?? 0.0;
        double dateColRight = dateCol != null ? dateCol.RightX + 25.0 : 150.0; // Allow 25 pt tolerance for column margins

        CandidateTransactionRow? currentRow = null;

        foreach (var pRow in physicalRows.OrderBy(r => r.Y))
        {
            // Skip rows outside table boundary
            if (pRow.Y < topBoundY - 2.0 || pRow.Y >= bottomBoundY)
            {
                continue;
            }

            var lineText = pRow.RawLineText?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(lineText)) continue;

            // Skip repeated column header rows
            if (IsColumnHeaderLine(pRow, structure))
            {
                continue;
            }

            // Skip summary / opening balance lines from becoming a transaction row
            if (IsSummaryOrBalanceLine(lineText))
            {
                continue;
            }

            // Check if this physical row contains a valid date token in or near the date column zone
            var (hasDate, dateToken) = CheckDateInDateColumn(pRow, dateColLeft, dateColRight);

            if (hasDate)
            {
                // This physical row starts a NEW candidate transaction
                currentRow = new CandidateTransactionRow
                {
                    PageNumber = page.PageNumber,
                    TopY = pRow.Y,
                    BottomY = pRow.Y + pRow.Height,
                    HasDateToken = true,
                    PrimaryDateText = dateToken,
                    Fragments = [.. pRow.LineFragments],
                    SourcePhysicalRows = [pRow]
                };
                result.Add(currentRow);
            }
            else if (currentRow != null)
            {
                // Continuation line (wrapped narration, sub-reference, or delayed amount)
                // Verify vertical proximity to prevent absorbing distant footers (must be within 40 points of previous row)
                double verticalGap = pRow.Y - currentRow.BottomY;
                if (verticalGap < 45.0)
                {
                    currentRow.BottomY = Math.Max(currentRow.BottomY, pRow.Y + pRow.Height);
                    currentRow.Fragments.AddRange(pRow.LineFragments);
                    currentRow.SourcePhysicalRows.Add(pRow);
                }
                else
                {
                    // Too distant from previous transaction; close current row
                    currentRow = null;
                }
            }
        }

        return result;
    }

    private static (bool hasDate, string? dateToken) CheckDateInDateColumn(
        PdfCandidateRow row,
        double dateColLeft,
        double dateColRight)
    {
        // 1. Inspect fragments located in the Date column horizontal interval
        var dateFrags = row.LineFragments
            .Where(f => f.X >= Math.Max(0, dateColLeft - 20.0) && f.X <= dateColRight + 15.0)
            .OrderBy(f => f.X)
            .ToList();

        foreach (var frag in dateFrags)
        {
            var match = DateTokenRegex.Match(frag.Text.Trim());
            if (match.Success)
            {
                return (true, match.Value);
            }
        }

        // 2. Also check combined text of first two fragments in the row
        if (dateFrags.Count >= 2)
        {
            string combined = string.Join(" ", dateFrags.Take(3).Select(f => f.Text.Trim()));
            var match = DateTokenRegex.Match(combined);
            if (match.Success)
            {
                return (true, match.Value);
            }
        }

        // 3. Fallback: If the whole line begins with a date pattern
        var lineMatch = DateTokenRegex.Match(row.RawLineText.Trim());
        if (lineMatch.Success && lineMatch.Index == 0)
        {
            return (true, lineMatch.Value);
        }

        return (false, null);
    }

    private static PdfCandidateRow? DetectRepeatedHeaderRow(
        List<PdfCandidateRow> rows,
        StatementStructureAnalysis structure)
    {
        foreach (var row in rows.Take(10))
        {
            if (IsColumnHeaderLine(row, structure))
            {
                return row;
            }
        }
        return null;
    }

    private static bool IsColumnHeaderLine(PdfCandidateRow row, StatementStructureAnalysis structure)
    {
        var text = row.RawLineText?.ToLowerInvariant() ?? string.Empty;
        int matches = 0;

        if (text.Contains("date") || text.Contains("txn date") || text.Contains("tran date")) matches++;
        if (text.Contains("particular") || text.Contains("narration") || text.Contains("description") || text.Contains("details")) matches++;
        if (text.Contains("debit") || text.Contains("dr.") || text.Contains("withdrawal")) matches++;
        if (text.Contains("credit") || text.Contains("cr.") || text.Contains("deposit")) matches++;
        if (text.Contains("balance")) matches++;

        return matches >= 2;
    }

    private static bool IsSummaryOrBalanceLine(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("opening balance") ||
               lower.Contains("brought forward") ||
               lower.Contains("b/f") ||
               lower.Contains("closing balance") ||
               lower.Contains("carried forward") ||
               lower.Contains("c/f") ||
               lower.Contains("grand total") ||
               lower.Contains("page total");
    }

    private static double DetectPageFooterBound(List<PdfCandidateRow> rows, double pageHeight)
    {
        double bound = pageHeight > 0 ? pageHeight - 25.0 : 800.0;

        foreach (var row in rows.OrderByDescending(r => r.Y))
        {
            var lower = row.RawLineText?.ToLowerInvariant() ?? string.Empty;
            if (lower.Contains("page ") ||
                lower.Contains("computer generated") ||
                lower.Contains("toll free") ||
                lower.Contains("disclaimer") ||
                lower.Contains("registered office") ||
                lower.Contains("end of statement"))
            {
                bound = Math.Min(bound, row.Y - 5.0);
            }
        }

        return bound;
    }
}
