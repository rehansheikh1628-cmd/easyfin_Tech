using System.Collections.Generic;
using EasyFin_Tech.Server.Parsing.Models;

namespace EasyFin_Tech.Server.Parsing.Interfaces;

public interface IBankParserRegistry
{
    IBankStatementParser? GetParser(int bankCode);

    IBankStatementParser? ResolveParser(BankDetectionResult detection);

    IEnumerable<IBankStatementParser> GetAllParsers();
}
