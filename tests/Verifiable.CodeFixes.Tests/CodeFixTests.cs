using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyCheck;
using GalaxyCheck.Gens;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Verifiable.Analyzers;
using Verifiable.Contracts;
using Xunit;

namespace Verifiable.CodeFixes.Tests;

public class CodeFixTests
{
    private static async Task<(string UpdatedCode, ImmutableArray<Diagnostic> RemainingDiagnostics, ImmutableArray<Diagnostic> CompilerDiagnostics)>
        ApplyCodeFixAsync(string source, string targetDiagnosticId, CodeFixProvider provider, int actionIndex = 0, DiagnosticAnalyzer? analyzer = null)
    {
        analyzer ??= new VerifiableGatekeeperAnalyzer();
        using var workspace = new AdhocWorkspace();
        var (doc, actions) = await GetCodeActionsAsync(workspace, source, targetDiagnosticId, provider, analyzer: analyzer);
        Assert.NotEmpty(actions);

        var ops = await actions[actionIndex].GetOperationsAsync(CancellationToken.None);
        var applyOp = ops.OfType<ApplyChangesOperation>().Single();
        var newDoc = applyOp.ChangedSolution.GetDocument(doc.Id)!;
        var updatedText = (await newDoc.GetTextAsync()).ToString();

        var comp2 = (await newDoc.Project.GetCompilationAsync())!;
        var compilerDiags = comp2.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        var analyzerDiags = await comp2.WithAnalyzers([analyzer]).GetAnalyzerDiagnosticsAsync();

        return (updatedText, analyzerDiags, compilerDiags);
    }

    private static async Task<(Document, List<CodeAction>)> GetCodeActionsAsync(AdhocWorkspace workspace,
        string source, string targetDiagnosticId, CodeFixProvider provider, string? otherSource = null, DiagnosticAnalyzer? analyzer = null)
    {
        analyzer ??= new VerifiableGatekeeperAnalyzer();
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .ToList<MetadataReference>();
        refs.Add(MetadataReference.CreateFromFile(typeof(VerifiableAttribute).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(ImmutableList).Assembly.Location));

        var proj = workspace.AddProject(
            ProjectInfo.Create(
                ProjectId.CreateNewId(),
                VersionStamp.Create(),
                "CodeFixProbe",
                "CodeFixProbe",
                LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable),
                parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
                metadataReferences: refs));

        if (otherSource is not null)
            workspace.AddDocument(proj.Id, "Caller.cs", SourceText.From(otherSource));
        var doc = workspace.AddDocument(proj.Id, "Probe.cs", SourceText.From(source));

        var comp1 = (await doc.Project.GetCompilationAsync())!;
        var withAnalyzers1 = comp1.WithAnalyzers([analyzer]);
        var diags1 = await withAnalyzers1.GetAllDiagnosticsAsync();
        var targetDiag = diags1.First(d => d.Id == targetDiagnosticId);

        var actions = new List<CodeAction>();
        var ctx = new CodeFixContext(
            doc,
            targetDiag,
            (a, _) => actions.Add(a),
            CancellationToken.None);

        await provider.RegisterCodeFixesAsync(ctx);
        return (doc, actions);
    }

    [Fact]
    public async Task HoistAmbientAuthority_ExtractsUtcNowAndGuid_AndUpdatesCallSites()
    {
        const string source = """
            using System;
            using Verifiable.Contracts;

            namespace Migration;

            public static class Orders
            {
                [VerifiableCandidate]
                private static string Stamp(string name)
                {
                    var ts = DateTimeOffset.UtcNow;
                    return $"{name}:{ts.ToUnixTimeSeconds()}";
                }

                public static string Caller() => Stamp("abc");
            }
            """;

        var (updated, remaining, compileErrors) = await ApplyCodeFixAsync(source, "VER004", new HoistAmbientAuthorityCodeFixProvider());

        Assert.Empty(compileErrors);
        Assert.DoesNotContain(remaining, d => d.Id == "VER004");
        Assert.Contains("DateTimeOffset now", updated);
        Assert.Contains("Stamp(\"abc\", DateTimeOffset.UtcNow)", updated);

        const string guidSource = """
            using System;
            using Verifiable.Contracts;

            namespace Migration;

            public static class Tokens
            {
                [VerifiableCandidate]
                private static Guid Next() => Guid.NewGuid();

                public static Guid UseIt() => Next();
            }
            """;

        var (updatedG, remG, compErrG) = await ApplyCodeFixAsync(guidSource, "VER004", new HoistAmbientAuthorityCodeFixProvider());
        Assert.Empty(compErrG);
        Assert.DoesNotContain(remG, d => d.Id == "VER004");
        Assert.Contains("Guid id", updatedG);
        Assert.Contains("Next(Guid.NewGuid())", updatedG);
    }

    [Theory]
    [InlineData("public static DateTime Read() => DateTime.UtcNow;")]
    [InlineData("private static DateTime Read() => DateTime.UtcNow;",
        "partial class Probe { DateTime Use() => Read(); }")]
    [InlineData("private static DateTime Read() => DateTime.UtcNow; Func<DateTime> use = Read;")]
    [InlineData("private static DateTime Read(int n = 0) => DateTime.UtcNow;")]
    [InlineData("private static DateTime Read(params int[] n) => DateTime.UtcNow;")]
    [InlineData("private static Guid[] Create() => Enumerable.Range(0, 2).Select(_ => Guid.NewGuid()).ToArray();")]
    [InlineData("private static void Create() { for (int i = 0; i < 2; i++) { var t = DateTime.UtcNow; } }")]
    [InlineData("private static IEnumerable<Guid> Create() { var id = Guid.NewGuid(); yield return id; }")]
    public async Task HoistAmbientAuthority_DeclinesUnsafeChanges(string members, string? otherSource = null)
    {
        using var workspace = new AdhocWorkspace();
        var source = $$"""
            using System;
            using System.Linq;
            using System.Collections.Generic;
            using Verifiable.Contracts;
            partial class Probe {
                [VerifiableCandidate]
                {{members}}
            }
            """;
        var (doc, actions) = await GetCodeActionsAsync(workspace, source, "VER004",
            new HoistAmbientAuthorityCodeFixProvider(), otherSource is null ? null : "using System; " + otherSource);
        var compilation = (await doc.Project.GetCompilationAsync())!;
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        Assert.Empty(actions);
    }

    [Fact]
    public async Task FreezeMutableState_FreezesClassToRecordAndReplacesMutableCollections()
    {
        const string mutableClass = """
            using Verifiable.Contracts;

            namespace Migration;

            [VerifiableCandidate]
            public class AccountDto
            {
                public int Id { get; set; }
                public decimal Balance { get; set; }
            }
            """;

        var (updatedType, rem1, err1) = await ApplyCodeFixAsync(mutableClass, "VER001", new FreezeMutableStateCodeFixProvider());
        Assert.Empty(err1);
        Assert.DoesNotContain(rem1, d => d.Id == "VER001");
        Assert.Contains("record AccountDto", updatedType);
        Assert.Contains("init;", updatedType);

        const string mutableCollection = """
            using System.Collections.Generic;
            using Verifiable.Contracts;

            namespace Migration;

            public static class Bag
            {
                [VerifiableCandidate]
                public static List<int> Empty() => new List<int>();
            }
            """;

        var (updatedCol, rem2, err2) = await ApplyCodeFixAsync(mutableCollection, "VER002", new FreezeMutableStateCodeFixProvider());
        Assert.Empty(err2);
        Assert.DoesNotContain(rem2, d => d.Id == "VER002");
        Assert.Contains("using System.Collections.Immutable;", updatedCol);
        Assert.Contains("ImmutableList<int>.Empty", updatedCol);

        const string propAssign = """
            using Verifiable.Contracts;

            namespace Migration;

            public sealed record Box
            {
                public int X { get; set; }
            }

            public static class Ops
            {
                [VerifiableCandidate]
                public static Box Bump(Box b)
                {
                    b.X = 42;
                    return b;
                }
            }
            """;

        var (updatedWith, rem3, err3) = await ApplyCodeFixAsync(propAssign, "VER001", new FreezeMutableStateCodeFixProvider());
        Assert.Empty(err3);
        Assert.DoesNotContain(rem3, d => d.Id == "VER001");
        Assert.True(updatedWith.Contains("b = b with") && updatedWith.Contains("X = 42"), $"Actual:\n{updatedWith}");
    }

    [Fact]
    public async Task SandwichIoSplit_ExtractsPureMiddleSliceIntoVerifiableCore()
    {
        const string source = """
            using System.Threading.Tasks;
            using Verifiable.Contracts;

            namespace Migration;

            public static class Service
            {
                private static Task<int> LoadAsync(int id) => Task.FromResult(id * 10);
                private static Task SaveAsync(int val) => Task.CompletedTask;

                [VerifiableCandidate]
                public static async Task<int> ProcessAsync(int id, int bonus)
                {
                    var loaded = await LoadAsync(id);
                    var adjusted = loaded + bonus * 2;
                    var doubled = adjusted * 2;
                    await SaveAsync(doubled);
                    return doubled;
                }
            }
            """;

        var (updated, remaining, compileErrors) = await ApplyCodeFixAsync(source, "VER005", new SandwichIoSplitCodeFixProvider());

        Assert.Empty(compileErrors);
        Assert.DoesNotContain(remaining, d => d.Id == "AD0001");
        Assert.Contains("[Verifiable]", updated);
        Assert.Contains("ProcessPureCore", updated);
        Assert.DoesNotContain(remaining, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task ExceptionToUnion_ConvertsThrowToResultFailAndWrapsReturns()
    {
        const string source = """
            using System;
            using Verifiable.Contracts;

            namespace Migration;

            public static class Validator
            {
                [VerifiableCandidate]
                public static int RequirePositive(int x)
                {
                    if (x <= 0)
                    {
                        throw new InvalidOperationException("Must be positive.");
                    }
                    return x * 2;
                }
            }
            """;

        var (updated, remaining, compileErrors) = await ApplyCodeFixAsync(source, "VER005", new ExceptionToUnionCodeFixProvider());

        Assert.Empty(compileErrors);
        Assert.DoesNotContain(remaining, d => d.Id == "VER005");
        Assert.Contains("Result<int, string>", updated);
        Assert.Contains(".Fail(\"Must be positive.\")", updated);
        Assert.Contains(".Ok(x * 2)", updated);
    }

    [Property(Iterations = 10)]
    public Property ExceptionToUnion_EliminatesVER005AcrossVariedNamesAndMessages() =>
        (from methodName in Gen.Element(new[] { "Validate", "Parse", "Check", "Compute" })
         from msg in Gen.String().WithLengthBetween(3, 20)
         // The message goes inside a regular C# string literal, so drop what cannot appear there unescaped:
         // quotes, backslashes and line breaks (a raw CR or LF is a "newline in constant" compile error).
         select (methodName, msg: new string(msg.Where(c => c is not ('"' or '\\' or '\r' or '\n' or '\u0085' or '\u2028' or '\u2029')).ToArray()))).ForAll(x =>
        {
            var safeMsg = string.IsNullOrEmpty(x.msg) ? "err" : x.msg;
            var src = $$"""
                using System;
                using Verifiable.Contracts;

                namespace PropMigration;

                public static class Probe
                {
                    [VerifiableCandidate]
                    public static int {{x.methodName}}(int v)
                    {
                        if (v < 0)
                        {
                            throw new ArgumentException("{{safeMsg}}");
                        }
                        return v + 1;
                    }
                }
                """;

            var (updated, rem, errs) = ApplyCodeFixAsync(src, "VER005", new ExceptionToUnionCodeFixProvider())
                .GetAwaiter().GetResult();

            return errs.IsEmpty
                && !rem.Any(d => d.Id == "VER005")
                && updated.Contains($"Result<int, string>.Fail(\"{safeMsg}\")");
        });

    [Fact]
    public async Task SplitSpecClause_SplitsDenseRequiresEnsuresAndEnsuresWhenIntoSeparateStatements()
    {
        const string requiresSource = """
            using Verifiable.Contracts;

            namespace SplitDemo;

            [Verifiable]
            public static class Rules
            {
                public static int Sum(int a, int b, int c, int d)
                {
                    Spec.Requires(a > 0 && b > 0 && c > 0 && d > 0, "All inputs must be positive.");
                    return a + b + c + d;
                }
            }
            """;

        var (reqUpdated, reqRem, reqErrs) = await ApplyCodeFixAsync(
            requiresSource,
            "VER301",
            new SplitSpecClauseCodeFixProvider(),
            analyzer: new SpecQualityAnalyzer());

        Assert.Empty(reqErrs);
        Assert.DoesNotContain(reqRem, d => d.Id == "VER301");
        Assert.Contains("Spec.Requires(a > 0, \"All inputs must be positive.\");", reqUpdated);
        Assert.Contains("Spec.Requires(b > 0, \"All inputs must be positive.\");", reqUpdated);
        Assert.Contains("Spec.Requires(c > 0, \"All inputs must be positive.\");", reqUpdated);
        Assert.Contains("Spec.Requires(d > 0, \"All inputs must be positive.\");", reqUpdated);

        const string ensuresWhenSource = """
            using Verifiable.Contracts;

            namespace SplitDemo;

            public readonly record struct Output(int A, int B, int C, int D);

            [Verifiable]
            public static class Rules
            {
                public static Output Build(bool active, int x)
                {
                    Spec.EnsuresWhen<Output>(active, r => r.A > 0 && r.B > 0 && r.C > 0 && r.D > 0, "Active outputs stay positive.");
                    return active ? new Output(1, 2, 3, 4) : new Output(0, 0, 0, 0);
                }
            }
            """;

        var (ensUpdated, ensRem, ensErrs) = await ApplyCodeFixAsync(
            ensuresWhenSource,
            "VER301",
            new SplitSpecClauseCodeFixProvider(),
            analyzer: new SpecQualityAnalyzer());

        Assert.Empty(ensErrs);
        Assert.DoesNotContain(ensRem, d => d.Id == "VER301");
        Assert.Contains("Spec.EnsuresWhen<Output>(active, r => r.A > 0, \"Active outputs stay positive.\");", ensUpdated);
        Assert.Contains("Spec.EnsuresWhen<Output>(active, r => r.B > 0, \"Active outputs stay positive.\");", ensUpdated);
        Assert.Contains("Spec.EnsuresWhen<Output>(active, r => r.C > 0, \"Active outputs stay positive.\");", ensUpdated);
        Assert.Contains("Spec.EnsuresWhen<Output>(active, r => r.D > 0, \"Active outputs stay positive.\");", ensUpdated);
    }
}
