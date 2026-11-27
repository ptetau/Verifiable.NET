using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Verifiable.Analyzers.Smt;

internal sealed partial class CfgSsaLowerer
{
    private bool _whitespaceDeclared;
    private bool _trimDeclared;

    // Unicode White_Space, as tested by .NET Char.IsWhiteSpace(char).
    private void DeclareWhitespace()
    {
        if (_whitespaceDeclared) return;
        _whitespaceDeclared = true;
        _decls.Add("""
            (define-fun dotnet_ws () (RegLan String) (re.union
              (re.range "\u{9}" "\u{d}") (str.to_re " ") (str.to_re "\u{85}")
              (str.to_re "\u{a0}") (str.to_re "\u{1680}") (re.range "\u{2000}" "\u{200a}")
              (re.range "\u{2028}" "\u{2029}") (str.to_re "\u{202f}") (str.to_re "\u{205f}")
              (str.to_re "\u{3000}")))
            """);
    }

    private SmtTypedTerm LowerStringIntrinsic(IInvocationOperation call, SmtTypedTerm? receiver,
        string gamma, BlockEnv env, bool emitObligations, int inlineDepth)
    {
        var method = call.TargetMethod;
        var loc = call.Syntax.GetLocation();
        if (!StringIntrinsics.IsSupported(call))
            throw new ContractLoweringException($"Unsupported string.{method.Name} overload or arguments: use ordinal comparisons or a bounded inline string.Join.");

        SmtTypedTerm Lower(IOperation op) => LowerExpression(op, gamma, env, emitObligations, inlineDepth);
        void RequireString(SmtTypedTerm s, string role)
        {
            if (!s.Term.StartsWith("(str-val ", System.StringComparison.Ordinal))
                AddObligation("Precondition", $"string.{method.Name} {role} must not be null", loc, gamma, $"(not (= {s.Term} str-null))");
        }
        static string TextOrEmpty(SmtTypedTerm s) => s.Type.SpecialType == SpecialType.System_Char
            ? $"(str.from_code {s.Term})"
            : $"(ite (= {s.Term} str-null) \"\" (str-text {s.Term}))";

        if (StringIntrinsics.TryGetJoinElements(call, out var elements))
        {
            var separator = "";
            var items = new List<string>(elements.Length);
            foreach (var arg in call.Arguments)
            {
                if (arg.Parameter!.Ordinal == 0) separator = TextOrEmpty(Lower(arg.Value));
                else foreach (var item in elements) items.Add(TextOrEmpty(Lower(item)));
            }
            var joined = items.Count switch
            {
                0 => "\"\"",
                1 => items[0],
                _ => $"(str.++ {string.Join(" " + separator + " ", items)})"
            };
            return new SmtTypedTerm($"(str-val {joined})", method.ReturnType);
        }

        // The only second argument admitted is the compile-time constant Ordinal.
        var argument = call.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0);
        var value = argument is null ? null : Lower(argument.Value);
        if (method.IsStatic)
        {
            var s = value!.Term;
            if (method.Name == "IsNullOrEmpty")
                return new SmtTypedTerm($"(or (= {s} str-null) (= (str-text {s}) \"\"))", method.ReturnType);
            DeclareWhitespace();
            return new SmtTypedTerm($"(or (= {s} str-null) (str.in_re (str-text {s}) (re.* dotnet_ws)))", method.ReturnType);
        }

        RequireString(receiver!, "receiver");
        var text = $"(str-text {receiver!.Term})";
        if (method.Name == "Trim")
        {
            DeclareWhitespace();
            if (!_trimDeclared)
            {
                _trimDeclared = true;
                _decls.Add("(declare-fun dotnet_trim (String) String)");
            }
            // Whitespace-only edges and a core with no whitespace at either
            // end determine precisely one trimmed result (including empty).
            var core = $"(dotnet_trim {text})";
            var left = $"trim_left_{++_ssaCounter}";
            var right = $"trim_right_{++_ssaCounter}";
            _decls.Add($"(declare-const {left} String)");
            _decls.Add($"(declare-const {right} String)");
            AddAssertion($"(and (= {text} (str.++ {left} {core} {right})) " +
                $"(str.in_re {left} (re.* dotnet_ws)) (str.in_re {right} (re.* dotnet_ws)) " +
                $"(not (str.in_re (str.at {core} 0) dotnet_ws)) " +
                $"(not (str.in_re (str.at {core} (- (str.len {core}) 1)) dotnet_ws)))",
                "StringAxiom", "Trim removes exactly the whitespace at each end", loc);
            return new SmtTypedTerm($"(str-val {core})", method.ReturnType);
        }

        string needle;
        if (value!.Type.SpecialType == SpecialType.System_Char)
            needle = $"(str.from_code {value.Term})";
        else
        {
            RequireString(value, "argument");
            needle = $"(str-text {value.Term})";
        }
        return new SmtTypedTerm(method.Name switch
        {
            "Contains" => $"(str.contains {text} {needle})",
            "StartsWith" => $"(str.prefixof {needle} {text})",
            "EndsWith" => $"(str.suffixof {needle} {text})",
            _ => throw new ContractLoweringException($"Unsupported string.{method.Name}.")
        }, method.ReturnType);
    }
}
