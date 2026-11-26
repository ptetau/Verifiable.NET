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
`-p:VerifiableZ3Path=/absolute/path/to/z3`. The analyzer package exposes both
`VerifiableZ3Path` and `VerifiableAgent` to the compiler; no hand-written
`CompilerVisibleProperty` entries are needed.

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

**Assembly boundary:** the prover reads helper bodies from the current compilation,
not IL in referenced assemblies. Marking an external predicate `[Verifiable]`
does not export its definition for use inside `Spec.Requires` or `Spec.Ensures`.
For a shared range rule, publish its bounds as public constants and compare the
input against those constants directly. This preserves the rule without
suppressing VER101 or excluding additional inputs.

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

---

## 4. Reading Elm-style compiler errors

When a rule breaks, Verifiable.NET formats compiler errors in an Elm-inspired layout so you can spot the problem in seconds:

- A **banner** (`-- PROOF FAILED ---- File.cs`) naming the check and file
- A short sentence saying what happened and where
- The **exact line of code** with `^` carets pointing at the expression
- **Why this rule exists** — showing your `because` / `description` string (or the built-in rationale for Gatekeeper rules `VER001`–`VER007`)
- **Contract** and **Counterexample** (when Z3 finds failing inputs)
- A concrete **Fix:** line

When `VERIFIABLE_AGENT=1` is set in CI or AI agent environments, `VER102` appends one single-line `<verifiable-counterexample>{...}</verifiable-counterexample>` JSON payload containing `"reason"`, `"counterexample"`, `"smtHash"`, and `"fix"`.

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

Every path that drops, overwrites, double-consumes, or discards a `[MustConsume]` value triggers `VER007 -- UNCONSUMED OBLIGATION` at compile time.

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
| `VER005` | `DISALLOWED CONTROL FLOW` | Unannotated `while`/`foreach`, `async`/`await`, `throw`, `try/catch`, `lock`, or `goto`. Annotate `while` loops with `Spec.LoopInvariant` + `Spec.Decreases`; use `Result<T, TError>` instead of exceptions. |
| `VER006` | `UNVERIFIED CALL` | Called an unverified external method, reflection, or `dynamic`. Mark deterministic helpers `[Verifiable]` / `[PureAxiom]`, or move I/O to the outer shell. |
| `VER007` | `UNCONSUMED OBLIGATION` | Dropped, overwrote, discarded, or double-consumed a `[MustConsume]` token. Route it to `[ConsumesObligation]`, return it, or call `Obligation.Discard(...)`. |
| `VER101` | `UNSUPPORTED CONTRACT` | Contract could not be lowered to SMT (for example mismatched `Spec.Ensures<T>` return type or `Spec.LoopInvariant` placed outside a loop). Rewrite with supported pure expressions. |
| `VER102` | `PROOF FAILED` | Z3 proved a contract can be violated and produced a counterexample plus your `because`/`description` spirit text. Strengthen `Spec.Requires` or fix the bug. |
| `VER103` | `SOLVER COULD NOT DECIDE` | Z3 returned `unknown` or timed out. Simplify non-linear arithmetic, add loop invariants, or split helper lemmas. |
| `VER104` | `Z3 SOLVER NOT FOUND` | Z3 binary missing (`scripts/install-z3.ps1` / `scripts/install-z3.sh`). Warning only; proof checks resume once Z3 is present. |
| `VER201` | `INVALID CONSTRAINED TYPE` | Type with `[TypeAlias<T>]` / `[Refined<T>]` is missing `readonly partial record struct` modifiers. |
| `VER202` | `MISSING ISVALID PREDICATE` | Constrained struct is missing `public static bool IsValid(T value) => ...;`. |

For integer overflow proofs, add `[CheckedArithmetic]` (on assembly, type, or method) so Z3 also checks 32/64-bit arithmetic bounds.

---

## 11. Worked Samples

See [**`samples/README.md`**](../samples/README.md) for full walkthroughs, GalaxyCheck property suites, SQLite crash/replay tests, `verifiable-query` proof outputs, and real bug counterexamples across:

- [**TodoApp**](../samples/TodoApp/README.md) — workflow state machine, `[MustConsume]` UI/API/SQLite protocol, and `CountItemsByStatus` loop proof.
- [**AccountingApp**](../samples/AccountingApp/README.md) — double-entry ledger, 3-leg fee splits, `ComputeTrialBalance`, and entry reversals.
- [**InventoryPricing**](../samples/InventoryPricing/README.md) — `[CheckedArithmetic]` BitVec overflow/underflow proofs, `BasisPoints` share bounds, and `WarehouseBin.ReadUnchecked`.
