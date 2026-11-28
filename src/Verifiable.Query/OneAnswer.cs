using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Verifiable.Analyzers;
using Verifiable.Shared;

namespace Verifiable.Query;

internal enum OneAnswerVerdict
{
    /// <summary>Any two results that meet the contract are equal: any body that passes is correct.</summary>
    Fixed,

    /// <summary>Some input has two different results that both meet the contract.</summary>
    Open,

    /// <summary>The solver could not decide, or the results cannot be compared.</summary>
    Inconclusive,
}

internal sealed record OneAnswerResult(
    string Symbol,
    OneAnswerVerdict Verdict,
    bool AnyValidResult,
    string? AnyValidReason,
    IReadOnlyList<string> Clauses,
    string? CounterexampleJson,
    string? Note,
    string File,
    int Line);

/// <summary>
/// Requirement R5: does a method's contract fix one answer? For each method a probe is added to its own
/// type — same parameters plus two candidate results, the method's preconditions, both results meeting
/// every postcondition — and the ordinary prover is asked whether the two results must be equal. A
/// refutation is an input with two different valid results: a gap to send back to Resolve as a question.
/// </summary>
internal static class OneAnswer
{
    private const string ProbePrefix = "__VerifiableOneAnswer_";
    private const string R1 = "__r1";
    private const string R2 = "__r2";

    public static List<OneAnswerResult> Run(CSharpCompilation compilation, IReadOnlyList<IMethodSymbol> methods, string? z3Path, int? timeoutMs)
    {
        var results = new List<OneAnswerResult>();
        foreach (var method in methods.OrderBy(m => ContextPacks.Name(m), StringComparer.Ordinal))
        {
            results.Add(Check(compilation, method, z3Path, timeoutMs));
        }
        return results;
    }

    /// <summary>The non-void methods with a body, in verified scope, that a one-answer check applies to.</summary>
    public static List<IMethodSymbol> Candidates(Compilation compilation) =>
        compilation.SyntaxTrees
            .Where(t => !t.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            .SelectMany(t =>
            {
                var model = compilation.GetSemanticModel(t);
                return t.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .Select(d => model.GetDeclaredSymbol(d))
                    .OfType<IMethodSymbol>();
            })
            .Where(m => !m.ReturnsVoid && VerifiableSymbolHelpers.GetScope(m) == VerificationScope.Verifiable && m.MethodKind == MethodKind.Ordinary)
            // A constrained type's IsValid and an [Invariant] member are rules themselves, not procedures.
            .Where(m => !(m.Name == "IsValid" && m.IsStatic) && !m.GetAttributes().Any(a => a.AttributeClass?.Name == "InvariantAttribute"))
            .Where(m => !m.Name.StartsWith(ProbePrefix, StringComparison.Ordinal))
            .ToList();

    private static OneAnswerResult Check(CSharpCompilation compilation, IMethodSymbol method, string? z3Path, int? timeoutMs)
    {
        var decl = (MethodDeclarationSyntax)method.DeclaringSyntaxReferences.First().GetSyntax();
        var span = decl.Identifier.GetLocation().GetLineSpan();
        var file = AgentOutput.NormalizePath(span.Path);
        var line = span.StartLinePosition.Line + 1;
        var name = ContextPacks.Name(method);

        var any = method.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "AnyValidResultAttribute");
        var anyReason = any?.ConstructorArguments.FirstOrDefault().Value as string;

        var calls = LeadingContractBlock.GetLeadingSpecCalls(decl, c => ClauseIds.SpecMethodName(c) is not null);
        var clauses = calls.Select(ClauseIds.FromSpecCall).Where(c => c is not null).Select(c => c!).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var requires = calls.Where(c => ClauseIds.SpecMethodName(c) is "Requires" or "RequiresWhen").ToList();
        var ensures = calls.Where(c => ClauseIds.SpecMethodName(c) is "Ensures" or "EnsuresWhen").ToList();

        OneAnswerResult Result(OneAnswerVerdict v, string? ce, string? note) =>
            new(name, v, any is not null, anyReason, clauses, ce, note, file, line);

        if (ensures.Count == 0)
        {
            return Result(OneAnswerVerdict.Open, null, "There is no postcondition, so every result meets the contract.");
        }

        var posts = new List<(string R1, string R2)>();
        foreach (var e in ensures)
        {
            var p = Postcondition(e);
            if (p is null) return Result(OneAnswerVerdict.Inconclusive, null, $"Cannot read the postcondition `{e}`.");
            posts.Add(p.Value);
        }

        var returnType = decl.ReturnType.ToString();
        var probeName = ProbePrefix + method.Name;
        var parameters = decl.ParameterList.Parameters.Select(p => p.ToString())
            .Concat(new[] { $"{returnType} {R1}", $"{returnType} {R2}" });
        var both = string.Join(" && ", posts.Select(p => $"({p.R1})").Concat(posts.Select(p => $"({p.R2})")));
        var probeText =
            $"{(method.IsStatic ? "public static " : "public ")}bool {probeName}{decl.TypeParameterList}({string.Join(", ", parameters)}){string.Concat(decl.ConstraintClauses.Select(c => " " + c))}\n" +
            "{\n" +
            string.Concat(requires.Select(r => "    " + r + ";\n")) +
            $"    Spec.Requires({both}, \"Both results meet the contract.\");\n" +
            "    Spec.Ensures<bool>(__ok => __ok, \"The contract fixes one answer.\");\n" +
            $"    return {R1} == {R2};\n" +
            "}\n";

        var probe = SyntaxFactory.ParseMemberDeclaration(probeText);
        if (probe is null) return Result(OneAnswerVerdict.Inconclusive, null, "Could not build the probe.");

        var typeDecl = decl.Ancestors().OfType<TypeDeclarationSyntax>().First();
        var oldTree = decl.SyntaxTree;
        var newRoot = oldTree.GetRoot().ReplaceNode(typeDecl, typeDecl.AddMembers(probe));
        var newTree = oldTree.WithRootAndOptions(newRoot, oldTree.Options);
        var probed = compilation.ReplaceSyntaxTree(oldTree, newTree);

        var compileErrors = probed.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error && d.Location.SourceTree == newTree &&
                        d.Location.SourceSpan.IntersectsWith(FindProbe(newTree, probeName)?.Span ?? default))
            .ToList();
        if (compileErrors.Count > 0)
        {
            return Result(OneAnswerVerdict.Inconclusive, null,
                $"Two results of type `{returnType}` cannot be compared with `==` ({compileErrors[0].Id}: {compileErrors[0].GetMessage()}).");
        }

        var report = ElmCheckEngine.CheckCompilation(probed, Array.Empty<Diagnostic>(), z3Path, includeWarnings: false,
            scope: new[] { $"{method.ContainingType.Name}.{probeName}" }, timeoutMs: timeoutMs);
        var onProbe = report.Diagnostics.Where(d => d.Facts?.Symbol == $"{method.ContainingType.Name}.{probeName}").ToList();

        var post = onProbe.FirstOrDefault(d => d.RuleId == "VER102" && d.Facts?.Obligation == "Postcondition");
        if (post is not null) return Result(OneAnswerVerdict.Open, RenameResults(post.Facts?.CounterexampleJson), null);
        if (report.Diagnostics.Any(d => d.RuleId == "VER104")) return Result(OneAnswerVerdict.Inconclusive, null, "Z3 was not found.");
        var undecided = onProbe.FirstOrDefault(d => d.RuleId is "VER103" or "VER106" or "VER105");
        if (undecided is not null) return Result(OneAnswerVerdict.Inconclusive, null, undecided.Summary);
        var other = onProbe.FirstOrDefault(d => d.Severity == "error");
        if (other is not null) return Result(OneAnswerVerdict.Inconclusive, null, $"{other.RuleId}: {other.Summary}");
        return Result(OneAnswerVerdict.Fixed, null, null);
    }

    private static SyntaxNode? FindProbe(SyntaxTree tree, string name) =>
        tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.ValueText == name);

    /// <summary>The postcondition with the result renamed to <c>__r1</c> and to <c>__r2</c>, as C# text.</summary>
    private static (string R1, string R2)? Postcondition(InvocationExpressionSyntax ensures)
    {
        var args = ensures.ArgumentList.Arguments;
        var isWhen = ClauseIds.SpecMethodName(ensures) == "EnsuresWhen";
        ExpressionSyntax? Arg(string named, int position) =>
            args.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == named)?.Expression ??
            (args.Count > position && args.Take(position + 1).All(a => a.NameColon is null) ? args[position].Expression : null);

        var guard = isWhen ? Arg("condition", 0) : null;
        var body = isWhen ? Arg("predicate", 1) ?? Arg("postcondition", 1) : Arg("predicate", 0) ?? Arg("condition", 0);
        if (body is null) return null;

        string Rename(string to)
        {
            ExpressionSyntax renamed;
            switch (body)
            {
                case SimpleLambdaExpressionSyntax { ExpressionBody: { } eb } sl:
                    renamed = new Renamer(sl.Parameter.Identifier.ValueText, to).Visit(eb) as ExpressionSyntax ?? eb;
                    break;
                case ParenthesizedLambdaExpressionSyntax { ExpressionBody: { } pb } pl when pl.ParameterList.Parameters.Count == 1:
                    renamed = new Renamer(pl.ParameterList.Parameters[0].Identifier.ValueText, to).Visit(pb) as ExpressionSyntax ?? pb;
                    break;
                default:
                    renamed = new ResultCallRenamer(to).Visit(body) as ExpressionSyntax ?? body;
                    break;
            }
            return guard is null ? renamed.ToString() : $"Spec.Implies({guard}, {renamed})";
        }

        return (Rename(R1), Rename(R2));
    }

    /// <summary>Renames a lambda parameter, leaving nested lambdas that shadow it alone.</summary>
    private sealed class Renamer(string from, string to) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            var isMemberName = node.Parent is MemberAccessExpressionSyntax ma && ma.Name == node;
            var isArgumentLabel = node.Parent is NameColonSyntax;
            return node.Identifier.ValueText == from && !isMemberName && !isArgumentLabel
                ? SyntaxFactory.IdentifierName(to).WithTriviaFrom(node)
                : node;
        }

        public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) =>
            node.Parameter.Identifier.ValueText == from ? node : base.VisitSimpleLambdaExpression(node);
    }

    /// <summary>Replaces <c>Spec.Result&lt;T&gt;()</c> with a named result.</summary>
    private sealed class ResultCallRenamer(string to) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node) =>
            node.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.ValueText: "Result" } } ma &&
            ma.Expression.ToString() is "Spec" or "Verifiable.Contracts.Spec"
                ? SyntaxFactory.IdentifierName(to).WithTriviaFrom(node)
                : base.VisitInvocationExpression(node);
    }

    private static string? RenameResults(string? counterexampleJson)
    {
        if (counterexampleJson is null) return null;
        using var doc = JsonDocument.Parse(counterexampleJson);
        return AgentOutput.Write(indented: false, w =>
        {
            w.WriteStartObject();
            w.WriteStartObject("input");
            foreach (var p in doc.RootElement.EnumerateObject().Where(p => p.Name is not (R1 or R2)).OrderBy(p => p.Name, StringComparer.Ordinal))
                p.WriteTo(w);
            w.WriteEndObject();
            foreach (var (from, to) in new[] { (R1, "result1"), (R2, "result2") })
            {
                if (doc.RootElement.TryGetProperty(from, out var v))
                {
                    w.WritePropertyName(to);
                    v.WriteTo(w);
                }
                else
                {
                    w.WriteNull(to);
                }
            }
            w.WriteEndObject();
        });
    }

    public static string Word(OneAnswerVerdict v) => v switch
    {
        OneAnswerVerdict.Fixed => "fixed",
        OneAnswerVerdict.Open => "open",
        _ => "inconclusive",
    };

    /// <summary>
    /// The outcome: refuted while an unmarked method is open, else undecided while one is inconclusive.
    /// A method marked <c>[AnyValidResult]</c> may be open.
    /// </summary>
    public static AgentOutcome Outcome(IReadOnlyList<OneAnswerResult> results) =>
        results.Any(r => r.Verdict == OneAnswerVerdict.Open && !r.AnyValidResult) ? AgentOutcome.Refuted
        : results.Any(r => r.Verdict == OneAnswerVerdict.Inconclusive) ? AgentOutcome.Undecided
        : AgentOutcome.Proved;

    public static string ToJson(IReadOnlyList<OneAnswerResult> results, bool indented, int limit, int offset)
    {
        var outcome = Outcome(results);
        var page = AgentOutput.Slice(results.Count, offset, limit);
        return AgentOutput.Write(indented, w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", AgentOutput.SchemaId("one-answer"));
            w.WriteString("command", "one-answer");
            w.WriteString("status", AgentOutput.StatusWord(outcome));
            w.WriteNumber("exitCode", (int)outcome);
            w.WriteStartArray("methods");
            for (var i = page.Offset; i < page.Offset + page.Count; i++)
            {
                var r = results[i];
                w.WriteStartObject();
                w.WriteString("symbol", r.Symbol);
                w.WriteString("verdict", Word(r.Verdict));
                w.WriteBoolean("anyValidResult", r.AnyValidResult);
                AgentOutput.StringOrNull(w, "anyValidReason", r.AnyValidReason);
                w.WriteStartArray("clauses");
                foreach (var c in r.Clauses) w.WriteStringValue(c);
                w.WriteEndArray();
                AgentOutput.RawOrNull(w, "counterexample", r.CounterexampleJson);
                AgentOutput.StringOrNull(w, "note", r.Note);
                w.WriteString("file", r.File);
                w.WriteNumber("line", r.Line);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            AgentOutput.WritePage(w, page, limit);
            w.WriteEndObject();
        });
    }
}
