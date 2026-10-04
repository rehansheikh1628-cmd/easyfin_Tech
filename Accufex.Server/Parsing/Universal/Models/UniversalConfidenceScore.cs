using System.Collections.Generic;

namespace Accufex.Server.Parsing.Universal.Models;

/// <summary>
/// Structured, evidence-based confidence assessment for an arbitrary statement.
/// Replaces arbitrary guessing with transparent metric components.
/// </summary>
public class UniversalConfidenceScore
{
    /// <summary>
    /// Confidence in table header identification (0.0 to 1.0).
    /// </summary>
    public double HeaderConfidence { get; set; }

    /// <summary>
    /// Confidence in column boundary and type classification (0.0 to 1.0).
    /// </summary>
    public double ColumnConfidence { get; set; }

    /// <summary>
    /// Confidence in transaction row sequencing and mathematical balance continuity (0.0 to 1.0).
    /// </summary>
    public double DataContinuityConfidence { get; set; }

    /// <summary>
    /// Composite weighted overall confidence score (0.0 to 1.0).
    /// </summary>
    public double OverallScore { get; set; }

    /// <summary>
    /// Categorical classification (Low, Medium, High).
    /// </summary>
    public UniversalConfidenceLevel Level { get; set; } = UniversalConfidenceLevel.Low;

    /// <summary>
    /// Transparent justifications when confidence is less than High.
    /// </summary>
    public List<string> ReviewReasons { get; set; } = [];
}
