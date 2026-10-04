namespace Accufex.Server.Parsing;

/// <summary>
/// Identifies bank formats supported by the statement parsing subsystem.
/// Extensible for additional banks in future phases.
/// </summary>
public enum BankType
{
    Unknown = 0,
    Hdfc = 1,
    YesBank = 2,
    Axis = 3,
    CentralBank = 4,
    ICICI = 5,
    SBI = 6,
    BOI = 7,
    Kotak = 8,
    PNB = 9,
    BOB = 10,
    Universal = 99
}
