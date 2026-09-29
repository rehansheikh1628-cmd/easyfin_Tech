using System.IO;

namespace Accufex.Server.ExcelToTally.Interfaces;

public interface IExcelTemplateService
{
    byte[] GenerateTemplate();
}
