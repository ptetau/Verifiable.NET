using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using GalaxyCheck;
using GalaxyCheck.Gens;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Verifiable.Contracts;
using Xunit;

[assembly: TypeAlias<int>("AliasOrderId", "Value >= 1", Namespace = "Verifiable.Generators.Tests")]
[assembly: TypeAlias<int>("AliasNonNegCount", "Value >= 0", Namespace = "Verifiable.Generators.Tests")]
[assembly: TypeAlias<decimal>("AliasPositiveMoney", "Value > 0m && Value <= 1000000m", Namespace = "Verifiable.Generators.Tests")]
[assembly: TypeAlias<string>("AliasLabel", "Length is >= 1 and <= 40", Namespace = "Verifiable.Generators.Tests")]

namespace Verifiable.Generators.Tests;

[Verifiable]
[Invariant("Items.Count == Count")]
public readonly record struct AliasLedger(
    AliasNonNegCount Count,
    ImmutableDictionary<AliasOrderId, AliasPositiveMoney> Items);

public static class AliasPortEngine
{
    [Verifiable, InboundPort]
    [Requires("amount > 0m")]
    public static AliasLedger RecordOrder(
        AliasLedger ledger,
        AliasOrderId orderId,
        AliasLabel label,
        AliasPositiveMoney amount,
        AliasOrderId? relatedOrderId)
    {
        _ = label.Length;
        _ = relatedOrderId;
        if (ledger.Items.ContainsKey(orderId))
        {
            return ledger;
        }

        return ledger with
        {
            Count = AliasNonNegCount.From(ledger.Count + 1),
            Items = ledger.Items.Add(orderId, amount)
        };
    }
}

public class TypeAliasTests
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
            "TypeAliasTestAssembly",
            [syntaxTree],
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new TypeAliasGenerator().AsSourceGenerator(), new RefinedTypeGenerator().AsSourceGenerator(), new InboundPortGenerator().AsSourceGenerator()],
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview));

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);
        var result = driver.GetRunResult();
        var sources = result.Results
            .SelectMany(r => r.GeneratedSources)
            .ToDictionary(s => s.HintName, s => s.SourceText.ToString(), StringComparer.Ordinal);

        return (outputCompilation, diagnostics, sources);
    }

    [Fact]
    public void AssemblyAndTypeLevelTypeAliases_GenerateFullSurfaceAndCompileWithInboundPort()
    {
        const string code = """
            using System.Collections.Immutable;
            using Verifiable.Contracts;

            [assembly: TypeAlias<int>("PositiveId", "Value >= 1", Namespace = "AliasDomain")]
            [assembly: TypeAlias<string>("TitleText", "Length is >= 1 and <= 80", Namespace = "AliasDomain")]

            namespace AliasDomain
            {
                [TypeAlias<decimal>("Money", "Value >= 0m")]
                public static class DomainAliases;

                [Verifiable]
                [Invariant("Balance >= 0m")]
                public readonly record struct Wallet(PositiveId Id, TitleText Name, Money Balance, PositiveId? ParentId);

                [Verifiable]
                public static class WalletOps
                {
                    [InboundPort]
                    public static Wallet Rename(Wallet wallet, TitleText newName, Money deposit, PositiveId? parentId)
                    {
                        decimal total = wallet.Balance + deposit;
                        int nameLen = newName.Length;
                        _ = nameLen;
                        return wallet with { Name = newName, Balance = Money.From(total), ParentId = parentId };
                    }
                }
            }
            """;

        var (output, genDiags, sources) = RunGenerators(code);

        Assert.Empty(genDiags);
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);

        Assert.True(sources.TryGetValue("AliasDomain.PositiveId.TypeAlias.g.cs", out var idSrc));
        Assert.Contains("[global::Verifiable.Contracts.Refined<int>(\"Value >= 1\")]", idSrc);
        Assert.Contains("implicit operator int(PositiveId alias) => alias.Value;", idSrc);
        Assert.Contains("operator <(PositiveId left, PositiveId right)", idSrc);

        Assert.True(sources.ContainsKey("AliasDomain.TitleText.TypeAlias.g.cs"));
        Assert.True(sources.ContainsKey("AliasDomain.Money.TypeAlias.g.cs"));
        Assert.True(sources.ContainsKey("AliasDomain.Wallet.Gate.g.cs"));
        Assert.True(sources.ContainsKey("AliasDomain.WalletOpsPort.InboundPort.g.cs"));
    }

    [Fact]
    public void InvalidAliasNameAndDuplicateAlias_ReportVER203AndVER204WithFix()
    {
        const string badNameCode = """
            using Verifiable.Contracts;
            [assembly: TypeAlias<int>("123Bad", "Value >= 0", Namespace = "Ns")]
            """;

        var (_, badDiags, _) = RunGenerators(badNameCode);
        var d203 = Assert.Single(badDiags, d => d.Id == "VER203");
        Assert.Contains("Fix:", d203.GetMessage());

        const string dupCode = """
            using Verifiable.Contracts;
            [assembly: TypeAlias<int>("ItemId", "Value >= 1", Namespace = "Ns")]
            [assembly: TypeAlias<int>("ItemId", "Value >= 2", Namespace = "Ns")]
            """;

        var (_, dupDiags, _) = RunGenerators(dupCode);
        var d204 = Assert.Single(dupDiags, d => d.Id == "VER204");
        Assert.Contains("Fix:", d204.GetMessage());
    }

    [Fact]
    public void CompiledTypeAliases_SupportImplicitWideningOrderingFormattingAndJsonDictionaryKeys()
    {
        var a = AliasOrderId.From(5);
        var b = AliasOrderId.From(12);
        int widened = a;
        Assert.Equal(5, widened);
        Assert.Equal(17, a + b);
        Assert.True(a < b);
        Assert.Equal("5", a.ToString());

        var money = AliasPositiveMoney.From(42.5m);
        decimal dec = money;
        Assert.Equal(42.5m, dec);

        var label = AliasLabel.From("Invoice #1");
        string labelStr = label;
        Assert.Equal("Invoice #1", labelStr);
        Assert.Equal(10, label.Length);

        var ledger = new AliasLedger(
            AliasNonNegCount.From(1),
            ImmutableDictionary<AliasOrderId, AliasPositiveMoney>.Empty.Add(a, money));
        var json = JsonSerializer.Serialize(ledger);
        var roundTrip = JsonSerializer.Deserialize<AliasLedger>(json);
        Assert.Equal(1, (int)roundTrip.Count);
        Assert.Equal(42.5m, (decimal)roundTrip.Items[a]);
    }

    [Property]
    public Property CompiledAliasInboundPort_AcceptsValidAndRejectsInvalid() =>
        (from orderId in Gen.Int32().Between(1, 500)
         from amount in Gen.Decimal().Between(1m, 1000m)
         from label in Gen.String().WithLengthBetween(1, 20)
         select (orderId, amount, label)).ForAll(x =>
        {
            var raw = new RawAliasLedger { Count = 0, Items = new Dictionary<int, decimal>() };
            var ok = AliasPortEnginePort.RecordOrder(raw, x.orderId, x.label, x.amount, x.orderId);
            var badAmount = AliasPortEnginePort.RecordOrder(raw, x.orderId, x.label, 0m, null);
            var badId = AliasPortEnginePort.RecordOrder(raw, 0, x.label, x.amount, null);
            var badLabel = AliasPortEnginePort.RecordOrder(raw, x.orderId, "", x.amount, null);

            return ok.IsValid
                && ok.Value.Count == 1
                && ok.Value.Items[AliasOrderId.From(x.orderId)] == x.amount
                && !badAmount.IsValid
                && !badId.IsValid
                && !badLabel.IsValid;
        });
}
