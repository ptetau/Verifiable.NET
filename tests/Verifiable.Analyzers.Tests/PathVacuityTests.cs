using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// A callee guarantee that cannot hold where it is called makes every later goal on that path "proved".
/// VER105 reports it at the call. Dead code and unreachable calls stay quiet. When Z3 cannot decide a
/// premise check, VER106 says the proofs may be vacuous instead of staying silent.
/// </summary>
public class PathVacuityTests
{
    private const string Axioms = """
        using Verifiable.Contracts;

        public static class Ax
        {
            [PureAxiom]
            public static int Weird(int x)
            {
                Spec.Ensures<int>(r => r > x && r < x, "Impossible guarantee.");
                return x;
            }

            [PureAxiom]
            public static int Abs(int x)
            {
                Spec.Ensures<int>(r => r >= 0 && (r == x || r == -x), "Absolute values are never negative.");
                return x < 0 ? -x : x;
            }

            [PureAxiom]
            public static int Split(int x)
            {
                Spec.Ensures<int>(r => r > x, "Grows.");
                Spec.Ensures<int>(r => r < x, "Shrinks.");
                return x;
            }

            [PureAxiom]
            public static int WeirdWhenPositive(int x)
            {
                Spec.EnsuresWhen<int>(x > 0, r => r > x && r < x, "Impossible for positive inputs.");
                return x;
            }

            [PureAxiom]
            public static int Root(int x)
            {
                Spec.Requires(x >= 0, "Roots are taken of non-negative numbers.");
                Spec.Ensures<int>(r => r >= 0 && x >= 0, "Roots are non-negative.");
                return x;
            }

            [PureAxiom]
            public static int Hard(int x, int y, int z)
            {
                Spec.Ensures<int>(r => r == x && x * x * x + y * y * y + z * z * z == 42 && x > 1000, "A sum of cubes Z3 cannot settle quickly.");
                return x;
            }
        }

        """;

    private static async Task<Diagnostic[]> AnalyzeAsync(string source) =>
        (await AnalyzerTestHarness.AnalyzeAsync(Axioms + source, new VerifiableSmtAnalyzer()))
            .Where(d => d.Id.StartsWith("VER", System.StringComparison.Ordinal))
            .ToArray();

    private static int LineOf(Diagnostic d) => d.Location.GetLineSpan().StartLinePosition.Line;

    private static int LineContaining(string source, string text) =>
        (Axioms + source).Split('\n').Select((l, i) => (l, i)).Single(x => x.l.Contains(text)).i;

    [Fact]
    public async Task ImpossibleGuarantee_OnOneBranch_IsRejectedAtTheCall()
    {
        const string source = """
            [Verifiable]
            public static class Calc
            {
                public static int M(int a)
                {
                    Spec.Ensures<int>(r => r == 42, "Nonsense that only an impossible guarantee could prove.");
                    if (a > 0) return Ax.Weird(a);
                    return 42;
                }
            }
            """;
        var diags = await AnalyzeAsync(source);

        var vacuous = Assert.Single(diags, d => d.Id == "VER105");
        Assert.Equal(DiagnosticSeverity.Error, vacuous.Severity);
        Assert.Equal(LineContaining(source, "return Ax.Weird(a)"), LineOf(vacuous));
        var message = vacuous.GetMessage();
        Assert.Contains("Ax.Weird: Spec.Ensures<int>(r => r > x && r < x", message);
        Assert.Contains("Calc.M", message);
        Assert.Contains("Impossible guarantee.", message);
        Assert.DoesNotContain(diags, d => d.Id is "VER102" or "VER106");
    }

    [Fact]
    public async Task DefensiveDeadCode_AfterAGuarantee_IsNotReported()
    {
        var diags = await AnalyzeAsync("""
            [Verifiable]
            public static class Calc
            {
                public static int M(int x)
                {
                    Spec.Ensures<int>(r => r >= 0, "Never negative.");
                    if (x != 0)
                    {
                        var r = Ax.Abs(x);
                        if (r < 0) return -1; // dead, because Abs never returns a negative number
                        return r;
                    }
                    return 0;
                }
            }
            """);

        Assert.DoesNotContain(diags, d => d.Id is "VER105" or "VER102" or "VER106");
    }

    [Fact]
    public async Task UnreachableCall_IsNotReported()
    {
        var diags = await AnalyzeAsync("""
            [Verifiable]
            public static class Calc
            {
                public static int M(int a)
                {
                    Spec.Requires(a > 0, "Only positive inputs.");
                    Spec.Ensures<int>(r => r == 42, "Always 42.");
                    if (a < 0) return Ax.Weird(a); // dead code: the call is never reached
                    return 42;
                }
            }
            """);

        Assert.DoesNotContain(diags, d => d.Id is "VER105" or "VER102" or "VER106");
    }

    [Fact]
    public async Task GuaranteesThatClashWithEachOther_AreRejected_AndTheClashIsNamed()
    {
        const string source = """
            [Verifiable]
            public static class Calc
            {
                public static int M(int a)
                {
                    Spec.Ensures<int>(r => r == 42, "Nonsense.");
                    if (a > 0) return Ax.Split(a);
                    return 42;
                }
            }
            """;
        var diags = await AnalyzeAsync(source);

        var vacuous = Assert.Single(diags, d => d.Id == "VER105");
        Assert.Equal(LineContaining(source, "return Ax.Split(a)"), LineOf(vacuous));
        var message = vacuous.GetMessage();
        Assert.Contains("Guarantee Ax.Split: Spec.Ensures<int>(r => r < x", message);
        Assert.Contains("CalleeEnsures: Ax.Split: Spec.Ensures<int>(r => r > x", message);
    }

    [Fact]
    public async Task GuardedGuarantee_ImpossibleUnderItsGuard_IsRejected()
    {
        var diags = await AnalyzeAsync("""
            [Verifiable]
            public static class Calc
            {
                public static int M(int a)
                {
                    Spec.Ensures<int>(r => r == 42 || a <= 0, "Nonsense for positive inputs.");
                    return Ax.WeirdWhenPositive(a);
                }
            }
            """);

        Assert.Contains(diags, d => d.Id == "VER105" && d.GetMessage().Contains("Ax.WeirdWhenPositive"));
    }

    [Fact]
    public async Task BrokenCalleePrecondition_IsReportedOnce_NotAsVacuity()
    {
        var diags = await AnalyzeAsync("""
            [Verifiable]
            public static class Calc
            {
                public static int M(int a)
                {
                    Spec.Ensures<int>(r => r >= 0, "Never negative.");
                    if (a < 0) return Ax.Root(a); // the precondition fails; its guarantee does not apply here
                    return a;
                }
            }
            """);

        Assert.Contains(diags, d => d.Id == "VER102");
        Assert.DoesNotContain(diags, d => d.Id is "VER105" or "VER106");
    }

    [Fact]
    public async Task ClashWithAnEarlierCallOnTheBranch_IsRejectedAtTheLaterCall()
    {
        const string source = """
            [Verifiable]
            public static class Calc
            {
                public static int M(int a)
                {
                    Spec.Ensures<int>(r => r == 42, "Nonsense.");
                    if (a > 0)
                    {
                        var b = Ax.Abs(a);
                        return Ax.Weird(b) + 0;
                    }
                    return 42;
                }
            }
            """;
        var diags = await AnalyzeAsync(source);

        var vacuous = Assert.Single(diags, d => d.Id == "VER105");
        Assert.Equal(LineContaining(source, "return Ax.Weird(b)"), LineOf(vacuous));
        Assert.Contains("Ax.Weird", vacuous.GetMessage());
    }

    [Fact]
    public async Task ImpossibleReturnTypeOfACall_OnOneBranch_IsRejected()
    {
        // Type rules of call results are assumed on every path, so the whole-method check catches them.
        var diags = await AnalyzeAsync("""
            [TypeAlias<int>("Impossible on purpose.")]
            public readonly partial record struct Never
            {
                public static bool IsValid(int value) => value > 0 && value < 0;
            }

            public static class Src
            {
                [PureAxiom]
                public static Never Make(int x) => default;
            }

            [Verifiable]
            public static class Calc
            {
                public static int M(int a)
                {
                    Spec.Ensures<int>(r => r == 42, "Nonsense.");
                    if (a > 0) return Src.Make(a).Value;
                    return 42;
                }
            }
            """);

        Assert.Contains(diags, d => d.Id == "VER105" && d.GetMessage().Contains("Calc.M"));
        Assert.DoesNotContain(diags, d => d.Id == "VER102");
    }

    [Fact]
    public async Task UndecidedWholeMethodCheck_IsReportedAsAWarning()
    {
        var diags = await AnalyzeAsync("""
            [Verifiable]
            public static class Cubes
            {
                public static int M(int x, int y, int z)
                {
                    Spec.Requires(x * x * x + y * y * y + z * z * z == 42 && x > 1000, "A sum of cubes Z3 cannot settle quickly.");
                    Spec.Ensures<int>(r => r == x, "Returns x.");
                    return x;
                }
            }
            """);

        var undecided = Assert.Single(diags, d => d.Id == "VER106");
        Assert.Equal(DiagnosticSeverity.Warning, undecided.Severity);
        Assert.Contains("Cubes.M", undecided.GetMessage());
        Assert.Contains("may be vacuous", undecided.GetMessage());
        Assert.DoesNotContain(diags, d => d.Id is "VER105" or "VER102" or "VER103");
    }

    [Fact]
    public async Task UndecidedGuaranteeCheck_IsReportedAsAWarning()
    {
        var diags = await AnalyzeAsync("""
            [Verifiable]
            public static class Cubes
            {
                public static int M(int x, int y, int z)
                {
                    Spec.Ensures<int>(r => r >= x || x <= 0, "Never below x.");
                    if (x > 0) return Ax.Hard(x, y, z);
                    return x;
                }
            }
            """);

        var undecided = Assert.Single(diags, d => d.Id == "VER106");
        Assert.Contains("at the call on line", undecided.GetMessage());
        Assert.Contains("Ax.Hard", undecided.GetMessage());
        Assert.DoesNotContain(diags, d => d.Id is "VER105" or "VER102" or "VER103");
    }
}
