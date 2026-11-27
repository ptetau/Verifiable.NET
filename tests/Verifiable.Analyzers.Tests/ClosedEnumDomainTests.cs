using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers.Smt;
using Xunit;

namespace Verifiable.Analyzers.Tests;

public class ClosedEnumDomainTests
{
    private const string PartitionSource = """
        using Verifiable.Contracts;

        namespace Sample;

        public enum OrderStatus { Draft = 0, Submitted = 1, Cancelled = 2 }

        [Verifiable]
        public static class StatusRules
        {
            public static bool IsActive(OrderStatus s) => s == OrderStatus.Draft || s == OrderStatus.Submitted;
            public static bool IsClosed(OrderStatus s) => s == OrderStatus.Cancelled;

            public static bool CheckPartition(OrderStatus s)
            {
                Spec.Ensures<bool>(r => r);
                return IsActive(s) != IsClosed(s);
            }
        }
        """;

    [Fact]
    public async Task OpenEnumByDefault_RefutesExhaustivePartition()
    {
        var diags = await AnalyzerTestHarness.AnalyzeAsync(PartitionSource, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(diags, d => d.Id == "AD0001" || d.Id == "VER104");
        Assert.Contains(diags, d => d.Id == "VER102");
    }

    [Fact]
    public async Task ClosedEnumsBuildProperty_ProvesExhaustivePartition_AndCatchesNewMember()
    {
        var closedProps = new Dictionary<string, string> { ["build_property.VerifiableClosedEnums"] = "true" };
        var okDiags = await AnalyzerTestHarness.AnalyzeWithOptionsAsync(PartitionSource, closedProps, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.Empty(okDiags.Where(d => d.Id == "AD0001" || d.Id == "VER104" || d.Severity == DiagnosticSeverity.Error));

        // Adding a 4th enum member immediately catches the missing branch as VER102!
        var withNewMember = PartitionSource.Replace("Cancelled = 2", "Cancelled = 2, Archived = 3");
        var failDiags = await AnalyzerTestHarness.AnalyzeWithOptionsAsync(withNewMember, closedProps, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(failDiags, d => d.Id == "AD0001" || d.Id == "VER104");
        Assert.Contains(failDiags, d => d.Id == "VER102");
    }

    [Fact]
    public async Task VerifiableAttributeOnEnum_EnablesClosedDomainWithoutGlobalOption()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace Sample;

            [Verifiable]
            public enum OrderStatus { Draft = 0, Submitted = 1, Cancelled = 2 }

            [Verifiable]
            public static class StatusRules
            {
                public static bool IsActive(OrderStatus s) => s == OrderStatus.Draft || s == OrderStatus.Submitted;
                public static bool IsClosed(OrderStatus s) => s == OrderStatus.Cancelled;

                public static bool CheckPartition(OrderStatus s)
                {
                    Spec.Ensures<bool>(r => r);
                    return IsActive(s) != IsClosed(s);
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.Empty(diags.Where(d => d.Id == "AD0001" || d.Id == "VER104" || d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public async Task FlagsEnum_RemainsOpenEvenWhenClosedEnumsEnabled()
    {
        const string source = """
            using System;
            using Verifiable.Contracts;

            namespace Sample;

            [Flags]
            public enum Perms { None = 0, Read = 1, Write = 2 }

            [Verifiable]
            public static class PermRules
            {
                public static bool OnlySingleFlag(Perms p)
                {
                    Spec.Ensures<bool>(r => r);
                    return p == Perms.None || p == Perms.Read || p == Perms.Write;
                }
            }
            """;

        var closedProps = new Dictionary<string, string> { ["build_property.VerifiableClosedEnums"] = "true" };
        var diags = await AnalyzerTestHarness.AnalyzeWithOptionsAsync(source, closedProps, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.Contains(diags, d => d.Id == "VER102");
    }

    [Fact]
    public async Task OpenEnum_StillCarriesUnderlyingIntegralBounds_AndSupportsDecimalCast()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace Sample;

            public enum ByteCode : byte { A = 1, B = 2 }

            [Verifiable]
            public static class CastRules
            {
                public static long SafeWiden(ByteCode code)
                {
                    Spec.Ensures<long>(r => r >= 0 && r <= 255);
                    return checked((long)code);
                }

                public static decimal ToDecimal(ByteCode code)
                {
                    Spec.Ensures<decimal>(r => r >= 0m && r <= 255m);
                    return (decimal)code;
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.Empty(diags.Where(d => d.Id == "AD0001" || d.Id == "VER104" || d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public async Task CheckedNullableEnumCastOverflow_IsDetectedAsVER102()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace Sample;

            public enum BigEnum : long { Small = 1, Big = 4294967296L }

            [Verifiable]
            public static class CheckedCasts
            {
                public static int? NarrowUnsafe(BigEnum e)
                {
                    Spec.Requires(e == BigEnum.Big);
                    Spec.Ensures<int?>(r => r == 0);
                    return checked((int?)e);
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(diags, d => d.Id == "AD0001" || d.Id == "VER104");
        Assert.Contains(diags, d => d.Id == "VER102");
    }

    [Fact]
    public async Task ClosedEnum_RejectsUncheckedRawCastAtReturnAndCallSite()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace Sample;

            [Verifiable]
            public enum OrderStatus { Draft = 0, Submitted = 1, Cancelled = 2 }

            [Verifiable]
            public static class CastGuard
            {
                public static OrderStatus UnsafeFromInt(int raw) => (OrderStatus)raw;

                public static OrderStatus SafeFromInt(int raw)
                {
                    Spec.Requires(raw == 0 || raw == 1 || raw == 2);
                    return (OrderStatus)raw;
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableGatekeeperAnalyzer(), new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(diags, d => d.Id == "AD0001" || d.Id == "VER104");
        var ver102 = Assert.Single(diags.Where(d => d.Id == "VER102"));
        Assert.Contains("UnsafeFromInt", ver102.GetMessage());
    }
}
