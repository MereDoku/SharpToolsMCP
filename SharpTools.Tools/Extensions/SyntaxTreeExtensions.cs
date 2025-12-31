using Microsoft.CodeAnalysis;
using System.Linq;

namespace SharpTools.Tools.Extensions;

public static class SyntaxTreeExtensions
{
    public static Project GetRequiredProjectForSymbol(this Solution solution, ISymbol symbol) {
        var assemblyName = symbol.ContainingAssembly?.Name;
        if (string.IsNullOrWhiteSpace(assemblyName))
            throw new InvalidOperationException("Symbol has no containing assembly name.");

        var matches = solution.Projects
            .Where(p => string.Equals(p.AssemblyName, assemblyName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
            throw new InvalidOperationException($"Could not find project for assembly '{assemblyName}'.");

        return matches
            .OrderBy(p => p.FilePath, StringComparer.OrdinalIgnoreCase)
            .First();
    }
}