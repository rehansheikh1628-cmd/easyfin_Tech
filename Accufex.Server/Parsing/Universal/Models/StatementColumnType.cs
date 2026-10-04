namespace Accufex.Server.Parsing.Universal.Models;

/// <summary>
/// Identifies the semantic purpose of a detected column in an arbitrary bank statement table.
/// Bank-agnostic representation used across the Universal Statement Engine.
/// </summary>
public enum StatementColumnType
{
    Unknown = 0,
    SerialNumber = 1,
    Date = 2,
    ValueDate = 3,
    Description = 4,
    Reference = 5,
    Debit = 6,
    Credit = 7,
    SignedAmount = 8,
    Balance = 9,
    Branch = 10,
    Category = 11,
    Indicator = 12
}
