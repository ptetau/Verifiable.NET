using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using GalaxyCheck;

namespace Verifiable.Analyzers.Tests.TranslationValidation;

/// <summary>
/// GalaxyCheck generators of small C# methods over the integer fragment the analyzer supports:
/// <c>+ - * / %</c>, unary minus, comparisons, <c>&amp;&amp; || !</c>, the ternary operator, <c>Math.Abs/Min/Max</c>,
/// locals and if/else. Bit programs add <c>~ &amp; | ^ &lt;&lt; &gt;&gt;</c>, casts that wrap through a narrower type, and
/// compound assignments on narrow and unsigned locals. String programs add <c>Length</c>, <c>+</c>, <c>Contains</c>, <c>StartsWith</c> and
/// <c>==</c> on strings.
/// </summary>
internal static class TvGenerators
{
    private static readonly string[] ArithOps = ["+", "-", "*", "/", "%"];
    private static readonly string[] BitOps = ["&", "|", "^"];
    private static readonly string[] ShiftOps = ["<<", ">>"];
    private static readonly string[] StepOps = ["+", "-", "*", "&", "|", "^", "/", "%", "++", "--", "<<", ">>"];
    private static readonly string[] IntNarrow = ["byte", "sbyte", "short", "ushort", "char", "uint"];
    private static readonly string[] LongNarrow = ["byte", "sbyte", "short", "ushort", "char", "int", "uint", "ulong"];
    private static readonly string[] CmpOps = ["<", "<=", ">", ">=", "==", "!="];
    private static readonly string[] MinMax = ["Min", "Max"];
    private static readonly string[] LogicOps = ["&&", "||"];
    private static readonly string[] StrPreds = ["Contains", "StartsWith", "=="];

    // Two letters are enough to make prefixes and substrings that do and do not match. Plain ASCII letters also keep
    // culture-sensitive StartsWith equal to the ordinal prefix test.
    private static readonly string[] Words = ["", "a", "b", "ab", "ba", "aab"];

    /// <summary>What an expression may use.</summary>
    private sealed record Scope(TvType T, string[] Vars, bool Strings, bool Bits = false);

    /// <summary>Mostly small values, sometimes a value at or next to the edge of the type.</summary>
    public static IGen<long> Value(TvType t)
    {
        long min = t == TvType.Int ? int.MinValue : long.MinValue;
        long max = t == TvType.Int ? int.MaxValue : long.MaxValue;
        var edges = new[] { min, min + 1, -1L, 0L, 1L, max - 1, max };
        return Gen.Choose(
            (Gen.Int64().Between(-12, 12), 8),
            (Gen.Element(edges), 2));
    }

    public static IGen<TvInput> Input(TvType t, bool strings) =>
        from x in Value(t)
        from y in Value(t)
        from b in Gen.Boolean()
        from s in strings ? Gen.Element(Words) : Gen.Constant("")
        from u in strings ? Gen.Element(Words) : Gen.Constant("")
        select new TvInput(x, y, b, s, u);

    public static IGen<IReadOnlyList<TvInput>> Inputs(TvType t, int count, bool strings = false) =>
        Input(t, strings).ListOf().WithCount(count).Select(l => (IReadOnlyList<TvInput>)l.ToList());

    public static IGen<TvProgram> Program(TvType t, TvArith arithmetic, bool strings = false, bool bits = false) =>
        from count in Gen.Int32().Between(0, 3)
        from body in Body(new Scope(t, ["x", "y"], strings, bits), count, 0)
        from result in Expr(new Scope(t, AllVars(body), strings, bits))
        select new TvProgram(t, arithmetic, body, result, strings, bits);

    private static string[] AllVars(ImmutableArray<TvStmt> body) =>
        new[] { "x", "y" }.Concat(body.OfType<TvDecl>().Select(d => d.Name)).Concat(body.OfType<TvNarrowStep>().Select(d => d.Name)).ToArray();

    private static IGen<ImmutableArray<TvStmt>> Body(Scope scope, int remaining, int nextLocal)
    {
        if (remaining == 0)
        {
            return Gen.Constant(ImmutableArray<TvStmt>.Empty);
        }

        var name = $"v{nextLocal}";
        var locals = scope.Vars.Where(v => v.StartsWith('v')).ToArray();

        var decl =
            from init in Expr(scope)
            from rest in Body(scope with { Vars = scope.Vars.Append(name).ToArray() }, remaining - 1, nextLocal + 1)
            select rest.Insert(0, new TvDecl(name, init));

        var ifReturn =
            from c in Bool(scope, Expr(scope), depth: 1)
            from v in Expr(scope)
            from rest in Body(scope, remaining - 1, nextLocal)
            select rest.Insert(0, new TvIfReturn(c, v));

        var narrowStep =
            from narrow in Gen.Element(scope.T == TvType.Int ? IntNarrow : LongNarrow)
            from init in Expr(scope)
            from op in Gen.Element(StepOps)
            from operand in Expr(scope)
            from count in Gen.Int32().Between(0, 9)
            from rest in Body(scope with { Vars = scope.Vars.Append(name).ToArray() }, remaining - 1, nextLocal + 1)
            select rest.Insert(0, new TvNarrowStep(name, narrow, init, op, operand, count));

        if (locals.Length == 0)
        {
            return scope.Bits ? Gen.Choose((decl, 3), (ifReturn, 1), (narrowStep, 3)) : Gen.Choose((decl, 3), (ifReturn, 1));
        }

        var ifAssign =
            from target in Gen.Element(locals)
            from c in Bool(scope, Expr(scope), depth: 1)
            from a in Expr(scope)
            from e in Expr(scope)
            from rest in Body(scope, remaining - 1, nextLocal)
            select rest.Insert(0, new TvIfAssign(c, target, a, e));

        return scope.Bits
            ? Gen.Choose((decl, 2), (ifAssign, 2), (ifReturn, 1), (narrowStep, 3))
            : Gen.Choose((decl, 2), (ifAssign, 2), (ifReturn, 1));
    }

    private static IGen<TvExpr> Expr(Scope scope) =>
        Gen.Recursive(
            Leaf(scope),
            smaller =>
            {
                var plain = Gen.Choose(
                    (from op in Gen.Element(ArithOps)
                     from l in smaller
                     from r in smaller
                     select (TvExpr)new TvBin(op, l, r), 6),
                    (smaller.Select(e => (TvExpr)new TvNeg(e)), 1),
                    (from c in Bool(scope, smaller, depth: 1)
                     from a in smaller
                     from e in smaller
                     select (TvExpr)new TvCond(c, a, e), 1),
                    (from name in Gen.Element(MinMax)
                     from a in smaller
                     from e in smaller
                     select (TvExpr)new TvMath(name, a, e), 1),
                    (smaller.Select(e => (TvExpr)new TvMath("Abs", e, null)), 1));
                if (!scope.Bits)
                {
                    return plain;
                }

                var bits = Gen.Choose(
                    (from op in Gen.Element(BitOps)
                     from l in smaller
                     from r in smaller
                     select (TvExpr)new TvBin(op, l, r), 3),
                    (from op in Gen.Element(ShiftOps)
                     from l in smaller
                     from c in smaller
                     select (TvExpr)new TvShift(op, l, c), 3),
                    (smaller.Select(e => (TvExpr)new TvBitNot(e)), 2),
                    (from narrow in Gen.Element(scope.T == TvType.Int ? IntNarrow : LongNarrow)
                     from e in smaller
                     select (TvExpr)new TvCast(narrow, e), 3));
                return Gen.Choose((plain, 6), (bits, 5));
            },
            maxDepth: 3);

    private static IGen<TvExpr> Leaf(Scope scope)
    {
        var variable = Gen.Element(scope.Vars).Select(v => (TvExpr)new TvVar(v));
        var literal = Value(scope.T).Select(v => (TvExpr)new TvLit(v));
        return scope.Strings
            ? Gen.Choose((variable, 3), (literal, 1), (Str().Select(s => (TvExpr)new TvLength(s)), 2))
            : Gen.Choose((variable, 3), (literal, 1));
    }

    private static IGen<TvStr> Str() =>
        Gen.Recursive(
            Gen.Choose(
                (Gen.Element(new[] { "s", "t" }).Select(n => (TvStr)new TvStrVar(n)), 2),
                (Gen.Element(Words).Select(w => (TvStr)new TvStrLit(w)), 1)),
            smaller =>
                from l in smaller
                from r in smaller
                select (TvStr)new TvConcat(l, r),
            maxDepth: 2);

    private static IGen<TvBool> Bool(Scope scope, IGen<TvExpr> operand, int depth)
    {
        var cmp =
            from op in Gen.Element(CmpOps)
            from l in operand
            from r in operand
            select (TvBool)new TvCmp(op, l, r);
        var boolVar = Gen.Constant((TvBool)new TvBoolVar("b"));
        var leaf = scope.Strings
            ? Gen.Choose(
                (cmp, 3),
                (boolVar, 1),
                (from op in Gen.Element(StrPreds)
                 from l in Str()
                 from r in Str()
                 select (TvBool)new TvStrPred(op, l, r), 3))
            : Gen.Choose((cmp, 4), (boolVar, 1));
        if (depth == 0)
        {
            return leaf;
        }

        var inner = Bool(scope, operand, depth - 1);
        return Gen.Choose(
            (leaf, 3),
            (inner.Select(c => (TvBool)new TvNot(c)), 1),
            (from op in Gen.Element(LogicOps)
             from l in inner
             from r in inner
             select (TvBool)new TvLogic(op, l, r), 2));
    }
}
