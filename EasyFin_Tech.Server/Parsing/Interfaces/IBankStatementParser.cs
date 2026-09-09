using EasyFin_Tech.Server.DTOs;
using EasyFin_Tech.Server.Parsing.Models;

namespace EasyFin_Tech.Server.Parsing.Interfaces;

public interface IBankStatementParser
{
    int BankCode { get; }

    string BankName { get; }

    string ParserVersion { get; }

    bool CanParse(BankDetectionResult detection);

    BankParsingResult Parse(PdfExtractionResult extraction, BankDetectionResult detection);
}
