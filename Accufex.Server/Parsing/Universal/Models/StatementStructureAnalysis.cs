using System.Collections.Generic;
using System.Linq;

namespace Accufex.Server.Parsing.Universal.Models;

/// <summary>
/// Bank-agnostic structural blueprint of a parsed statement document.
/// Encapsulates table geometry, spatial boundaries, and detected column configurations.
/// </summary>
public class StatementStructureAnalysis
{
    public string? PotentialBankName { get; set; }

    public string? PotentialAccountNumber { get; set; }

    public double HeaderTopY { get; set; }

    public double HeaderBottomY { get; set; }

    public double FooterTopY { get; set; } = 1000.0;

    public List<DetectedColumnLayout> Columns { get; set; } = [];

    public bool HasDateColumn => Columns.Any(c => c.ColumnType == StatementColumnType.Date);

    public bool HasValueDateColumn => Columns.Any(c => c.ColumnType == StatementColumnType.ValueDate);

    public bool HasDebitColumn => Columns.Any(c => c.ColumnType == StatementColumnType.Debit);

    public bool HasCreditColumn => Columns.Any(c => c.ColumnType == StatementColumnType.Credit);

    public bool HasBalanceColumn => Columns.Any(c => c.ColumnType == StatementColumnType.Balance);

    public bool HasDescriptionColumn => Columns.Any(c => c.ColumnType == StatementColumnType.Description);

    public bool HasReferenceColumn => Columns.Any(c => c.ColumnType == StatementColumnType.Reference);

    public bool HasSufficientColumnsForExtraction =>
        HasDateColumn &&
        HasDescriptionColumn &&
        (HasDebitColumn || HasCreditColumn || Columns.Any(c => c.ColumnType == StatementColumnType.SignedAmount)) &&
        HasBalanceColumn;

    public string? DetectedDateFormat { get; set; }

    public int AnalyzedPageCount { get; set; }

    public int DetectedTableRowCount { get; set; }

    public List<string> StructuralWarnings { get; set; } = [];
}
