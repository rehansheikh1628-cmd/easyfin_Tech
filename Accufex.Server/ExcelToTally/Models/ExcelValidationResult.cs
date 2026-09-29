namespace Accufex.Server.ExcelToTally.Models;

public class ExcelValidationResult
{
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }
    public int TotalRows { get; set; }
    public int ValidRows { get; set; }
    public int WarningRows { get; set; }
    public int InvalidRows { get; set; }
    public int SampleRowsSkipped { get; set; }
    public bool HasOfficialTemplateSignature { get; set; }
    public bool IsReadyForXmlGeneration { get; set; }
    public List<ExcelValidationIssue> ValidationMessages { get; set; } = new();
    public List<ValidatedExcelTransaction> ParsedTransactions { get; set; } = new();
    public string TemplateInfo { get; set; } = "Accufex Official XLSM Template (9 columns)";
}
