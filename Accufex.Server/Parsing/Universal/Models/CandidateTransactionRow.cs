using System.Collections.Generic;
using System.Linq;
using Accufex.Server.DTOs;

namespace Accufex.Server.Parsing.Universal.Models;

/// <summary>
/// Represents a logically segmented candidate transaction row, which may span one or more
/// physical PDF text lines (e.g., wrapped multi-line narration or multi-line references).
/// </summary>
public class CandidateTransactionRow
{
    public int PageNumber { get; set; }

    public int RowIndex { get; set; }

    public double TopY { get; set; }

    public double BottomY { get; set; }

    public bool HasDateToken { get; set; }

    public string? PrimaryDateText { get; set; }

    public List<PdfTextBlock> Fragments { get; set; } = [];

    public List<PdfCandidateRow> SourcePhysicalRows { get; set; } = [];

    public string RawJoinedText => string.Join(" ", Fragments.OrderBy(f => f.Y).ThenBy(f => f.X).Select(f => f.Text.Trim()));
}
