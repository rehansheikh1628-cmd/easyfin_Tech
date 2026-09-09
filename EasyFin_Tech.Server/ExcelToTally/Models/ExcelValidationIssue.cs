namespace EasyFin_Tech.Server.ExcelToTally.Models;

public class ExcelValidationIssue
{
    public int? RowNumber { get; set; }
    public string? Column { get; set; }
    public ExcelValidationSeverity Severity { get; set; }
    public string Message { get; set; } = string.Empty;

    public string FormattedMessage => RowNumber.HasValue
        ? $"Row {RowNumber}: {Message}"
        : Message;

    public override string ToString() => FormattedMessage;
}
