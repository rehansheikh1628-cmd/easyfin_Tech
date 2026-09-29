using Accufex.Server.DTOs;
using Accufex.Server.Parsing.Models;

namespace Accufex.Server.Parsing.Interfaces;

public interface IBankStatementParser
{
    int BankCode { get; }

    string BankName { get; }

    string ParserVersion { get; }

    bool CanParse(BankDetectionResult detection);

    BankParsingResult Parse(PdfExtractionResult extraction, BankDetectionResult detection);
}
