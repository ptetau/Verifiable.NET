using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Verifiable.Analyzers.Smt;

internal sealed partial class CfgSsaLowerer
{
    private bool _declaredWhitespaceRegex;
    private bool _declaredTrimFunction;
    private readonly HashSet<string> _emittedTrimAxioms = new(StringComparer.Ordinal);

    private void EnsureWhitespaceRegexDeclared()
    {
        if (_declaredWhitespaceRegex) return;
        _declaredWhitespaceRegex = true;
        _decls.Add($"(define-fun ver-ws-char () (RegLan) {RegexModel.WhitespaceCharRegex})");
    }

    private void EnsureTrimDeclared()
    {
        if (_declaredTrimFunction) return;
        EnsureWhitespaceRegexDeclared();
        _declaredTrimFunction = true;
        _decls.Add("(declare-fun ver-trim (String) String)");
    }

    private void EmitTrimAxiomFor(string rawStrTerm, Location loc)
    {
        if (_inQuantifierDepth > 0)
        {
            throw new ContractLoweringException("string.Trim() inside a quantified LINQ predicate is not supported.");
        }

        if (!_emittedTrimAxioms.Add(rawStrTerm))
        {
            return;
        }

        EnsureTrimDeclared();
        var id = ++_ssaCounter;
        var pre = $"trim_pre_{id}";
        var suf = $"trim_suf_{id}";
        var res = $"(ver-trim {rawStrTerm})";
        _decls.Add($"(declare-const {pre} String)");
        _decls.Add($"(declare-const {suf} String)");

        var axiom =
            $"(ite (str.in_re {rawStrTerm} (re.* ver-ws-char)) " +
            $"(= {res} \"\") " +
            $"(and " +
            $"(= {rawStrTerm} (str.++ {pre} {res} {suf})) " +
            $"(str.in_re {pre} (re.* ver-ws-char)) " +
            $"(str.in_re {suf} (re.* ver-ws-char)) " +
            $"(> (str.len {res}) 0) " +
            $"(not (str.in_re (str.at {res} 0) ver-ws-char)) " +
            $"(not (str.in_re (str.at {res} (- (str.len {res}) 1)) ver-ws-char))))";

        AddAssertion(axiom, "StringAxiom", "string.Trim specification", loc);
    }

    /// <summary>
    /// The raw text of a C# string term, with null read as "" (what concatenation does). A term that is
    /// literally <c>(str-val X)</c> is never null, so its text is just X. Unwrapping it keeps a chain of
    /// concatenations linear in size instead of copying each left operand twice per step.
    /// </summary>
    internal static string TextOrEmptyIfNull(string term)
    {
        const string wrapper = "(str-val ";
        if (term.StartsWith(wrapper, StringComparison.Ordinal) && IndexOfClosingParen(term, 0) == term.Length - 1)
        {
            return term.Substring(wrapper.Length, term.Length - wrapper.Length - 1);
        }

        return $"(ite (= {term} str-null) \"\" (str-text {term}))";
    }

    /// <summary>
    /// Index of the parenthesis that closes the one opened at <paramref name="open"/>, skipping string
    /// literals (where <c>""</c> is an escaped quote) and <c>|quoted|</c> symbols. -1 if it is not closed.
    /// </summary>
    private static int IndexOfClosingParen(string term, int open)
    {
        var depth = 0;
        for (var i = open; i < term.Length; i++)
        {
            var ch = term[i];
            if (ch == '"')
            {
                i++;
                while (i < term.Length && !(term[i] == '"' && (i + 1 >= term.Length || term[i + 1] != '"')))
                {
                    i += term[i] == '"' ? 2 : 1;
                }
            }
            else if (ch == '|')
            {
                i = term.IndexOf('|', i + 1);
                if (i < 0) return -1;
            }
            else if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }

        return -1;
    }

    private void EmitStringNonNullObligation(SmtTypedTerm strTerm, string context, Location loc, string gamma)
    {
        EmitSafetyObligation("Precondition", context, loc, gamma, $"(not (= {strTerm.Term} str-null))");
    }

    private bool TryLowerStringIntrinsic(
        IInvocationOperation inv,
        SmtTypedTerm? recvTerm,
        string gamma,
        BlockEnv env,
        bool emitObligations,
        int inlineDepth,
        out SmtTypedTerm result)
    {
        result = default!;
        var kind = StringIntrinsics.Classify(inv, out var error);
        if (error is not null)
        {
            throw new ContractLoweringException(error);
        }

        if (kind == StringIntrinsicKind.None)
        {
            return false;
        }

        var loc = inv.Syntax.GetLocation();
        var boolType = _compilation.GetSpecialType(SpecialType.System_Boolean);
        var strType = _compilation.GetSpecialType(SpecialType.System_String);

        switch (kind)
        {
            case StringIntrinsicKind.CharToString:
            {
                // A char is one UTF-16 code unit (0..65535); Z3's alphabet covers that range, so this is exact.
                var idx = _translator.IntTermForIndex(recvTerm!);
                result = new SmtTypedTerm($"(str-val (str.from_code {idx}))", strType);
                return true;
            }

            case StringIntrinsicKind.IsNullOrEmpty:
            {
                var arg = _translator.UnwrapRefinedIfApplicable(
                    LowerExpression(GetOrderedArgOp(inv, 0), gamma, env, emitObligations, inlineDepth));
                result = new SmtTypedTerm(
                    $"(or (= {arg.Term} str-null) (= (str.len (str-text {arg.Term})) 0))",
                    boolType);
                return true;
            }

            case StringIntrinsicKind.IsNullOrWhiteSpace:
            {
                EnsureWhitespaceRegexDeclared();
                var arg = _translator.UnwrapRefinedIfApplicable(
                    LowerExpression(GetOrderedArgOp(inv, 0), gamma, env, emitObligations, inlineDepth));
                result = new SmtTypedTerm(
                    $"(or (= {arg.Term} str-null) (str.in_re (str-text {arg.Term}) (re.* ver-ws-char)))",
                    boolType);
                return true;
            }

            case StringIntrinsicKind.Trim:
            {
                if (inv.Instance is not null && ExtractConstantStringArg(inv.Instance) is { } constTrimInput)
                {
                    var trimmed = constTrimInput.Trim();
                    result = new SmtTypedTerm($"(str-val {ContractExpressionTranslator.EscapeSmtString(trimmed)})", strType);
                    return true;
                }
                var recv = _translator.UnwrapRefinedIfApplicable(recvTerm!);
                EmitStringNonNullObligation(recv, $"Receiver must be non-null for '{inv.Syntax}'", loc, gamma);
                var raw = $"(str-text {recv.Term})";
                EmitTrimAxiomFor(raw, loc);
                result = new SmtTypedTerm($"(str-val (ver-trim {raw}))", strType);
                return true;
            }

            case StringIntrinsicKind.ContainsString:
            case StringIntrinsicKind.StartsWithString:
            case StringIntrinsicKind.EndsWithString:
            {
                var recv = _translator.UnwrapRefinedIfApplicable(recvTerm!);
                var arg = _translator.UnwrapRefinedIfApplicable(
                    LowerExpression(GetOrderedArgOp(inv, 0), gamma, env, emitObligations, inlineDepth));
                EmitStringNonNullObligation(recv, $"Receiver must be non-null for '{inv.Syntax}'", loc, gamma);
                EmitStringNonNullObligation(arg, $"Argument must be non-null for '{inv.Syntax}'", loc, gamma);

                var recvRaw = $"(str-text {recv.Term})";
                var argRaw = $"(str-text {arg.Term})";
                var formula = kind switch
                {
                    StringIntrinsicKind.ContainsString => $"(str.contains {recvRaw} {argRaw})",
                    StringIntrinsicKind.StartsWithString => $"(str.prefixof {argRaw} {recvRaw})",
                    StringIntrinsicKind.EndsWithString => $"(str.suffixof {argRaw} {recvRaw})",
                    _ => throw new InvalidOperationException()
                };
                result = new SmtTypedTerm(formula, boolType);
                return true;
            }

            case StringIntrinsicKind.ContainsChar:
            case StringIntrinsicKind.StartsWithChar:
            case StringIntrinsicKind.EndsWithChar:
            {
                var recv = _translator.UnwrapRefinedIfApplicable(recvTerm!);
                EmitStringNonNullObligation(recv, $"Receiver must be non-null for '{inv.Syntax}'", loc, gamma);
                var chRaw = LowerCharAsSmtString(GetOrderedArgOp(inv, 0), gamma, env, emitObligations, inlineDepth);
                var recvRaw = $"(str-text {recv.Term})";
                var formula = kind switch
                {
                    StringIntrinsicKind.ContainsChar => $"(str.contains {recvRaw} {chRaw})",
                    StringIntrinsicKind.StartsWithChar => $"(str.prefixof {chRaw} {recvRaw})",
                    StringIntrinsicKind.EndsWithChar => $"(str.suffixof {chRaw} {recvRaw})",
                    _ => throw new InvalidOperationException()
                };
                result = new SmtTypedTerm(formula, boolType);
                return true;
            }

            case StringIntrinsicKind.JoinStringParams:
            case StringIntrinsicKind.JoinCharParams:
            {
                string sepRaw;
                if (kind == StringIntrinsicKind.JoinCharParams)
                {
                    sepRaw = LowerCharAsSmtString(GetOrderedArgOp(inv, 0), gamma, env, emitObligations, inlineDepth);
                }
                else
                {
                    var sep = _translator.UnwrapRefinedIfApplicable(
                        LowerExpression(GetOrderedArgOp(inv, 0), gamma, env, emitObligations, inlineDepth));
                    sepRaw = TextOrEmptyIfNull(sep.Term);
                }

                var valuesArg = GetOrderedArgOp(inv, 1);
                if (!TryExtractBoundedJoinElements(valuesArg, out var elemOps, out var joinErr))
                {
                    throw new ContractLoweringException(joinErr ?? "string.Join requires a bounded sequence of elements.");
                }

                if (elemOps.Count > StringIntrinsics.MaxBoundedJoinElements)
                {
                    throw new ContractLoweringException(
                        $"string.Join with {elemOps.Count} elements exceeds the bounded limit of {StringIntrinsics.MaxBoundedJoinElements}.");
                }

                if (elemOps.Count == 0)
                {
                    result = new SmtTypedTerm("(str-val \"\")", strType);
                    return true;
                }

                var rawParts = new List<string>(elemOps.Count * 2 - 1);
                for (var i = 0; i < elemOps.Count; i++)
                {
                    if (i > 0)
                    {
                        rawParts.Add(sepRaw);
                    }
                    var elem = _translator.UnwrapRefinedIfApplicable(
                        LowerExpression(elemOps[i], gamma, env, emitObligations, inlineDepth));
                    rawParts.Add(TextOrEmptyIfNull(elem.Term));
                }

                var joinedRaw = rawParts.Count == 1
                    ? rawParts[0]
                    : $"(str.++ {string.Join(" ", rawParts)})";
                result = new SmtTypedTerm($"(str-val {joinedRaw})", strType);
                return true;
            }
        }

        return false;
    }

    private string LowerCharAsSmtString(IOperation op, string gamma, BlockEnv env, bool emitObligations, int inlineDepth)
    {
        var cur = op;
        while (cur is IConversionOperation conv)
        {
            cur = conv.Operand;
        }
        if (cur.ConstantValue.HasValue && cur.ConstantValue.Value is char c)
        {
            return ContractExpressionTranslator.EscapeSmtString(c.ToString());
        }

        var chTerm = LowerExpression(op, gamma, env, emitObligations, inlineDepth);
        var idx = _translator.IntTermForIndex(chTerm);
        return $"(str.from_code {idx})";
    }

    private static IOperation GetOrderedArgOp(IInvocationOperation inv, int ordinal)
    {
        foreach (var arg in inv.Arguments)
        {
            if (arg.Parameter?.Ordinal == ordinal)
            {
                return arg.Value;
            }
        }
        return inv.Arguments[ordinal].Value;
    }

    private static bool TryExtractBoundedJoinElements(
        IOperation valuesOp,
        out List<IOperation> elements,
        out string? error)
    {
        elements = new List<IOperation>();
        error = null;

        var cur = valuesOp;
        while (cur is IConversionOperation conv)
        {
            cur = conv.Operand;
        }

        if (cur is IArrayCreationOperation arrCreate)
        {
            if (arrCreate.Initializer is not null)
            {
                foreach (var elem in arrCreate.Initializer.ElementValues)
                {
                    elements.Add(elem);
                }
                return true;
            }

            if (arrCreate.DimensionSizes.Length == 1 &&
                arrCreate.DimensionSizes[0].ConstantValue.HasValue &&
                Convert.ToInt32(arrCreate.DimensionSizes[0].ConstantValue.Value) == 0)
            {
                return true;
            }

            error = "string.Join array argument must have a bounded initializer.";
            return false;
        }

        if (cur is ICollectionExpressionOperation colExpr)
        {
            foreach (var elem in colExpr.Elements)
            {
                if (elem is ISpreadOperation)
                {
                    error = "Spread elements inside string.Join collection expressions are not supported.";
                    return false;
                }
                elements.Add(elem);
            }
            return true;
        }

        if (cur is IInvocationOperation inv)
        {
            var typeFull = inv.TargetMethod.ContainingType?.OriginalDefinition.ToDisplayString();
            if (typeFull == "System.Array" && inv.TargetMethod.Name == "Empty" && inv.Arguments.Length == 0)
            {
                return true;
            }
            if (typeFull is "System.Collections.Immutable.ImmutableList" or "System.Collections.Immutable.ImmutableArray" &&
                inv.TargetMethod.Name == "Create")
            {
                if (inv.Arguments.Length == 0) return true;
                if (inv.Arguments.Length == 1 && inv.Arguments[0].Parameter?.IsParams == true)
                {
                    return TryExtractBoundedJoinElements(inv.Arguments[0].Value, out elements, out error);
                }
                foreach (var arg in inv.Arguments)
                {
                    elements.Add(arg.Value);
                }
                return true;
            }
        }

        if (cur is IMemberReferenceOperation memRef && memRef.Instance is null && memRef.Member.Name == "Empty")
        {
            var containing = memRef.Member.ContainingType?.OriginalDefinition.ToDisplayString() ?? string.Empty;
            if (containing.StartsWith("System.Collections.Immutable.", StringComparison.Ordinal))
            {
                return true;
            }
        }

        error = "string.Join only supports bounded params/array/collection-expression arguments (unbounded sequences are not supported).";
        return false;
    }
}
