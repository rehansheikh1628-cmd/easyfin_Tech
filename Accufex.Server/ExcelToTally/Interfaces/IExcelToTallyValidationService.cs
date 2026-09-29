using Accufex.Server.ExcelToTally.Models;

namespace Accufex.Server.ExcelToTally.Interfaces;

public interface IExcelToTallyValidationService
{
    ExcelValidationResult Validate(ExcelReadResult readResult);
}
