using System.Collections.Generic;
using Accufex.Server.Parsing.Models;
using Accufex.Server.Parsing.Universal.Models;

namespace Accufex.Server.Parsing.Universal.Interfaces;

/// <summary>
/// Mathematical and sequential balance reconciliation engine for candidate statement transactions.
/// </summary>
public interface IFinancialValidator
{
    /// <summary>
    /// Executes sequential running balance checks and global opening/closing balance reconciliation.
    /// </summary>
    FinancialValidationResult Validate(
        List<ParsedTransaction> transactions,
        decimal? openingBalance,
        decimal? closingBalance);
}
