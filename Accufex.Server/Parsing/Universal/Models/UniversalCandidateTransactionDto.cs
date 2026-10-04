using System;
using System.Collections.Generic;

namespace Accufex.Server.Parsing.Universal.Models;

/// <summary>
/// Represents an extracted candidate transaction in the review workbench.
/// Preserves both current/edited values and pristine original extracted values for audit.
/// </summary>
public class UniversalCandidateTransactionDto
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public int RowNumber { get; set; }

    public int PageNumber { get; set; }

    public DateTime? Date { get; set; }

    public DateTime? ValueDate { get; set; }

    public string Description { get; set; } = string.Empty;

    public string? Reference { get; set; }

    public decimal? Debit { get; set; }

    public decimal? Credit { get; set; }

    public decimal Amount { get; set; }

    public decimal? Balance { get; set; }

    /// <summary>
    /// Transaction direction: "Debit", "Credit", or "Unknown"
    /// </summary>
    public string Direction { get; set; } = "Unknown";

    public bool IsDateAmbiguous { get; set; }

    public bool IsDirectionAmbiguous { get; set; }

    public bool IsBalanceMismatch { get; set; }

    public List<string> ValidationWarnings { get; set; } = new();

    public List<string> ValidationErrors { get; set; } = new();

    public bool IsUserEdited { get; set; }

    /// <summary>
    /// Pristine extracted values before user edits, keyed by field name.
    /// </summary>
    public Dictionary<string, string?> OriginalValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
