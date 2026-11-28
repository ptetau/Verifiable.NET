# Verifiable.NET User Manual

This manual is organized around the concrete problems you run into while building reliable C# applications, with plain-English recipes you can copy straight into your project.

---

### Bring your own Z3

If build agents already provide Z3, reference `Verifiable.Contracts`,
`Verifiable.Analyzers`, `Verifiable.Generators`, and `Verifiable.CodeFixes` at
matching versions instead of `Verifiable.NET`. Use the component `.nupkg` files
from the GitHub release assets (or pack this checkout) in your own feed.
The component packages do not
require `Verifiable.Z3`. Put `z3` on `PATH`, set `VERIFIABLE_Z3_PATH`, or pass
`-p:VerifiableZ3Path=/absolute/path/to/z3`. The analyzer package exposes
`VerifiableZ3Path`, `VerifiableAgent`, `VerifiableAllowMissingZ3` and
`VerifiableAllowWeakenedProofs` to the compiler; no hand-written `CompilerVisibleProperty` entries are needed.

If Z3 cannot be found, `VER104` stops the build, because nothing was proved.
To build without proofs on purpose (for example on a machine that only packs),
set `<VerifiableAllowMissingZ3>true</VerifiableAllowMissingZ3>`. `VER104` then
stays a warning, even under `TreatWarningsAsErrors` or `-warnaserror`.

`[Verifiable]` code is sound by default. Anything that lowers a proof check (a
`<NoWarn>`, a `#pragma warning disable`, a `[SuppressMessage]`, an `.editorconfig`
severity) is reported by `VER107`, and that report is a build **error**. A project
that has to weaken a check on purpose, for example while migrating, sets
`<VerifiableAllowWeakenedProofs>true</VerifiableAllowWeakenedProofs>`. `VER107` then
stays a warning, even under `TreatWarningsAsErrors` or `-warnaserror`, so each
weakening is still listed in the build output.

## 1. Making invalid values unrepresentable (`[TypeAlias<T>]`)

**Problem:** Primitive types like `int`, `decimal`, `string`, and `Guid` let bad data (`0`, `-5m`, empty strings, `Guid.Empty`) walk through your entire system until something crashes later.

**Solution:** Declare a one-line `readonly partial record struct` with `[TypeAlias<T>]` (or `[Refined<T>]`), a single predicate expression `IsValid`, and a plain-English reason explaining *why* the rule exists:

```csharp
using Verifiable.Contracts;

[TypeAlias<int>("Todo IDs start at 1 so zero never aliases an uninitialized record.")]
public readonly partial record struct TodoId
{
    public static bool IsValid(int value) => value >= 1;
}

[TypeAlias<string>("Titles must be 1–200 characters so blank items never enter the board.")]
public readonly partial record struct TodoTitle
{
    public static bool IsValid(string value) => value is { Length: >= 1 and <= 200 };
}
```

### What gets generated for you

For each constrained type, `Verifiable.Generators` creates:

- Primary constructor `(TUnderlying Value)` and `IComparable<T>`
- Implicit widening back to `TUnderlying` (`int raw = todoId;`) so existing math and formatting stay ergonomic
- Relational operators (`<`, `<=`, `>`, `>=`) for numeric types and `.Length` / `ToString()` for `string`
- Checked `From(value)` (adds `Spec.Requires(IsValid(value), description)` for Z3 and throws `ArgumentOutOfRangeException` with your description on invalid runtime input)
- Safe `TryFrom(value, out result)` and `TryFrom(value) -> MarshalResult<T>`
- Validating `<Name>JsonConverter` (handles JSON values and dictionary keys, throwing `JsonException` with your description if invalid wire data arrives)
- For `0 <= value < N` bounds predicates (`int`): `UpperBoundExclusive` and `ReadUnchecked<T>(ReadOnlySpan<T>)`

> **Tip:** Both `[TypeAlias<T>("...")]` and `[Refined<T>("...")]` are supported and generate identical runtime code; `[TypeAlias<T>]` is recommended for clarity.

---

## 2. Grouping multi-field business rules (`[Invariant]`)

**Problem:** Individual fields can all look valid on their own (`ActiveCount = 5`, `Items = empty`), yet together they violate a rule of your domain model.

**Solution:** Put `[Invariant("why")]` on any parameterless `bool` method or computed property on your immutable record or struct:

```csharp
[Verifiable]
public readonly record struct LedgerState(
    ImmutableDictionary<AccountId, Account> Accounts,
    ImmutableList<PostedJournalEntry> Journal,
    JournalEntryId NextEntryId,
    NonNegativeDecimal TotalDebits,
    NonNegativeDecimal TotalCredits)
{
    [Invariant("Across the entire ledger, total debits must always equal total credits.")]
    public bool IsBalanced() => TotalDebits == TotalCredits;
}
```

Both Z3 (at compile time) and generated `[InboundPort]` gates (at runtime) automatically verify every `[Invariant]` on return values and incoming payloads.

---

## 3. Proving method behavior for all inputs (`Spec.*`)

**Cross-assembly verification:** When a library is compiled with `Verifiable.Generators`, `ContractMetadataGenerator` automatically exports canonical `[assembly: VerifiableContractMetadata(...)]` metadata for externally accessible `[Verifiable]` / `[Ghost]` helpers, `Spec.Requires` / `Spec.Ensures` contracts, `[Invariant]` members, `[TypeAlias<T>]` predicates, and `[MapTo]` projections. Referencing compilations bind and verify these contracts across assembly boundaries with Z3 automatically. Helpers that rely on private non-constant implementation details (or assemblies built without `Verifiable.Generators`) fail closed with `VER101` rather than assuming unverified behavior.

**Problem:** You want to be 100% sure a method maintains invariants, never overflows, and handles every branch—without relying solely on example unit tests.

**Solution:** Write standard C# assertions with `Spec.Requires`, `Spec.Ensures<T>`, `Spec.Ensures`, and `Spec.Result<T>()`. Passing a plain-English `because` message as the second argument documents *the spirit of the rule* and surfaces inside Z3 failure reports:

```csharp
public static LedgerState PostCheckedAssetTransfer(
    LedgerState state,
    AccountId from,
    AccountId to,
    PositiveAmount amount,
    string memo,
    UnixTimestamp now)
{
    Spec.Requires(
        from != to && state.Accounts.ContainsKey(from) && state.Accounts.ContainsKey(to) && state.Accounts[from].Balance >= amount,
        "Transfers require two distinct existing accounts and enough balance in the source account to avoid overdraft.");
    Spec.Ensures<LedgerState>(
        result => result.Accounts.ContainsKey(from) && result.Accounts[from].Balance >= 0m,
        "After the transfer, the source account remains non-negative.");
    Spec.Ensures<LedgerState>(
        result => result.TotalDebits == result.TotalCredits,
        "Total debits still equal total credits across the ledger.");
    // ...
}
```

All `Spec.*` methods carry `[Conditional("VERIFIABLE_SPEC")]`, so the compiler strips them completely from compiled IL—zero runtime overhead.

**Where preconditions go.** Put `Spec.Requires` / `Spec.RequiresWhen` in the method's *leading contract block*: the top-level statements before the first statement that is not a `Spec.*` call. `Spec.Requires`, `Spec.Ensures` and their `When` forms may come in any order inside it. The same rule holds for constructors and accessors. An expression-bodied member whose whole body is one `Spec.*` call also counts. A `Spec.Requires` anywhere else (inside an `if`, `else`, loop or nested block, after an ordinary statement, or inside a lambda or local function) is a `VER101` error. It is not assumed when the method is proved and not required of callers, so it cannot hide a bug on another path. For a rule that applies only in some cases, write `Spec.RequiresWhen(condition, requirement, because)` at the top, or have callers check the condition with an `if`. The prover, the `[InboundPort]` runtime gate and the cross-assembly contract export all use this rule.

### Proving `while` loops (`Spec.LoopInvariant` & `Spec.Decreases`)

**Problem:** Loops can loop forever or drift out of bounds unless you state what stays true each step and why the loop must stop.

**Solution:** Inside any `while` loop in a `[Verifiable]` method, add `Spec.LoopInvariant(cond, because)` and `Spec.Decreases(measure, because)`:

```csharp
var i = 0;
while (i < lines.Count)
{
    Spec.LoopInvariant(
        i >= 0 && i <= lines.Count && debits >= 0m && credits >= 0m,
        "Index stays in range and running debit/credit sums never go negative.");
    Spec.Decreases(
        lines.Count - i,
        "Remaining journal lines shrink by one on each iteration.");

    // process lines[i]
    i = i + 1;
}
```

Z3 checks four facts automatically:
1. `LoopInvariant` holds right before entering the loop.
2. `LoopInvariant` still holds at the end of every iteration.
3. `Decreases` starts `>= 0`.
4. `Decreases` stays `>= 0` and strictly drops on every iteration.

**The rules.**
- `Spec.LoopInvariant` and `Spec.Decreases` are the first statements of the loop body, before any other statement. They are checked where the loop is entered and at the top of every pass, so one written further down would not mean what it says; it is a `VER101` error.
- Every `while`, `do` and `for` loop needs a `Spec.Decreases`. Without it nothing shows the loop ends, and a proof about what follows it would hold without ever being used. A `foreach` over a finite collection ends on its own and needs none.
- Loops can be nested. Each loop has its own invariant and measure; an inner loop runs all its passes on every pass of the outer one.
- `foreach` over an `ImmutableArray<T>`, `ImmutableList<T>` or `IReadOnlyList<T>` hands out the items of the sequence, so facts about them from `Spec.Requires` (for example `xs.All(...)`) hold for each item. The loop does not know which item it is on.

### Recursion

A method that calls itself, or that is part of a cycle of `[Verifiable]` methods that call each other, needs a termination measure at the top of each of them: `Spec.Decreases(measure, because)`, a non-negative `int` or `long` expression over the parameters. At every recursive call the measure of the callee must be `>= 0` and smaller than the caller's measure on entry. A recursive method without one is a `VER101` error; a measure that does not go down is a `VER102` proof failure. Without this, a method that never returns would prove any postcondition.

```csharp
public static int Sum(int n)
{
    Spec.Requires(n >= 0 && n <= 200, "Small non-negative input.");
    Spec.Ensures<int>(r => 2 * r == n * (n + 1), "Triangle number.");
    Spec.Decreases(n, "n goes down by one at each call.");
    return n == 0 ? 0 : n + Sum(n - 1);
}
```

---

### Writing contracts with helpers

A pure helper in the same compilation can describe its result with its body. For example, this projection needs no field-by-field postcondition on `Project`:

```csharp
public record OrderDto(int Id, int Extras);
public readonly record struct OrderState(int Id, int Extras);

[Verifiable]
public static class Orders
{
    static OrderState Project(OrderDto dto, bool includeExtras = true) =>
        new(dto.Id, includeExtras ? dto.Extras : 0);

    public static OrderState Adapt(OrderDto dto)
    {
        Spec.Ensures<OrderState>(s => s.Extras == dto.Extras);
        return Project(dto);
    }
}
```

`Adapt` calls `Project` with `includeExtras: true`; the default is **not** a precondition on `Project`. Explicit callers can still pass `false`. The verifier can expand a supported, statically resolved, non-generic helper with an expression body or a single return after leading `Spec.Requires` or `Spec.Ensures` statements. Recursive definitions are not expanded. Use `[Ghost]` for a helper intended only for specifications. A call inside a specification must still satisfy the helper's preconditions.

Nullable expressions work in both the implementation and the contract. For `decimal? value`, `value >= 10m` is false when `value` is null, and `value ?? 0m` returns zero in that case. For a struct instance method, use a boolean postcondition such as `Spec.Ensures(Spec.Result<bool>() == (Left != 0 || Right != 0));` to avoid capturing `this` in a lambda.

### Supported domain primitives & pure pipelines

- **Strings & Regular Expressions:** `string.IsNullOrEmpty`, `string.IsNullOrWhiteSpace` (Unicode whitespace), `Trim()`, `Contains`, `StartsWith`, `EndsWith` (ordinal only: `StartsWith(string)` and `EndsWith(string)` without a `StringComparison` are culture-sensitive, so they are rejected with VER101, as are non-ordinal `StringComparison` values; write `StringComparison.Ordinal`; `Contains(string)` and the `char` overloads are ordinal already). Reading `s.Length` or calling a string method needs `s` proved non-null, and `x.Value` or `(int)x` on a nullable needs `x.HasValue`. Pattern kinds that are not modelled, such as list patterns, are rejected with VER101, bounded `string.Join` (up to 32 literal/`params` elements) and `char.ToString()` lower directly to Z3 string theory with path-guarded non-null receiver/argument checks. Static `Regex.IsMatch(input, constPattern[, options])`, `[GeneratedRegex]` factories, and `static readonly Regex` fields with constant patterns translate a decidable subset (anchors `^`/`$`/`\A`/`\Z`/`\z`, character classes, quantifiers, grouping, alternation) to Z3 `str.in_re` and always require non-null input. A quantifier on an anchor (`^*`, `^{2}`, `$?`) is rejected with VER101, as are patterns .NET itself refuses (a quantifier with nothing to repeat such as `{2}a`, or stacked quantifiers such as `a{2}{3}` and `a*{2}`); a `{` that does not form a valid `{n}`, `{n,}` or `{n,m}` stays literal text.
- **Enums & Closed-Domain Verification:** Every `enum` input automatically carries its underlying integral bounds (e.g. `0..255` for `byte`-backed enums), and checked/unchecked casts (including nullable targets and exact enum-to-`decimal` conversions) are modeled accurately. By default enums remain open to match C# runtime casting semantics; opt into closed-domain axioms (constraining non-`[Flags]` enums to declared members so newly added enum values surface missing branches as `VER102`) either project-wide via `<VerifiableClosedEnums>true</VerifiableClosedEnums>` or per enum with `[Verifiable] public enum OrderStatus { ... }`.
- **Object-Initializer Projections, Write-Once Boundary DTOs & `[MapTo]`:** Pure single-expression helpers expand both positional record constructors (`new State(dto.A, dto.B)`) and object initializers (`new State { A = dto.A, B = dto.B }`). Constructing and immediately initializing an external mutable boundary DTO in a single `new TargetDto { ... }` expression is supported for field-level postconditions, while post-init mutation (`VER001`) and reference-identity `==`/`Equals` on non-record classes (`VER101`) remain rejected. To eliminate 1:1 projection boilerplate, apply `[MapTo(typeof(OrderState))]` (with optional `MethodName = "..."`) on a source DTO type to generate a `[Verifiable]` extension method (`dto.ToOrderState()`), or apply `[MapTo]` on a `static partial` mapper method (`public static partial OrderResponse ToResponse(OrderDto dto);`) when mapping external types. Missing fields or non-implicit type mismatches emit `VER203` (use `[InboundPort]` when validating raw primitives into `[TypeAlias<T>]`).
- **Conditional Contracts & Index Quantifiers (`Spec.Implies`, `Spec.EnsuresWhen`, `Spec.RequiresWhen`, `Spec.ForAll`, `Spec.Exists`):** Express conditional rules directly without `!cond || ...` boilerplate via `Spec.Implies(antecedent, consequent)`, `Spec.RequiresWhen(cond, req, because)`, and `Spec.EnsuresWhen<T>(cond, r => ..., because)` (or `Spec.EnsuresWhen(cond, post, because)`). State index-relational sequence properties directly with bounded quantifiers `Spec.ForAll(startInclusive, endExclusive, i => ...)` and `Spec.Exists(startInclusive, endExclusive, i => ...)`, including order-preservation proofs across `ImmutableArray<T>` / `ImmutableList<T>` `RemoveAt` and `Insert`.
- **Pure LINQ over Immutable Sequences & `[NonDefault]`:** `Any`, `All`, `Count`, `Where`, `Select`, and `GroupBy` uniqueness predicates (e.g. `items.GroupBy(i => i.CategoryId).Any(g => g.Count() > 1)` or `.All(g => g.Count() == 1)`) over `ImmutableList<T>`, `ImmutableArray<T>`, `IImmutableList<T>`, and `IReadOnlyList<T>` lower to quantified Z3 array formulas. `ImmutableArray<T>` models `IsDefault` distinctly from `Empty`, requiring non-default arrays on reads, `Add`/`SetItem`/`RemoveAt`/`Insert`, and LINQ queries. Eliminate repeated `!arr.IsDefault` boilerplate by annotating parameters, fields/properties, or returns with `[NonDefault]` (or enabling project-wide `<VerifiableNonDefaultImmutableArrays>true</VerifiableNonDefaultImmutableArrays>` / `.editorconfig` `verifiable.non_default_immutable_arrays = true`), which assumes incoming arrays are initialized and verifies call sites, constructors, and returns never supply `default(ImmutableArray<T>)`.
- **Decimal Rounding & Pure Calendar Arithmetic:** `decimal.Round` / `Math.Round` (constant scale `0..28`, all `MidpointRounding` modes), `decimal.Truncate`, `decimal.Floor`, and `decimal.Ceiling` use exact linear integer/real constraints. Pure deterministic `DateOnly`, `DateTime`, `DateTimeOffset`, and `TimeSpan` operations (`AddDays`, `AddHours`, `AddMinutes`, `AddSeconds`, `AddMilliseconds`, `AddTicks`, `DayNumber`, `DayOfWeek`, `TotalSeconds`/`TotalHours`/`TotalDays`, subtraction, comparisons) are verified with automatic calendar-range and overflow checks, while ambient clock reads (`Now`, `UtcNow`, `Today`) continue to fail with `VER004`.

---

## 4. Reading Elm-style compiler errors

When a rule breaks, Verifiable.NET formats compiler and query diagnostics in an Elm-inspired layout so you can spot the problem in seconds:

- A **banner** (`-- PROOF FAILED ---- File.cs`) naming the check and file
- A short, 78-column reflowed sentence saying what happened and where
- The **exact code** as you wrote it—single-line spans use `^` carets, while multi-line blocks switch to Elm-style `|>` gutter markers
- A **second linked snippet** (and IDE `AdditionalLocations`) whenever two locations matter—such as the failing `Spec.Ensures` / `[Invariant]` / callee `Spec.Requires` declaration (`VER102`), the first consumption or binding site of a `[MustConsume]` token (`VER007`), or an in-source unverified callee (`VER006`)
- **Why this rule exists** — showing your `because` / `description` string (or a context-specific rationale for Gatekeeper rules `VER001`–`VER007`)
- **Contract**, **Counterexample** (including the concrete `result` value), and **failed-clause pinpointing** showing which conjunct broke along with evaluated `Left` / `Right` values and boundary/off-by-one hints
- **Typo suggestions** (Damerau-Levenshtein `"Did you mean ...?"`) in `Verifiable.Query` and `[TypeAlias<T>]` generator checks (`VER202`)
- A concrete, context-specific **Fix:** line

When `VERIFIABLE_AGENT=1` is set in CI or AI agent environments, `VER102` appends one single-line `<verifiable-counterexample>{...}</verifiable-counterexample>` JSON payload containing `"reason"`, `"counterexample"`, `"smtHash"`, and `"fix"`.

For full ANSI-colored multi-line Elm output (without MSBuild's single-line console truncation) and Elm-style rewriting of standard C# (`CS*`) compiler errors, run `verifiable-query check --project MyProject.csproj`, `verifiable-query build MyProject.csproj`, or `verifiable-query check --sarif build.sarif` (`--color auto|always|never`, honoring `NO_COLOR`). Inside `[Verifiable]` methods, `VerifiableNullabilitySuppressor` also automatically suppresses redundant C# `CS8602`/`CS8604` nullable warnings whenever Z3 formally proves the expression is non-null.

---

## 5. Validating HTTP and wire data cleanly (`[InboundPort]`)

**Problem:** Raw HTTP JSON uses primitives (`int`, `string`, `decimal`, mutable `List<T>`) and can violate your domain rules before reaching your core logic.

**Solution:** Decorate your entry-point method with `[InboundPort]`:

```csharp
[Verifiable]
public static class TodoEngine
{
    [InboundPort]
    public static TodoTransition Add(TodoBoard board, TodoTitle title, UnixTimestamp now) { /* ... */ }
}
```

`Verifiable.Generators` emits `RawTodoBoard`, `TodoBoardGate.TryMarshal(...)`, and `TodoEnginePort.Add(...)`, checking nested constrained fields, `[Invariant("...")]` checks, and leading `Spec.Requires(..., "...")` preconditions, returning `MarshalResult<T>` with human-readable `ContractViolation` objects you can serialize directly as `400 Bad Request`.

---

## 6. Tracking work that cannot be dropped (`[MustConsume]`)

**Problem:** Returning an unsaved command or uncommitted transaction token makes it easy for a refactor to accidentally skip committing or sending it on an error branch.

**Solution:** Mark the token struct `[MustConsume]`, create it normally, and consume it in `[ConsumesObligation]` methods (or return it up the call stack, or call `Obligation.Discard(token, "reason")`):

```csharp
[MustConsume]
public readonly record struct PendingTodoCommit(/* ... */);

[ConsumesObligation]
public static TodoCommitReceipt PersistCommit(SqliteConnection conn, PendingTodoCommit commit) { /* ... */ }
```

Every path that drops, overwrites, double-consumes, or discards a `[MustConsume]` value triggers `VER007 -- UNCONSUMED OBLIGATION` at compile time. The check follows `try`/`catch`/`finally` (a `finally` runs on every way out of the `try`, and a `catch` can start after any call in the `try`), loops, and a second consumption after only some branches consumed the token (`may be consumed more than once`).

To keep every token followable, `VER007` only accepts tokens in local variables and parameters:

- Pass a token only to a `[ConsumesObligation]` method (mark your own helper `[ConsumesObligation]` if it takes the token over). A call to any other method that takes the token by value is rejected.
- Do not store a token in an array, tuple, `Nullable<T>`, collection, field or property (`VER007 -- UNTRACKED OBLIGATION`). Deconstruct a tuple straight into locals, or return it.
- Do not hold a token in a field or property, either: a field or auto-property initializer (`static readonly T Shared = new(1);`) is rejected, and so is reading a token out of a field or property of another object (`Commit(holder.Tok)`), because each read would hand out the same obligation again. Pass the token itself. Reading a plain field of a token (`token.Id`) is fine.
- `using` on a token is a call to its `Dispose` on every way out of the scope. If `Dispose` is `[ConsumesObligation]`, the `using` consumes the token, so consuming it again inside the scope is a double consume. If `Dispose` is not a sink, the `using` does not consume it and the token must still be consumed.
- An async `[ConsumesObligation]` method must have its `Task` awaited or returned. `_ = CommitAsync(token)` is rejected.
- A token that a method hands back through an `out` parameter is tracked by the caller; a local function that consumes a captured token consumes it at each call.

---

## 7. Choosing where verification applies (Scopes)

You rarely want to mark every individual helper method one by one. Scopes cascade downward cleanly:

1. **Assembly level** — `[assembly: Verifiable]` covers every type and method in your shared domain library (used by `TodoApp.Shared`).
2. **Type level** — `[Verifiable]` on a class or struct covers all its methods (used by `AccountingApp.Shared`).
3. **Method level** — `[Verifiable]` on one critical calculation inside a larger legacy service.
4. **Opt-out** — `[Unverifiable]` excludes a single shell/adapter method or helper inside an otherwise verified class or assembly.

Nearest attribute wins: a method `[Unverifiable]` overrides a class `[Verifiable]`, and a class `[Verifiable]` overrides `[assembly: VerifiableCandidate]`.

---

## 8. Migrating legacy C# safely (`[VerifiableCandidate]` + CodeFixes)

**Problem:** Flipping `[Verifiable]` onto an older service immediately raises too many errors at once.

**Solution:**

1. Start with `[VerifiableCandidate]` at the assembly or class level. Every `VER001`–`VER006` rule reports as a **warning** instead of an error.
2. Apply automated refactorings from `Verifiable.CodeFixes`:
   - `VER001` / `VER002`: Freeze mutable classes/collections into `readonly record struct` and `Immutable*` collections.
   - `VER004`: The code fix hoists an eager clock or GUID read in a private method only when all callers can be updated safely in the same document. For public APIs, optional parameters, or reads inside a loop or lambda, move the operation to the calling shell manually. Supply one value per item when the original operation ran once per item.
   - `VER005`: Convert `throw` statements into explicit `Result<T, TError>` returns.
   - `VER006`: Apply **Sandwich I/O splitting** to separate I/O at the edges from pure logic in the middle.
3. Promote `[VerifiableCandidate]` to `[Verifiable]`, add `[TypeAlias<T>("...")]` types and `Spec.*` contracts, and let Z3 verify your core logic.

---

## 9. Asking Formal Questions (`Verifiable.Query` / `verifiable-query`)

**Problem:** You want to verify a domain hypothesis right now (for example *"does every ledger transition preserve `TotalDebits == TotalCredits`?"*), check why a property holds, or test a `--assume` hypothesis without modifying your source files.

**Solution:** Use `Verifiable.Query` (`verifiable-query`, a pure `.NET Core` CLI requiring only .NET + Z3):

- **Check a single method (`ask`):**
  ```bash
  dotnet run --project src/Verifiable.Query -c Release -- ask \
    --project samples/AccountingApp/AccountingApp.Shared/AccountingApp.Shared.csproj \
    --target LedgerEngine.PostTransfer \
    --assume "result.IsSuccess" \
    --assert "result.State.TotalDebits > state.TotalDebits"
  ```
- **Verify an invariant across every method returning a type (`check-invariant`):**
  ```bash
  dotnet run --project src/Verifiable.Query -c Release -- check-invariant \
    --project samples/AccountingApp/AccountingApp.Shared/AccountingApp.Shared.csproj \
    --type LedgerState \
    --expr "s => s.TotalDebits == s.TotalCredits"
  ```
- **Inspect available types, `[Invariant]` members, and `[Verifiable]` methods (`list`):**
  ```bash
  dotnet run --project src/Verifiable.Query -c Release -- list \
    --project samples/AccountingApp/AccountingApp.Shared/AccountingApp.Shared.csproj
  ```
- **Query quick snippets from stdin (`--stdin`) or standalone `.cs` files (`--file`):**
  Pipe C# directly into `--stdin`, or pass `--file FileA.cs --file FileB.cs`.
- **Exit codes & machine output:**
  - `0` — Claim proved across all reachable paths (Z3 unsat core lists exact `[Invariant]` / `[TypeAlias<T>]` / `Spec.Requires` rules and spirit strings; flags vacuous proofs if premises are unreachable).
  - `1` — Refuted (concrete counterexample + `file:line`).
  - `2` — Inconclusive (solver timeout/unknown).
  - `3` — Input/compilation error or unbound query expression (lists variables and properties in scope).
  - `4` — Z3 binary not found (`VERIFIABLE_Z3_PATH` / `scripts/install-z3.*`).
  Pass `--format json` for pure JSON, or set `VERIFIABLE_AGENT=1` with default `--format elm` to append `<verifiable-query-result>{...}</verifiable-query-result>`.

---

## 10. Diagnostic Reference (`VER001`–`VER202`)

| Code | Banner Title | What Happened & Typical Fix |
| --- | --- | --- |
| `VER001` | `MUTABLE STATE` | Mutable class, non-readonly struct, settable property, or `ref`/`out` aliasing inside a verified cage. Use `readonly record struct` or immutable `record` with `with`. |
| `VER002` | `MUTABLE COLLECTION` | Mutable collection (`List<T>`, `Dictionary<K,V>`, `T[]`, `HashSet<T>`). Switch to `ImmutableList<T>`, `ImmutableDictionary<K,V>`, `ImmutableHashSet<T>`, or `ImmutableArray<T>`. |
| `VER003` | `UNCHECKED INITIALIZATION` | `default(T)` or `new T()` skipped `[TypeAlias<T>]` / `[Invariant]`. Construct via `T.From(...)`, `T.TryFrom(...)`, or `[InboundPort]`. |
| `VER004` | `AMBIENT AUTHORITY` | Called `DateTime.Now`/`UtcNow`, `Guid.NewGuid()`, `Random`, `Stopwatch`, `Environment.*`, or touched mutable static state. Pass values in as parameters. |
| `VER005` | `DISALLOWED CONTROL FLOW` | Unannotated `while`/`foreach`, `async`/`await`, `throw`, `try/catch`, `lock`, `goto`, or a local function (its body is not analysed: use a `static` method instead). Annotate `while` loops with `Spec.LoopInvariant` + `Spec.Decreases`; use `Result<T, TError>` instead of exceptions. |
| `VER006` | `UNVERIFIED CALL` | Called an unverified external method, reflection, or `dynamic`. Mark deterministic helpers `[Verifiable]` / `[PureAxiom]`, or move I/O to the outer shell. |
| `VER007` | `UNCONSUMED OBLIGATION` / `UNTRACKED OBLIGATION` | Dropped, overwrote, discarded, or double-consumed a `[MustConsume]` token, passed it to a method that is not `[ConsumesObligation]`, or stored it in an array, tuple, `Nullable<T>`, field or collection. Route it to `[ConsumesObligation]`, return it, or call `Obligation.Discard(...)`. |
| `VER101` | `UNSUPPORTED CONTRACT` / `METHOD NOT CHECKED` | Contract or code could not be lowered to SMT (for example mismatched `Spec.Ensures<T>` return type, `Spec.LoopInvariant` placed outside a loop or below another loop-body statement, a `while`/`do`/`for` loop with no `Spec.Decreases`, or a recursive method with no `Spec.Decreases`). Rewrite with supported pure expressions. In a `[Verifiable]` method with no contracts and no obligation found, it is a warning, so the method does not look proved; mark it `[Unverifiable]` if it needs no proof. A `Spec.Requires` / `Spec.RequiresWhen` that is not in the method's leading contract block (inside a branch, loop, lambda or local function, or after an ordinary statement) is also `VER101`: it is not a precondition, so move it to the top of the method, or use `Spec.RequiresWhen(condition, requirement, because)` or an `if` guard in callers. |
| `VER102` | `PROOF FAILED` | Z3 proved a contract can be violated and produced a counterexample plus your `because`/`description` spirit text. Strengthen `Spec.Requires` or fix the bug. |
| `VER103` | `SOLVER COULD NOT DECIDE` | Z3 returned `unknown` or timed out. Simplify non-linear arithmetic, add loop invariants, or split helper lemmas. |
| `VER104` | `Z3 SOLVER NOT FOUND` | Z3 binary missing (`scripts/install-z3.ps1` / `scripts/install-z3.sh`), so no proof was checked. Build error by default, because unchecked code must not pass as verified. To build without proofs on purpose, set `<VerifiableAllowMissingZ3>true</VerifiableAllowMissingZ3>` (downgrades it to a warning). |
| `VER105` | `CONTRADICTORY PREMISES` | A proof rests on assumptions that cannot hold, so Z3 would "prove" any contract there. Reported at the method when no input satisfies all its assumptions (`Spec.Requires`, `[Invariant]`, `[TypeAlias<T>]`, callee guarantees, ...), listing the ones that clash. Also reported at a call when the callee's `Spec.Ensures` (or `[PureAxiom]`) cannot hold there, although the call is reachable: every goal after it on that path would be vacuous. Fix the conflicting rule. Dead branches and unreachable calls are not reported. Only the `Spec.Requires` calls in the leading contract block are entry preconditions; one inside a branch is a `VER101` error and is not assumed. |
| `VER106` | `VACUITY CHECK UNDECIDED` | Warning. Z3 could not decide, within its fixed work budget, whether a method's assumptions, or a callee guarantee at one of its calls, can hold, so its proofs may be vacuous. Reported once per method. Simplify the contracts it relies on (non-linear arithmetic, quantifiers) or split it into helpers with their own contracts. |
| `VER107` | `PROOF CHECK WEAKENED` | A setting lowers a proof check (`VER101`-`VER106`) below an error, so unproved code would build: `<NoWarn>` / `-nowarn`, a ruleset, `dotnet_diagnostic.VER10x.severity` (or a category or all-analyzer severity) in `.editorconfig` / `.globalconfig`, `#pragma warning disable`, or `[SuppressMessage]` / `[assembly: SuppressMessage]`. Reported once per setting, at the pragma or attribute, or without a location for project-wide settings. The message names the setting and how to undo it. Only checked in projects (and, for file-level settings, files) with `[Verifiable]` code. Default severity: `Error`; with `<VerifiableAllowWeakenedProofs>true</VerifiableAllowWeakenedProofs>` it is a `Warning`, so a project that weakens a check on purpose still lists every weakening. `VER104` as a warning under `VerifiableAllowMissingZ3` is the deliberate opt-out and is not reported. Limits: `VER107` itself can be switched off the same ways, and `<WarningsNotAsErrors>` only changes promotion, so it is not reported (see `docs/trusted-core.md`). |
| `VER201` | `INVALID CONSTRAINED TYPE` | Type with `[TypeAlias<T>]` / `[Refined<T>]` is missing `readonly partial record struct` modifiers. |
| `VER202` | `MISSING ISVALID PREDICATE` | Constrained struct is missing `public static bool IsValid(T value) => ...;`. |
| `VER203` | `INVALID MAPTO PROJECTION` | `[MapTo]` declaration is malformed, missing a matching source member for a target field/parameter, or requires narrowing/validation (use `[InboundPort]` for `[TypeAlias<T>]` targets). |
| `VER301` | `COMPLEX CONTRACT` | `Spec.Requires` / `Spec.Ensures` packs more than 3 top-level `&&` clauses (configurable via `verifiable.max_spec_clauses` / `VerifiableMaxSpecClauses`, with an automatic split CodeFix) or uses nested ternaries. Default severity: `Info` (configurable via `.editorconfig`). |
| `VER302` | `MISSING CONTRACT EXPLANATION` | A `[Verifiable]` `Spec.Requires` / `Spec.Ensures` call omits or leaves blank its plain-English `because` string. Default severity: `Info` (configurable via `.editorconfig`). |

### Integer overflow

`[Verifiable]` code is sound by default, so `int`, `uint`, `long` and `ulong` arithmetic must not overflow. Z3 models these types with their real 32- and 64-bit ranges, assumes every input (parameter, field or property read, call result) is a value of its type, and checks every `+`, `-`, `*`, unary `-`, `Math.Abs` and `MinValue / -1` for overflow. A method that can overflow is rejected with a `VER102` `Overflow` counterexample; bound the inputs with `Spec.Requires` to fix it:

```csharp
public static int Next(int x)
{
    Spec.Requires(x >= 0 && x <= 1000, "Small inputs only.");   // without this bound: VER102 Overflow at x = int.MaxValue
    Spec.Ensures<int>(r => r > x, "The successor is bigger.");
    return x + 1;
}
```

Wrap-around is allowed only when you say so:

- `unchecked(...)` or `unchecked { }` around an expression lets just that arithmetic wrap, and the prover models the wrapped result (so `unchecked(x + 1) > x` is *not* provable).
- `[UncheckedArithmetic]` (on assembly, type or method) lets all `int`/`long`/`uint`/`ulong` arithmetic in the scope wrap in the same way, as a default C# build does. Proofs stay sound because the wrapped value is what the prover reasons about; it just does not require that overflow never happens. Use it for hashes, checksums and ring-buffer indices.
- `[CheckedArithmetic]` (on assembly, type or method) restores the default inside an `[UncheckedArithmetic]` scope, and models integers as bit-vectors, which is the most precise model and the slowest to prove.
- A `checked(...)` expression or block must never overflow, even inside an `[UncheckedArithmetic]` scope, because there C# throws.
- The nearest attribute wins: method, then enclosing types, then the assembly.
- `MinValue / -1`, `MinValue % -1` and `Math.Abs(MinValue)` throw `OverflowException` in every mode, so they are always checked.

Contracts follow the same rules. A `Spec.Requires`, `Spec.Ensures`, `Spec.LoopInvariant`, `Spec.Decreases` measure, or the bounds and body of `Spec.ForAll` / `Spec.Exists` is evaluated as ordinary C# at run time, so its `+ - * -x` on `int`, `uint`, `long` and `ulong` is checked like code arithmetic, in the mode of the enclosing method or type:

- Default mode: each such operation must be shown not to overflow, under the conditions where it is evaluated (`a && b` only evaluates `b` when `a` holds, so `x <= 1000 && x + x >= 0` is fine). A `Requires` of the method itself is checked under the clauses before it; a callee's `Requires` is checked at each call; an `Ensures` is checked at each return. A failure is a `VER102` `Overflow` on the contract.
- `[UncheckedArithmetic]`: the contract wraps like the code, and the prover reasons about the wrapped value.
- `[CheckedArithmetic]`: bit-vectors, as for code.
- A quantifier variable `Spec.ForAll(lo, hi, i => ...)` is an `int` in `[lo, hi)`, and its body is checked the same way.

```csharp
Spec.Requires(x >= 0 && x <= 1000, "Small inputs only.");   // without the bound: VER102 on `x + 1`
Spec.Ensures<int>(r => r == x + 1, "The successor.");
```

### Integer widths, casts and bit operations

The prover gives each integer type the meaning C# gives it:

- `~ & | ^ << >>` (and `>>>`) work on `int`, `uint`, `long`, `ulong` and on enums by their underlying type. A shift count is masked to 5 or 6 bits, `>>` is arithmetic on a signed type and logical on an unsigned one. Under `[CheckedArithmetic]`, `int` and `long` are bit-vectors and use the bit-vector operators directly. Otherwise the operands are converted to a bit-vector of the type's width and back; `x & 0xFF`, `x & 1` and a shift by a constant are plain arithmetic. Facts about the bits of unknown operands (`x ^ y ^ y == x`) can make Z3 answer `unknown` (`VER103`) in that mode; bounding the operands, or `[CheckedArithmetic]`, helps.
- An unchecked narrowing cast keeps the low bits: `(byte)300` is 44, `(sbyte)200` is -56, `(int)` of a `long` wraps, and `(uint)-1` is `uint.MaxValue`. Inside `checked(...)` the value must fit and the prover checks it. A `decimal` (or `double`) cast to an integer truncates toward zero, and a `decimal` that does not fit throws.
- `byte`, `sbyte`, `short`, `ushort` and `char` arithmetic is done in `int`, and `b += 1`, `b++` and `b <<= 1` convert the result back to the narrow type, as C# does. In the default mode the conversion must not lose bits (an `Overflow` refutation, like `int` arithmetic); `unchecked(...)` and `[UncheckedArithmetic]` let it wrap.
- `Math.Clamp` and `Math.Sign` are modelled (`Math.Clamp` throws when `min > max`, so that is checked).
- A division inside a contract (a `Spec.Ensures`, a callee's `Spec.Requires` at a call, a loop invariant) has the same divisor check as in code, because evaluating the contract at run time throws too.

---

## 11. Worked Samples

See [**`samples/README.md`**](../samples/README.md) for full walkthroughs, GalaxyCheck property suites, SQLite crash/replay tests, `verifiable-query` proof outputs, and real bug counterexamples across:

- [**TodoApp**](../samples/TodoApp/README.md) — workflow state machine, `[MustConsume]` UI/API/SQLite protocol, and `CountItemsByStatus` loop proof.
- [**AccountingApp**](../samples/AccountingApp/README.md) — double-entry ledger, 3-leg fee splits, `ComputeTrialBalance`, and entry reversals.
- [**InventoryPricing**](../samples/InventoryPricing/README.md) — overflow/underflow proofs (the default; `[CheckedArithmetic]` for BitVec), `BasisPoints` share bounds, and `WarehouseBin.ReadUnchecked`.
