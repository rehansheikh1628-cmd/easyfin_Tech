using System.Collections.Generic;
using Accufex.Server.Parsing.Models;
using Accufex.Server.Parsing.Universal.Models;

namespace Accufex.Server.Parsing.Universal.Interfaces;

/// <summary>
/// Calculates deterministic, evidence-based confidence metrics for unknown statement parsing.
/// </summary>
public interface IUniversalConfidenceCalculator
{
    /// <summary>
    /// Computes component and composite confidence scores based on structural and transaction evidence.
    /// </summary>
    UniversalConfidenceScore CalculateConfidence(
        StatementStructureAnalysis structure,
        List<ParsedTransaction> candidateTransactions,
        decimal? openingBalance,
        decimal? closingBalance,
        FinancialValidationResult? validation = null);
}
