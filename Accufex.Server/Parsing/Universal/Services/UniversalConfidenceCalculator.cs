using System;
using System.Collections.Generic;
using System.Linq;
using Accufex.Server.Parsing.Models;
using Accufex.Server.Parsing.Universal.Interfaces;
using Accufex.Server.Parsing.Universal.Models;

namespace Accufex.Server.Parsing.Universal.Services;

/// <summary>
/// Deterministic confidence calculator for the Universal Statement Engine.
/// Replaces arbitrary guessing with transparent metric components and review justifications.
/// </summary>
public class UniversalConfidenceCalculator : IUniversalConfidenceCalculator
{
    public UniversalConfidenceScore CalculateConfidence(
        StatementStructureAnalysis structure,
        List<ParsedTransaction> candidateTransactions,
        decimal? openingBalance,
        decimal? closingBalance,
        FinancialValidationResult? validation = null)
    {
        var score = new UniversalConfidenceScore();

        // 1. Header Confidence (0.0 to 1.0)
        double headerScore = 0.0;
        if (structure.HasDateColumn) headerScore += 0.25;
        if (structure.HasDescriptionColumn) headerScore += 0.25;
        if (structure.HasDebitColumn || structure.HasCreditColumn || structure.Columns.Any(c => c.ColumnType == StatementColumnType.SignedAmount)) headerScore += 0.25;
        if (structure.HasBalanceColumn) headerScore += 0.25;
        score.HeaderConfidence = headerScore;

        if (headerScore < 0.75)
        {
            score.ReviewReasons.Add("Essential statement columns (Date, Description, Debit/Credit/Amount, Balance) could not all be identified with certainty.");
        }

        // 2. Column Geometry & Layout Confidence (0.0 to 1.0)
        double colScore = 0.0;
        if (structure.Columns.Count >= 4)
        {
            // Verify monotonic left-to-right column progression
            bool isMonotonic = true;
            for (int i = 0; i < structure.Columns.Count - 1; i++)
            {
                if (structure.Columns[i].LeftX >= structure.Columns[i + 1].LeftX)
                {
                    isMonotonic = false;
                    break;
                }
            }

            colScore = isMonotonic ? 0.90 : 0.40;
        }
        else
        {
            colScore = structure.Columns.Count * 0.15;
            score.ReviewReasons.Add($"Only {structure.Columns.Count} distinct table column intervals detected (minimum 4 required for reliable extraction).");
        }
        score.ColumnConfidence = colScore;

        // 3. Data Continuity & Financial Math Verification (0.0 to 1.0)
        double dataScore = 0.0;
        if (candidateTransactions.Count > 0)
        {
            if (validation != null)
            {
                if (validation.IsFullyReconciled)
                {
                    dataScore = 1.0;
                }
                else if (validation.TotalRowsChecked > 0)
                {
                    dataScore = validation.ReconciliationRate;
                }

                if (validation.FailedRowsCount > 0)
                {
                    score.ReviewReasons.Add($"Sequential balance reconciliation failed on {validation.FailedRowsCount} transaction row(s).");
                }

                if (validation.MissingAmountCount > 0)
                {
                    score.ReviewReasons.Add($"{validation.MissingAmountCount} candidate transaction(s) have missing debit and credit amounts.");
                }

                if (validation.Discrepancies.Count > 0)
                {
                    foreach (var disc in validation.Discrepancies.Take(2))
                    {
                        if (!score.ReviewReasons.Contains(disc))
                        {
                            score.ReviewReasons.Add(disc);
                        }
                    }
                }
            }
            else
            {
                int validContinuityCount = 0;
                for (int i = 1; i < candidateTransactions.Count; i++)
                {
                    var prev = candidateTransactions[i - 1];
                    var curr = candidateTransactions[i];

                    if (prev.Balance.HasValue && curr.Balance.HasValue)
                    {
                        decimal expected = prev.Balance.Value - (curr.Debit ?? 0m) + (curr.Credit ?? 0m);
                        if (Math.Abs(curr.Balance.Value - expected) <= 0.05m)
                        {
                            validContinuityCount++;
                        }
                    }
                }

                dataScore = candidateTransactions.Count > 1
                    ? (double)validContinuityCount / (candidateTransactions.Count - 1)
                    : 0.60;
            }

            if (dataScore < 0.80 && !score.ReviewReasons.Any(r => r.Contains("balance") || r.Contains("continuity")))
            {
                score.ReviewReasons.Add("Sequential running balance mathematical continuity could not be verified across all extracted rows.");
            }
        }
        else
        {
            dataScore = 0.0;
            score.ReviewReasons.Add("No valid candidate financial transactions could be extracted from the detected table rows.");
        }
        score.DataContinuityConfidence = dataScore;

        // 4. Weighted Composite Score
        // 35% Header, 35% Column Geometry, 30% Financial Continuity
        double overall = (score.HeaderConfidence * 0.35) +
                         (score.ColumnConfidence * 0.35) +
                         (score.DataContinuityConfidence * 0.30);

        score.OverallScore = Math.Round(Math.Max(0.0, Math.Min(1.0, overall)), 2);

        // 5. Categorical Classification
        if (score.OverallScore >= 0.80 && score.ReviewReasons.Count == 0)
        {
            score.Level = UniversalConfidenceLevel.High;
        }
        else if (score.OverallScore >= 0.50)
        {
            score.Level = UniversalConfidenceLevel.Medium;
        }
        else
        {
            score.Level = UniversalConfidenceLevel.Low;
        }

        return score;
    }
}
