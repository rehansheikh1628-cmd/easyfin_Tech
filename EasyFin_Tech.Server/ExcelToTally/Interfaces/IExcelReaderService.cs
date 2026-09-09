using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EasyFin_Tech.Server.ExcelToTally.Models;

namespace EasyFin_Tech.Server.ExcelToTally.Interfaces;

public interface IExcelReaderService
{
    Task<ExcelReadResult> ReadExcelAsync(Stream fileStream, CancellationToken cancellationToken = default);
}
