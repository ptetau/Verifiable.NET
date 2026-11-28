using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Verifiable.Analyzers;
using Verifiable.Shared;

namespace Verifiable.Query;

/// <summary>One exported contract or rule.</summary>
internal sealed record ExportedContract(
    string Symbol,
    string Kind,
    string? Clause,
    string? Reason,
    string Text,
    string? ResultParameter,
    ExpressionSyntax? Guard,
    ExpressionSyntax? Expression,
    SemanticModel Model,
    string File,
    int Line);

/// <summary>
/// The contract export (requirement R2): every contract, constrained type and invariant in a stable,
/// documented JSON format (schema <c>verifiable-query/export@1</c>), each with its symbol, its predicate
/// as an expression tree, its reason and its clause. Consumers — the round trip back to ACE, SQL CHECK
/// constraints, UI messages, and change impact (R7) — read this instead of C#.
/// </summary>
internal static class ContractExport
{
    public static List<ExportedContract> Collect(Compilation compilation)
    {
        var list = new List<ExportedContract>();
        foreach (var tree in compilation.SyntaxTrees.Where(t => !t.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)))
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            foreach (var decl in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is not IMethodSymbol method) continue;
                var key = ContractLock.Key(decl);
                if (key is null) continue;
                foreach (var call in LeadingContractBlock.GetLeadingSpecCalls(decl, c => ClauseIds.SpecMethodName(c) is not null))
                {
                    list.Add(FromSpecCall(key, call, model));
                }
                // Loop invariants and termination measures live inside the body.
                foreach (var call in decl.DescendantNodes().OfType<InvocationExpressionSyntax>()
                             .Where(c => ClauseIds.SpecMethodName(c) is "LoopInvariant" or "Decreases" &&
                                         c.Ancestors().Any(a => a is ForStatementSyntax or WhileStatementSyntax or DoStatementSyntax or ForEachStatementSyntax)))
                {
                    list.Add(FromSpecCall(key, call, model));
                }
            }

            foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                foreach (var attr in type.AttributeLists.SelectMany(l => l.Attributes).Where(a => ClauseIds.IsClauseAttribute(a) && !a.Name.ToString().StartsWith("Invariant", StringComparison.Ordinal)))
                {
                    var isValid = (type as TypeDeclarationSyntax)?.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.ValueText == "IsValid");
                    var (file, line) = Where(attr);
                    list.Add(new ExportedContract(
                        type.Identifier.ValueText,
                        attr.Name is GenericNameSyntax g && g.Identifier.ValueText.StartsWith("Refined", StringComparison.Ordinal) ? "refined" : "typeAlias",
                        ClauseIds.FromAttribute(attr),
                        FirstStringArg(attr),
                        isValid is null ? attr.ToString() : OneLine(isValid.ToString()),
                        null,
                        null,
                        isValid is null ? null : BodyExpression(isValid),
                        model, file, line));
                }

                if (type is not TypeDeclarationSyntax tds) continue;
                foreach (var member in tds.Members)
                {
                    var inv = member.AttributeLists.SelectMany(l => l.Attributes)
                        .FirstOrDefault(a => ClauseIds.IsClauseAttribute(a) && a.Name.ToString().StartsWith("Invariant", StringComparison.Ordinal));
                    if (inv is null) continue;
                    var (name, expr) = member switch
                    {
                        PropertyDeclarationSyntax p => (p.Identifier.ValueText, p.ExpressionBody?.Expression ?? GetterExpression(p)),
                        MethodDeclarationSyntax m => (m.Identifier.ValueText, BodyExpression(m)),
                        _ => ("?", (ExpressionSyntax?)null),
                    };
                    var (file, line) = Where(member);
                    list.Add(new ExportedContract($"{type.Identifier.ValueText}.{name}", "invariant", ClauseIds.FromAttribute(inv), FirstStringArg(inv),
                        OneLine(member.ToString()), null, null, expr, model, file, line));
                }
            }
        }

        return list
            .OrderBy(c => c.Symbol, StringComparer.Ordinal)
            .ThenBy(c => c.File, StringComparer.Ordinal)
            .ThenBy(c => c.Line)
            .ThenBy(c => c.Kind, StringComparer.Ordinal)
            .ToList();
    }

    private static ExportedContract FromSpecCall(string symbol, InvocationExpressionSyntax call, SemanticModel model)
    {
        var method = ClauseIds.SpecMethodName(call)!;
        var args = call.ArgumentList.Arguments;
        ExpressionSyntax? Arg(string name, int position) =>
            args.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == name)?.Expression ??
            (args.Count > position && args.Take(position + 1).All(a => a.NameColon is null) ? args[position].Expression : null);

        var isWhen = method is "RequiresWhen" or "EnsuresWhen";
        var guard = isWhen ? Arg("condition", 0) : null;
        var body = method switch
        {
            "Requires" or "LoopInvariant" => Arg("condition", 0),
            "Decreases" => Arg("measure", 0),
            "RequiresWhen" => Arg("requirement", 1),
            "Ensures" => Arg("predicate", 0) ?? Arg("condition", 0),
            "EnsuresWhen" => Arg("predicate", 1) ?? Arg("postcondition", 1),
            _ => null,
        };
        var reason = Arg("because", isWhen ? 2 : 1) is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression) ? lit.Token.ValueText : null;

        string? resultParam = null;
        if (body is SimpleLambdaExpressionSyntax sl) { resultParam = sl.Parameter.Identifier.ValueText; body = sl.ExpressionBody; }
        else if (body is ParenthesizedLambdaExpressionSyntax pl && pl.ParameterList.Parameters.Count == 1)
        {
            resultParam = pl.ParameterList.Parameters[0].Identifier.ValueText;
            body = pl.ExpressionBody;
        }

        var kind = method switch
        {
            "Requires" => "requires",
            "RequiresWhen" => "requires",
            "Ensures" => "ensures",
            "EnsuresWhen" => "ensures",
            "LoopInvariant" => "loopInvariant",
            _ => "decreases",
        };
        var (file, line) = Where(call);
        return new ExportedContract(symbol, kind, ClauseIds.FromSpecCall(call), reason, OneLine(call.ToString()), resultParam, guard, body, model, file, line);
    }

    private static ExpressionSyntax? GetterExpression(PropertyDeclarationSyntax p)
    {
        var get = p.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        return get is null ? null : get.ExpressionBody?.Expression ?? SingleReturn(get.Body);
    }

    private static ExpressionSyntax? BodyExpression(MethodDeclarationSyntax m) => m.ExpressionBody?.Expression ?? SingleReturn(m.Body);

    private static ExpressionSyntax? SingleReturn(BlockSyntax? body) =>
        body?.Statements.Count == 1 && body.Statements[0] is ReturnStatementSyntax r ? r.Expression : null;

    private static string? FirstStringArg(AttributeSyntax attr) =>
        attr.ArgumentList?.Arguments.FirstOrDefault(a => a.NameEquals is null && a.NameColon is null)?.Expression is LiteralExpressionSyntax lit &&
        lit.IsKind(SyntaxKind.StringLiteralExpression) ? lit.Token.ValueText : null;

    private static (string File, int Line) Where(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return (AgentOutput.NormalizePath(span.Path), span.StartLinePosition.Line + 1);
    }

    private static string OneLine(string text) =>
        string.Join(" ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));

    private static readonly Dictionary<SyntaxKind, string> BinaryOps = new()
    {
        [SyntaxKind.LogicalAndExpression] = "and",
        [SyntaxKind.LogicalOrExpression] = "or",
        [SyntaxKind.EqualsExpression] = "eq",
        [SyntaxKind.NotEqualsExpression] = "ne",
        [SyntaxKind.LessThanExpression] = "lt",
        [SyntaxKind.LessThanOrEqualExpression] = "le",
        [SyntaxKind.GreaterThanExpression] = "gt",
        [SyntaxKind.GreaterThanOrEqualExpression] = "ge",
        [SyntaxKind.AddExpression] = "add",
        [SyntaxKind.SubtractExpression] = "sub",
        [SyntaxKind.MultiplyExpression] = "mul",
        [SyntaxKind.DivideExpression] = "div",
        [SyntaxKind.ModuloExpression] = "mod",
        [SyntaxKind.BitwiseAndExpression] = "bitAnd",
        [SyntaxKind.BitwiseOrExpression] = "bitOr",
        [SyntaxKind.ExclusiveOrExpression] = "xor",
        [SyntaxKind.CoalesceExpression] = "coalesce",
    };

    /// <summary>
    /// Writes an expression as a tree of <c>{"op": ...}</c> nodes. Every node carries the C# <c>type</c>
    /// of its value. Forms without a dedicated node are written as <c>{"op":"csharp","text":...}</c>.
    /// </summary>
    internal static void WriteExpr(Utf8JsonWriter w, ExpressionSyntax? e, SemanticModel model, string? resultParam)
    {
        // Parentheses and `!` carry no meaning in a tree.
        while (e is ParenthesizedExpressionSyntax or PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression })
        {
            e = e is ParenthesizedExpressionSyntax p ? p.Expression : ((PostfixUnaryExpressionSyntax)e).Operand;
        }

        if (e is null)
        {
            w.WriteNullValue();
            return;
        }

        // `x is { Length: >= 1 and <= 100 }` and friends become ordinary comparisons.
        if (e is IsPatternExpressionSyntax isp && CanWritePattern(isp.Pattern))
        {
            var operand = isp.Expression;
            WritePattern(w, isp.Pattern, () => WriteExpr(w, operand, model, resultParam), model.GetTypeInfo(operand).Type, model, resultParam);
            return;
        }

        w.WriteStartObject();
        var type = model.GetTypeInfo(e).Type;
        void Type() => AgentOutput.StringOrNull(w, "type", type?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));

        switch (e)
        {
            case BinaryExpressionSyntax b when BinaryOps.TryGetValue(b.Kind(), out var op):
                w.WriteString("op", op);
                Type();
                w.WritePropertyName("left");
                WriteExpr(w, b.Left, model, resultParam);
                w.WritePropertyName("right");
                WriteExpr(w, b.Right, model, resultParam);
                break;

            case PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.LogicalNotExpression) || u.IsKind(SyntaxKind.UnaryMinusExpression):
                w.WriteString("op", u.IsKind(SyntaxKind.LogicalNotExpression) ? "not" : "neg");
                Type();
                w.WritePropertyName("operand");
                WriteExpr(w, u.Operand, model, resultParam);
                break;

            case ConditionalExpressionSyntax c:
                w.WriteString("op", "cond");
                Type();
                w.WritePropertyName("test");
                WriteExpr(w, c.Condition, model, resultParam);
                w.WritePropertyName("then");
                WriteExpr(w, c.WhenTrue, model, resultParam);
                w.WritePropertyName("else");
                WriteExpr(w, c.WhenFalse, model, resultParam);
                break;

            case LiteralExpressionSyntax lit:
                w.WriteString("op", "const");
                Type();
                w.WriteString("value", lit.IsKind(SyntaxKind.NullLiteralExpression) ? "null" : lit.Token.ValueText);
                break;

            case IdentifierNameSyntax id:
            {
                var sym = model.GetSymbolInfo(id).Symbol;
                var name = id.Identifier.ValueText;
                w.WriteString("op", name == resultParam ? "result" : sym switch
                {
                    IParameterSymbol => "param",
                    ILocalSymbol => "local",
                    IFieldSymbol or IPropertySymbol => "member",
                    _ => "name",
                });
                Type();
                w.WriteString("name", name);
                break;
            }

            case MemberAccessExpressionSyntax ma when model.GetSymbolInfo(ma.Expression).Symbol is INamedTypeSymbol:
                w.WriteString("op", "static");
                Type();
                w.WriteString("name", ma.ToString());
                break;

            case MemberAccessExpressionSyntax ma:
                w.WriteString("op", "member");
                Type();
                w.WriteString("name", ma.Name.Identifier.ValueText);
                w.WritePropertyName("target");
                WriteExpr(w, ma.Expression, model, resultParam);
                break;

            case InvocationExpressionSyntax inv:
            {
                var m = model.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                var isSpec = m?.ContainingType?.ToDisplayString() == VerifiableSymbolHelpers.SpecTypeName;
                if (isSpec && m!.Name is "ForAll" or "Exists" && inv.ArgumentList.Arguments.Count == 3 &&
                    inv.ArgumentList.Arguments[2].Expression is SimpleLambdaExpressionSyntax q)
                {
                    w.WriteString("op", m.Name == "ForAll" ? "forall" : "exists");
                    Type();
                    w.WriteString("variable", q.Parameter.Identifier.ValueText);
                    w.WritePropertyName("from");
                    WriteExpr(w, inv.ArgumentList.Arguments[0].Expression, model, resultParam);
                    w.WritePropertyName("to");
                    WriteExpr(w, inv.ArgumentList.Arguments[1].Expression, model, resultParam);
                    w.WritePropertyName("body");
                    WriteExpr(w, q.ExpressionBody, model, resultParam);
                    break;
                }

                if (isSpec && m!.Name == "Implies" && inv.ArgumentList.Arguments.Count == 2)
                {
                    w.WriteString("op", "implies");
                    Type();
                    w.WritePropertyName("left");
                    WriteExpr(w, inv.ArgumentList.Arguments[0].Expression, model, resultParam);
                    w.WritePropertyName("right");
                    WriteExpr(w, inv.ArgumentList.Arguments[1].Expression, model, resultParam);
                    break;
                }

                w.WriteString("op", "call");
                Type();
                w.WriteString("method", m is null ? inv.Expression.ToString() : $"{m.ContainingType?.Name}.{m.Name}");
                w.WritePropertyName("target");
                if (inv.Expression is MemberAccessExpressionSyntax callee && m is { IsStatic: false })
                    WriteExpr(w, callee.Expression, model, resultParam);
                else
                    w.WriteNullValue();
                w.WriteStartArray("args");
                foreach (var a in inv.ArgumentList.Arguments) WriteExpr(w, a.Expression, model, resultParam);
                w.WriteEndArray();
                break;
            }

            case ElementAccessExpressionSyntax ea when ea.ArgumentList.Arguments.Count == 1:
                w.WriteString("op", "index");
                Type();
                w.WritePropertyName("target");
                WriteExpr(w, ea.Expression, model, resultParam);
                w.WritePropertyName("index");
                WriteExpr(w, ea.ArgumentList.Arguments[0].Expression, model, resultParam);
                break;

            case CastExpressionSyntax cast:
                w.WriteString("op", "cast");
                Type();
                w.WritePropertyName("operand");
                WriteExpr(w, cast.Expression, model, resultParam);
                break;

            case IsPatternExpressionSyntax { Pattern: ConstantPatternSyntax { Expression: LiteralExpressionSyntax nl } } isNull when nl.IsKind(SyntaxKind.NullLiteralExpression):
                w.WriteString("op", "isNull");
                Type();
                w.WritePropertyName("operand");
                WriteExpr(w, isNull.Expression, model, resultParam);
                break;

            case IsPatternExpressionSyntax { Pattern: UnaryPatternSyntax { Pattern: ConstantPatternSyntax { Expression: LiteralExpressionSyntax nn } } } notNull when nn.IsKind(SyntaxKind.NullLiteralExpression):
                w.WriteString("op", "notNull");
                Type();
                w.WritePropertyName("operand");
                WriteExpr(w, notNull.Expression, model, resultParam);
                break;

            default:
                w.WriteString("op", "csharp");
                Type();
                w.WriteString("text", OneLine(e.ToString()));
                break;
        }

        w.WriteEndObject();
    }

    /// <summary>The export document. With <paramref name="limit"/> at <c>int.MaxValue</c> it holds everything.</summary>
    public static string Document(IReadOnlyList<ExportedContract> contracts, bool indented, int limit, int offset, string? writtenTo) =>
        AgentOutput.Write(indented, w =>
        {
            var page = AgentOutput.Slice(contracts.Count, offset, limit);
            w.WriteStartObject();
            w.WriteString("schema", AgentOutput.SchemaId("export"));
            w.WriteString("command", "export");
            w.WriteString("status", AgentOutput.StatusWord(AgentOutcome.Proved));
            w.WriteNumber("exitCode", 0);
            AgentOutput.StringOrNull(w, "file", writtenTo is null ? null : AgentOutput.NormalizePath(writtenTo));
            w.WriteStartArray("contracts");
            for (var i = page.Offset; i < page.Offset + page.Count; i++)
            {
                var c = contracts[i];
                w.WriteStartObject();
                w.WriteString("symbol", c.Symbol);
                w.WriteString("kind", c.Kind);
                AgentOutput.StringOrNull(w, "clause", c.Clause);
                AgentOutput.StringOrNull(w, "reason", c.Reason);
                w.WriteString("text", c.Text);
                AgentOutput.StringOrNull(w, "result", c.ResultParameter);
                w.WritePropertyName("guard");
                WriteExpr(w, c.Guard, c.Model, c.ResultParameter);
                w.WritePropertyName("expr");
                WriteExpr(w, c.Expression, c.Model, c.ResultParameter);
                w.WriteString("file", c.File);
                w.WriteNumber("line", c.Line);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            AgentOutput.WritePage(w, page, Math.Min(limit, AgentOutput.MaxLimit));
            w.WriteEndObject();
        });

    private static bool CanWritePattern(PatternSyntax p) => p switch
    {
        ConstantPatternSyntax => true,
        RelationalPatternSyntax => true,
        BinaryPatternSyntax b => CanWritePattern(b.Left) && CanWritePattern(b.Right),
        UnaryPatternSyntax u => CanWritePattern(u.Pattern),
        ParenthesizedPatternSyntax pp => CanWritePattern(pp.Pattern),
        RecursivePatternSyntax r => r.Type is null && r.Designation is null && r.PositionalPatternClause is null &&
            (r.PropertyPatternClause?.Subpatterns.All(sp => sp.NameColon is not null && CanWritePattern(sp.Pattern)) ?? true),
        _ => false,
    };

    /// <summary>Writes a pattern applied to an operand (written by <paramref name="operand"/>) as a predicate.</summary>
    private static void WritePattern(Utf8JsonWriter w, PatternSyntax p, Action operand, ITypeSymbol? operandType, SemanticModel model, string? resultParam)
    {
        void Binary(string op, Action left, Action right)
        {
            w.WriteStartObject();
            w.WriteString("op", op);
            w.WriteString("type", "bool");
            w.WritePropertyName("left");
            left();
            w.WritePropertyName("right");
            right();
            w.WriteEndObject();
        }

        switch (p)
        {
            case ParenthesizedPatternSyntax pp:
                WritePattern(w, pp.Pattern, operand, operandType, model, resultParam);
                return;
            case ConstantPatternSyntax c when c.Expression is LiteralExpressionSyntax nl && nl.IsKind(SyntaxKind.NullLiteralExpression):
                w.WriteStartObject();
                w.WriteString("op", "isNull");
                w.WriteString("type", "bool");
                w.WritePropertyName("operand");
                operand();
                w.WriteEndObject();
                return;
            case ConstantPatternSyntax c:
                Binary("eq", operand, () => WriteExpr(w, c.Expression, model, resultParam));
                return;
            case RelationalPatternSyntax r:
                var op = r.OperatorToken.Kind() switch
                {
                    SyntaxKind.LessThanToken => "lt",
                    SyntaxKind.LessThanEqualsToken => "le",
                    SyntaxKind.GreaterThanToken => "gt",
                    _ => "ge",
                };
                Binary(op, operand, () => WriteExpr(w, r.Expression, model, resultParam));
                return;
            case BinaryPatternSyntax b:
                Binary(b.OperatorToken.IsKind(SyntaxKind.AndKeyword) ? "and" : "or",
                    () => WritePattern(w, b.Left, operand, operandType, model, resultParam),
                    () => WritePattern(w, b.Right, operand, operandType, model, resultParam));
                return;
            case UnaryPatternSyntax u:
                w.WriteStartObject();
                w.WriteString("op", "not");
                w.WriteString("type", "bool");
                w.WritePropertyName("operand");
                WritePattern(w, u.Pattern, operand, operandType, model, resultParam);
                w.WriteEndObject();
                return;
            case RecursivePatternSyntax rec:
            {
                // `{ A: p, B: q }` on a reference type means: not null, and A matches p, and B matches q.
                var parts = new List<Action>();
                if (operandType is { IsReferenceType: true })
                {
                    parts.Add(() =>
                    {
                        w.WriteStartObject();
                        w.WriteString("op", "notNull");
                        w.WriteString("type", "bool");
                        w.WritePropertyName("operand");
                        operand();
                        w.WriteEndObject();
                    });
                }
                foreach (var sub in rec.PropertyPatternClause?.Subpatterns ?? default)
                {
                    var name = sub.NameColon!.Name.Identifier.ValueText;
                    var memberType = operandType?.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault()?.Type
                                     ?? operandType?.GetMembers(name).OfType<IFieldSymbol>().FirstOrDefault()?.Type;
                    void Member()
                    {
                        w.WriteStartObject();
                        w.WriteString("op", "member");
                        AgentOutput.StringOrNull(w, "type", memberType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
                        w.WriteString("name", name);
                        w.WritePropertyName("target");
                        operand();
                        w.WriteEndObject();
                    }
                    var subPattern = sub.Pattern;
                    parts.Add(() => WritePattern(w, subPattern, Member, memberType, model, resultParam));
                }

                if (parts.Count == 0)
                {
                    w.WriteStartObject();
                    w.WriteString("op", "const");
                    w.WriteString("type", "bool");
                    w.WriteString("value", "true");
                    w.WriteEndObject();
                    return;
                }

                void Chain(int i)
                {
                    if (i == parts.Count - 1) { parts[i](); return; }
                    Binary("and", parts[i], () => Chain(i + 1));
                }
                Chain(0);
                return;
            }
        }
    }
}
