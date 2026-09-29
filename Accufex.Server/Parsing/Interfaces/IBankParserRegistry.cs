using System.Collections.Generic;
using Accufex.Server.Parsing.Models;

namespace Accufex.Server.Parsing.Interfaces;

public interface IBankParserRegistry
{
    IBankStatementParser? GetParser(int bankCode);

    IBankStatementParser? ResolveParser(BankDetectionResult detection);

    IEnumerable<IBankStatementParser> GetAllParsers();
}
