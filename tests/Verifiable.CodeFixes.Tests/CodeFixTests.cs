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
        ApplyCodeFixAsync(string source, string targetDiagnosticId, CodeFixProvider provider, int actionIndex = 0)
    {
        using var workspace = new AdhocWorkspace();
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

        var doc = workspace.AddDocument(proj.Id, "Probe.cs", SourceText.From(source));
        var analyzer = new VerifiableGatekeeperAnalyzer();

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
                public static string Stamp(string name)
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
                public static Guid Next() => Guid.NewGuid();

                public static Guid UseIt() => Next();
            }
            """;

        var (updatedG, remG, compErrG) = await ApplyCodeFixAsync(guidSource, "VER004", new HoistAmbientAuthorityCodeFixProvider());
        Assert.Empty(compErrG);
        Assert.DoesNotContain(remG, d => d.Id == "VER004");
        Assert.Contains("Guid id", updatedG);
        Assert.Contains("Next(Guid.NewGuid())", updatedG);
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
         select (methodName, msg: msg.Replace("\"", "").Replace("\\", ""))).ForAll(x =>
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
}
