using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Accufex.Server.DTOs;
using Accufex.Server.Parsing.Universal.Models;

namespace Accufex.Server.Parsing.Universal.Interfaces;

public interface IStatementFormatLearner
{
    StatementFormatFingerprint GenerateFingerprint(UniversalParseResult parseResult, List<DetectedColumnLayout> columns, PdfExtractionResult extraction);

    Task SaveFingerprintAsync(StatementFormatFingerprint fingerprint, CancellationToken cancellationToken = default);

    Task<StatementFormatFingerprint?> FindMatchingFingerprintAsync(PdfExtractionResult extraction, List<DetectedColumnLayout> columns, CancellationToken cancellationToken = default);
}
