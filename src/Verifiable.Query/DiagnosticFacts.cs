using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verifiable.Query;

/// <summary>
/// The structured parts of a diagnostic that an agent acts on (requirement R12): the symbol it is
/// about, the spec clause, the contract and its reason, any counterexample, and the fix.
/// </summary>
internal sealed record DiagnosticFacts(
    string? Symbol,
    string? Clause,
    string? Obligation,
    string? Contract,
    string? Reason,
    string? CounterexampleJson,
    string? Fix)
{
    public static readonly DiagnosticFacts Empty = new(null, null, null, null, null, null, null);

    /// <summary>
    /// Builds facts from a diagnostic's properties where the analyzer supplied them, and from the
    /// sections of its Elm text otherwise.
    /// </summary>
    public static DiagnosticFacts From(IReadOnlyDictionary<string, string?>? properties, string elmText, string? symbol)
    {
        var parsed = ParseElm(elmText);
        string? Prop(string key) =>
            properties is not null && properties.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        return new DiagnosticFacts(
            Symbol: Prop("Symbol") ?? symbol,
            Clause: Prop("Clause"),
            Obligation: Prop("ObligationKind"),
            Contract: Prop("Contract") ?? parsed.Contract,
            Reason: Prop("Reason") ?? parsed.Reason,
            CounterexampleJson: Prop("Counterexample") ?? parsed.CounterexampleJson,
            Fix: StripFixPrefix(Prop("Fix") ?? parsed.Fix));
    }

    private static string? StripFixPrefix(string? fix)
    {
        if (fix is null) return null;
        var t = fix.Trim();
        return t.StartsWith("Fix:", StringComparison.OrdinalIgnoreCase) ? t.Substring(4).Trim() : t;
    }

    /// <summary>Reads the labelled sections the Elm formatter writes.</summary>
    internal static DiagnosticFacts ParseElm(string elmText)
    {
        var lines = elmText.Replace("\r\n", "\n").Split('\n');
        string? reason = null, contract = null, fix = null, ce = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            switch (line)
            {
                case "Why this rule exists:":
                    reason = JoinIndented(lines, ref i, " ");
                    continue;
                case "Contract:":
                    contract = JoinIndented(lines, ref i, "\n");
                    continue;
                case "Counterexample:":
                    ce = CounterexampleToJson(CollectIndented(lines, ref i));
                    continue;
            }

            if (line.StartsWith("Fix:", StringComparison.Ordinal))
            {
                var sb = new StringBuilder(line.Trim());
                while (i + 1 < lines.Length && lines[i + 1].Length > 0 && !lines[i + 1].StartsWith("<verifiable-", StringComparison.Ordinal))
                {
                    sb.Append(' ').Append(lines[++i].Trim());
                }
                fix = sb.ToString();
            }
        }

        return new DiagnosticFacts(null, null, null, contract, reason, ce, fix);
    }

    private static List<string> CollectIndented(string[] lines, ref int i)
    {
        var collected = new List<string>();
        while (i + 1 < lines.Length && lines[i + 1].StartsWith("    ", StringComparison.Ordinal))
        {
            collected.Add(lines[++i].Trim());
        }
        return collected;
    }

    private static string? JoinIndented(string[] lines, ref int i, string separator)
    {
        var collected = CollectIndented(lines, ref i);
        return collected.Count == 0 ? null : string.Join(separator, collected);
    }

    /// <summary>Turns <c>name = value</c> lines into a JSON object; anything else gives null.</summary>
    private static string? CounterexampleToJson(List<string> lines)
    {
        var pairs = new List<(string Name, string Value)>();
        foreach (var line in lines)
        {
            var eq = line.IndexOf(" = ", StringComparison.Ordinal);
            if (eq <= 0) continue;
            pairs.Add((line.Substring(0, eq).Trim(), line.Substring(eq + 3).Trim()));
        }

        if (pairs.Count == 0) return null;
        return AgentOutput.Write(indented: false, w =>
        {
            w.WriteStartObject();
            foreach (var (name, value) in pairs) w.WriteString(name, value);
            w.WriteEndObject();
        });
    }

    /// <summary>
    /// Names the member that encloses a source position as <c>Type.Member</c> (or just <c>Type</c>),
    /// or null when the position is outside any type.
    /// </summary>
    public static string? EnclosingSymbol(Compilation? compilation, Location location)
    {
        if (compilation is null || !location.IsInSource || location.SourceTree is null) return null;
        return EnclosingSymbol(location.SourceTree, location.SourceSpan.Start);
    }

    public static string? EnclosingSymbol(Compilation? compilation, string filePath, int line, int column)
    {
        if (compilation is null || line <= 0) return null;
        var tree = compilation.SyntaxTrees.FirstOrDefault(t =>
            string.Equals(t.FilePath, filePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        if (tree is null) return null;
        var text = tree.GetText();
        if (line > text.Lines.Count) return null;
        var lineInfo = text.Lines[line - 1];
        var pos = Math.Min(lineInfo.Start + Math.Max(column - 1, 0), lineInfo.End);
        return EnclosingSymbol(tree, pos);
    }

    private static string? EnclosingSymbol(SyntaxTree tree, int position)
    {
        var node = tree.GetRoot().FindToken(position).Parent;
        string? member = null;
        for (var n = node; n is not null; n = n.Parent)
        {
            switch (n)
            {
                case MethodDeclarationSyntax m when member is null: member = m.Identifier.ValueText; break;
                case ConstructorDeclarationSyntax c when member is null: member = c.Identifier.ValueText; break;
                case PropertyDeclarationSyntax p when member is null: member = p.Identifier.ValueText; break;
                case OperatorDeclarationSyntax o when member is null: member = "op_" + o.OperatorToken.ValueText; break;
                case BaseTypeDeclarationSyntax t:
                    return member is null ? t.Identifier.ValueText : $"{t.Identifier.ValueText}.{member}";
            }
        }
        return null;
    }
}
