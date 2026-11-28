using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Verifiable.Analyzers.Smt;

namespace Verifiable.Analyzers.Tests.TranslationValidation;

/// <summary>What one run of a generated method did.</summary>
public sealed record TvOutcome(long? Value, string? Exception)
{
    public bool Returned => Exception is null;
    public override string ToString() => Returned ? $"returned {Value}" : $"threw {Exception}";
}

/// <summary>A VER102 the analyzer reported, read from its agent JSON block.</summary>
public sealed record TvRefutation(string Method, string Kind, string? Reason);

/// <summary>
/// The differential oracle. It runs a generated method on the CLR to get the real answer, then asks the analyzer to
/// verify the same method with the inputs pinned by <c>Spec.Requires</c>, and compares the two.
/// </summary>
internal static class TvOracle
{
    public const string PinnedResult = "pinned result";
    public const string WrongResult = "wrong result";

    private static readonly Lazy<ImmutableArray<MetadataReference>> ExecReferences = new(() =>
    {
        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        return tpa.Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is "System.Runtime.dll" or "System.Private.CoreLib.dll" or "netstandard.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToImmutableArray();
    });

    /// <summary>
    /// Compiles the method twice into one assembly, once as written (unchecked, like a default C# build) and once inside
    /// a <c>checked</c> block, and runs both on each input. Returns null when the method does not compile (for example
    /// a constant that overflows or a constant division by zero, which the C# compiler rejects).
    /// </summary>
    public static (TvOutcome Unchecked, TvOutcome Checked)[]? Execute(TvProgram program, IReadOnlyList<TvInput> inputs)
    {
        var source = $$"""
            using System;
            namespace TvExec;
            public static class U
            {
                {{program.Signature("M")}}
                {
            {{program.RenderBody("        ")}}
                }
            }
            public static class C
            {
                {{program.Signature("M")}}
                {
                    checked
                    {
            {{program.RenderBody("            ")}}
                    }
                }
            }
            """;

        var compilation = CSharpCompilation.Create(
            "TvExec" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source)],
            ExecReferences.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        if (!emit.Success)
        {
            return null;
        }

        var context = new AssemblyLoadContext("tv", isCollectible: true);
        try
        {
            ms.Position = 0;
            var asm = context.LoadFromStream(ms);
            var u = asm.GetType("TvExec.U")!.GetMethod("M")!;
            var c = asm.GetType("TvExec.C")!.GetMethod("M")!;
            return inputs.Select(i => (Run(u, program, i), Run(c, program, i))).ToArray();
        }
        finally
        {
            context.Unload();
        }
    }

    private static TvOutcome Run(MethodInfo m, TvProgram p, TvInput input)
    {
        try
        {
            var r = m.Invoke(null, input.Box(p));
            return new TvOutcome(Convert.ToInt64(r, System.Globalization.CultureInfo.InvariantCulture), null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is DivideByZeroException)
        {
            return new TvOutcome(null, "DivideByZeroException");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is OverflowException)
        {
            return new TvOutcome(null, "OverflowException");
        }
    }

    /// <summary>One pinned copy of the method to verify, with what the CLR did on its inputs.</summary>
    public sealed record Case(TvInput Input, TvOutcome Unchecked, TvOutcome Checked);

    /// <summary>The verification source: one method per case, inputs pinned, result claimed and denied.</summary>
    public static string BuildVerificationSource(TvProgram program, IReadOnlyList<Case> cases)
    {
        var t = program.Type;
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using Verifiable.Contracts;");
        sb.AppendLine("namespace TvCheck;");
        sb.AppendLine("[Verifiable]");
        if (program.CheckedArithmetic)
        {
            sb.AppendLine("[CheckedArithmetic]");
        }

        sb.AppendLine("public static class V");
        sb.AppendLine("{");
        for (var i = 0; i < cases.Count; i++)
        {
            var c = cases[i];
            sb.Append("    ").AppendLine(program.Signature($"M{i}"));
            sb.AppendLine("    {");
            sb.Append("        Spec.Requires(").Append(c.Input.RenderPin(program)).AppendLine(", \"pinned inputs\");");
            if (c.Unchecked.Returned)
            {
                var lit = TvSyntax.Literal(c.Unchecked.Value!.Value, t);
                sb.Append("        Spec.Ensures<").Append(TvSyntax.TypeName(t)).Append(">(r => r == ").Append(lit)
                    .Append(", \"").Append(PinnedResult).AppendLine("\");");
                sb.Append("        Spec.Ensures<").Append(TvSyntax.TypeName(t)).Append(">(r => r != ").Append(lit)
                    .Append(", \"").Append(WrongResult).AppendLine("\");");
            }

            sb.Append(program.RenderBody("        "));
            sb.AppendLine("    }");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static readonly Dictionary<string, string> AgentProps = new() { ["build_property.VerifiableAgent"] = "1" };

    private static readonly Regex AgentJson = new(
        "<verifiable-counterexample>(?<json>\\{.*\\})</verifiable-counterexample>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Runs the analyzer on the verification source. Returns the VER102s, and a list of problems that are not
    /// verdicts at all (analyzer crash, unsupported construct, undecided or contradictory premises, compile errors).
    /// </summary>
    public static (List<TvRefutation> Refutations, List<string> Problems) Analyze(string source)
    {
        var diags = AnalyzerTestHarness.AnalyzeWithOptionsAsync(source, AgentProps, new VerifiableSmtAnalyzer())
            .GetAwaiter().GetResult();
        var refutations = new List<TvRefutation>();
        var problems = new List<string>();
        foreach (var d in diags)
        {
            if (d.Id == "VER102")
            {
                var m = AgentJson.Match(d.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
                if (!m.Success)
                {
                    problems.Add("VER102 without an agent JSON block: " + FirstLine(d));
                    continue;
                }

                using var doc = JsonDocument.Parse(m.Groups["json"].Value);
                var root = doc.RootElement;
                var symbol = root.GetProperty("symbol").GetString() ?? "";
                var kind = root.GetProperty("kind").GetString() ?? "";
                var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;
                refutations.Add(new TvRefutation(symbol.Split('.').Last(), kind, reason));
            }
            else if (d.Severity == DiagnosticSeverity.Error || d.Id is "AD0001" or "VER101" or "VER103" or "VER104" or "VER105")
            {
                problems.Add($"{d.Id}: {FirstLine(d)}");
            }
        }

        return (refutations, problems);
    }

    private static string FirstLine(Diagnostic d)
    {
        var msg = d.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        var nl = msg.IndexOf('\n');
        return nl < 0 ? msg : msg[..nl];
    }

    /// <summary>
    /// The whole differential check for one program. Returns null when the analyzer and the CLR agree, else a
    /// description of each disagreement. <paramref name="gaps"/> counts inputs that leave the analyzer's model on
    /// purpose (an overflow without <c>[CheckedArithmetic]</c>, where the analyzer uses unbounded integers).
    /// </summary>
    public static string? Check(TvProgram program, IReadOnlyList<TvInput> inputs, out int gaps)
    {
        gaps = 0;
        var runs = Execute(program, inputs);
        if (runs is null)
        {
            return null;
        }

        var cases = new List<Case>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var (u, c) = runs[i];
            if (!program.CheckedArithmetic && c.Exception == "OverflowException")
            {
                gaps++;
                continue;
            }

            cases.Add(new Case(inputs[i], u, c));
        }

        if (cases.Count == 0)
        {
            return null;
        }

        var source = BuildVerificationSource(program, cases);
        var (refutations, problems) = Analyze(source);
        var errors = new List<string>(problems);
        for (var i = 0; i < cases.Count; i++)
        {
            var c = cases[i];
            var mine = refutations.Where(r => r.Method == $"M{i}").ToList();
            var expectDbz = c.Unchecked.Exception == "DivideByZeroException";
            var expectOverflow = program.CheckedArithmetic && c.Checked.Exception == "OverflowException";
            var where = $"M{i} ({c.Input}; CLR unchecked {c.Unchecked}, checked {c.Checked})";

            if (c.Unchecked.Returned)
            {
                if (mine.Any(r => r.Kind == "Postcondition" && r.Reason == PinnedResult))
                    errors.Add($"{where}: the analyzer refuted r == {c.Unchecked.Value}, which the CLR returned.");
                if (!mine.Any(r => r.Kind == "Postcondition" && r.Reason == WrongResult))
                    errors.Add($"{where}: the analyzer did not refute r != {c.Unchecked.Value}, but the CLR returned it.");
            }
            else if (c.Unchecked.Exception is not ("DivideByZeroException" or "OverflowException"))
            {
                errors.Add($"{where}: unexpected exception.");
            }

            var dbz = mine.Any(r => r.Kind == "DivisionByZero");
            var overflow = mine.Any(r => r.Kind == "Overflow");
            if (expectDbz && !dbz)
                errors.Add($"{where}: the CLR divided by zero, but the analyzer did not refute a DivisionByZero obligation.");
            if (expectOverflow && !overflow)
                errors.Add($"{where}: checked execution overflowed, but the analyzer did not refute an Overflow obligation.");

            // After a throw the rest of the method never runs, but the analyzer still checks it: it does not assume
            // that a refuted obligation held. So extra refutations are only wrong when the method returned.
            if (c.Unchecked.Returned && dbz)
                errors.Add($"{where}: the analyzer refuted a DivisionByZero obligation, but no divisor was zero.");
            if (c.Unchecked.Returned && overflow && !expectOverflow)
                errors.Add($"{where}: the analyzer refuted an Overflow obligation, but checked execution did not overflow.");

            foreach (var other in mine.Where(r => r.Kind is not ("Postcondition" or "DivisionByZero" or "Overflow")))
                errors.Add($"{where}: unexpected refuted {other.Kind} obligation.");
        }

        return errors.Count == 0
            ? null
            : string.Join(Environment.NewLine, errors) + Environment.NewLine + "Verification source:" + Environment.NewLine + source;
    }
}
