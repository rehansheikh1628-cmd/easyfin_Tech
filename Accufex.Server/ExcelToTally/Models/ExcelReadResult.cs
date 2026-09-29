namespace Accufex.Server.ExcelToTally.Models;

public class ExcelReadResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> Headers { get; set; } = new();
    public List<string> MissingRequiredHeaders { get; set; } = new();
    public List<ExcelTransactionRow> Rows { get; set; } = new();
    public int TotalPhysicalRowsRead { get; set; }
    public int BlankRowsSkipped { get; set; }
    public int SampleRowsSkipped { get; set; }
    public bool HasOfficialTemplateSignature { get; set; }
}
