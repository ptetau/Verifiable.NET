using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Verifiable.Analyzers;
using Verifiable.Analyzers.Smt;
using Verifiable.Contracts;
using Xunit;

namespace Verifiable.Analyzers.Tests;

public sealed class SpecErgonomicsTests
{
    // -------------------------------------------------------------------------
    // D20: Spec.Implies, Spec.EnsuresWhen, Spec.RequiresWhen
    // -------------------------------------------------------------------------

    [Fact]
    public async Task D20_SpecImpliesAndEnsuresWhen_ProveMultiBranchContract_AndRefuteBuggyBranch()
    {
        const string validSource = """
            using Verifiable.Contracts;

            namespace SampleD20;

            public readonly record struct RemoveResult(int RemainingCount, bool Removed, bool ReleasedLock);

            [Verifiable]
            public static class QueueRules
            {
                public static RemoveResult RemoveAt(int count, int index, bool hasBlocker)
                {
                    Spec.Requires(count >= 0, "Queue count is non-negative.");

                    Spec.EnsuresWhen<RemoveResult>(index < 0, r => r.RemainingCount == count && !r.Removed,
                        "Out-of-range index leaves the queue unchanged.");
                    Spec.EnsuresWhen<RemoveResult>(index >= 0 && index < count, r => r.RemainingCount == count - 1 && r.Removed,
                        "Valid index removes one item.");
                    Spec.Ensures<RemoveResult>(r => Spec.Implies(r.ReleasedLock, r.Removed && !hasBlocker),
                        "Lock is released only when an item is removed and no blocker remains.");

                    if (index < 0 || index >= count)
                    {
                        return new RemoveResult(count, false, false);
                    }

                    return new RemoveResult(count - 1, true, !hasBlocker);
                }

                public static int ConditionalPrecondition(bool strict, int value)
                {
                    Spec.RequiresWhen(strict, value > 0, "Strict mode requires a positive value.");
                    Spec.EnsuresWhen(strict, Spec.Result<int>() > 0, "Strict mode returns a positive value.");
                    return strict ? value : 0;
                }

                public static int CallConditional(int x)
                {
                    Spec.Requires(x > 0, "Caller guarantees x is positive.");
                    return ConditionalPrecondition(true, x) + ConditionalPrecondition(false, -10);
                }
            }
            """;

        var validDiags = await AnalyzerTestHarness.AnalyzeAsync(
            validSource,
            new VerifiableGatekeeperAnalyzer(),
            new VerifiableSmtAnalyzer());

        Assert.DoesNotContain(validDiags, d => d.Severity == DiagnosticSeverity.Error);

        const string buggySource = """
            using Verifiable.Contracts;

            namespace SampleD20Buggy;

            public readonly record struct RemoveResult(int RemainingCount, bool Removed, bool ReleasedLock);

            [Verifiable]
            public static class BuggyQueueRules
            {
                public static RemoveResult RemoveAt(int count, int index, bool hasBlocker)
                {
                    Spec.Requires(count >= 0, "Queue count is non-negative.");
                    Spec.EnsuresWhen<RemoveResult>(index >= 0 && index < count, r => r.RemainingCount == count - 1 && r.Removed,
                        "Valid index removes one item.");
                    Spec.Ensures<RemoveResult>(r => Spec.Implies(r.ReleasedLock, r.Removed && !hasBlocker),
                        "Lock is released only when an item is removed and no blocker remains.");

                    if (index < 0 || index >= count)
                    {
                        return new RemoveResult(count, false, false);
                    }

                    // Bug: releases lock even when hasBlocker is true!
                    return new RemoveResult(count - 1, true, true);
                }

                public static int RequiresPositiveWhenStrict(bool strict, int value)
                {
                    Spec.RequiresWhen(strict, value > 0, "Strict mode requires positive value.");
                    return value;
                }

                public static int BadCaller() => RequiresPositiveWhenStrict(true, 0);
            }
            """;

        var buggyDiags = await AnalyzerTestHarness.AnalyzeAsync(buggySource, new VerifiableSmtAnalyzer());
        var ver102 = buggyDiags.Where(d => d.Id == "VER102").ToList();
        Assert.Contains(ver102, d => d.GetMessage().Contains("Lock is released only when an item is removed"));
        Assert.Contains(ver102, d => d.GetMessage().Contains("Strict mode requires positive value."));
    }

    // -------------------------------------------------------------------------
    // D21: [NonDefault] attribute & VerifiableNonDefaultImmutableArrays option
    // -------------------------------------------------------------------------

    [Fact]
    public async Task D21_NonDefaultAttribute_EliminatesIsDefaultBoilerplate_AndRejectsDefaultAtCallSitesAndReturns()
    {
        const string source = """
            using System.Collections.Immutable;
            using Verifiable.Contracts;

            namespace SampleD21;

            public readonly record struct ItemBag([NonDefault] ImmutableArray<int> Items);

            [Verifiable]
            public static class ArrayRules
            {
                public static int HeadOrZero([NonDefault] ImmutableArray<int> items)
                {
                    // No `!items.IsDefault` needed because of [NonDefault]
                    return items.Length > 0 ? items[0] : 0;
                }

                public static int BagHead(ItemBag bag)
                {
                    // No `!bag.Items.IsDefault` needed because of [NonDefault] on record field
                    return bag.Items.Length > 0 ? bag.Items[0] : 0;
                }

                [return: NonDefault]
                public static ImmutableArray<int> SafeCreate(int x) => ImmutableArray.Create(x);

                public static int ChainedCall(int x) => HeadOrZero(SafeCreate(x));

                // Bug 1: passing default(ImmutableArray<int>) to [NonDefault] parameter
                public static int BadPassDefault() => HeadOrZero(default);

                // Bug 2: returning default(ImmutableArray<int>) from [return: NonDefault]
                [return: NonDefault]
                public static ImmutableArray<int> BadReturnDefault() => default;

                // Bug 3: constructing ItemBag with default(ImmutableArray<int>)
                public static ItemBag BadBag() => new ItemBag(default);
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableSmtAnalyzer());
        var errors = diags.Where(d => d.Id == "VER102").ToList();

        // Safe methods must produce ZERO VER102 failures
        Assert.DoesNotContain(errors, d => d.GetMessage().Contains("of ArrayRules.HeadOrZero fails:"));
        Assert.DoesNotContain(errors, d => d.GetMessage().Contains("of ArrayRules.BagHead fails:"));
        Assert.DoesNotContain(errors, d => d.GetMessage().Contains("of ArrayRules.SafeCreate fails:"));
        Assert.DoesNotContain(errors, d => d.GetMessage().Contains("of ArrayRules.ChainedCall fails:"));

        // Buggy methods must each trigger VER102
        Assert.Contains(errors, d => d.GetMessage().Contains("of ArrayRules.BadPassDefault fails:"));
        Assert.Contains(errors, d => d.GetMessage().Contains("of ArrayRules.BadReturnDefault fails:"));
        Assert.Contains(errors, d => d.GetMessage().Contains("of ArrayRules.BadBag fails:"));
    }

    [Fact]
    public async Task D21_GlobalOptionAndEditorConfig_EnforceNonDefaultImmutableArraysProjectWide()
    {
        const string source = """
            using System.Collections.Immutable;
            using Verifiable.Contracts;

            namespace SampleD21Global;

            [Verifiable]
            public static class GlobalArrayRules
            {
                public static int FirstOrZero(ImmutableArray<int> items) =>
                    items.Length > 0 ? items[0] : 0;

                public static int BadCaller() => FirstOrZero(default);
            }
            """;

        // Without option: FirstOrZero fails VER102 because items could be default
        var defaultDiags = await AnalyzerTestHarness.AnalyzeAsync(source, new VerifiableSmtAnalyzer());
        Assert.Contains(defaultDiags, d => d.Id == "VER102" && d.GetMessage().Contains("of GlobalArrayRules.FirstOrZero fails:"));

        // With MSBuild property: FirstOrZero proves cleanly, BadCaller fails VER102
        var msbuildOpts = new Dictionary<string, string>
        {
            ["build_property.VerifiableNonDefaultImmutableArrays"] = "true"
        };
        var msbuildDiags = await AnalyzerTestHarness.AnalyzeWithOptionsAsync(source, msbuildOpts, new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(msbuildDiags, d => d.Id == "VER102" && d.GetMessage().Contains("of GlobalArrayRules.FirstOrZero fails:"));
        Assert.Contains(msbuildDiags, d => d.Id == "VER102" && d.GetMessage().Contains("of GlobalArrayRules.BadCaller fails:"));

        // With .editorconfig option: same behavior
        var editorConfigOpts = new Dictionary<string, string>
        {
            ["verifiable.non_default_immutable_arrays"] = "true"
        };
        var editorDiags = await AnalyzerTestHarness.AnalyzeWithOptionsAsync(source, editorConfigOpts, new VerifiableSmtAnalyzer());
        Assert.DoesNotContain(editorDiags, d => d.Id == "VER102" && d.GetMessage().Contains("of GlobalArrayRules.FirstOrZero fails:"));
        Assert.Contains(editorDiags, d => d.Id == "VER102" && d.GetMessage().Contains("of GlobalArrayRules.BadCaller fails:"));
    }

    // -------------------------------------------------------------------------
    // D22: Spec.ForAll & Spec.Exists index quantifiers (+ RemoveAt / Insert)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task D22_SpecForAllAndExists_ProveRemoveAtAndInsertPreservation_AndRefuteBuggyClaim()
    {
        const string validSource = """
            using System.Collections.Immutable;
            using Verifiable.Contracts;

            namespace SampleD22;

            [Verifiable]
            public static class SequenceRules
            {
                [return: NonDefault]
                public static ImmutableArray<int> RemovePreservingOrder([NonDefault] ImmutableArray<int> items, int index)
                {
                    Spec.Requires(index >= 0 && index < items.Length, "Index is in bounds.");
                    Spec.Ensures<ImmutableArray<int>>(
                        r => r.Length == items.Length - 1,
                        "Removing an element decreases length by one.");
                    Spec.Ensures<ImmutableArray<int>>(
                        r => Spec.ForAll(0, index, i => r[i] == items[i]),
                        "Items before the removed index stay at their original positions.");
                    Spec.Ensures<ImmutableArray<int>>(
                        r => Spec.ForAll(index, r.Length, i => r[i] == items[i + 1]),
                        "Items after the removed index shift left by one.");

                    return items.RemoveAt(index);
                }

                [return: NonDefault]
                public static ImmutableArray<int> InsertAndFind([NonDefault] ImmutableArray<int> items, int index, int value)
                {
                    Spec.Requires(index >= 0 && index <= items.Length, "Insert index is valid.");
                    Spec.Ensures<ImmutableArray<int>>(
                        r => Spec.Exists(0, r.Length, i => r[i] == value),
                        "Inserted value exists at some valid index in the result.");
                    Spec.Ensures<ImmutableArray<int>>(
                        r => Spec.ForAll(0, index, i => r[i] == items[i]),
                        "Prefix before insert index is preserved.");

                    return items.Insert(index, value);
                }
            }
            """;

        var validDiags = await AnalyzerTestHarness.AnalyzeAsync(
            validSource,
            new VerifiableGatekeeperAnalyzer(),
            new VerifiableSmtAnalyzer());

        Assert.DoesNotContain(validDiags, d => d.Severity == DiagnosticSeverity.Error);

        const string buggySource = """
            using System.Collections.Immutable;
            using Verifiable.Contracts;

            namespace SampleD22Buggy;

            [Verifiable]
            public static class BuggySequenceRules
            {
                [return: NonDefault]
                public static ImmutableArray<int> WrongShiftClaim([NonDefault] ImmutableArray<int> items, int index)
                {
                    Spec.Requires(index >= 0 && index < items.Length, "Index is in bounds.");
                    // Buggy postcondition: claims elements up to r.Length stayed in place (false after index!)
                    Spec.Ensures<ImmutableArray<int>>(
                        r => Spec.ForAll(0, r.Length, i => r[i] == items[i]),
                        "Incorrectly claims all elements stay at original indices.");

                    return items.RemoveAt(index);
                }
            }
            """;

        var buggyDiags = await AnalyzerTestHarness.AnalyzeAsync(buggySource, new VerifiableSmtAnalyzer());
        Assert.Contains(buggyDiags, d => d.Id == "VER102" && d.GetMessage().Contains("Incorrectly claims all elements stay at original indices."));
    }

    [Fact]
    public void D20AndD22_RuntimeHelpers_EvaluateAccurately()
    {
        Assert.True(Spec.Implies(false, false));
        Assert.True(Spec.Implies(false, true));
        Assert.False(Spec.Implies(true, false));
        Assert.True(Spec.Implies(true, true));

        var arr = ImmutableArray.Create(2, 4, 6, 8);
        Assert.True(Spec.ForAll(0, arr.Length, i => arr[i] % 2 == 0));
        Assert.False(Spec.ForAll(0, arr.Length, i => arr[i] < 6));
        Assert.True(Spec.ForAll(5, 3, _ => false)); // empty range is vacuously true

        Assert.True(Spec.Exists(0, arr.Length, i => arr[i] == 6));
        Assert.False(Spec.Exists(0, arr.Length, i => arr[i] == 7));
        Assert.False(Spec.Exists(5, 3, _ => true)); // empty range is false
    }

    // -------------------------------------------------------------------------
    // D23: VER301 (complex Spec.* clause) & VER302 (missing `because`)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task D23_SpecQualityAnalyzer_FlagsDenseConjunctsNestedTernariesAndMissingBecause()
    {
        const string source = """
            using Verifiable.Contracts;

            namespace SampleD23;

            [Verifiable]
            public static class SpecQualityProbe
            {
                public static int Clean(int a, int b, int c)
                {
                    Spec.Requires(a > 0 && b > 0 && c > 0, "All three inputs must be positive.");
                    Spec.Ensures<int>(r => r >= a && r >= b, "Result is at least a and b.");
                    return a + b + c;
                }

                public static int TooManyConjuncts(int a, int b, int c, int d)
                {
                    Spec.Requires(a > 0 && b > 0 && c > 0 && d > 0, "Too many clauses in one Requires.");
                    Spec.Ensures<int>(r => r > 0);
                    return a + b + c + d;
                }

                public static int NestedTernary(int x, bool flagA, bool flagB)
                {
                    Spec.Ensures<int>(r => flagA ? (flagB ? r > 1 : r == 1) : r == 0, "Nested ternary rule.");
                    Spec.Requires(x >= 0, "   ");
                    return flagA ? (flagB ? x + 2 : 1) : 0;
                }
            }
            """;

        var diags = await AnalyzerTestHarness.AnalyzeAsync(source, new SpecQualityAnalyzer());

        // Clean method should have zero VER301 / VER302
        var cleanSpan = source.IndexOf("public static int Clean");
        var tooManySpan = source.IndexOf("public static int TooManyConjuncts");
        Assert.DoesNotContain(diags, d => d.Location.SourceSpan.Start >= cleanSpan && d.Location.SourceSpan.Start < tooManySpan);

        // VER301 on TooManyConjuncts (with FixKind = VER301_SplitConjuncts)
        var ver301s = diags.Where(d => d.Id == "VER301").ToList();
        Assert.Equal(2, ver301s.Count);
        Assert.Contains(ver301s, d =>
            d.Severity == DiagnosticSeverity.Info &&
            d.Properties.TryGetValue("FixKind", out var fk) && fk == "VER301_SplitConjuncts" &&
            d.GetMessage().Contains("-- COMPLEX CONTRACT"));

        // VER301 on NestedTernary (without split FixKind)
        Assert.Contains(ver301s, d =>
            !d.Properties.ContainsKey("FixKind") &&
            d.GetMessage().Contains("nested ternary"));

        // VER302 on omitted `because` and whitespace-only `because`
        var ver302s = diags.Where(d => d.Id == "VER302").ToList();
        Assert.Equal(2, ver302s.Count);
        Assert.All(ver302s, d =>
        {
            Assert.Equal(DiagnosticSeverity.Info, d.Severity);
            Assert.Contains("-- MISSING CONTRACT EXPLANATION", d.GetMessage());
        });

        // Configurable threshold via .editorconfig (`verifiable.max_spec_clauses = 4`) silences the 4-clause Requires
        var relaxedOpts = new Dictionary<string, string>
        {
            ["verifiable.max_spec_clauses"] = "4"
        };
        var relaxedDiags = await AnalyzerTestHarness.AnalyzeWithOptionsAsync(source, relaxedOpts, new SpecQualityAnalyzer());
        Assert.Single(relaxedDiags.Where(d => d.Id == "VER301")); // only the nested ternary remains
    }
}
