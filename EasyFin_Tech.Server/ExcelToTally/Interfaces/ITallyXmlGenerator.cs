using System.Threading;
using System.Threading.Tasks;
using EasyFin_Tech.Server.ExcelToTally.Models;

namespace EasyFin_Tech.Server.ExcelToTally.Interfaces;

/// <summary>
/// Foundation interface for Tally XML generation.
/// Exact Tally XML schema and voucher builder will be implemented in Step 2.
/// </summary>
public interface ITallyXmlGenerator
{
    Task<byte[]> GenerateTallyXmlAsync(ExcelValidationResult validatedStatement, CancellationToken cancellationToken = default);
}
