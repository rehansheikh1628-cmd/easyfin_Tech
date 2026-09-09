using EasyFin_Tech.Server.ExcelToTally.Models;

namespace EasyFin_Tech.Server.ExcelToTally.Interfaces;

public interface IExcelToTallyValidationService
{
    ExcelValidationResult Validate(ExcelReadResult readResult);
}
