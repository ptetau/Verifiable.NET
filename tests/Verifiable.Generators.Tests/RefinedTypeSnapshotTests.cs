using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Verifiable.Contracts;
using Xunit;

namespace Verifiable.Generators.Tests;

public class RefinedTypeSnapshotTests
{
    private static (Compilation OutputCompilation, ImmutableArray<Diagnostic> GeneratorDiagnostics, IReadOnlyDictionary<string, string> Sources)
        RunGenerators(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .ToList<MetadataReference>();

        refs.Add(MetadataReference.CreateFromFile(typeof(VerifiableAttribute).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(ImmutableDictionary).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(JsonSerializer).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            "GeneratorTestAssembly",
            [syntaxTree],
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new TypeAliasGenerator().AsSourceGenerator(), new InboundPortGenerator().AsSourceGenerator()],
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview));

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);
        var result = driver.GetRunResult();
        var sources = result.Results
            .SelectMany(r => r.GeneratedSources)
            .ToDictionary(s => s.HintName, s => s.SourceText.ToString(), StringComparer.Ordinal);

        return (outputCompilation, diagnostics, sources);
    }

    [Fact]
    public void TypeAliasInt_GeneratesFullContractSurface_AndCompilesCleanly()
    {
        const string code = """
            using Verifiable.Contracts;

            namespace SampleDomain;

            [TypeAlias<int>]
            public readonly partial record struct TodoId
            {
                public static bool IsValid(int value) => value >= 1;
            }
            """;

        var (output, genDiags, sources) = RunGenerators(code);

        Assert.Empty(genDiags);
        var compileErrors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(compileErrors);

        Assert.True(sources.TryGetValue("SampleDomain.TodoId.TypeAlias.g.cs", out var generated));
        Assert.Contains("[global::Verifiable.Contracts.Verifiable]", generated);
        Assert.Contains("IComparable<TodoId>", generated);
        Assert.Contains("global::Verifiable.Contracts.Spec.Requires(IsValid(value));", generated);
        Assert.Contains("global::Verifiable.Contracts.Spec.Ensures<TodoId>(result => result.Value == value);", generated);
        Assert.Contains("TodoIdJsonConverter", generated);
        Assert.Contains("ReadAsPropertyName", generated);
        Assert.Contains("WriteAsPropertyName", generated);
    }

    [Fact]
    public void ZeroBasedBoundedInt_GeneratesPureAxiomReadUnchecked()
    {
        const string code = """
            using Verifiable.Contracts;

            namespace SampleDomain;

            [Refined<int>]
            public readonly partial record struct BufferIndex
            {
                public static bool IsValid(int idx) => idx is >= 0 and < 64;
            }
            """;

        var (output, genDiags, sources) = RunGenerators(code);

        Assert.Empty(genDiags);
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var generated = sources["SampleDomain.BufferIndex.Refined.g.cs"];
        Assert.Contains("public const int UpperBoundExclusive = 64;", generated);
        Assert.Contains("[global::Verifiable.Contracts.PureAxiom]", generated);
        Assert.Contains("global::Verifiable.Contracts.Spec.Requires(buffer.Length >= 64);", generated);
        Assert.Contains("ReadUnchecked<T>(global::System.ReadOnlySpan<T> buffer)", generated);
    }

    [Fact]
    public void TypeAliasOnNonPartialClass_ReportsVER201WithFixLine()
    {
        const string code = """
            using Verifiable.Contracts;

            namespace SampleDomain;

            [TypeAlias<int>]
            public class BadTodoId
            {
                public static bool IsValid(int value) => value >= 1;
            }
            """;

        var (_, genDiags, _) = RunGenerators(code);

        var diag = Assert.Single(genDiags);
        Assert.Equal("VER201", diag.Id);
        Assert.Contains("Fix:", diag.GetMessage());
    }

    [Fact]
    public void TypeAliasWithoutIsValid_ReportsVER202WithFixLine()
    {
        const string code = """
            using Verifiable.Contracts;

            namespace SampleDomain;

            [TypeAlias<int>]
            public readonly partial record struct BrokenId;
            """;

        var (_, genDiags, _) = RunGenerators(code);

        var diag = Assert.Single(genDiags);
        Assert.Equal("VER202", diag.Id);
        Assert.Contains("Fix:", diag.GetMessage());
    }

    [Fact]
    public void InboundPort_GeneratesRawDtosGateAndPort_AndCompilesCleanly()
    {
        const string code = """
            using System.Collections.Immutable;
            using Verifiable.Contracts;

            namespace SampleDomain;

            [TypeAlias<int>]
            public readonly partial record struct ItemId
            {
                public static bool IsValid(int value) => value >= 1;
            }

            [TypeAlias<string>]
            public readonly partial record struct ItemTitle
            {
                public static bool IsValid(string value) => value is { Length: >= 1 and <= 50 };
            }

            [Verifiable]
            public readonly record struct Board(
                ItemId NextId,
                ImmutableDictionary<ItemId, ItemTitle> Items)
            {
                [Invariant]
                public bool IsConsistent() => Items.Count == NextId - 1;
            }

            [Verifiable]
            public static class Engine
            {
                [InboundPort]
                public static Board Add(Board board, ItemTitle title)
                {
                    Spec.Requires(board.NextId >= 1);
                    var next = ItemId.From(board.NextId + 1);
                    return board with { NextId = next, Items = board.Items.Add(board.NextId, title) };
                }
            }
            """;

        var (output, genDiags, sources) = RunGenerators(code);

        Assert.Empty(genDiags);
        var compileErrors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(compileErrors);

        Assert.True(sources.ContainsKey("SampleDomain.Board.Gate.g.cs"));
        Assert.True(sources.ContainsKey("SampleDomain.EnginePort.InboundPort.g.cs"));
        var gateSrc = sources["SampleDomain.Board.Gate.g.cs"];
        Assert.Contains("candidate.IsConsistent()", gateSrc);
        var portSrc = sources["SampleDomain.EnginePort.InboundPort.g.cs"];
        Assert.Contains("board.NextId >= 1", portSrc);
    }

    [Fact]
    public void SpiritDescriptionAndBecause_PropagateIntoGeneratedSpecAndGates()
    {
        const string code = """
            using Verifiable.Contracts;

            namespace SampleDomain;

            [TypeAlias<int>("IDs start at 1 so zero never aliases an uninitialized record.")]
            public readonly partial record struct CustomerId
            {
                public static bool IsValid(int value) => value >= 1;
            }

            public sealed record Account(CustomerId Owner, int Balance)
            {
                [Invariant("Accounts cannot go into overdraft.")]
                public bool IsSolvent => Balance >= 0;
            }

            [Verifiable]
            public static class AccountEngine
            {
                [InboundPort]
                public static Account Deposit(Account account, int amount)
                {
                    Spec.Requires(amount > 0, "Only positive deposits increase a balance.");
                    return account with { Balance = account.Balance + amount };
                }
            }
            """;

        var (output, genDiags, sources) = RunGenerators(code);

        Assert.Empty(genDiags);
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var typeSrc = sources["SampleDomain.CustomerId.TypeAlias.g.cs"];
        Assert.Contains("\"IDs start at 1 so zero never aliases an uninitialized record.\"", typeSrc);

        var gateSrc = sources["SampleDomain.Account.Gate.g.cs"];
        Assert.Contains("Accounts cannot go into overdraft.", gateSrc);

        var portSrc = sources["SampleDomain.AccountEnginePort.InboundPort.g.cs"];
        Assert.Contains("Only positive deposits increase a balance.", portSrc);
    }

    [Fact]
    public void InvalidTargetDiagnostics_FormatWithElmBannerCaretAndFix()
    {
        const string code = """
            using Verifiable.Contracts;

            namespace BrokenDomain;

            [TypeAlias<int>]
            public record struct NotReadOnlyOrPartial
            {
                public static bool IsValid(int value) => value >= 1;
            }
            """;

        var (_, genDiags, _) = RunGenerators(code);
        var diag = Assert.Single(genDiags.Where(d => d.Id == "VER201"));
        var msg = diag.GetMessage();
        Assert.Contains("-- INVALID CONSTRAINED TYPE", msg);
        Assert.Contains("Why this rule exists:", msg);
        Assert.Contains("|", msg);
        Assert.Contains("^", msg);
        Assert.Contains("Fix:", msg);
        Assert.Contains("`readonly`", msg);
        Assert.Contains("`partial`", msg);
    }

    [Fact]
    public void TypeAlias_IsValidTypoAndSignatureMismatch_ProduceElmSuggestions()
    {
        const string typoCode = """
            using Verifiable.Contracts;

            namespace BrokenDomain;

            [TypeAlias<int>]
            public readonly partial record struct OrderId
            {
                public static bool IsVald(int value) => value >= 1;
            }
            """;

        var (_, typoDiags, _) = RunGenerators(typoCode);
        var typoDiag = Assert.Single(typoDiags.Where(d => d.Id == "VER202"));
        Assert.Contains("`IsVald`", typoDiag.GetMessage());
        Assert.Contains("Did you mean `IsValid`?", typoDiag.GetMessage());

        const string wrongParamCode = """
            using Verifiable.Contracts;

            namespace BrokenDomain;

            [TypeAlias<int>]
            public readonly partial record struct AccountId
            {
                private bool IsValid(long value) => value >= 1;
            }
            """;

        var (_, sigDiags, _) = RunGenerators(wrongParamCode);
        var sigDiag = Assert.Single(sigDiags.Where(d => d.Id == "VER202"));
        var sigMsg = sigDiag.GetMessage();
        Assert.Contains("not `static`", sigMsg);
        Assert.Contains("not `public`", sigMsg);
        Assert.Contains("takes `long` instead of `int`", sigMsg);
    }
}
