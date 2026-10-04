using System.Threading;
using System.Threading.Tasks;
using Accufex.Server.DTOs;
using Accufex.Server.Parsing.Models;
using Accufex.Server.Parsing.Universal.Models;

namespace Accufex.Server.Parsing.Universal.Interfaces;

/// <summary>
/// Core orchestrator for the ACCUFEX Universal Statement Engine.
/// Analyzes unknown statement PDFs, classifies structure, calculates confidence,
/// and returns either verified transactions or a safe review status.
/// </summary>
public interface IUniversalStatementEngine
{
    /// <summary>
    /// Processes an unknown or unsupported statement PDF.
    /// Guarantees financial safety: never fabricates transactions when structure is uncertain.
    /// </summary>
    Task<UniversalParseResult> ProcessStatementAsync(
        PdfExtractionResult extraction,
        BankDetectionResult detection,
        CancellationToken cancellationToken = default);
}
