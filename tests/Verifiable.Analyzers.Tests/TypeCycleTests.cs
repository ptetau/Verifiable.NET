using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

/// <summary>
/// Delegate types and self-referential types must never make the analyzer recurse without end (#49). Delegates are
/// rejected early (VER006); every type walk has a cycle guard, so even a type that reaches itself is safe.
/// Each analysis runs on a thread with a small stack: before the fix these programs overflowed it.
/// </summary>
public class TypeCycleTests
{
    private const string Usings = "using System;\nusing Verifiable.Contracts;\n";

    private static List<string> Run(string source, bool gatekeeper, bool smt)
    {
        List<string>? result = null;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var analyzers = new List<Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer>();
                if (gatekeeper) analyzers.Add(new VerifiableGatekeeperAnalyzer());
                if (smt) analyzers.Add(new VerifiableSmtAnalyzer());
                var diags = AnalyzerTestHarness.AnalyzeAsync(Usings + source, analyzers.ToArray()).GetAwaiter().GetResult();
                result = diags.Where(d => d.Id.StartsWith("VER", StringComparison.Ordinal) || d.Id == "AD0001")
                              .Select(d => d.Id + ": " + d.GetMessage()).ToList();
            }
            catch (Exception ex) { error = ex; }
        }, 4 * 1024 * 1024);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException("analyzer threw: " + error);
        return result!;
    }

    private static void AssertNoCrash(List<string> diags) =>
        Assert.DoesNotContain(diags, d => d.StartsWith("VER101", StringComparison.Ordinal) || d.StartsWith("AD0001", StringComparison.Ordinal));

    private const string ViaDelegate = """
        namespace C;
        [Verifiable]
        public static class ViaDelegate
        {
            static readonly Func<int, int> Self = F;
            public static int F(int x)
            {
                Spec.Requires(x >= 0 && x <= 10, "Small inputs.");
                Spec.Ensures<int>(r => r == 0, "Zero.");
                Spec.Decreases(x, "x.");
                if (x == 0) { return 0; }
                return Self(x);
            }
        }
        """;

    private const string LambdaParam = """
        namespace C;
        [Verifiable]
        public static class R2Lam
        {
            public static int Use(Func<int, int> f)
            {
                Spec.Ensures<int>(r => r >= 0, "Non-negative.");
                return f(3);
            }
        }
        """;

    [Fact]
    public void DelegateField_IsRejected()
    {
        var d = Run(ViaDelegate, gatekeeper: true, smt: false);
        Assert.Contains(d, x => x.StartsWith("VER006", StringComparison.Ordinal) && x.Contains("Func<int, int>"));
    }

    [Fact]
    public void DelegateParameter_IsRejected()
    {
        var d = Run(LambdaParam, gatekeeper: true, smt: false);
        Assert.Contains(d, x => x.StartsWith("VER006", StringComparison.Ordinal) && x.Contains("Func<int, int>"));
    }

    [Fact]
    public void DelegateLocalAndReturn_AreRejected()
    {
        var d = Run("""
            namespace C;
            [Verifiable]
            public static class L
            {
                public static int A() { Func<int, int> g = x => x; return 1; }
                public static Action B() { return null!; }
            }
            """, gatekeeper: true, smt: false);
        Assert.Equal(2, d.Count(x => x.StartsWith("VER006", StringComparison.Ordinal) && x.Contains("delegate type")));
    }

    [Fact]
    public void DelegateProgramsDoNotCrashTheSmtAnalyzer()
    {
        AssertNoCrash(Run(ViaDelegate, gatekeeper: false, smt: true));
        AssertNoCrash(Run(LambdaParam, gatekeeper: false, smt: true));
    }

    [Fact]
    public void SelfReferentialRecord_DoesNotCrash()
    {
        var d = Run("""
            namespace C;
            [Verifiable] public sealed record Node(Node? Next, int V);
            [Verifiable]
            public static class S
            {
                public static int Value(Node n)
                {
                    Spec.Requires(n.V >= 0, "pre");
                    Spec.Ensures<int>(r => r >= 0, "non-negative");
                    return n.V;
                }
                public static Node Make(int v)
                {
                    Spec.Requires(v >= 0, "pre");
                    Spec.Ensures<Node>(r => r.V >= 0, "post");
                    return new Node(null, v);
                }
            }
            """, gatekeeper: true, smt: true);
        AssertNoCrash(d);
    }

    [Fact]
    public void MutuallyRecursiveRecordsAndNonNullableCycle_DoNotCrash()
    {
        var d = Run("""
            namespace C;
            [Verifiable] public sealed record A(B Other, int V);
            [Verifiable] public sealed record B(A Other, int W);
            [Verifiable] public sealed record Tree(System.Collections.Immutable.ImmutableArray<Tree> Kids, int V);
            [Verifiable]
            public static class S
            {
                public static int Va(A a) { Spec.Ensures<int>(r => r == a.V, "v"); return a.V; }
                public static int Vt(Tree t) { Spec.Ensures<int>(r => r == t.V, "v"); return t.V; }
            }
            """, gatekeeper: true, smt: true);
        AssertNoCrash(d);
    }
}
