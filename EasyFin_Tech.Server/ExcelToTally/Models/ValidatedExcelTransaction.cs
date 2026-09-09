namespace EasyFin_Tech.Server.ExcelToTally.Models;

public class ValidatedExcelTransaction
{
    public int RowNumber { get; set; }
    public DateTime? Date { get; set; }
    public string? Narration { get; set; }
    public string? ChequeRefNo { get; set; }
    public DateTime? ValueDate { get; set; }
    public decimal? DrAmount { get; set; }
    public decimal? CrAmount { get; set; }
    public decimal? ClosingBalance { get; set; }
    public string? LedgerName { get; set; }
    public string? BankName { get; set; }
    public string? RawDate { get; set; }
    public string? RawDrAmount { get; set; }
    public string? RawCrAmount { get; set; }
    public string? RawClosingBalance { get; set; }

    public string Status { get; set; } = "Valid";
    public List<ExcelValidationIssue> Issues { get; set; } = new();

    public bool IsDebit => DrAmount.HasValue && DrAmount.Value > 0;
    public bool IsCredit => CrAmount.HasValue && CrAmount.Value > 0;
    public decimal Amount => DrAmount ?? CrAmount ?? 0m;
}
