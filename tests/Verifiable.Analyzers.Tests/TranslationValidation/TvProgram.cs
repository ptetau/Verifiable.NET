using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Verifiable.Analyzers.Tests.TranslationValidation;

/// <summary>The integer type a generated method computes in.</summary>
public enum TvType
{
    Int,
    Long
}

/// <summary>An integer-valued expression of a generated method.</summary>
public abstract record TvExpr
{
    public abstract string Render(TvType t);
    public override string ToString() => Render(TvType.Long);
}

public sealed record TvLit(long Value) : TvExpr
{
    public override string Render(TvType t) => TvSyntax.Literal(Value, t);
}

public sealed record TvVar(string Name) : TvExpr
{
    public override string Render(TvType t) => Name;
}

public sealed record TvNeg(TvExpr Operand) : TvExpr
{
    public override string Render(TvType t) => $"(-{Operand.Render(t)})";
}

public sealed record TvBin(string Op, TvExpr Left, TvExpr Right) : TvExpr
{
    public override string Render(TvType t) => $"({Left.Render(t)} {Op} {Right.Render(t)})";
}

public sealed record TvCond(TvBool Condition, TvExpr Then, TvExpr Else) : TvExpr
{
    public override string Render(TvType t) => $"({Condition.Render(t)} ? {Then.Render(t)} : {Else.Render(t)})";
}

/// <summary>A call to <c>Math.Abs</c>, <c>Math.Min</c> or <c>Math.Max</c>.</summary>
public sealed record TvMath(string Name, TvExpr A, TvExpr? B) : TvExpr
{
    public override string Render(TvType t) =>
        B is null ? $"Math.{Name}({A.Render(t)})" : $"Math.{Name}({A.Render(t)}, {B.Render(t)})";
}

/// <summary>A string-valued expression: <c>s</c>, <c>t</c>, a literal, or a concatenation.</summary>
public abstract record TvStr
{
    public abstract string Render();
    public override string ToString() => Render();
}

public sealed record TvStrVar(string Name) : TvStr
{
    public override string Render() => Name;
}

public sealed record TvStrLit(string Value) : TvStr
{
    public override string Render() => $"\"{Value}\"";
}

public sealed record TvConcat(TvStr Left, TvStr Right) : TvStr
{
    public override string Render() => $"({Left.Render()} + {Right.Render()})";
}

/// <summary><c>str.Length</c>, an int.</summary>
public sealed record TvLength(TvStr Operand) : TvExpr
{
    public override string Render(TvType t) => $"{Operand.Render()}.Length";
}

/// <summary>A boolean expression of a generated method.</summary>
public abstract record TvBool
{
    public abstract string Render(TvType t);
    public override string ToString() => Render(TvType.Long);
}

public sealed record TvBoolVar(string Name) : TvBool
{
    public override string Render(TvType t) => Name;
}

public sealed record TvCmp(string Op, TvExpr Left, TvExpr Right) : TvBool
{
    public override string Render(TvType t) => $"({Left.Render(t)} {Op} {Right.Render(t)})";
}

public sealed record TvNot(TvBool Operand) : TvBool
{
    public override string Render(TvType t) => $"(!{Operand.Render(t)})";
}

public sealed record TvLogic(string Op, TvBool Left, TvBool Right) : TvBool
{
    public override string Render(TvType t) => $"({Left.Render(t)} {Op} {Right.Render(t)})";
}

/// <summary><c>a.Contains(b)</c>, <c>a.StartsWith(b)</c> or <c>a == b</c> on strings.</summary>
public sealed record TvStrPred(string Op, TvStr Left, TvStr Right) : TvBool
{
    public override string Render(TvType t) =>
        Op == "==" ? $"({Left.Render()} == {Right.Render()})" : $"{Left.Render()}.{Op}({Right.Render()})";
}

/// <summary>A statement of a generated method body.</summary>
public abstract record TvStmt
{
    public abstract string Render(TvType t);
    public override string ToString() => Render(TvType.Long);
}

/// <summary><c>T v = e;</c></summary>
public sealed record TvDecl(string Name, TvExpr Init) : TvStmt
{
    public override string Render(TvType t) => $"{TvSyntax.TypeName(t)} {Name} = {Init.Render(t)};";
}

/// <summary><c>if (c) { v = a; } else { v = b; }</c></summary>
public sealed record TvIfAssign(TvBool Condition, string Name, TvExpr Then, TvExpr Else) : TvStmt
{
    public override string Render(TvType t) =>
        $"if ({Condition.Render(t)}) {{ {Name} = {Then.Render(t)}; }} else {{ {Name} = {Else.Render(t)}; }}";
}

/// <summary><c>if (c) { return e; }</c></summary>
public sealed record TvIfReturn(TvBool Condition, TvExpr Value) : TvStmt
{
    public override string Render(TvType t) => $"if ({Condition.Render(t)}) {{ return {Value.Render(t)}; }}";
}

/// <summary>
/// A generated method <c>static T M(T x, T y, bool b)</c>, with <c>string s, string t</c> added when
/// <paramref name="Strings"/> is set: some statements, then a return.
/// </summary>
public sealed record TvProgram(
    TvType Type, bool CheckedArithmetic, ImmutableArray<TvStmt> Body, TvExpr Result, bool Strings = false)
{
    public string RenderBody(string indent)
    {
        var sb = new StringBuilder();
        foreach (var s in Body)
        {
            sb.Append(indent).AppendLine(s.Render(Type));
        }

        sb.Append(indent).Append("return ").Append(Result.Render(Type)).AppendLine(";");
        return sb.ToString();
    }

    public string Signature(string name) =>
        $"public static {TvSyntax.TypeName(Type)} {name}({TvSyntax.TypeName(Type)} x, {TvSyntax.TypeName(Type)} y, bool b" +
        (Strings ? ", string s, string t)" : ")");

    public bool Equals(TvProgram? other) =>
        other is not null && Type == other.Type && CheckedArithmetic == other.CheckedArithmetic &&
        Strings == other.Strings && Body.SequenceEqual(other.Body) && Result == other.Result;

    public override int GetHashCode() => HashCode.Combine(Type, CheckedArithmetic, Body.Length, Result);

    public override string ToString() =>
        $"{(CheckedArithmetic ? "[CheckedArithmetic] " : "")}{Signature("M")} {{ {RenderBody("").Replace(Environment.NewLine, " ").Trim()} }}";
}

/// <summary>One set of argument values for a generated method. <c>S</c> and <c>T</c> are used only by string programs.</summary>
public sealed record TvInput(long X, long Y, bool B, string S = "", string T = "")
{
    public object[] Box(TvProgram p)
    {
        // Box each value as its own type: a ternary here would widen the int to long.
        var x = p.Type == TvType.Int ? (object)checked((int)X) : X;
        var y = p.Type == TvType.Int ? (object)checked((int)Y) : Y;
        return p.Strings ? [x, y, B, S, T] : [x, y, B];
    }

    public string RenderPin(TvProgram p) =>
        $"x == {TvSyntax.Literal(X, p.Type)} && y == {TvSyntax.Literal(Y, p.Type)} && {(B ? "b" : "!b")}" +
        (p.Strings ? $" && s == \"{S}\" && t == \"{T}\"" : "");

    public override string ToString() => $"x={X}, y={Y}, b={(B ? "true" : "false")}, s=\"{S}\", t=\"{T}\"";
}

internal static class TvSyntax
{
    public static string TypeName(TvType t) => t == TvType.Int ? "int" : "long";

    /// <summary>A literal of type <paramref name="t"/>, in parentheses when negative.</summary>
    public static string Literal(long value, TvType t)
    {
        if (t == TvType.Int)
        {
            if (value == int.MinValue) return "int.MinValue";
            var s = ((int)value).ToString(CultureInfo.InvariantCulture);
            return value < 0 ? $"({s})" : s;
        }

        if (value == long.MinValue) return "long.MinValue";
        var l = value.ToString(CultureInfo.InvariantCulture) + "L";
        return value < 0 ? $"({l})" : l;
    }
}
