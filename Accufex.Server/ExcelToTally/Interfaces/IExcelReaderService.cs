using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Accufex.Server.ExcelToTally.Models;

namespace Accufex.Server.ExcelToTally.Interfaces;

public interface IExcelReaderService
{
    Task<ExcelReadResult> ReadExcelAsync(Stream fileStream, CancellationToken cancellationToken = default);
}
