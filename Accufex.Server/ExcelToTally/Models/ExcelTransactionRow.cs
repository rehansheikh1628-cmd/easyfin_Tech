namespace Accufex.Server.ExcelToTally.Models;

public class ExcelTransactionRow
{
    public int RowNumber { get; set; }
    public string? RawDate { get; set; }
    public string? RawNarration { get; set; }
    public string? RawChequeRefNo { get; set; }
    public string? RawValueDate { get; set; }
    public string? RawDrAmount { get; set; }
    public string? RawCrAmount { get; set; }
    public string? RawClosingBalance { get; set; }
    public string? RawLedgerName { get; set; }
    public string? RawBankName { get; set; }

    public bool IsCompletelyEmpty =>
        string.IsNullOrWhiteSpace(RawDate) &&
        string.IsNullOrWhiteSpace(RawNarration) &&
        string.IsNullOrWhiteSpace(RawChequeRefNo) &&
        string.IsNullOrWhiteSpace(RawValueDate) &&
        string.IsNullOrWhiteSpace(RawDrAmount) &&
        string.IsNullOrWhiteSpace(RawCrAmount) &&
        string.IsNullOrWhiteSpace(RawClosingBalance) &&
        string.IsNullOrWhiteSpace(RawLedgerName) &&
        string.IsNullOrWhiteSpace(RawBankName);
}
