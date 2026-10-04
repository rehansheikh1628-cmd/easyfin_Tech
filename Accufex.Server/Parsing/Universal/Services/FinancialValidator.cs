using System;
using System.Collections.Generic;
using System.Linq;
using Accufex.Server.Parsing.Models;
using Accufex.Server.Parsing.Universal.Interfaces;
using Accufex.Server.Parsing.Universal.Models;
using Microsoft.Extensions.Logging;

namespace Accufex.Server.Parsing.Universal.Services;

/// <summary>
/// Mathematical and sequential balance reconciliation engine for statement parsing.
/// Verifies row-by-row arithmetic continuity and document-level opening/closing integrity.
/// Never fabricates corrections; reports discrepancies deterministically.
/// </summary>
public class FinancialValidator : IFinancialValidator
{
    private readonly ILogger<FinancialValidator> _logger;
    private const decimal BalanceTolerance = 0.05m;

    public FinancialValidator(ILogger<FinancialValidator> logger)
    {
        _logger = logger;
    }

    public FinancialValidationResult Validate(
        List<ParsedTransaction> transactions,
        decimal? openingBalance,
        decimal? closingBalance)
    {
        var result = new FinancialValidationResult
        {
            TotalRowsChecked = transactions.Count,
            OpeningBalance = openingBalance,
            ClosingBalance = closingBalance
        };

        if (transactions.Count == 0)
        {
            result.IsFullyReconciled = false;
            result.Discrepancies.Add("Validation failed: Zero transactions available to evaluate.");
            return result;
        }

        decimal runningSumDebits = 0m;
        decimal runningSumCredits = 0m;

        for (int i = 0; i < transactions.Count; i++)
        {
            var tx = transactions[i];

            // 1. Audit Amount Integrity
            bool hasDebit = tx.Debit.HasValue && tx.Debit.Value > 0m;
            bool hasCredit = tx.Credit.HasValue && tx.Credit.Value > 0m;

            if (!hasDebit && !hasCredit)
            {
                result.MissingAmountCount++;
                result.Discrepancies.Add($"Row {i + 1} ({tx.TransactionDate:dd/MM/yyyy}): Missing both debit and credit amounts.");
            }

            if (hasDebit) runningSumDebits += tx.Debit!.Value;
            if (hasCredit) runningSumCredits += tx.Credit!.Value;

            // 2. Audit Balance Integrity
            if (!tx.Balance.HasValue)
            {
                result.MissingBalanceCount++;
                continue;
            }

            // 3. Row-by-Row Running Balance Reconciliation
            if (i == 0)
            {
                if (openingBalance.HasValue)
                {
                    decimal expectedFirstBal = openingBalance.Value - (tx.Debit ?? 0m) + (tx.Credit ?? 0m);
                    decimal diff = Math.Abs(tx.Balance.Value - expectedFirstBal);
                    if (diff <= BalanceTolerance)
                    {
                        result.ReconciledRowsCount++;
                    }
                    else
                    {
                        result.FailedRowsCount++;
                        result.Discrepancies.Add($"Row 1 ({tx.TransactionDate:dd/MM/yyyy}): Expected balance {expectedFirstBal:F2} from Opening Balance {openingBalance.Value:F2}, but statement shows {tx.Balance.Value:F2} (diff: {diff:F2}).");
                    }
                }
            }
            else
            {
                var prev = transactions[i - 1];
                if (prev.Balance.HasValue)
                {
                    decimal expectedBal = prev.Balance.Value - (tx.Debit ?? 0m) + (tx.Credit ?? 0m);
                    decimal diff = Math.Abs(tx.Balance.Value - expectedBal);
                    if (diff <= BalanceTolerance)
                    {
                        result.ReconciledRowsCount++;
                    }
                    else
                    {
                        result.FailedRowsCount++;
                        result.Discrepancies.Add($"Row {i + 1} ({tx.TransactionDate:dd/MM/yyyy}): Expected balance {expectedBal:F2} from previous balance {prev.Balance.Value:F2}, but statement shows {tx.Balance.Value:F2} (diff: {diff:F2}).");
                    }
                }
            }
        }

        result.TotalDebits = runningSumDebits;
        result.TotalCredits = runningSumCredits;

        // 4. Document-Level Opening vs Closing Balance Verification
        if (openingBalance.HasValue)
        {
            result.CalculatedClosingBalance = openingBalance.Value + runningSumCredits - runningSumDebits;

            if (closingBalance.HasValue)
            {
                decimal globalDiff = Math.Abs(result.CalculatedClosingBalance.Value - closingBalance.Value);
                if (globalDiff > BalanceTolerance)
                {
                    result.Discrepancies.Add($"Global Statement Math Discrepancy: Opening ({openingBalance.Value:F2}) + Total Credits ({runningSumCredits:F2}) - Total Debits ({runningSumDebits:F2}) = {result.CalculatedClosingBalance.Value:F2}, but stated closing balance is {closingBalance.Value:F2} (diff: {globalDiff:F2}).");
                }
            }
        }

        // 5. Evaluate Full Reconciliation Status
        int eligibleBalancePairs = openingBalance.HasValue ? transactions.Count : Math.Max(0, transactions.Count - 1);
        bool allEligibleReconciled = eligibleBalancePairs > 0 && result.ReconciledRowsCount == eligibleBalancePairs && result.FailedRowsCount == 0;

        result.IsFullyReconciled = allEligibleReconciled &&
                                  result.MissingAmountCount == 0 &&
                                  result.Discrepancies.Count == 0;

        _logger.LogInformation("Financial validation complete. Rows: {Total}, Reconciled: {Reconciled}, Failed: {Failed}, MissingBal: {MissingBal}, FullyReconciled: {FullyReconciled}",
            result.TotalRowsChecked, result.ReconciledRowsCount, result.FailedRowsCount, result.MissingBalanceCount, result.IsFullyReconciled);

        return result;
    }
}
