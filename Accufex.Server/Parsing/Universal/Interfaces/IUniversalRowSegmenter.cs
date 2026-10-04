using System.Collections.Generic;
using Accufex.Server.DTOs;
using Accufex.Server.Parsing.Universal.Models;

namespace Accufex.Server.Parsing.Universal.Interfaces;

/// <summary>
/// Segments raw PDF text fragments into logical transaction rows across single and multi-line entries,
/// filtering out headers, footers, repeated headers, page numbers, and non-transaction metadata.
/// </summary>
public interface IUniversalRowSegmenter
{
    /// <summary>
    /// Segments physical candidate rows from a single page into logical candidate transaction rows.
    /// </summary>
    List<CandidateTransactionRow> SegmentPageRows(PdfPageResult page, StatementStructureAnalysis structure);

    /// <summary>
    /// Segments physical candidate rows across all document pages into logical candidate transaction rows,
    /// maintaining reading order and handling page breaks.
    /// </summary>
    List<CandidateTransactionRow> SegmentDocumentRows(List<PdfPageResult> pages, StatementStructureAnalysis structure);
}
