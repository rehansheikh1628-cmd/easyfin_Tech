using Accufex.Server.DTOs;
using Accufex.Server.Parsing.Universal.Models;

namespace Accufex.Server.Parsing.Universal.Interfaces;

/// <summary>
/// Analyzes raw digital PDF extraction artifacts to discover structural bounds, table headers,
/// and geometry without bank-specific hardcoding.
/// </summary>
public interface IStatementStructureAnalyzer
{
    /// <summary>
    /// Analyzes the PDF extraction result to discover the document's tabular layout.
    /// </summary>
    StatementStructureAnalysis AnalyzeStructure(PdfExtractionResult extraction);
}
