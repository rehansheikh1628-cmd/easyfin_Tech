using System.IO;

namespace EasyFin_Tech.Server.ExcelToTally.Interfaces;

public interface IExcelTemplateService
{
    byte[] GenerateTemplate();
}
