using System;
using System.Collections.Generic;

namespace Accufex.Server.Parsing.Universal.Models;

/// <summary>
/// Deterministic layout fingerprint generated from an approved unknown statement.
/// Enables template learning and fast matching for future uploads of the same format.
/// </summary>
public class StatementFormatFingerprint
{
    /// <summary>
    /// SHA-256 hash uniquely identifying this structural layout.
    /// </summary>
    public string FingerprintHash { get; set; } = string.Empty;

    public string BankName { get; set; } = "Unknown Bank";

    public int ColumnCount { get; set; }

    public List<StatementColumnType> ColumnOrder { get; set; } = new();

    /// <summary>
    /// Normalized relative X positions (X / PageWidth) for each detected column.
    /// </summary>
    public List<double> RelativeColumnPositions { get; set; } = new();

    public List<string> HeaderKeywords { get; set; } = new();

    public string DateFormat { get; set; } = string.Empty;

    public bool HasExplicitDebitCredit { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public int UsageCount { get; set; } = 1;
}
