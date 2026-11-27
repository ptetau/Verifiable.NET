using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Verifiable.Analyzers.Smt;

internal static class RegexModel
{
    private const string RegexType = "System.Text.RegularExpressions.Regex";

    public static bool IsMatchMethod(IMethodSymbol m) =>
        m.Name == "IsMatch" && m.ContainingType.ToDisplayString() == RegexType &&
        m.ContainingType.Locations.All(l => l.IsInMetadata);

    public static bool IsSupportedInvocation(IInvocationOperation inv, Compilation compilation)
    {
        // Also admit the nested factory call, but only as the receiver of this intrinsic.
        if (!IsMatchMethod(inv.TargetMethod) && inv.Parent is IInvocationOperation parent && parent.Instance == inv)
            inv = parent;
        if (!IsMatchMethod(inv.TargetMethod)) return false;
        try { Describe(inv, compilation); return true; }
        catch (ContractLoweringException) { return false; }
    }

    public static (IOperation Input, string Regex) Describe(IInvocationOperation inv, Compilation compilation)
    {
        var method = inv.TargetMethod;
        var args = inv.Arguments.OrderBy(a => a.Parameter?.Ordinal).ToArray();
        string? pattern;
        if (method.IsStatic && args.Length is 2 or 3 &&
            method.Parameters[0].Type.SpecialType == SpecialType.System_String &&
            method.Parameters[1].Type.SpecialType == SpecialType.System_String)
        {
            pattern = args[1].Value.ConstantValue is { HasValue: true, Value: string p } ? p : null;
            if (args.Length == 3 && args[2].Value.ConstantValue is not { HasValue: true, Value: 0 })
                throw Unsupported("only constant RegexOptions.None is supported");
        }
        else if (!method.IsStatic && args.Length == 1 && method.Parameters[0].Type.SpecialType == SpecialType.System_String &&
            inv.Instance is IInvocationOperation factory && factory.TargetMethod is { IsStatic: true, Parameters.Length: 0 } f &&
            f.IsPartialDefinition && f.ReturnType.ToDisplayString() == RegexType &&
            f.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "System.Text.RegularExpressions.GeneratedRegexAttribute" &&
                a.AttributeClass.Locations.All(l => l.IsInMetadata)) is { } attribute)
        {
            var values = attribute.ConstructorArguments;
            if (values.Length is not (1 or 2) || (values.Length == 2 && values[1].Value is not 0))
                throw Unsupported("generated options, culture, and timeout overrides are not supported");
            pattern = values[0].Value as string;
            if (pattern == null || !IsSdkFactory(compilation, f, pattern))
                throw Unsupported("expected the SDK-generated regex singleton for the declared pattern");
        }
        else throw Unsupported("expected Regex.IsMatch(string, constant pattern) or a GeneratedRegex factory");

        if (pattern == null) throw Unsupported("expected a constant non-null pattern");
        return (args[0].Value, Translate(pattern));
    }

    private static bool IsSdkFactory(Compilation compilation, IMethodSymbol factory, string pattern)
    {
        // An analyzer has the resulting Compilation, not the GeneratorDriverRunResult.
        // Check both the SDK's generated tree and the factory binding. In particular,
        // a GeneratedCode attribute on a hand-written method establishes neither.
        const string sdkTree = "System.Text.RegularExpressions.Generator/System.Text.RegularExpressions.Generator.RegexGenerator/RegexGenerator.g.cs";
        if (factory.PartialImplementationPart is not { } impl ||
            impl.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not MethodDeclarationSyntax
                { ExpressionBody.Expression: { } expression } declaration ||
            declaration.SyntaxTree == factory.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree ||
            !("/" + declaration.SyntaxTree.FilePath.Replace('\\', '/')).EndsWith("/" + sdkTree, StringComparison.Ordinal) ||
            !impl.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.CodeDom.Compiler.GeneratedCodeAttribute" &&
                a.ConstructorArguments.FirstOrDefault().Value is "System.Text.RegularExpressions.Generator"))
            return false;

        var model = compilation.GetSemanticModel(declaration.SyntaxTree);
        if (model.GetOperation(expression) is not IFieldReferenceOperation
            { Field: { Name: "Instance", IsReadOnly: true, IsStatic: true } field } ||
            field.ContainingType is not { IsFileLocal: true, IsSealed: true } type ||
            type.ContainingNamespace.ToDisplayString() != "System.Text.RegularExpressions.Generated" ||
            !SymbolEqualityComparer.Default.Equals(type.BaseType, factory.ReturnType) ||
            !SymbolEqualityComparer.Default.Equals(field.Type, type) ||
            field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not VariableDeclaratorSyntax
                { Initializer.Value: { } initializer } ||
            initializer.SyntaxTree != declaration.SyntaxTree ||
            model.GetOperation(initializer) is not IObjectCreationOperation { Constructor: { Parameters.Length: 0 } ctor } ||
            !SymbolEqualityComparer.Default.Equals(ctor.ContainingType, type) ||
            ctor.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not ConstructorDeclarationSyntax { Body: { } body } ||
            body.SyntaxTree != declaration.SyntaxTree)
            return false;

        // Bind the annotation to THIS generated singleton, not a different regex
        // emitted into the same file. Fail closed on unknown generator shapes.
        var hasPattern = false;
        var hasOptions = false;
        foreach (var stmt in body.Statements)
        {
            if (stmt is not ExpressionStatementSyntax es || model.GetOperation(es.Expression) is not ISimpleAssignmentOperation
                { Target: IFieldReferenceOperation { Field: var member }, Value.ConstantValue: { HasValue: true } constant } ||
                !SymbolEqualityComparer.Default.Equals(member.ContainingType, factory.ReturnType))
                continue;
            if (member.Name == "pattern") hasPattern = constant.Value is string p && p == pattern;
            if (member.Name == "roptions") hasOptions = constant.Value is 0;
        }
        return hasPattern && hasOptions;
    }

    private static ContractLoweringException Unsupported(string reason) =>
        new($"Unsupported Regex.IsMatch: {reason}.");

    internal static string Translate(string pattern) => new Parser(pattern).Parse();

    private static string Literal(char c) => ContractExpressionTranslator.EscapeSmtString(c.ToString());
    private static string Single(char c) => $"(str.to_re {Literal(c)})";
    private static string Range(char lo, char hi) => $"(re.range {Literal(lo)} {Literal(hi)})";
    private static string Join(string op, List<string> items) => items.Count switch
    {
        0 => "(str.to_re \"\")",
        1 => items[0],
        _ => $"({op} {string.Join(" ", items)})"
    };

    private static readonly Lazy<string> Digits = new(() =>
    {
        // Regex operates on UTF-16 chars, not scalar values. Supplementary-plane Nd
        // characters are two surrogates and do not match \d in .NET.
        var ranges = new List<string>();
        for (var i = 0; i <= char.MaxValue; i++)
        {
            if (!char.IsDigit((char)i)) continue;
            var start = i;
            while (i < char.MaxValue && char.IsDigit((char)(i + 1))) i++;
            ranges.Add(Range((char)start, (char)i));
        }
        return Join("re.union", ranges);
    });

    private sealed class Parser(string pattern)
    {
        private const string AnyString = "(re.* re.allchar)";
        private int _pos;
        private int _cost;
        private char Current => _pos < pattern.Length ? pattern[_pos] : '\0';
        private bool Eat(char ch)
        {
            if (_pos == pattern.Length || Current != ch) return false;
            _pos++;
            return true;
        }

        public string Parse()
        {
            if (pattern.Length > 256) throw Unsupported("pattern exceeds 256 UTF-16 code units");
            var result = Alternatives(0);
            if (_pos != pattern.Length) throw Unsupported("unmatched closing parenthesis");
            return result;
        }

        private string Alternatives(int depth)
        {
            if (depth > 12) throw Unsupported("groups nested more than 12 levels");
            var branches = new List<string>();
            do
            {
                var sequence = new List<string>();
                var anchoredEnd = false;
                if (depth == 0 && !Eat('^') && !EatEscape('A')) sequence.Add(AnyString);
                while (_pos < pattern.Length && Current is not ('|' or ')'))
                {
                    if (depth == 0 && (Current == '$' || AtEscape('z') || AtEscape('Z')))
                    {
                        if (!EatEscape('z'))
                        {
                            if (!Eat('$')) EatEscape('Z');
                            sequence.Add($"(re.opt {Single('\n')})");
                        }
                        anchoredEnd = true;
                        if (_pos < pattern.Length && Current is not ('|' or ')'))
                            throw Unsupported("end anchors must terminate a top-level alternative");
                        break;
                    }
                    var previousCost = _cost++;
                    var atom = Atom(depth);
                    if (Eat('*')) atom = $"(re.* {atom})";
                    else if (Eat('+')) atom = $"(re.+ {atom})";
                    else if (Eat('?')) atom = $"(re.opt {atom})";
                    else if (Eat('{'))
                    {
                        var lo = Number();
                        var hi = Eat(',') ? Number() : lo;
                        if (!Eat('}') || lo > hi) throw Unsupported("invalid bounded quantifier");
                        _cost += (_cost - previousCost) * Math.Max(0, hi - 1);
                        atom = $"((_ re.loop {lo.ToString(CultureInfo.InvariantCulture)} {hi.ToString(CultureInfo.InvariantCulture)}) {atom})";
                    }
                    if (_cost > 4096) throw Unsupported("expanded repetition budget exceeded");
                    sequence.Add(atom);
                }
                if (depth == 0 && !anchoredEnd) sequence.Add(AnyString);
                branches.Add(Join("re.++", sequence));
            } while (Eat('|'));
            return Join("re.union", branches);
        }

        private int Number()
        {
            if (Current is < '0' or > '9') throw Unsupported("expected finite repetition count");
            var n = 0;
            while (Current is >= '0' and <= '9')
            {
                n = 10 * n + pattern[_pos++] - '0';
                if (n > 128) throw Unsupported("repetition count exceeds 128");
            }
            return n;
        }

        private bool AtEscape(char ch) => Current == '\\' && _pos + 1 < pattern.Length && pattern[_pos + 1] == ch;
        private bool EatEscape(char ch)
        {
            if (!AtEscape(ch)) return false;
            _pos += 2;
            return true;
        }

        private string Atom(int depth)
        {
            if (Eat('('))
            {
                if (Eat('?') && !Eat(':')) throw Unsupported("unsupported group construct");
                var result = Alternatives(depth + 1);
                if (!Eat(')')) throw Unsupported("unterminated group");
                return result;
            }
            if (Eat('[')) return CharacterClass();
            if (EatEscape('d')) return Digits.Value;
            return Single(Character(false));
        }

        private string CharacterClass()
        {
            if (Current == '^') throw Unsupported("negated character class");
            var members = new List<string>();
            while (_pos < pattern.Length && Current != ']')
            {
                if (EatEscape('d')) members.Add(Digits.Value);
                else
                {
                    var lo = Character(true);
                    if (!Eat('-')) members.Add(Single(lo));
                    else
                    {
                        var hi = Character(true);
                        if (hi < lo) throw Unsupported("descending character range");
                        members.Add(Range(lo, hi));
                    }
                }
            }
            if (members.Count == 0 || !Eat(']')) throw Unsupported("empty or unterminated character class");
            return Join("re.union", members);
        }

        private char Character(bool inClass)
        {
            if (Eat('\\'))
            {
                if (_pos >= pattern.Length) throw Unsupported("trailing backslash");
                var escaped = pattern[_pos++];
                if (@"\.^$|?*+()[]{}-".IndexOf(escaped) >= 0) return escaped;
                return escaped switch
                {
                    'n' => '\n', 'r' => '\r', 't' => '\t',
                    _ => throw Unsupported("unsupported escape")
                };
            }
            if (_pos == pattern.Length || (inClass ? "[]-" : ".^$|?*+()[]{}").IndexOf(Current) >= 0)
                throw Unsupported("unsupported metacharacter or misplaced anchor");
            return pattern[_pos++];
        }
    }
}
