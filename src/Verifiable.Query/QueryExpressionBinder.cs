using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Verifiable.Query;

internal sealed record BoundQueryExpressions(
    CSharpCompilation CompilationWithProbe,
    IMethodSymbol TargetMethodInProbe,
    IOperation AssertBodyOp,
    IParameterSymbol? ResultParam,
    IParameterSymbol? ThisParam,
    IReadOnlyList<IParameterSymbol> ForwardedMethodParams,
    IOperation? AssumeBodyOp,
    bool AssumeUsesResult);

internal sealed record QueryBindOutcome(
    BoundQueryExpressions? Bound,
    string? ErrorMessage,
    string? FixHint);

internal static class QueryExpressionBinder
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    public static bool TryParseLambdaClaim(
        string claimText,
        out string paramName,
        out ExpressionSyntax? bodyExpr,
        out string? error)
    {
        paramName = "result";
        bodyExpr = null;
        error = null;

        var parsed = SyntaxFactory.ParseExpression(claimText);
        if (parsed.ContainsDiagnostics)
        {
            var first = parsed.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
            error = $"Syntax error in expression `{claimText}`: {first?.GetMessage() ?? "invalid C# expression"}.";
            return false;
        }

        switch (parsed)
        {
            case SimpleLambdaExpressionSyntax simple:
                if (simple.ExpressionBody is null)
                {
                    error = "Query expression must be a single boolean expression, not a statement block.";
                    return false;
                }
                paramName = simple.Parameter.Identifier.ValueText;
                bodyExpr = simple.ExpressionBody;
                return true;

            case ParenthesizedLambdaExpressionSyntax paren:
                if (paren.ExpressionBody is null || paren.ParameterList.Parameters.Count > 1)
                {
                    error = "Lambda queries must take at most one parameter (`r => ...`) and have a single boolean expression body.";
                    return false;
                }
                paramName = paren.ParameterList.Parameters.Count == 1
                    ? paren.ParameterList.Parameters[0].Identifier.ValueText
                    : "result";
                bodyExpr = paren.ExpressionBody;
                return true;

            default:
                bodyExpr = parsed;
                return true;
        }
    }

    public static ExpressionSyntax SubstituteParameter(ExpressionSyntax expr, string paramName, ExpressionSyntax replacement)
    {
        var rewriter = new ParamSubstituter(paramName, replacement);
        return (ExpressionSyntax)rewriter.Visit(expr);
    }

    public static QueryBindOutcome Bind(
        CSharpCompilation baseCompilation,
        IMethodSymbol targetMethod,
        string assertText,
        string? assumeText)
    {
        if (targetMethod.ReturnsVoid)
        {
            return new QueryBindOutcome(
                null,
                $"Method `{targetMethod.ContainingType?.Name}.{targetMethod.Name}` returns `void`.",
                "Query a method that returns a value, state record, or Result<T, E>.");
        }

        if (!TryParseLambdaClaim(assertText, out var assertParamName, out var assertBodySyntax, out var parseErr))
        {
            return new QueryBindOutcome(null, parseErr, "Pass a valid boolean C# expression such as `result => result.TotalDebits == result.TotalCredits`.");
        }

        ExpressionSyntax? assumeBodySyntax = null;
        if (!string.IsNullOrWhiteSpace(assumeText))
        {
            if (!TryParseLambdaClaim(assumeText!, out var assumeParamName, out var parsedAssumeBody, out var assumeErr))
            {
                return new QueryBindOutcome(null, assumeErr, "Pass a valid boolean C# expression to `--assume`.");
            }

            assumeBodySyntax = assumeParamName != assertParamName && parsedAssumeBody is not null
                ? SubstituteParameter(parsedAssumeBody, assumeParamName, SyntaxFactory.IdentifierName(assertParamName))
                : parsedAssumeBody;
        }

        // Rewrite bare `this` to `__ver_this` for instance methods
        var thisRewriter = new ThisToParamRewriter("__ver_this");
        assertBodySyntax = (ExpressionSyntax)thisRewriter.Visit(assertBodySyntax!);
        if (assumeBodySyntax is not null)
        {
            assumeBodySyntax = (ExpressionSyntax)thisRewriter.Visit(assumeBodySyntax);
        }

        var usings = CollectUsings(targetMethod);
        var ns = targetMethod.ContainingNamespace is { IsGlobalNamespace: false } n
            ? n.ToDisplayString()
            : string.Empty;

        var probeParams = new List<(string ParamName, ITypeSymbol Type, bool IsThis, bool IsResult)>();
        if (!targetMethod.IsStatic && targetMethod.ContainingType is { } ct)
        {
            probeParams.Add(("__ver_this", ct, true, false));
        }

        foreach (var p in targetMethod.Parameters)
        {
            if (p.Name == assertParamName)
            {
                continue;
            }
            probeParams.Add((p.Name, p.Type, false, false));
        }

        probeParams.Add((assertParamName, targetMethod.ReturnType, false, true));

        var paramDeclList = string.Join(", ",
            probeParams.Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {p.ParamName}"));

        var sb = new StringBuilder();
        foreach (var u in usings)
        {
            sb.AppendLine(u);
        }
        sb.AppendLine("#nullable enable");
        if (!string.IsNullOrEmpty(ns))
        {
            sb.Append("namespace ").Append(ns).AppendLine(";");
        }
        sb.AppendLine("internal static class __VerifiableQueryProbe");
        sb.AppendLine("{");
        sb.Append("    public static bool __Assert(").Append(paramDeclList).Append(") => (")
            .Append(assertBodySyntax.ToFullString().Trim()).AppendLine(");");
        if (assumeBodySyntax is not null)
        {
            sb.Append("    public static bool __Assume(").Append(paramDeclList).Append(") => (")
                .Append(assumeBodySyntax.ToFullString().Trim()).AppendLine(");");
        }
        sb.AppendLine("}");

        var probeTree = CSharpSyntaxTree.ParseText(sb.ToString(), ParseOptions, path: "__VerifiableQueryProbe.g.cs");
        var withProbe = baseCompilation.AddSyntaxTrees(probeTree);

        var probeErrors = withProbe.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error && d.Location.SourceTree == probeTree)
            .ToList();

        if (probeErrors.Count > 0)
        {
            var diagMsg = string.Join(" ", probeErrors.Select(e => e.GetMessage()));
            var available = DescribeAvailableIdentifiers(targetMethod, assertParamName);
            return new QueryBindOutcome(
                null,
                $"Could not compile query expression against `{targetMethod.ContainingType?.Name}.{targetMethod.Name}`: {diagMsg}",
                $"Available variables in scope: {available}");
        }

        var probeModel = withProbe.GetSemanticModel(probeTree);
        var probeRoot = probeTree.GetRoot();
        var methods = probeRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        var assertDecl = methods.First(m => m.Identifier.ValueText == "__Assert");
        var assertSymbol = probeModel.GetDeclaredSymbol(assertDecl)!;
        var assertOp = ExtractExpressionOperation(probeModel, assertDecl);
        if (assertOp is null)
        {
            return new QueryBindOutcome(null, "Failed to bind query assertion expression to Roslyn IOperation.", null);
        }

        var thisParamSym = assertSymbol.Parameters.FirstOrDefault(p => p.Name == "__ver_this");
        var resultParamSym = assertSymbol.Parameters.First(p => p.Name == assertParamName);
        var forwarded = assertSymbol.Parameters
            .Where(p => p.Name != "__ver_this" && p.Name != assertParamName)
            .ToList();

        IOperation? assumeOp = null;
        var assumeUsesResult = false;
        var allForwarded = new List<IParameterSymbol>(forwarded);
        var assumeDecl = methods.FirstOrDefault(m => m.Identifier.ValueText == "__Assume");
        if (assumeDecl is not null)
        {
            var assumeSymbol = probeModel.GetDeclaredSymbol(assumeDecl)!;
            var assumeResultParam = assumeSymbol.Parameters.First(p => p.Name == assertParamName);
            allForwarded.AddRange(assumeSymbol.Parameters);
            assumeOp = ExtractExpressionOperation(probeModel, assumeDecl);
            if (assumeOp is not null)
            {
                assumeUsesResult = assumeOp.DescendantsAndSelf()
                    .OfType<IParameterReferenceOperation>()
                    .Any(pr => SymbolEqualityComparer.Default.Equals(pr.Parameter, assumeResultParam));
            }
        }

        var targetSynRef = targetMethod.DeclaringSyntaxReferences.FirstOrDefault();
        var targetMethodInProbe = targetSynRef is not null
            ? withProbe.GetSemanticModel(targetSynRef.SyntaxTree).GetDeclaredSymbol(targetSynRef.GetSyntax()) as IMethodSymbol
            : null;
        if (targetMethodInProbe is null)
        {
            return new QueryBindOutcome(null, $"Failed to re-resolve `{targetMethod.Name}` in probe compilation.", null);
        }

        return new QueryBindOutcome(
            new BoundQueryExpressions(withProbe, targetMethodInProbe, assertOp, resultParamSym, thisParamSym, allForwarded, assumeOp, assumeUsesResult),
            null,
            null);
    }

    private static IOperation? ExtractExpressionOperation(SemanticModel model, MethodDeclarationSyntax md)
    {
        var bodyOp = model.GetOperation(md);
        if (bodyOp is IMethodBodyOperation mbo)
        {
            var block = mbo.ExpressionBody ?? mbo.BlockBody;
            if (block is not null)
            {
                var ret = block.Operations.OfType<IReturnOperation>().FirstOrDefault();
                if (ret?.ReturnedValue is not null)
                {
                    return ret.ReturnedValue;
                }
            }
        }

        if (md.ExpressionBody?.Expression is { } expr)
        {
            while (expr is ParenthesizedExpressionSyntax p)
            {
                expr = p.Expression;
            }
            return model.GetOperation(expr);
        }

        return null;
    }

    private static List<string> CollectUsings(IMethodSymbol method)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var synRef in method.DeclaringSyntaxReferences)
        {
            var root = synRef.SyntaxTree.GetRoot();
            foreach (var u in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                var text = u.ToString().Trim();
                if (seen.Add(text))
                {
                    result.Add(text);
                }
            }
        }
        return result;
    }

    private static string DescribeAvailableIdentifiers(IMethodSymbol targetMethod, string resultParamName)
    {
        var items = new List<string>();
        var retProps = targetMethod.ReturnType.GetMembers().OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && !p.IsIndexer && p.DeclaredAccessibility == Accessibility.Public && p.Name != "EqualityContract")
            .Select(p => $"{resultParamName}.{p.Name}: {p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}")
            .Take(8)
            .ToList();

        var retDesc = $"{resultParamName}: {targetMethod.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}";
        if (retProps.Count > 0)
        {
            retDesc += $" (properties: {string.Join(", ", retProps)})";
        }
        items.Add(retDesc);

        foreach (var p in targetMethod.Parameters)
        {
            items.Add($"{p.Name}: {p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");
        }

        return string.Join("; ", items);
    }

    private sealed class ThisToParamRewriter(string paramName) : CSharpSyntaxRewriter
    {
        public override SyntaxNode VisitThisExpression(ThisExpressionSyntax node) =>
            SyntaxFactory.IdentifierName(paramName).WithTriviaFrom(node);
    }

    private sealed class ParamSubstituter(string paramName, ExpressionSyntax replacement) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            var visitedExpr = (ExpressionSyntax)Visit(node.Expression)!;
            return node.WithExpression(visitedExpr);
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (node.Identifier.ValueText == paramName)
            {
                return SyntaxFactory.ParenthesizedExpression(replacement.WithoutTrivia()).WithTriviaFrom(node);
            }
            return base.VisitIdentifierName(node);
        }
    }
}
