using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// Only a <c>Spec.Requires</c> / <c>Spec.RequiresWhen</c> in a method's leading contract block is a precondition.
/// Any other one is a VER101 error, and is neither assumed at entry nor required of callers.
/// </summary>
public class RequiresPlacementTests
{
    private static async Task<Diagnostic[]> AnalyzeAsync(string source) =>
        (await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableSmtAnalyzer()))
            .Where(d => d.Id.StartsWith("VER", System.StringComparison.Ordinal) || d.Id == "AD0001")
            .ToArray();

    private static Diagnostic[] Misplaced(Diagnostic[] diags) =>
        diags.Where(d => d.Id == "VER101" && Msg(d).Contains("is not a precondition")).ToArray();

    // Elm diagnostics wrap long lines; compare on single-spaced text.
    private static string Msg(Diagnostic d) => Regex.Replace(d.GetMessage(), @"\s+", " ");

    [Fact]
    public async Task RequiresInsideIf_IsAnError_AndDoesNotMakePremisesContradict()
    {
        var diags = await AnalyzeAsync("""
            using Verifiable.Contracts;

            [Verifiable]
            public static class Calc
            {
                public static int Clamp(int x)
                {
                    Spec.Requires(x >= 0, "Inputs are non-negative.");
                    Spec.Ensures<int>(r => r >= 0, "Clamped values are never negative.");
                    if (x < 0)
                    {
                        Spec.Requires(x < -5, "Dead path.");
                        return 0;
                    }
                    return x;
                }
            }
            """);

        var error = Assert.Single(Misplaced(diags));
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        var message = Msg(error);
        Assert.Contains("Calc.Clamp", message);
        Assert.Contains("inside an `if` statement", message);
        Assert.Contains("Spec.RequiresWhen(condition, requirement", message);
        Assert.Contains("x < -5", error.Location.SourceTree!.GetText().GetSubText(error.Location.SourceSpan).ToString());
        Assert.DoesNotContain(diags, d => d.Id is "VER102" or "VER105" or "AD0001");
    }

    [Fact]
    public async Task MisplacedRequires_IsNotAssumed_SoTheBugItHidIsReported()
    {
        // With the inner Requires assumed at entry, x > 0 everywhere and `return x` "proves" r >= 0.
        var diags = await AnalyzeAsync("""
            using Verifiable.Contracts;

            [Verifiable]
            public static class Calc
            {
                public static int Abs(int x)
                {
                    Spec.Ensures<int>(r => r >= 0, "Absolute values are never negative.");
                    if (x < 0)
                    {
                        Spec.Requires(x > 0, "Wrongly placed: this would silently rule out negative inputs.");
                        return x;
                    }
                    return x;
                }
            }
            """);

        Assert.Single(Misplaced(diags));
        Assert.Contains(diags, d => d.Id == "VER102" && Msg(d).Contains("Postcondition"));
        Assert.DoesNotContain(diags, d => d.Id is "VER105" or "AD0001");
    }

    [Fact]
    public async Task MisplacedRequires_IsNotRequiredOfCallers()
    {
        var diags = await AnalyzeAsync("""
            using Verifiable.Contracts;

            [Verifiable]
            public static class Calc
            {
                public static int Half(int x)
                {
                    Spec.Requires(x >= 0, "Halving is only defined here for non-negative inputs.");
                    var y = x / 2;
                    Spec.Requires(x < 100, "Comes after a statement, so it is not a precondition.");
                    return y;
                }

                public static int UseBig() => Half(500);

                public static int UseNegative() => Half(-1);
            }
            """);

        var error = Assert.Single(Misplaced(diags));
        Assert.Contains("comes after `var y = x / 2;`", Msg(error));
        var preconditionFailures = diags.Where(d => d.Id == "VER102" && Msg(d).Contains("Precondition")).ToArray();
        var failure = Assert.Single(preconditionFailures);
        Assert.Contains("x >= 0", Msg(failure));
        Assert.DoesNotContain(diags, d => d.Id == "AD0001");
    }

    [Fact]
    public async Task EveryNestedPlacement_IsAnError()
    {
        var diags = await AnalyzeAsync("""
            using System;
            using Verifiable.Contracts;

            [Verifiable]
            public static class Places
            {
                public static int InElse(int x)
                {
                    Spec.Requires(x >= 0, "Leading rule.");
                    if (x > 10)
                    {
                        return 1;
                    }
                    else
                    {
                        Spec.Requires(x <= 10, "In the else branch.");
                    }
                    return 0;
                }

                public static int InLoop(int n)
                {
                    Spec.Requires(n >= 0 && n <= 100, "Small inputs.");
                    var total = 0;
                    for (var i = 0; i < n; i++)
                    {
                        Spec.LoopInvariant(total == i && i <= n, "Counts each step.");
                        Spec.Decreases(n - i, "Moves toward n.");
                        Spec.Requires(i < 1000, "In a loop body.");
                        total = total + 1;
                    }
                    return total;
                }

                public static int InLambda(int x)
                {
                    Spec.Requires(x >= 0, "Leading rule.");
                    Func<int, int> f = y =>
                    {
                        Spec.Requires(y > 0, "In a lambda.");
                        return y;
                    };
                    return x;
                }

                public static int InLocalFunction(int x)
                {
                    Spec.Requires(x >= 0, "Leading rule.");
                    return x;

                    static int Local(int y)
                    {
                        Spec.Requires(y > 0, "In a local function.");
                        return y;
                    }
                }

                public static int AfterStatement(int x)
                {
                    var y = x + 1;
                    Spec.RequiresWhen(x > 0, y > 1, "After a statement.");
                    return y;
                }

                public static int InNestedBlock(int x)
                {
                    {
                        Spec.Requires(x >= 0, "In a bare block.");
                    }
                    return x;
                }
            }
            """);

        var messages = Misplaced(diags).Select(d => Msg(d)).ToArray();
        Assert.Equal(6, messages.Length);
        Assert.Contains(messages, m => m.Contains("Places.InElse") && m.Contains("the `else` branch of an `if` statement"));
        Assert.Contains(messages, m => m.Contains("Places.InLoop") && m.Contains("inside a loop"));
        Assert.Contains(messages, m => m.Contains("Places.InLambda") && m.Contains("inside a lambda"));
        Assert.Contains(messages, m => m.Contains("Places.InLocalFunction") && m.Contains("inside a local function"));
        Assert.Contains(messages, m => m.Contains("Places.AfterStatement") && m.Contains("Spec.RequiresWhen") && m.Contains("comes after `var y = x + 1;`"));
        Assert.Contains(messages, m => m.Contains("Places.InNestedBlock") && m.Contains("a nested `{ }` block"));
        Assert.DoesNotContain(diags, d => d.Id is "VER105" or "AD0001");
    }

    [Fact]
    public async Task LeadingBlock_InAnyOrder_IsAPrecondition()
    {
        var diags = await AnalyzeAsync("""
            using Verifiable.Contracts;

            [Verifiable]
            public static class Pricing
            {
                public static int Net(int price, int discount, bool member)
                {
                    Spec.Ensures<int>(r => r >= 0, "Prices never go negative.");
                    Spec.Requires(price >= 0, "Prices are non-negative.");
                    Spec.EnsuresWhen<int>(member, r => r <= price, "Members never pay more than the list price.");
                    Spec.RequiresWhen(member, discount >= 0 && discount <= price, "Member discounts stay within the price.");
                    Spec.RequiresWhen(!member, discount == 0, "Only members get a discount.");
                    return price - discount;
                }

                public static int Caller() => Net(10, 20, member: true);
            }
            """);

        Assert.Empty(Misplaced(diags));
        // The Requires after an Ensures still proves the method and still binds the caller.
        var failure = Assert.Single(diags, d => d.Id == "VER102");
        Assert.Contains("Precondition", Msg(failure));
        Assert.Contains("Pricing.Caller", Msg(failure));
        Assert.DoesNotContain(diags, d => d.Id is "VER101" or "VER105" or "AD0001");
    }

    [Fact]
    public async Task ExpressionBodiedMembers_AreFine()
    {
        var diags = await AnalyzeAsync("""
            using Verifiable.Contracts;

            [Verifiable]
            public static class Arrow
            {
                public static void Check(int x) => Spec.Requires(x >= 0, "Checks are only for non-negative inputs.");

                public static int Twice(int x) => unchecked(x * 2);

                public static int UseCheck()
                {
                    Check(-1);
                    return 0;
                }
            }
            """);

        Assert.Empty(Misplaced(diags));
        var failure = Assert.Single(diags, d => d.Id == "VER102");
        Assert.Contains("Arrow.UseCheck", Msg(failure));
        Assert.DoesNotContain(diags, d => d.Id is "VER101" or "VER105" or "AD0001");
    }

    [Fact]
    public async Task Constructors_FollowTheSameRule()
    {
        var diags = await AnalyzeAsync("""
            using Verifiable.Contracts;

            [Verifiable]
            public sealed class Range
            {
                public int Low { get; }
                public int High { get; }

                public Range(int low, int high)
                {
                    Spec.Requires(low <= high, "A range never runs backwards.");
                    Low = low;
                    High = high;
                }

                public Range(int width)
                    : this(0, width < 0 ? 0 : width)
                {
                    if (width > 1000)
                    {
                        Spec.Requires(width < 2000, "Inside an if in a constructor.");
                    }
                }
            }
            """);

        var error = Assert.Single(Misplaced(diags));
        Assert.Contains("inside an `if` statement", Msg(error));
        Assert.DoesNotContain(diags, d => d.Id is "VER102" or "VER105" or "AD0001");
    }

    [Fact]
    public async Task CrossAssembly_OnlyLeadingRequiresAreExported()
    {
        var libRef = AnalyzerTestHarness.CompileLibraryReference("""
            using Verifiable.Contracts;

            namespace PlacementLib;

            [Verifiable]
            public static class Halves
            {
                public static int Half(int x)
                {
                    Spec.Requires(x >= 0, "Halving needs a non-negative input.");
                    var y = x / 2;
                    Spec.Requires(x < 100, "Comes after a statement, so it is not a precondition.");
                    return y;
                }
            }
            """, "PlacementLib");

        const string caller = """
            using Verifiable.Contracts;

            namespace App;

            [Verifiable]
            public static class Use
            {
                public static int Big() => PlacementLib.Halves.Half(500);

                public static int Negative() => PlacementLib.Halves.Half(-1);
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeWithReferencesAsync(
            caller, [libRef], null, null, new VerifiableSmtAnalyzer());
        var failure = Assert.Single(diags, d => d.Id == "VER102");
        Assert.Contains("Halving needs a non-negative input.", Msg(failure));
        Assert.Contains("Use.Negative", Msg(failure));
        Assert.DoesNotContain(diags, d => d.Id is "VER101" or "VER104" or "AD0001");
    }

    [Fact]
    public async Task CandidateScope_IsNotChecked()
    {
        var diags = await AnalyzeAsync("""
            using Verifiable.Contracts;

            [VerifiableCandidate]
            public static class Legacy
            {
                public static int F(int x)
                {
                    var y = x;
                    Spec.Requires(y >= 0, "Not yet verified.");
                    return y;
                }
            }
            """);

        Assert.Empty(Misplaced(diags));
    }
}
