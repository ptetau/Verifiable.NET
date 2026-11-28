#pragma warning disable RS1025, RS1036, RS1041, RS2008 // a test-only analyzer, never shipped
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Verifiable.Analyzers.Tests.Soundness;

/// <summary>
/// A test-only analyzer that crashes, hangs or eats memory, to prove the corpus run survives such an analyzer.
/// The mode is a string so it can cross into the worker process: <c>recurse:N</c> (N nested calls, about 1 KB of stack each),
/// <c>recurse:forever</c> (a real stack overflow), <c>hang</c>, or <c>alloc</c> (holds memory until killed).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
internal sealed class MisbehavingAnalyzer(string mode) : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Done = new("TST001", "done", "recursed to {0}", "Test", DiagnosticSeverity.Warning, true);

    public MisbehavingAnalyzer() : this("recurse:0") { }

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Done];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.RegisterSyntaxTreeAction(c =>
        {
            if (mode == "hang") Thread.Sleep(Timeout.Infinite);
            if (mode == "alloc")
            {
                var held = new List<byte[]>();
                while (true)
                {
                    var chunk = new byte[64 * 1024 * 1024];
                    Array.Fill(chunk, (byte)1); // touch the pages so they count towards the working set
                    held.Add(chunk);
                }
            }

            var depth = mode == "recurse:forever" ? int.MaxValue : int.Parse(mode.Substring("recurse:".Length));
            c.ReportDiagnostic(Diagnostic.Create(Done, Location.None, Recurse(depth)));
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Recurse(int n)
    {
        if (n == 0) return 0;
        Span<byte> pad = stackalloc byte[900];
        pad[0] = (byte)n;
        return Recurse(n - 1) + 1 + pad[1];
    }
}
