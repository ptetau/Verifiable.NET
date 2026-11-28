using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Verifiable.Query;

/// <summary>What <c>explain</c> says about one rule.</summary>
internal sealed record RuleInfo(
    string Id,
    string Title,
    string Category,
    string Severity,
    string Why,
    string Fix,
    string? Bad,
    string? Good,
    IReadOnlyList<string> CodeFixes);

/// <summary>
/// The rule catalogue behind <c>explain</c> (requirement R15). Titles, categories and severities come
/// from the analyzers' own descriptors; this adds why each rule exists, how to fix it, and a worked example.
/// </summary>
internal static class RuleCatalog
{
    private sealed record Text(string Why, string Fix, string? Bad, string? Good);

    private static readonly Dictionary<string, Text> Texts = new(StringComparer.Ordinal)
    {
        ["VER001"] = new(
            "A proof is about values. Mutable fields and shared mutable objects let other code change what the proof relied on, so it would no longer mean what it says.",
            "Make the type immutable: a readonly record struct or record with init-only members, and `with` expressions for changes.",
            "[Verifiable]\npublic class Counter\n{\n    public int Count;\n    public void Increment() => Count++;\n}",
            "[Verifiable]\npublic readonly record struct Counter(int Count)\n{\n    public Counter Increment() => this with { Count = Count + 1 };\n}"),
        ["VER002"] = new(
            "Mutable collections can change behind a proof's back, and the prover models only immutable collections.",
            "Use ImmutableList, ImmutableArray or ImmutableDictionary.",
            "[Verifiable]\npublic static class Cart\n{\n    public static int Count(List<int> items) => items.Count;\n}",
            "[Verifiable]\npublic static class Cart\n{\n    public static int Count(ImmutableList<int> items) => items.Count;\n}"),
        ["VER003"] = new(
            "A constrained type is only trustworthy if every value of it passed IsValid. Building one any other way skips the check.",
            "Create values with the generated From or TryFrom, never `new` or `default`.",
            "var price = new Price();",
            "var price = Price.From(100m);"),
        ["VER004"] = new(
            "A proof must hold every time. Reading the clock, random numbers, GUIDs or the environment gives different answers on different runs.",
            "Pass the value in as a parameter, read in the calling shell (the code fix can hoist eligible reads).",
            "[Verifiable]\npublic static class Orders\n{\n    public static long Stamp() => DateTime.UtcNow.Ticks;\n}",
            "[Verifiable]\npublic static class Orders\n{\n    public static long Stamp(long nowTicks) => nowTicks;\n}"),
        ["VER005"] = new(
            "A throw is a hidden exit the prover cannot follow, so a proof about the result would not cover it.",
            "Return a Result<T, TError> (or another union) and let the caller decide.",
            "[Verifiable]\npublic static class Accounts\n{\n    public static int Withdraw(int balance, int amount)\n    {\n        if (amount > balance) throw new InvalidOperationException();\n        return balance - amount;\n    }\n}",
            "[Verifiable]\npublic static class Accounts\n{\n    public static Result<int, string> Withdraw(int balance, int amount) =>\n        amount > balance ? Result<int, string>.Fail(\"Insufficient funds.\") : Result<int, string>.Ok(balance - amount);\n}"),
        ["VER006"] = new(
            "A call into unverified code, or a virtual or dynamic call, has no contract the prover can rely on, so anything could happen there.",
            "Mark the callee [Verifiable] (with contracts), call a verified method instead, or move the call to the shell around the pure core (the Sandwich split code fix).",
            "public static class Helpers\n{\n    public static int Twice(int x) => x * 2;\n}\n\n[Verifiable]\npublic static class Calc\n{\n    public static int F(int x) => Helpers.Twice(x);\n}",
            "[Verifiable]\npublic static class Helpers\n{\n    public static int Twice(int x)\n    {\n        Spec.Requires(x >= -1000 && x <= 1000, \"Small inputs.\");\n        return x * 2;\n    }\n}\n\n[Verifiable]\npublic static class Calc\n{\n    public static int F(int x)\n    {\n        Spec.Requires(x >= -1000 && x <= 1000, \"Small inputs.\");\n        return Helpers.Twice(x);\n    }\n}"),
        ["VER007"] = new(
            "Some values stand for work that must happen exactly once, such as a command that must reach the database. Dropping or reusing one loses or repeats that work.",
            "Consume every [MustConsume] value exactly once on every path, or pass it to a method marked [ConsumesObligation].",
            null, null),
        ["VER101"] = new(
            "A contract the prover cannot translate would be silently ignored, so it is reported instead.",
            "Rewrite the contract with supported pure expressions, move a misplaced Spec.Requires to the top of the method, or split it into helpers.",
            "public static int F(int x)\n{\n    var y = x + 1;\n    Spec.Requires(x > 0, \"Positive.\");\n    return y;\n}",
            "public static int F(int x)\n{\n    Spec.Requires(x > 0, \"Positive.\");\n    var y = x + 1;\n    return y;\n}"),
        ["VER102"] = new(
            "Z3 found concrete inputs that break a contract, an invariant, or a safety check such as overflow. The counterexample shows them.",
            "Strengthen the preconditions or correct the body so the contract holds on every path.",
            "public static int Discount(int price)\n{\n    Spec.Requires(price >= 0 && price <= 1000, \"Small prices.\");\n    Spec.Ensures<int>(r => r < price, \"Always cheaper.\");\n    return price - price / 10;\n}",
            "public static int Discount(int price)\n{\n    Spec.Requires(price >= 0 && price <= 1000, \"Small prices.\");\n    Spec.Ensures<int>(r => r <= price, \"Never dearer.\");\n    return price - price / 10;\n}"),
        ["VER103"] = new(
            "The solver gave up (unknown or timeout). That is never counted as a proof.",
            "Split the obligation named in the diagnostic, add a helper with its own contract, add loop invariants, or raise --timeout.",
            null, null),
        ["VER104"] = new(
            "Without Z3 nothing can be proved, and unchecked code must not pass as verified.",
            "Run `verifiable-query tools install z3`, or set VERIFIABLE_Z3_PATH.",
            null, null),
        ["VER105"] = new(
            "When a method's assumptions contradict each other, every goal is trivially true, so its proofs would mean nothing.",
            "Fix the contradictory Spec.Requires or the callee guarantee the diagnostic names.",
            "public static int F(int x)\n{\n    Spec.Requires(x > 5 && x < 3, \"Impossible.\");\n    Spec.Ensures<int>(r => r == x, \"Returns its input.\");\n    return x;\n}",
            "public static int F(int x)\n{\n    Spec.Requires(x > 3 && x < 5, \"Possible.\");\n    Spec.Ensures<int>(r => r == x, \"Returns its input.\");\n    return x;\n}"),
        ["VER106"] = new(
            "Z3 could not tell whether the assumptions can all hold, so the proofs might be vacuous.",
            "Simplify the contracts the method relies on, or split it into helpers.",
            null, null),
        ["VER107"] = new(
            "A project or file switched off a proof diagnostic (VER101-VER106), so failing proofs would pass silently.",
            "Remove the suppression, or set VerifiableAllowWeakenedProofs on purpose.",
            null, null),
        ["VER108"] = new(
            "Spec.Hole marks a body nobody has written yet. A placeholder must never ship or count as proved.",
            "Write the body so it meets its contracts, then remove Spec.Hole. `verifiable-query pack --holes` gives the context.",
            "public static int Fee(int nights)\n{\n    Spec.Ensures<int>(r => r == nights * 100, \"A night costs 100.\", clause: \"C1\");\n    return Spec.Hole<int>(\"C1\");\n}",
            "public static int Fee(int nights)\n{\n    Spec.Ensures<int>(r => r == nights * 100, \"A night costs 100.\", clause: \"C1\");\n    return nights * 100;\n}"),
        ["VER109"] = new(
            "Whoever fills in a body edits the same method that holds its contract. Without a lock it could weaken the contract until the proof passes.",
            "Restore the generated signature and contracts exactly; change only the body. If the spec changed, regenerate the method and its lock.",
            null, null),
        ["VER110"] = new(
            "A lock that cannot be read protects nothing.",
            "Regenerate it with `verifiable-query lock --out <name>.contracts.lock.json`.",
            null, null),
        ["VER111"] = new(
            "If two different results both meet a contract, a body can pass its proof and still be wrong.",
            "Strengthen the postcondition until it fixes one result, send the gap back to Resolve, or mark the method [AnyValidResult(\"why\")].",
            "public static int Fee(int nights)\n{\n    Spec.Ensures<int>(r => r >= nights * 90 && r <= nights * 100, \"Within the band.\");\n    return nights * 100;\n}",
            "public static int Fee(int nights)\n{\n    Spec.Ensures<int>(r => r == nights * 100, \"A night costs 100.\");\n    return nights * 100;\n}"),
        ["VER112"] = new(
            "A migration must give every valid old value a new value, or some old records would be lost; the prover can only show that for a static, verified mapping that always returns.",
            "Make the migration static, keep it in [Verifiable] code, and return the new type itself (not nullable, not Result).",
            null, null),
        ["VER113"] = new(
            "A field the mapping never reads is lost in the new type. Losing data must be a decision, not an accident.",
            "Map the field to the new type, or name it: [Migration(Drops = new[] { \"Notes\" })].",
            "[Verifiable]\npublic static class Migrations\n{\n    [Migration]\n    public static NewStay Migrate(OldStay old) => new NewStay(old.Nights);\n}\n\npublic sealed record OldStay(int Nights, string Notes);\npublic sealed record NewStay(int Nights);",
            "[Verifiable]\npublic static class Migrations\n{\n    [Migration(Drops = new[] { \"Notes\" })]\n    public static NewStay Migrate(OldStay old) => new NewStay(old.Nights);\n}\n\npublic sealed record OldStay(int Nights, string Notes);\npublic sealed record NewStay(int Nights);"),
        ["VER114"] = new(
            "A Drops entry for a field that is read, or does not exist, means the list no longer says what is lost.",
            "Remove the entry, or fix its name (`parameter.Field` when the migration merges several types).",
            null, null),
        ["VER201"] = new(
            "The generator can only make a safe constrained type from a readonly partial record struct.",
            "Declare it as `public readonly partial record struct Name { public static bool IsValid(T value) => ...; }`.",
            null, null),
        ["VER202"] = new(
            "A constrained type is defined by its IsValid method; without a valid one there is no rule to check.",
            "Add `public static bool IsValid(T value)` returning the rule as a pure expression.",
            null, null),
        ["VER203"] = new(
            "A [MapTo] projection must map every target member from a source member, or values would be lost silently.",
            "Fix the projection so each target member has a matching source member.",
            null, null),
        ["VER204"] = new(
            "A date with no value in a dated table would leave the rule undefined on that day.",
            "Make each row's `to` the day before the next row's `from`, or leave `to` empty so a row runs until the next one starts.",
            "# type: decimal\nfrom,to,value\n2026-01-01,2026-06-30,100\n2026-07-05,,120",
            "# type: decimal\nfrom,to,value\n2026-01-01,2026-06-30,100\n2026-07-01,,120"),
        ["VER205"] = new(
            "Two values for one date make the rule ambiguous.",
            "Shorten one range so no two rows cover the same day.",
            "# type: decimal\nfrom,to,value\n2026-01-01,2026-07-03,100\n2026-07-01,,120",
            "# type: decimal\nfrom,to,value\n2026-01-01,2026-06-30,100\n2026-07-01,,120"),
        ["VER206"] = new(
            "A table that cannot be read generates no lookup.",
            "Start with `# type: decimal` (int, long, decimal, bool or string), then a `from,to,value` header and yyyy-MM-dd dates.",
            null, null),
        ["VER207"] = new(
            "Versions of a dated rule must be interchangeable, and only one may start on a given day.",
            "Give each version a distinct date and the same signature, set `Rule = \"Name\"`, and put them in a static partial class.",
            null, null),
        ["VER208"] = new(
            "Effects are requests the domain hands to the shell. Each needs a name of its own and plain inputs, and the domain must not wait on a result, or it would be doing I/O itself.",
            "Declare the effects as void, non-generic methods with distinct names on a non-nested partial interface marked [Effects].",
            null, null),
        ["VER301"] = new(
            "One long contract is hard to read and gives one vague failure. Separate clauses fail separately and say which part broke.",
            "Split the conjunction into several Spec calls (the code fix does this).",
            null, null),
        ["VER302"] = new(
            "The `because` string is what a person reads when a contract fails, and what traces it back to the spec.",
            "Add a plain-English `because` argument.",
            "Spec.Requires(x > 0);",
            "Spec.Requires(x > 0, \"Quantities are positive.\");"),
        ["VER401"] = new(
            "A clause the spec signed off with no code behind it is a requirement nobody built.",
            "Add `clause: \"C...\"` to the contract that implements it, or remove the clause from the spec.",
            null, null),
        ["VER402"] = new(
            "A contract with no clause traces to no sentence a person signed off, so a spec change cannot find it.",
            "Add `clause: \"C...\"` (or `Clause = \"C...\"` on an attribute).",
            "Spec.Requires(nights >= 1, \"At least one night.\");",
            "Spec.Requires(nights >= 1, \"At least one night.\", clause: \"C4\");"),
        ["VER403"] = new(
            "A clause ID the spec does not have is a typo or a stale reference.",
            "Fix the ID, or add the clause to the spec.",
            null, null),
        ["VER404"] = new(
            "A manifest that cannot be read turns the drift gate off.",
            "Write it as {\"clauses\":[{\"id\":\"C1\",\"text\":\"...\"}]} with unique IDs.",
            null, null),
    };

    private static readonly Dictionary<string, (string Title, string Category, string Severity)> Extra = new(StringComparer.Ordinal)
    {
        ["VER111"] = ("Contract allows more than one answer", "Verifiable.SMT", "Error"),
    };

    /// <summary>Every rule, in ID order.</summary>
    public static IReadOnlyList<RuleInfo> All()
    {
        var descriptors = typeof(Verifiable.Analyzers.VerifiableDescriptors)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(DiagnosticDescriptor))
            .Select(f => (DiagnosticDescriptor)f.GetValue(null)!)
            .Concat(new Verifiable.Analyzers.ProofSuppressionAuditor().SupportedDiagnostics)
            .Concat(typeof(Verifiable.Generators.DatedRulesGenerator).Assembly.GetType("Verifiable.Generators.GeneratorDescriptors")!
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(DiagnosticDescriptor))
                .Select(f => (DiagnosticDescriptor)f.GetValue(null)!))
            .GroupBy(d => d.Id)
            .Select(g => g.First())
            .ToDictionary(d => d.Id, StringComparer.Ordinal);

        var fixes = CodeFixProviders()
            .SelectMany(p => p.FixableDiagnosticIds.Select(id => (id, p.GetType().Name)))
            .GroupBy(x => x.id)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList());

        return descriptors.Keys.Concat(Extra.Keys).Distinct().OrderBy(id => id, StringComparer.Ordinal)
            .Select(id =>
            {
                var (title, category, severity) = descriptors.TryGetValue(id, out var d)
                    ? (d.Title.ToString(), d.Category, d.DefaultSeverity.ToString())
                    : Extra[id];
                var text = Texts.TryGetValue(id, out var t) ? t : new Text("(not documented yet)", "(not documented yet)", null, null);
                return new RuleInfo(id, title, category, severity, text.Why, text.Fix, text.Bad, text.Good,
                    fixes.TryGetValue(id, out var f) ? f : Array.Empty<string>());
            })
            .ToList();
    }

    public static IReadOnlyList<string> DocumentedIds => Texts.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>Every code fix the Verifiable.CodeFixes assembly ships.</summary>
    public static IReadOnlyList<CodeFixProvider> CodeFixProviders() =>
        typeof(Verifiable.CodeFixes.FreezeMutableStateCodeFixProvider).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(CodeFixProvider).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .Select(t => (CodeFixProvider)Activator.CreateInstance(t)!)
            .ToList();
}
