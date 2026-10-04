using System;
using System.Collections.Generic;

namespace Accufex.Server.Parsing.Universal.Models;

/// <summary>
/// Detailed mathematical and financial validation report for extracted candidate transactions.
/// Provides transparency into sequential balance reconciliation and net statement continuity.
/// </summary>
public class FinancialValidationResult
{
    public int TotalRowsChecked { get; set; }

    public int ReconciledRowsCount { get; set; }

    public int FailedRowsCount { get; set; }

    public int MissingBalanceCount { get; set; }

    public int MissingAmountCount { get; set; }

    public decimal TotalDebits { get; set; }

    public decimal TotalCredits { get; set; }

    public decimal? OpeningBalance { get; set; }

    public decimal? ClosingBalance { get; set; }

    public decimal? CalculatedClosingBalance { get; set; }

    public bool IsFullyReconciled { get; set; }

    public double ReconciliationRate =>
        TotalRowsChecked > 0 && (TotalRowsChecked - MissingBalanceCount) > 0
            ? Math.Round((double)ReconciledRowsCount / (TotalRowsChecked - MissingBalanceCount), 4)
            : 0.0;

    public List<string> Discrepancies { get; set; } = [];
}
