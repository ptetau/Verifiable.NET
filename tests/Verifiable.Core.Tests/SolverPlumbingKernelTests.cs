using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyCheck;
using Verifiable.Core;
using Verifiable.Core.Proofs;
using Xunit;

namespace Verifiable.Core.Tests;

/// <summary>
/// Checks of <see cref="SolverPlumbingKernel"/> that do not depend on Z3: an independent decoder reads every
/// key back into its parts, which shows directly that different queries get different keys.
/// </summary>
public class SolverPlumbingKernelTests
{
    /// <summary>Short strings that broke the old newline-separated key, plus surrogates and length-like text.</summary>
    private static readonly string[] HostileParts =
    [
        "", "a", "\n", "EVALS:", "\nEVALS:\nk=e", "k=e", "=", "CORE", "2500", "\0", "\0\0", "\0\u0001",
        "\ud800", "\udbff", "�", "\u0000\u0003abc", "(assert false)"
    ];

    private static string ReferenceField(string text) =>
        new string(new[] { (char)(text.Length >> 16), (char)(text.Length & 0xFFFF) }) + text;

    /// <summary>Reads one length-prefixed field at <paramref name="pos"/>; null if the text is malformed.</summary>
    private static string? ReadField(string key, ref int pos)
    {
        if (pos + 2 > key.Length) return null;
        var length = (key[pos] << 16) | key[pos + 1];
        if (pos + 2 + length > key.Length) return null;
        var text = key.Substring(pos + 2, length);
        pos += 2 + length;
        return text;
    }

    private static List<string>? DecodeFields(string key)
    {
        var fields = new List<string>();
        var pos = 0;
        while (pos < key.Length)
        {
            var field = ReadField(key, ref pos);
            if (field is null) return null;
            fields.Add(field);
        }

        return fields;
    }

    private static string EvalBlock(IEnumerable<(string Key, string Expr)> evals) =>
        string.Concat(evals.Select(e => SolverPlumbingKernel.CacheKeyEvalEntry(e.Key, e.Expr)));

    [Property(Iterations = 300)]
    public bool CacheKeyField_IsTheLengthAsTwoCodeUnitsThenTheText(string text)
    {
        text ??= string.Empty;
        return SolverPlumbingKernel.CacheKeyField(text) == ReferenceField(text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(55296)]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(65537)]
    [InlineData(200000)]
    public void CacheKeyField_LongTextsUseTheHighCodeUnit(int length)
    {
        var text = new string('x', length);
        var field = SolverPlumbingKernel.CacheKeyField(text);

        Assert.Equal(ReferenceField(text), field);
        Assert.Equal(length, (field[0] << 16) | field[1]);
    }

    [Property(Iterations = 500)]
    public Property CacheKeyMaterial_DecodesBackToItsParts() =>
        (from solver in Gen.String().WithLengthBetween(0, 12)
         from mode in Gen.Element(new[] { "", "CORE" })
         from timeout in Gen.Element(new[] { "2500", "5000", "1" })
         from script in Gen.String().WithLengthBetween(0, 40)
         from evals in Gen.String().WithLengthBetween(0, 6).Select(k => (k, k + "!")).ListOf().WithCountBetween(0, 4)
         select (solver, mode, timeout, script, evals)).ForAll(x =>
        {
            var key = SolverPlumbingKernel.CacheKeyMaterial(x.solver, x.mode, x.timeout, x.script, EvalBlock(x.evals));
            var fields = DecodeFields(key);
            if (fields is null || fields.Count != 5) return false;

            var evalFields = DecodeFields(fields[4]);
            var expectedEvals = x.evals.SelectMany(e => new[] { e.k, e.Item2 }).ToList();
            return fields[0] == x.solver && fields[1] == x.mode && fields[2] == x.timeout && fields[3] == x.script &&
                   evalFields is not null && evalFields.SequenceEqual(expectedEvals);
        });

    [Property(Iterations = 2000)]
    public Property CacheKeyMaterial_DifferentQueriesGetDifferentKeys() =>
        (from a in Gen.Element(HostileParts).ListOf().WithCount(5)
         from b in Gen.Element(HostileParts).ListOf().WithCount(5)
         from evalsA in Gen.Element(HostileParts).ListOf().WithCountBetween(0, 4)
         from evalsB in Gen.Element(HostileParts).ListOf().WithCountBetween(0, 4)
         select (a, b, evalsA, evalsB)).ForAll(x =>
        {
            // Pair up eval keys and expressions from the list, so lists of different lengths are compared too.
            static string Evals(IReadOnlyList<string> parts) =>
                EvalBlock(Enumerable.Range(0, parts.Count / 2).Select(i => (parts[2 * i], parts[2 * i + 1])));

            var keyA = SolverPlumbingKernel.CacheKeyMaterial(x.a[0], x.a[1], x.a[2], x.a[3], Evals(x.evalsA));
            var keyB = SolverPlumbingKernel.CacheKeyMaterial(x.b[0], x.b[1], x.b[2], x.b[3], Evals(x.evalsB));
            var sameInput = x.a.Take(4).SequenceEqual(x.b.Take(4)) &&
                            x.evalsA.Take(x.evalsA.Count / 2 * 2).SequenceEqual(x.evalsB.Take(x.evalsB.Count / 2 * 2));
            return (keyA == keyB) == sameInput;
        });

    [Property(Iterations = 300)]
    public Property Lemmas_HoldOnSampledInputs() =>
        (from a in Gen.Element(HostileParts)
         from b in Gen.Element(HostileParts)
         from restA in Gen.Element(HostileParts)
         from restB in Gen.Element(HostileParts)
         select (a, b, restA, restB)).ForAll(x =>
            CacheKeyLaws.FieldThenRestDecodesUniquely(x.a, x.restA, x.b, x.restB) &&
            CacheKeyLaws.FieldThenRestDecodesUniquely(x.a, x.restA, x.a, x.restA) &&
            CacheKeyLaws.EvalEntryThenRestDecodesUniquely(x.a, x.b, x.restA, x.b, x.a, x.restB) &&
            CacheKeyLaws.CacheKeyIsInjective(x.a, x.b, x.restA, x.restB, x.a, x.a, x.b, x.restA, x.restB, x.b));

    [Property(Iterations = 300)]
    public bool NamedAssertion_IsExactlyTheLabelledFormula(string formula, string name)
    {
        formula ??= string.Empty;
        name ??= string.Empty;
        return SolverPlumbingKernel.NamedAssertion(formula, name) == "(assert (! " + formula + " :named " + name + "))\n";
    }
}
