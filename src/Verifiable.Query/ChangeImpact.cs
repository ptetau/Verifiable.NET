using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Verifiable.Analyzers.Smt;
using Verifiable.Core;

namespace Verifiable.Query;

/// <summary>A contract as read back from an export document.</summary>
internal sealed record ExportEntry(string Symbol, string Kind, string? Clause, string Text, JsonElement Guard, JsonElement Expr);

internal sealed record ContractChange(string Symbol, string Kind, string? Clause, string Change, string? OldText, string? NewText, string? Note);

internal sealed record BrokenProof(string Rule, string? Symbol, string? Obligation, string? Contract, string File, int Line, IReadOnlyList<ContractChange> DependsOn);

/// <summary>
/// Requirement R7: compare two contract exports and sort each change by kind. A changed predicate is
/// <c>widened</c> when the old one implies the new (it accepts more: code that relied on the old limit may
/// break), <c>narrowed</c> when the new implies the old (it accepts less: old data may break),
/// <c>equivalent</c> when both hold, and <c>changed</c> when neither does — each decided by Z3.
/// Every proof that now fails is listed with the changed contracts it depends on.
/// </summary>
internal static class ChangeImpact
{
    public static List<ExportEntry> Read(string exportJson)
    {
        using var doc = JsonDocument.Parse(exportJson);
        return doc.RootElement.GetProperty("contracts").EnumerateArray()
            .Select(c => new ExportEntry(
                c.GetProperty("symbol").GetString()!,
                c.GetProperty("kind").GetString()!,
                c.GetProperty("clause").ValueKind == JsonValueKind.String ? c.GetProperty("clause").GetString() : null,
                c.GetProperty("text").GetString()!,
                c.GetProperty("guard").Clone(),
                c.GetProperty("expr").Clone()))
            .ToList();
    }

    public static List<ContractChange> Compare(IReadOnlyList<ExportEntry> before, IReadOnlyList<ExportEntry> after, string? z3Path)
    {
        var changes = new List<ContractChange>();
        var groups = before.Select(e => (e.Symbol, e.Kind)).Concat(after.Select(e => (e.Symbol, e.Kind))).Distinct()
            .OrderBy(g => g.Symbol, StringComparer.Ordinal).ThenBy(g => g.Kind, StringComparer.Ordinal);

        foreach (var (symbol, kind) in groups)
        {
            var olds = before.Where(e => e.Symbol == symbol && e.Kind == kind).ToList();
            var news = after.Where(e => e.Symbol == symbol && e.Kind == kind).ToList();
            var pairs = new List<(ExportEntry? Old, ExportEntry? New)>();

            // Pair by clause where it is unambiguous, then by position.
            foreach (var o in olds.ToList())
            {
                if (o.Clause is null) continue;
                var sameClause = news.Where(n => n.Clause == o.Clause).ToList();
                if (sameClause.Count == 1 && olds.Count(x => x.Clause == o.Clause) == 1)
                {
                    pairs.Add((o, sameClause[0]));
                    olds.Remove(o);
                    news.Remove(sameClause[0]);
                }
            }
            for (var i = 0; i < Math.Max(olds.Count, news.Count); i++)
            {
                pairs.Add((i < olds.Count ? olds[i] : null, i < news.Count ? news[i] : null));
            }

            foreach (var (o, n) in pairs)
            {
                if (o is null) { changes.Add(new ContractChange(symbol, kind, n!.Clause, "added", null, n.Text, null)); continue; }
                if (n is null) { changes.Add(new ContractChange(symbol, kind, o.Clause, "removed", o.Text, null, null)); continue; }
                if (Normalize(o.Text) == Normalize(n.Text)) continue;
                var (change, note) = Relate(o, n, z3Path);
                changes.Add(new ContractChange(symbol, kind, n.Clause ?? o.Clause, change, o.Text, n.Text, note));
            }
        }
        return changes;
    }

    private static string Normalize(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static (string Change, string? Note) Relate(ExportEntry oldE, ExportEntry newE, string? z3Path)
    {
        string oldTerm, newTerm;
        var smt = new SmtBuilder();
        try
        {
            oldTerm = smt.Predicate(oldE.Guard, oldE.Expr);
            newTerm = smt.Predicate(newE.Guard, newE.Expr);
        }
        catch (NotSupportedException ex)
        {
            return ("unknown", $"Cannot compare automatically: {ex.Message}");
        }

        var oldImpliesNew = Valid(smt, $"(=> {oldTerm} {newTerm})", z3Path);
        var newImpliesOld = Valid(smt, $"(=> {newTerm} {oldTerm})", z3Path);
        if (oldImpliesNew is null || newImpliesOld is null) return ("unknown", "Z3 could not decide.");
        return (oldImpliesNew.Value, newImpliesOld.Value) switch
        {
            (true, true) => ("equivalent", null),
            (true, false) => ("widened", null),
            (false, true) => ("narrowed", null),
            _ => ("changed", null),
        };
    }

    /// <summary>True when the formula holds for every value, false when it does not, null when Z3 cannot say.</summary>
    private static bool? Valid(SmtBuilder smt, string formula, string? z3Path)
    {
        var script = smt.Declarations() + $"(assert (not {formula}))\n";
        var result = Z3SolverRunner.Execute(script, z3Path, null);
        return result.Status switch
        {
            SmtStatus.Unsat => true,
            SmtStatus.Sat => false,
            _ => null,
        };
    }

    /// <summary>Translates export expression trees to SMT-LIB over free variables named by their path.</summary>
    private sealed class SmtBuilder
    {
        private readonly SortedDictionary<string, string> _vars = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _bounds = new(StringComparer.Ordinal);

        private static readonly Dictionary<string, (string Min, string Max)> IntRanges = new(StringComparer.Ordinal)
        {
            ["int"] = ("-2147483648", "2147483647"),
            ["long"] = ("-9223372036854775808", "9223372036854775807"),
            ["short"] = ("-32768", "32767"),
            ["byte"] = ("0", "255"),
            ["uint"] = ("0", "4294967295"),
            ["ulong"] = ("0", "18446744073709551615"),
        };

        private static readonly Dictionary<string, string> Statics = new(StringComparer.Ordinal)
        {
            ["int.MaxValue"] = "2147483647", ["int.MinValue"] = "(- 2147483648)",
            ["long.MaxValue"] = "9223372036854775807", ["long.MinValue"] = "(- 9223372036854775808)",
        };

        public string Declarations()
        {
            var sb = new StringBuilder();
            foreach (var (name, sort) in _vars) sb.Append($"(declare-const {name} {sort})\n");
            foreach (var b in _bounds) sb.Append(b).Append('\n');
            return sb.ToString();
        }

        public string Predicate(JsonElement guard, JsonElement expr)
        {
            var body = Bool(expr);
            return guard.ValueKind == JsonValueKind.Null ? body : $"(=> {Bool(guard)} {body})";
        }

        private string Bool(JsonElement e)
        {
            var (term, sort) = Term(e);
            if (sort != "Bool") throw new NotSupportedException("a predicate that is not boolean");
            return term;
        }

        private static string? SortOf(string? csType)
        {
            if (csType is null) return null;
            if (IntRanges.ContainsKey(csType)) return "Int";
            return csType switch { "bool" => "Bool", "decimal" or "double" or "float" => "Real", _ => null };
        }

        private string Var(string path, string? csType)
        {
            var sort = SortOf(csType) ?? throw new NotSupportedException($"a value of type `{csType}` ({path})");
            var name = "|" + path.Replace("|", "_") + "|";
            _vars[name] = sort;
            if (csType is not null && IntRanges.TryGetValue(csType, out var r))
            {
                _bounds.Add($"(assert (<= {Num(r.Min)} {name} {r.Max}))");
            }
            return name;
        }

        private static string Num(string n) => n.StartsWith("-", StringComparison.Ordinal) ? $"(- {n.Substring(1)})" : n;

        /// <summary>The dotted path of a variable or member chain, or null if the node is not one.</summary>
        private static string? Path(JsonElement e) => e.GetProperty("op").GetString() switch
        {
            "param" or "local" or "result" or "name" => e.GetProperty("name").GetString(),
            "member" when e.GetProperty("target").ValueKind == JsonValueKind.Object && Path(e.GetProperty("target")) is { } t => t + "." + e.GetProperty("name").GetString(),
            _ => null,
        };

        private (string Term, string Sort) Term(JsonElement e)
        {
            var op = e.GetProperty("op").GetString();
            var type = e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            (string, string) Bin(string smtOp, string resultSort)
            {
                var (l, ls) = Term(e.GetProperty("left"));
                var (r, rs) = Term(e.GetProperty("right"));
                if (ls != rs && !(ls is "Int" or "Real" && rs is "Int" or "Real")) throw new NotSupportedException($"mixed operand sorts for `{op}`");
                if (ls != rs) { l = ls == "Int" ? $"(to_real {l})" : l; r = rs == "Int" ? $"(to_real {r})" : r; }
                var sort = resultSort == "same" ? (ls == "Real" || rs == "Real" ? "Real" : ls) : resultSort;
                return ($"({smtOp} {l} {r})", sort);
            }

            switch (op)
            {
                case "and": return Bin("and", "Bool");
                case "or": return Bin("or", "Bool");
                case "implies": return Bin("=>", "Bool");
                case "eq": return Bin("=", "Bool");
                case "ne": { var (x, _) = Bin("=", "Bool"); return ($"(not {x})", "Bool"); }
                case "lt": return Bin("<", "Bool");
                case "le": return Bin("<=", "Bool");
                case "gt": return Bin(">", "Bool");
                case "ge": return Bin(">=", "Bool");
                case "add": return Bin("+", "same");
                case "sub": return Bin("-", "same");
                case "mul": return Bin("*", "same");
                case "div" when SortOf(type) == "Real": return Bin("/", "Real");
                case "div" or "mod": throw new NotSupportedException("integer division (C# truncates; SMT floors)");
                case "not": return ($"(not {Bool(e.GetProperty("operand"))})", "Bool");
                case "neg": { var (x, s) = Term(e.GetProperty("operand")); return ($"(- {x})", s); }
                case "cond":
                {
                    var (a, sa) = Term(e.GetProperty("then"));
                    var (b, sb) = Term(e.GetProperty("else"));
                    if (sa != sb) throw new NotSupportedException("a conditional with mixed sorts");
                    return ($"(ite {Bool(e.GetProperty("test"))} {a} {b})", sa);
                }
                case "const":
                {
                    var v = e.GetProperty("value").GetString()!;
                    var sort = SortOf(type) ?? throw new NotSupportedException($"a constant of type `{type}`");
                    return sort switch
                    {
                        "Bool" => (v == "True" || v == "true" ? "true" : "false", "Bool"),
                        "Real" => (RealLiteral(v), "Real"),
                        _ => (Num(v), "Int"),
                    };
                }
                case "static":
                {
                    var name = e.GetProperty("name").GetString()!;
                    return Statics.TryGetValue(name, out var lit) ? (lit, "Int") : throw new NotSupportedException($"the constant `{name}`");
                }
                case "cast":
                {
                    var (x, s) = Term(e.GetProperty("operand"));
                    var target = SortOf(type) ?? throw new NotSupportedException($"a cast to `{type}`");
                    return s == target ? (x, s) : s == "Int" && target == "Real" ? ($"(to_real {x})", "Real") : throw new NotSupportedException("a narrowing cast");
                }
                default:
                {
                    var path = Path(e) ?? throw new NotSupportedException($"`{op}` expressions");
                    return (Var(path, type), SortOf(type)!);
                }
            }
        }

        private static string RealLiteral(string v)
        {
            if (!decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) throw new NotSupportedException($"the number `{v}`");
            var text = d.ToString(CultureInfo.InvariantCulture);
            if (!text.Contains('.')) text += ".0";
            return text.StartsWith("-", StringComparison.Ordinal) ? $"(- {text.Substring(1)})" : text;
        }
    }

    /// <summary>
    /// The proofs that fail in the new code, each with the changed contracts it can depend on: its own,
    /// its callees', and the rules of the types in its signature.
    /// </summary>
    public static List<BrokenProof> Broken(CSharpCompilation compilation, IReadOnlyList<ContractChange> changes, string? z3Path)
    {
        var report = ElmCheckEngine.CheckCompilation(compilation, Array.Empty<Diagnostic>(), z3Path, includeWarnings: false);
        var methods = ContextPacks.Resolve(compilation, compilation.SyntaxTrees
            .SelectMany(t => t.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>()).Select(t => t.Identifier.ValueText).Distinct().ToList());

        static string Bare(string symbol) => symbol.Split('(')[0];
        var broken = new List<BrokenProof>();
        foreach (var d in report.Diagnostics.Where(d => d.Severity == "error" && d.RuleId is "VER102" or "VER103"))
        {
            var symbol = d.Facts?.Symbol;
            var related = new HashSet<string>(StringComparer.Ordinal);
            if (symbol is not null)
            {
                related.Add(symbol);
                var method = methods.FirstOrDefault(m => ContextPacks.Name(m) == symbol);
                if (method is not null)
                {
                    var decl = method.DeclaringSyntaxReferences.First().GetSyntax();
                    var model = compilation.GetSemanticModel(decl.SyntaxTree);
                    foreach (var inv in decl.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        if (model.GetSymbolInfo(inv).Symbol is IMethodSymbol callee) related.Add(ContextPacks.Name(callee.OriginalDefinition));
                    }
                    foreach (var p in method.Parameters) related.Add(p.Type.Name);
                    related.Add(method.ReturnType.Name);
                    foreach (var p in method.Parameters.SelectMany(p => p.Type.GetMembers().OfType<IPropertySymbol>())) related.Add(p.Type.Name);
                }
            }

            var dependsOn = changes.Where(c => related.Contains(Bare(c.Symbol)) || related.Contains(Bare(c.Symbol).Split('.')[0])).ToList();
            broken.Add(new BrokenProof(d.RuleId, symbol, d.Facts?.Obligation, d.Facts?.Contract, d.FilePath is "" ? "" : AgentOutput.NormalizePath(d.FilePath), d.Line, dependsOn));
        }
        return broken;
    }

    public static string ToJson(IReadOnlyList<ContractChange> changes, IReadOnlyList<BrokenProof>? broken, bool indented)
    {
        var outcome = broken is { Count: > 0 } ? AgentOutcome.Refuted : AgentOutcome.Proved;
        void WriteChange(Utf8JsonWriter w, ContractChange c)
        {
            w.WriteStartObject();
            w.WriteString("symbol", c.Symbol);
            w.WriteString("kind", c.Kind);
            AgentOutput.StringOrNull(w, "clause", c.Clause);
            w.WriteString("change", c.Change);
            AgentOutput.StringOrNull(w, "oldText", c.OldText);
            AgentOutput.StringOrNull(w, "newText", c.NewText);
            AgentOutput.StringOrNull(w, "note", c.Note);
            w.WriteEndObject();
        }

        return AgentOutput.Write(indented, w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", AgentOutput.SchemaId("impact"));
            w.WriteString("command", "impact");
            w.WriteString("status", AgentOutput.StatusWord(outcome));
            w.WriteNumber("exitCode", (int)outcome);
            w.WriteStartArray("changes");
            foreach (var c in changes) WriteChange(w, c);
            w.WriteEndArray();
            if (broken is null) w.WriteNull("brokenProofs");
            else
            {
                w.WriteStartArray("brokenProofs");
                foreach (var b in broken)
                {
                    w.WriteStartObject();
                    w.WriteString("rule", b.Rule);
                    AgentOutput.StringOrNull(w, "symbol", b.Symbol);
                    AgentOutput.StringOrNull(w, "obligation", b.Obligation);
                    AgentOutput.StringOrNull(w, "contract", b.Contract);
                    w.WriteString("file", b.File);
                    w.WriteNumber("line", b.Line);
                    w.WriteStartArray("dependsOn");
                    foreach (var c in b.DependsOn) WriteChange(w, c);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        });
    }
}
