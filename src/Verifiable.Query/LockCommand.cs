using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers;

namespace Verifiable.Query;

/// <summary>
/// <c>verifiable-query lock</c> (requirement R3): writes the contract lock the generator ships with the
/// code it generates, so the build fails if the body's author changes a contract (VER109).
/// </summary>
internal static class LockCommand
{
    public static List<ContractLock.Current> Select(Compilation compilation, IReadOnlyList<string>? scope) =>
        ContractLock.Compute(compilation.SyntaxTrees)
            .Where(c => scope is null || ElmCheckEngine.SymbolInScope(c.Symbol.Substring(0, c.Symbol.IndexOf('(')), scope))
            .OrderBy(c => c.Symbol, StringComparer.Ordinal)
            .ToList();

    /// <summary>The lock file itself: stable order, two-space indent, <c>\n</c> line endings.</summary>
    public static string LockFileText(IReadOnlyList<ContractLock.Current> locks) =>
        AgentOutput.Write(indented: true, w =>
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteStartArray("locks");
            foreach (var l in locks)
            {
                w.WriteStartObject();
                w.WriteString("symbol", l.Symbol);
                w.WriteString("hash", l.Hash);
                w.WriteStartArray("clauses");
                foreach (var c in l.Clauses) w.WriteStringValue(c);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }) + "\n";

    public static string ResultJson(IReadOnlyList<ContractLock.Current> locks, string? written, bool indented) =>
        AgentOutput.Write(indented, w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", AgentOutput.SchemaId("lock"));
            w.WriteString("command", "lock");
            w.WriteString("status", AgentOutput.StatusWord(AgentOutcome.Proved));
            w.WriteNumber("exitCode", 0);
            AgentOutput.StringOrNull(w, "file", written is null ? null : AgentOutput.NormalizePath(written));
            w.WriteStartArray("locks");
            foreach (var l in locks)
            {
                w.WriteStartObject();
                w.WriteString("symbol", l.Symbol);
                w.WriteString("hash", l.Hash);
                w.WriteStartArray("clauses");
                foreach (var c in l.Clauses) w.WriteStringValue(c);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
}
