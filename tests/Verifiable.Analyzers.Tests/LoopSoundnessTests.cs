using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// Loops keep every iteration, and loops and recursion must end. Each rule has a program that must be rejected and a
/// close variant that must be accepted, so neither side can be bought by rejecting (or accepting) everything.
/// </summary>
public class LoopSoundnessTests
{
    private static async Task<Diagnostic[]> AnalyzeAsync(string methods)
    {
        var source = $$"""
            using System.Collections.Immutable;
            using System.Linq;
            using Verifiable.Contracts;

            [Verifiable]
            public static class Subject
            {
                {{methods}}
            }
            """;
        return (await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableSmtAnalyzer()))
            .Where(d => d.Id.StartsWith("VER", StringComparison.Ordinal) || d.Id == "AD0001" || d.Severity == DiagnosticSeverity.Error)
            .ToArray();
    }

    // Elm diagnostics wrap long lines; compare on single-spaced text.
    private static string Msg(Diagnostic d) => Regex.Replace(d.GetMessage(), @"\s+", " ");

    private static void AssertAccepted(Diagnostic[] diags) =>
        Assert.True(diags.Length == 0, "Expected no errors, got:\n" + string.Join("\n", diags.Select(d => $"{d.Id}: {Msg(d)}")));

    private static void AssertRefuted(Diagnostic[] diags, string expectedId = "VER102") =>
        Assert.True(diags.Any(d => d.Id == expectedId), $"Expected {expectedId}, got:\n" + string.Join("\n", diags.Select(d => $"{d.Id}: {Msg(d)}")));

    // ---- a loop nested in another loop's body keeps its iterations ----

    [Fact]
    public async Task NestedLoop_CountsEveryInnerIteration()
    {
        AssertAccepted(await AnalyzeAsync("""
            public static int Twice(int n)
            {
                Spec.Requires(n >= 0 && n <= 5, "Small inputs.");
                Spec.Ensures<int>(r => r == 2 * n, "Counted twice.");
                var c = 0;
                var k = 0;
                while (k < 2)
                {
                    Spec.LoopInvariant(k >= 0 && k <= 2 && c == (k == 0 ? 0 : (k == 1 ? n : 2 * n)), "n per pass.");
                    Spec.Decreases(2 - k, "k moves toward two.");
                    var start = c;
                    var j = 0;
                    while (j < n)
                    {
                        Spec.LoopInvariant(j >= 0 && j <= n && c == start + j, "j counted so far.");
                        Spec.Decreases(n - j, "j moves toward n.");
                        c = c + 1;
                        j = j + 1;
                    }
                    k = k + 1;
                }
                return c;
            }
            """));
    }

    [Fact]
    public async Task NestedLoop_InnerIterationsAreNotDropped()
    {
        // The inner loop adds n on every pass, so the total is not zero.
        AssertRefuted(await AnalyzeAsync("""
            public static int Count(int n)
            {
                Spec.Requires(n >= 0 && n <= 5, "Small inputs.");
                Spec.Ensures<int>(r => r == 0, "Nothing is counted.");
                var c = 0;
                for (var i = 0; i < 1; i++)
                {
                    Spec.Decreases(1 - i, "i moves toward one.");
                    var j = 0;
                    while (j < n)
                    {
                        Spec.Decreases(n - j, "j moves toward n.");
                        c = c + 1;
                        j = j + 1;
                    }
                }
                return c;
            }
            """));
    }

    [Fact]
    public async Task NestedLoop_InnerInvariantIsCheckedOnItsBackEdge()
    {
        AssertRefuted(await AnalyzeAsync("""
            public static int Run(int n)
            {
                Spec.Requires(n >= 1 && n <= 5, "Small inputs.");
                Spec.Ensures<int>(r => r >= 0, "Non-negative.");
                var c = 0;
                for (var i = 0; i < 2; i++)
                {
                    Spec.Decreases(2 - i, "i moves toward two.");
                    var j = 0;
                    while (j < n)
                    {
                        Spec.LoopInvariant(j <= 1, "j stays small.");
                        Spec.Decreases(n - j, "j moves toward n.");
                        j = j + 1;
                    }
                    c = c + j;
                }
                return c;
            }
            """));
    }

    [Fact]
    public async Task NestedLoop_ForeachInsideForKeepsItsItems()
    {
        AssertAccepted(await AnalyzeAsync("""
            public static int Total(ImmutableArray<int> xs)
            {
                Spec.Requires(!xs.IsDefault && xs.Length <= 10 && xs.All(v => v >= 0 && v <= 10), "Small items.");
                Spec.Ensures<int>(r => r >= 0 && r <= 10, "Between zero and ten.");
                var last = 0;
                for (var i = 0; i < 2; i++)
                {
                    Spec.LoopInvariant(last >= 0 && last <= 10, "In range.");
                    Spec.Decreases(2 - i, "i moves toward two.");
                    foreach (var v in xs)
                    {
                        Spec.LoopInvariant(last >= 0 && last <= 10, "In range.");
                        last = v;
                    }
                }
                return last;
            }
            """));
    }

    [Fact]
    public async Task NestedLoop_ThreeLevelsAndSequentialInnerLoops()
    {
        AssertAccepted(await AnalyzeAsync("""
            public static int Run(int n)
            {
                Spec.Requires(n >= 0 && n <= 3, "Small inputs.");
                Spec.Ensures<int>(r => r >= 0 && r <= 3, "In range.");
                var c = 0;
                var i = 0;
                while (i < n)
                {
                    Spec.LoopInvariant(i >= 0 && i <= n && c == 0, "Nothing counted at this level.");
                    Spec.Decreases(n - i, "i moves toward n.");
                    var j = 0;
                    while (j < n)
                    {
                        Spec.LoopInvariant(j >= 0 && j <= n && c == 0, "Nothing counted.");
                        Spec.Decreases(n - j, "j moves toward n.");
                        var k = 0;
                        while (k < n)
                        {
                            Spec.LoopInvariant(k >= 0 && k <= n && c == 0, "Nothing counted.");
                            Spec.Decreases(n - k, "k moves toward n.");
                            k = k + 1;
                        }
                        j = j + 1;
                    }
                    var m = 0;
                    while (m < n)
                    {
                        Spec.LoopInvariant(m >= 0 && m <= n && c == 0, "Nothing counted.");
                        Spec.Decreases(n - m, "m moves toward n.");
                        m = m + 1;
                    }
                    i = i + 1;
                }
                return c + i;
            }
            """));
    }

    [Fact]
    public async Task NestedLoop_SecondInnerLoopSeesTheFirstOnesWork()
    {
        AssertRefuted(await AnalyzeAsync("""
            public static int Run(int n)
            {
                Spec.Requires(n >= 1 && n <= 3, "Small inputs.");
                Spec.Ensures<int>(r => r == 0, "Nothing is counted.");
                var c = 0;
                var i = 0;
                while (i < 1)
                {
                    Spec.Decreases(1 - i, "i moves toward one.");
                    var j = 0;
                    while (j < n)
                    {
                        Spec.Decreases(n - j, "j moves toward n.");
                        c = c + 1;
                        j = j + 1;
                    }
                    var m = 0;
                    while (m < 1)
                    {
                        Spec.Decreases(1 - m, "m moves toward one.");
                        m = m + 1;
                    }
                    i = i + 1;
                }
                return c;
            }
            """));
    }

    // ---- variables written in a deconstruction, or in a condition, are havocked ----

    [Fact]
    public async Task Deconstruction_InLoopChangesTheVariables()
    {
        AssertRefuted(await AnalyzeAsync("""
            public static int Run(int n)
            {
                Spec.Requires(n >= 0 && n <= 5, "Small inputs.");
                Spec.Ensures<int>(r => r == 0, "a is never changed.");
                var a = 0;
                var b = 0;
                for (var i = 0; i < n; i++)
                {
                    Spec.Decreases(n - i, "i moves toward n.");
                    (a, b) = (a + 1, b);
                }
                return a;
            }
            """));
    }

    [Fact]
    public async Task Deconstruction_InLoopIsTrackedByTheInvariant()
    {
        AssertAccepted(await AnalyzeAsync("""
            public static int Run(int n)
            {
                Spec.Requires(n >= 0 && n <= 5, "Small inputs.");
                Spec.Ensures<int>(r => r == n, "a counts the passes.");
                var a = 0;
                var b = 0;
                for (var i = 0; i < n; i++)
                {
                    Spec.LoopInvariant(a == i && i <= n, "a counts the passes.");
                    Spec.Decreases(n - i, "i moves toward n.");
                    (a, b) = (a + 1, b);
                }
                return a;
            }
            """));
    }

    [Fact]
    public async Task AssignmentInWhileCondition_IsHavocked()
    {
        AssertRefuted(await AnalyzeAsync("""
            public static int Run(int n)
            {
                Spec.Requires(n >= 1 && n <= 5, "Small inputs.");
                Spec.Ensures<int>(r => r == 1, "The loop stops after one bump.");
                var i = 0;
                while ((i = i + 1) < n)
                {
                    Spec.Decreases(n - i, "i moves toward n.");
                }
                return i;
            }
            """));
    }

    [Fact]
    public async Task AssignmentInWhileCondition_ProvesWhatItDoes()
    {
        AssertAccepted(await AnalyzeAsync("""
            public static int Run(int n)
            {
                Spec.Requires(n >= 1 && n <= 5, "Small inputs.");
                Spec.Ensures<int>(r => r == n, "The loop runs until i reaches n.");
                var i = 0;
                while ((i = i + 1) < n)
                {
                    Spec.LoopInvariant(i >= 0 && i < n, "i stays below n.");
                    Spec.Decreases(n - i, "i moves toward n.");
                }
                return i;
            }
            """));
    }

    [Fact]
    public async Task AssignmentInIfConditionInLoopBody_IsHavocked()
    {
        AssertRefuted(await AnalyzeAsync("""
            public static int Run(int n)
            {
                Spec.Requires(n >= 0 && n <= 5, "Small inputs.");
                Spec.Ensures<int>(r => r == 0, "k is never bumped.");
                var k = 0;
                for (var i = 0; i < n; i++)
                {
                    Spec.Decreases(n - i, "i moves toward n.");
                    if ((k = k + 1) > 100)
                    {
                        return 0;
                    }
                }
                return k;
            }
            """));
    }

    [Fact]
    public async Task AssignmentInIfConditionInLoopBody_ProvesWhatItDoes()
    {
        AssertAccepted(await AnalyzeAsync("""
            public static int Run(int n)
            {
                Spec.Requires(n >= 0 && n <= 5, "Small inputs.");
                Spec.Ensures<int>(r => r == n, "k counts the passes.");
                var k = 0;
                for (var i = 0; i < n; i++)
                {
                    Spec.LoopInvariant(k == i && i <= n, "k counts the passes.");
                    Spec.Decreases(n - i, "i moves toward n.");
                    if ((k = k + 1) > 100)
                    {
                        return 0;
                    }
                }
                return k;
            }
            """));
    }

    // ---- a do-while whose body is one block checks its invariant and measure ----

    [Fact]
    public async Task DoWhileSingleBlock_InvariantIsCheckedAfterAnIteration()
    {
        AssertRefuted(await AnalyzeAsync("""
            public static int Run(int n)
            {
                Spec.Requires(n >= 1 && n <= 5, "Small inputs.");
                Spec.Ensures<int>(r => r <= 3, "Stops by three.");
                var i = 0;
                do
                {
                    Spec.Decreases(5 - i, "i moves toward five.");
                    Spec.LoopInvariant(i <= 2, "i stays small.");
                    i = i + 1;
                }
                while (i < n);
                return i;
            }
            """));
    }

    [Fact]
    public async Task DoWhileSingleBlock_ProvesWhenInductive()
    {
        AssertAccepted(await AnalyzeAsync("""
            public static int Run(int n)
            {
                Spec.Requires(n >= 1 && n <= 5, "Small inputs.");
                Spec.Ensures<int>(r => r == n, "Stops at n.");
                var i = 0;
                do
                {
                    Spec.Decreases(n - i, "i moves toward n.");
                    Spec.LoopInvariant(i < n, "i stays below n.");
                    i = i + 1;
                }
                while (i < n);
                return i;
            }
            """));
    }

    [Fact]
    public async Task DoWhileSingleBlock_MeasureMustGoDown()
    {
        AssertRefuted(await AnalyzeAsync("""
            public static int Run(int n)
            {
                Spec.Requires(n >= 1 && n <= 5, "Small inputs.");
                var i = 0;
                do
                {
                    Spec.Decreases(5 - i, "i moves toward five.");
                    i = i + 0;
                }
                while (i < n);
                return i;
            }
            """));
    }

    // ---- loop contracts are the first statements of the loop body ----

    [Fact]
    public async Task LoopInvariant_AfterABodyStatement_IsRejected()
    {
        var diags = await AnalyzeAsync("""
            public static int Run(int x0)
            {
                Spec.Requires(x0 >= 0 && x0 <= 2, "Small inputs.");
                Spec.Ensures<int>(r => r == 3, "Counts up to three.");
                var x = x0;
                while (x < 3)
                {
                    Spec.Decreases(3 - x, "x moves toward three.");
                    x = x + 100;
                    Spec.LoopInvariant(x <= 3, "x never passes three.");
                    x = x - 99;
                }
                return x;
            }
            """);
        AssertRefuted(diags, "VER101");
        Assert.Contains(diags, d => Msg(d).Contains("not at the top of its loop body"));
    }

    [Fact]
    public async Task LoopInvariant_AtTheTopOfTheBody_IsAccepted()
    {
        AssertAccepted(await AnalyzeAsync("""
            public static int Run(int x0)
            {
                Spec.Requires(x0 >= 0 && x0 <= 2, "Small inputs.");
                Spec.Ensures<int>(r => r == 3, "Counts up to three.");
                var x = x0;
                while (x < 3)
                {
                    Spec.LoopInvariant(x >= 0 && x <= 3, "x stays in range.");
                    Spec.Decreases(3 - x, "x moves toward three.");
                    x = x + 100;
                    x = x - 99;
                }
                return x;
            }
            """));
    }

    [Fact]
    public async Task Decreases_NestedInAnIf_IsRejected()
    {
        var diags = await AnalyzeAsync("""
            public static int Run(int n)
            {
                Spec.Requires(n >= 0 && n <= 5, "Small inputs.");
                var i = 0;
                while (i < n)
                {
                    Spec.Decreases(n - i, "i moves toward n.");
                    if (i == 0)
                    {
                        Spec.LoopInvariant(i == 0, "Only at the start.");
                    }
                    i = i + 1;
                }
                return i;
            }
            """);
        AssertRefuted(diags, "VER101");
    }

    // ---- while, do and for loops must carry a Decreases ----

    [Fact]
    public async Task WhileWithoutDecreases_IsRejected()
    {
        var diags = await AnalyzeAsync("""
            public static int Run(int x)
            {
                Spec.Requires(x >= 0, "Non-negative input.");
                Spec.Ensures<int>(r => r == -1, "Returns minus one.");
                var i = x;
                while (i >= 0)
                {
                    Spec.LoopInvariant(i >= 0, "i stays non-negative.");
                    i = i + 1 - 1;
                }
                return i;
            }
            """);
        AssertRefuted(diags, "VER101");
        Assert.Contains(diags, d => Msg(d).Contains("has no `Spec.Decreases(...)`"));
    }

    [Fact]
    public async Task ForWithoutDecreases_IsRejected_AndWithDecreasesIsAccepted()
    {
        AssertRefuted(await AnalyzeAsync("""
            public static int Run(int n)
            {
                Spec.Requires(n >= 0 && n <= 5, "Small inputs.");
                Spec.Ensures<int>(r => r == n, "Counts to n.");
                var c = 0;
                for (var i = 0; i < n; i++)
                {
                    Spec.LoopInvariant(c == i && i <= n, "c counts the passes.");
                    c = c + 1;
                }
                return c;
            }
            """), "VER101");

        AssertAccepted(await AnalyzeAsync("""
            public static int Run(int n)
            {
                Spec.Requires(n >= 0 && n <= 5, "Small inputs.");
                Spec.Ensures<int>(r => r == n, "Counts to n.");
                var c = 0;
                for (var i = 0; i < n; i++)
                {
                    Spec.LoopInvariant(c == i && i <= n, "c counts the passes.");
                    Spec.Decreases(n - i, "i moves toward n.");
                    c = c + 1;
                }
                return c;
            }
            """));
    }

    [Fact]
    public async Task DoWhileWithoutDecreases_IsRejected()
    {
        AssertRefuted(await AnalyzeAsync("""
            public static int Run(int n)
            {
                Spec.Requires(n >= 1 && n <= 5, "Small inputs.");
                var i = 0;
                do
                {
                    Spec.LoopInvariant(i >= 0, "i is not negative.");
                    i = i + 1;
                }
                while (i < n);
                return i;
            }
            """), "VER101");
    }

    [Fact]
    public async Task ForeachNeedsNoDecreases()
    {
        AssertAccepted(await AnalyzeAsync("""
            public static int Count(ImmutableArray<int> xs)
            {
                Spec.Requires(!xs.IsDefault && xs.Length <= 20, "Small input.");
                Spec.Ensures<int>(r => r >= 0 && r <= 1, "Zero or one.");
                var c = 0;
                foreach (var v in xs)
                {
                    Spec.LoopInvariant(c >= 0 && c <= 1, "Zero or one.");
                    c = 1;
                }
                return c;
            }
            """));
    }

    // ---- recursion needs a measure that goes down ----

    [Fact]
    public async Task SelfRecursionWithoutDecreases_IsRejected()
    {
        var diags = await AnalyzeAsync("""
            public static int F(int x)
            {
                Spec.Requires(x >= 0, "Non-negative input.");
                Spec.Ensures<int>(r => r == 0, "Always zero.");
                return F(x) * 2;
            }
            """);
        AssertRefuted(diags, "VER101");
        Assert.Contains(diags, d => Msg(d).Contains("calls itself but has no `Spec.Decreases(...)`"));
    }

    [Fact]
    public async Task SelfRecursionWithAMeasureThatGoesDown_IsAccepted()
    {
        AssertAccepted(await AnalyzeAsync("""
            public static int Sum(int n)
            {
                Spec.Requires(n >= 0 && n <= 200, "Small non-negative input.");
                Spec.Ensures<int>(r => 2 * r == n * (n + 1), "Triangle number.");
                Spec.Decreases(n, "n goes down by one at each call.");
                if (n == 0)
                {
                    return 0;
                }
                return n + Sum(n - 1);
            }
            """));
    }

    [Fact]
    public async Task SelfRecursionWithAMeasureThatDoesNotGoDown_IsRefuted()
    {
        AssertRefuted(await AnalyzeAsync("""
            public static int Spin(int x)
            {
                Spec.Requires(x >= 0 && x <= 1000, "Small inputs only.");
                Spec.Ensures<int>(r => r > x, "Grows (vacuously: it never returns).");
                Spec.Decreases(x, "x is the measure.");
                return Spin(x);
            }
            """));
    }

    [Fact]
    public async Task SelfRecursionWithAMeasureThatCanBecomeNegative_IsRefuted()
    {
        AssertRefuted(await AnalyzeAsync("""
            public static int Down(int x)
            {
                Spec.Requires(x >= 0 && x <= 1000, "Small inputs only.");
                Spec.Ensures<int>(r => r >= 0, "Non-negative.");
                Spec.Decreases(x, "x goes down.");
                if (x < 0)
                {
                    return 0;
                }
                return Down(x - 2);
            }
            """));
    }

    [Fact]
    public async Task MutualRecursion_NeedsAMeasureOnBothMethods()
    {
        AssertRefuted(await AnalyzeAsync("""
            public static bool IsEven(int n)
            {
                Spec.Requires(n >= 0 && n <= 100, "Small inputs.");
                Spec.Ensures<bool>(r => r == (n % 2 == 0), "Parity.");
                Spec.Decreases(n, "n goes down.");
                if (n == 0)
                {
                    return true;
                }
                return IsOdd(n - 1);
            }

            public static bool IsOdd(int n)
            {
                Spec.Requires(n >= 0 && n <= 100, "Small inputs.");
                Spec.Ensures<bool>(r => r == (n % 2 == 1), "Parity.");
                if (n == 0)
                {
                    return false;
                }
                return IsEven(n - 1);
            }
            """), "VER101");

        AssertAccepted(await AnalyzeAsync("""
            public static bool IsEven(int n)
            {
                Spec.Requires(n >= 0 && n <= 100, "Small inputs.");
                Spec.Ensures<bool>(r => r == (n % 2 == 0), "Parity.");
                Spec.Decreases(n, "n goes down.");
                if (n == 0)
                {
                    return true;
                }
                return IsOdd(n - 1);
            }

            public static bool IsOdd(int n)
            {
                Spec.Requires(n >= 0 && n <= 100, "Small inputs.");
                Spec.Ensures<bool>(r => r == (n % 2 == 1), "Parity.");
                Spec.Decreases(n, "n goes down.");
                if (n == 0)
                {
                    return false;
                }
                return IsEven(n - 1);
            }
            """));
    }

    [Fact]
    public async Task CallingANonRecursiveMethodNeedsNoMeasure()
    {
        AssertAccepted(await AnalyzeAsync("""
            public static int Twice(int x)
            {
                Spec.Requires(x >= 0 && x <= 200, "Small inputs.");
                Spec.Ensures<int>(r => r == 2 * x, "Doubles.");
                return x + x;
            }

            public static int Quad(int x)
            {
                Spec.Requires(x >= 0 && x <= 100, "Small inputs.");
                Spec.Ensures<int>(r => r == 4 * x, "Quadruples.");
                return Twice(Twice(x));
            }
            """));
    }

    // ---- a switch expression that misses a value throws ----

    [Fact]
    public async Task SwitchExpressionMissingAnArm_IsRefuted()
    {
        AssertRefuted(await AnalyzeAsync("""
            public static int Sign(int x)
            {
                Spec.Ensures<int>(r => r == 1 || r == -1, "A unit sign.");
            #pragma warning disable CS8509
                return x switch
                {
                    > 0 => 1,
                    < 0 => -1,
                };
            #pragma warning restore CS8509
            }
            """));
    }

    [Fact]
    public async Task SwitchExpressionMissingAnArm_IsFineWhenThePreconditionRulesTheValueOut()
    {
        AssertAccepted(await AnalyzeAsync("""
            public static int Sign(int x)
            {
                Spec.Requires(x != 0, "Zero has no sign.");
                Spec.Ensures<int>(r => r == 1 || r == -1, "A unit sign.");
            #pragma warning disable CS8509
                return x switch
                {
                    > 0 => 1,
                    < 0 => -1,
                };
            #pragma warning restore CS8509
            }
            """));
    }

    [Fact]
    public async Task SwitchExpressionCoveringEveryValue_IsAccepted()
    {
        AssertAccepted(await AnalyzeAsync("""
            public static int Sign(int x)
            {
                Spec.Ensures<int>(r => r >= -1 && r <= 1, "A sign.");
                return x switch
                {
                    > 0 => 1,
                    < 0 => -1,
                    _ => 0,
                };
            }
            """));
    }

    [Fact]
    public async Task SwitchExpressionInALoop_IsChecked()
    {
        AssertRefuted(await AnalyzeAsync("""
            public static int Run(int n)
            {
                Spec.Requires(n >= 0 && n <= 5, "Small inputs.");
                var total = 0;
                for (var i = 0; i < n; i++)
                {
                    Spec.Decreases(n - i, "i moves toward n.");
            #pragma warning disable CS8509
                    total = total + (i switch { > 2 => 1, < 2 => 2 });
            #pragma warning restore CS8509
                }
                return total;
            }
            """));
    }

    [Fact]
    public async Task TupleSwitchExpressionMissingACase_IsRefuted()
    {
        AssertRefuted(await AnalyzeAsync("""
            public static int Cls(int a, int b)
            {
                Spec.Ensures<int>(r => r >= 1 && r <= 3, "A class.");
            #pragma warning disable CS8509
                return (a, b) switch
                {
                    (> 0, > 0) => 1,
                    (< 0, _) => 2,
                    (_, < 0) => 3,
                };
            #pragma warning restore CS8509
            }
            """));
    }

    [Fact]
    public async Task TupleSwitchExpressionCoveringEveryCase_IsAccepted()
    {
        AssertAccepted(await AnalyzeAsync("""
            public static int Cls(int a, int b)
            {
                Spec.Ensures<int>(r => r >= 1 && r <= 4, "A class.");
                return (a, b) switch
                {
                    (> 0, > 0) => 1,
                    (<= 0, > 0) => 2,
                    (> 0, <= 0) => 3,
                    (<= 0, <= 0) => 4,
                };
            }
            """));
    }

    [Fact]
    public async Task TupleSwitchArmBodyUsesItsElementConditions()
    {
        AssertRefuted(await AnalyzeAsync("""
            public static int Pick(int a, int b)
            {
                Spec.Ensures<int>(r => r > 0, "Positive.");
                return (a, b) switch
                {
                    (> 0, _) => a,
                    (_, > 0) => b,
                    _ => 0,
                };
            }
            """));
    }

    [Fact]
    public async Task TupleIsPattern_IsModelled()
    {
        AssertAccepted(await AnalyzeAsync("""
            public static int Pick(int a, int b)
            {
                Spec.Ensures<int>(r => r >= 0, "Not negative.");
                if ((a, b) is (> 0, > 0))
                {
                    return a + 0 * b;
                }
                return 0;
            }
            """));
        AssertRefuted(await AnalyzeAsync("""
            public static int Pick(int a, int b)
            {
                Spec.Ensures<int>(r => r > 0, "Positive.");
                if ((a, b) is (> 0, _))
                {
                    return b;
                }
                return 1;
            }
            """));
    }

    [Fact]
    public async Task TupleSwitchStatement_ArmsAreLowered()
    {
        AssertRefuted(await AnalyzeAsync("""
            public static int Pick(int a, int b)
            {
                Spec.Ensures<int>(r => r > 0, "Positive.");
                switch ((a, b))
                {
                    case (> 0, > 0):
                        return 1;
                    case (_, > 0):
                        return b - 5;
                }
                return 1;
            }
            """));
    }

    [Fact]
    public async Task PositionalPatternOnARecord_IsRejectedNotAssumedToMatch()
    {
        var result = await AnalyzeAsync("""
            public readonly record struct Pt(int X, int Y);

            public static int Cls(Pt p)
            {
                Spec.Ensures<int>(r => r == 1 || r == 2, "A class.");
            #pragma warning disable CS8509
                return p switch
                {
                    (> 0, _) => 1,
                    (< 0, _) => 2,
                };
            #pragma warning restore CS8509
            }
            """);
        AssertRefuted(result, "VER101");
    }

    // ---- foreach over a sequence hands out its items ----

    [Fact]
    public async Task Foreach_ItemsCarryTheFactsFromThePrecondition()
    {
        AssertAccepted(await AnalyzeAsync("""
            public static int Largest(ImmutableArray<int> xs)
            {
                Spec.Requires(!xs.IsDefault && xs.Length <= 20 && xs.All(v => v >= 0 && v <= 100), "Small non-negative items.");
                Spec.Ensures<int>(r => r >= 0 && r <= 100, "Within the item bounds.");
                var best = 0;
                foreach (var v in xs)
                {
                    Spec.LoopInvariant(best >= 0 && best <= 100, "best is one of the items, or zero.");
                    if (v > best)
                    {
                        best = v;
                    }
                }
                return best;
            }
            """));
    }

    [Fact]
    public async Task Foreach_ItemsAreNotMoreThanThePreconditionSays()
    {
        AssertRefuted(await AnalyzeAsync("""
            public static int Sum(ImmutableArray<int> xs)
            {
                Spec.Requires(!xs.IsDefault && xs.Length <= 20 && xs.All(v => v >= 0 && v <= 100), "Small non-negative items.");
                Spec.Ensures<int>(r => r <= 100, "Too small a bound for 20 items.");
                var total = 0;
                foreach (var v in xs)
                {
                    Spec.LoopInvariant(total >= 0, "Running total.");
                    total = total + v;
                }
                return total;
            }
            """));
    }

    [Fact]
    public async Task Foreach_CanBeEmpty()
    {
        AssertRefuted(await AnalyzeAsync("""
            public static int First(ImmutableArray<int> xs)
            {
                Spec.Requires(!xs.IsDefault && xs.Length <= 20 && xs.All(v => v >= 1), "Positive items.");
                Spec.Ensures<int>(r => r >= 1, "Positive.");
                var last = 0;
                foreach (var v in xs)
                {
                    Spec.LoopInvariant(last >= 0, "Not negative.");
                    last = v;
                }
                return last;
            }
            """));
    }

    [Fact]
    public async Task Foreach_ItemsOfAnIReadOnlyList()
    {
        AssertAccepted(await AnalyzeAsync("""
            public static int Last(System.Collections.Generic.IReadOnlyList<int> xs)
            {
                Spec.Requires(xs.Count <= 20 && xs.All(v => v >= 0 && v <= 100), "Small non-negative items.");
                Spec.Ensures<int>(r => r >= 0 && r <= 100, "A valid item, or zero.");
                var last = 0;
                foreach (var v in xs)
                {
                    Spec.LoopInvariant(last >= 0 && last <= 100, "In range.");
                    last = v;
                }
                return last;
            }
            """));
    }
}
