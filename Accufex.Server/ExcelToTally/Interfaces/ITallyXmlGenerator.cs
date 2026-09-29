using System.Threading;
using System.Threading.Tasks;
using Accufex.Server.ExcelToTally.Models;

namespace Accufex.Server.ExcelToTally.Interfaces;

/// <summary>
/// Foundation interface for Tally XML generation.
/// Exact Tally XML schema and voucher builder will be implemented in Step 2.
/// </summary>
public interface ITallyXmlGenerator
{
    Task<byte[]> GenerateTallyXmlAsync(ExcelValidationResult validatedStatement, CancellationToken cancellationToken = default);
}
