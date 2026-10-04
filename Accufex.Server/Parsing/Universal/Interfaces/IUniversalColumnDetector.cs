using System.Collections.Generic;
using Accufex.Server.DTOs;
using Accufex.Server.Parsing.Universal.Models;

namespace Accufex.Server.Parsing.Universal.Interfaces;

/// <summary>
/// Identifies and calibrates transaction table columns from arbitrary statement tables
/// using spatial coordinates and semantic header tokens.
/// </summary>
public interface IUniversalColumnDetector
{
    /// <summary>
    /// Detects transaction columns from a candidate header line or page text blocks.
    /// </summary>
    List<DetectedColumnLayout> DetectColumns(List<PdfCandidateRow> candidateRows, List<PdfTextBlock> textBlocks, double pageWidth);
}
