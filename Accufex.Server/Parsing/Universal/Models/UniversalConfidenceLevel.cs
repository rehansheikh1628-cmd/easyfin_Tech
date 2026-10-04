namespace Accufex.Server.Parsing.Universal.Models;

/// <summary>
/// Categorical confidence levels for the Universal Statement Engine.
/// Distinguishes between automatic conversion candidates, assisted review, and rejection.
/// </summary>
public enum UniversalConfidenceLevel
{
    /// <summary>
    /// Low confidence (0.00 - 0.49). Insufficient structural evidence to export financial data safely.
    /// </summary>
    Low = 0,

    /// <summary>
    /// Medium confidence (0.50 - 0.79). Statement structure is partially identified; requires human review before conversion.
    /// </summary>
    Medium = 1,

    /// <summary>
    /// High confidence (0.80 - 1.00). Unambiguous tabular structure with verified balance continuity.
    /// </summary>
    High = 2
}
