# Verifiable.NET User Guide

**Verifiable.NET** brings compile-time formal verification, constrained domain types, linear protocol checking, and incremental refactoring directly into ordinary C# (.NET 9 and .NET 10).

Instead of learning a new theorem-proving language or paying runtime overhead, you write 100% type-checked C# expressions (`Spec.Requires`, `Spec.Ensures<T>`, `Spec.LoopInvariant`, `Spec.Decreases`, `[Invariant]`, and `[TypeAlias<T>]`) inside `[Verifiable]` scopes. At compile time, **Roslyn** and **Z3** mathematically prove your invariants hold on every code path—or give you an exact counterexample when they don't.

---

## Contents

1. [5-Minute Quick Start](#1-5-minute-quick-start)
2. [Push Constraints Into Your Types (`[TypeAlias<T>]` & `[Refined<T>]`)](#2-push-constraints-into-your-types-typealiast--refinedt)
3. [Seal Your Domain at the Highest Level (`[Verifiable]` & `[Unverifiable]`)](#3-seal-your-domain-at-the-highest-level-verifiable--unverifiable)
4. [Type-Safe Contracts & Type Invariants (`Spec.*` & `[Invariant]`)](#4-type-safe-contracts--type-invariants-spec--invariant)
5. [Safe I/O Boundaries (`[InboundPort]`)](#5-safe-io-boundaries-inboundport)
6. [Never Drop a Command or Commit (`[MustConsume]`)](#6-never-drop-a-command-or-commit-mustconsume)
7. [Migrating Existing Code Incrementally (`[VerifiableCandidate]`)](#7-migrating-existing-code-incrementally-verifiablecandidate)
8. [Testing Persistence Under Faults with GalaxyCheck](#8-testing-persistence-under-faults-with-galaxycheck)
9. [Sample Applications (`TodoApp` & `AccountingApp`)](#9-sample-applications-todoapp--accountingapp)
10. [Diagnostic Reference (`VER001`–`VER202`) & Agent UX](#10-diagnostic-reference-ver001ver202--agent-ux)

---

## 1. 5-Minute Quick Start

### Install & Build

```powershell
# 1. Clone the repository (including the GalaxyCheck submodule)
git clone --recursive https://github.com/ptetau/csharpest.git
cd csharpest

# 2. Install Z3 once (Windows)
powershell -ExecutionPolicy Bypass -File scripts/install-z3.ps1
# Or on Linux / macOS:
# bash scripts/install-z3.sh

# 3. Verify everything (libs, Blazor Wasm clients, Z3 proofs, GalaxyCheck tests)
.\scripts\verify.ps1
# Or on Linux / macOS: ./scripts/verify.sh
```

### Your First Verified Domain Function

1. Declare constrained domain types so invalid values cannot even be constructed accidentally.
2. Mark your domain class (or whole assembly) `[Verifiable]`.
3. Write type-safe preconditions, postconditions, and invariants using `Spec.*` and `[Invariant]`:

```csharp
using Verifiable.Contracts;

[assembly: Verifiable]

namespace BankDemo;

[TypeAlias<decimal>]
public readonly partial record struct PositiveAmount
{
    public static bool IsValid(decimal value) => value > 0m;
}

public readonly record struct Account(decimal Balance)
{
    [Invariant]
    public bool IsSolvent() => Balance >= 0m;
}

public static class Ledger
{
    [InboundPort]
    public static Account Withdraw(Account account, PositiveAmount amount)
    {
        Spec.Requires(account.Balance >= amount);
        Spec.Ensures<Account>(result => result.Balance == account.Balance - amount);

        return account with { Balance = account.Balance - amount };
    }
}
```

If you remove `Spec.Requires(account.Balance >= amount);`, building fails immediately with a concrete Z3 counterexample (`VER102`):

```text
error VER102: Proof obligation 'result => result.Balance >= 0m' / '[Invariant] Account.IsSolvent' failed for 'Ledger.Withdraw' (counterexample { account = mk-Account(-0m?), amount = 1m }) - Fix: Strengthen Spec.Requires(...) preconditions or correct the return value to satisfy [Invariant].
```

Because `Spec.Requires` and `Spec.Ensures<T>` carry `[Conditional("VERIFIABLE_SPEC")]`, **all contract calls and lambdas are completely stripped from your compiled IL** at runtime—zero allocations, zero runtime cost.

---

## 2. Push Constraints Into Your Types (`[TypeAlias<T>]` & `[Refined<T>]`)

Rather than copying `Spec.Requires(id >= 1 && amount > 0m)` into every method, attach invariants directly to zero-allocation `readonly partial record struct` wrappers:

```csharp
[TypeAlias<int>]
public readonly partial record struct TodoId
{
    public static bool IsValid(int value) => value >= 1;
}

[TypeAlias<string>]
public readonly partial record struct TodoTitle
{
    public static bool IsValid(string value) => value is { Length: >= 1 and <= 200 };
}

[TypeAlias<int>]
public readonly partial record struct BufferIndex
{
    public static bool IsValid(int idx) => idx is >= 0 and < 64;
}
```

`[Refined<T>]` works identically (useful if you want a semantic distinction for bespoke wrapper types with custom helper members).

For every `[TypeAlias<T>]` / `[Refined<T>]`, **Verifiable.Generators** generates:
- **`Value` property & constructor**, plus `IComparable<T>`.
- **Implicit widening**: `public static implicit operator TUnderlying(T alias) => alias.Value`—use `id + 1`, `account.Balance - amount`, or `title.Length` directly without `.Value` noise everywhere.
- **Comparison operators** `<`, `<=`, `>`, `>=` (on numeric and `char` aliases) and clean **`ToString()`** / `ToString(format)`.
- **`From(value)`**: checked constructor marked `[PureAxiom]` with `Spec.Requires(IsValid(value))` and `Spec.Ensures<T>(result => result.Value == value)`—Z3 proves every call site inside `[Verifiable]` code satisfies `IsValid`.
- **`TryFrom(value, out result)`**, **`TryFrom(value) -> MarshalResult<T>`**, and **`TryMarshal(value, fieldName)`** for safe edge validation.
- **`JsonConverter<T>`**: rejects tampered JSON wire payloads and implements `ReadAsPropertyName` / `WriteAsPropertyName` so `ImmutableDictionary<TodoId, TodoItem>` serializes cleanly via `System.Text.Json`.
- **Bounds-check elimination**: Zero-based integer ranges (like `idx is >= 0 and < 64`) generate `public const int UpperBoundExclusive = 64;` and `[PureAxiom] ReadUnchecked<T>(ReadOnlySpan<T> buffer)` with `Spec.Requires(buffer.Length >= 64)`.

Inside `[Verifiable]` code, `VER003` forbids `default(TodoId)` or `new TodoId()` so no code can bypass `IsValid`.

---

## 3. Seal Your Domain at the Highest Level (`[Verifiable]` & `[Unverifiable]`)

Put `[Verifiable]` at the **highest scope** that fits your project—don't repeat it on every method:

- **Assembly**: `[assembly: Verifiable]` seals every type and method in the project (see `samples/TodoApp/TodoApp.Shared`).
- **Class / Struct**: `[Verifiable]` on `public static class LedgerEngine` or `public readonly record struct LedgerState` cascades automatically to all members and nested types (see `samples/AccountingApp/AccountingApp.Shared`).
- **Opt-Out Boundary DTOs/Helpers**: Mark specific classes, structs, or methods inside a sealed assembly/class with `[Unverifiable]`:

```csharp
[assembly: Verifiable]

namespace MyApp.Shared;

public static class OrdersEngine
{
    // Verified automatically via [assembly: Verifiable]
    public static Order PlaceOrder(...) => ...;

    // Boundary token builder exempted from [Verifiable]
    [Unverifiable]
    public static PendingCommit PrepareCommit(...) => ...;
}

// HTTP wire DTOs outside the pure domain model
[Unverifiable]
public sealed record PlaceOrderHttpRequest(Guid CommandId, decimal Amount);
```

### Gatekeeper Rules Inside `[Verifiable]` (`VER001`–`VER006`)

| Rule | What It Prevents | Use Instead |
| --- | --- | --- |
| `VER001` | Mutable classes, non-`readonly struct`, `set;` properties, `ref`/`out`, member mutations | `readonly record struct` / `record` with `init;` and `with` |
| `VER002` | `List<T>`, `Dictionary<K,V>`, `HashSet<T>`, `T[]`, etc. | `ImmutableList<T>`, `ImmutableDictionary<K,V>`, `ImmutableHashSet<T>`, `ImmutableArray<T>` |
| `VER003` | `default(T)` or `new T()` on `[Invariant]`, `[TypeAlias<T>]`, or `[Refined<T>]` structs | `T.From(...)`, `T.TryFrom(...)`, or `[InboundPort]` |
| `VER004` | `DateTime.Now`/`UtcNow`, `Guid.NewGuid()`, `Random`, `Stopwatch`, mutable static state, I/O | Pass explicit deterministic parameters (`UnixTimestamp now`, `CommandId id`) |
| `VER005` | Unannotated loops, `goto`, `async`/`await`, `Task`, `lock`, `throw`, `try`/`catch` | Loops with `Spec.LoopInvariant`/`Spec.Decreases`; `Result<T, TError>` returns |
| `VER006` | Calling unverified methods outside `[Verifiable]`/`[PureAxiom]`/trusted pure BCL, reflection, `dynamic` | Move logic into `[Verifiable]` or split I/O via Sandwich Refactoring |

---

## 4. Type-Safe Contracts & Type Invariants (`Spec.*` & `[Invariant]`)

### Method Preconditions & Postconditions

```csharp
public static LedgerTransition ReverseEntry(LedgerState state, JournalEntryId entryId, UnixTimestamp now)
{
    Spec.Ensures<LedgerTransition>(result => result.State.TotalDebits == result.State.TotalCredits);
    Spec.Ensures<LedgerTransition>(result => result.IsSuccess || result.State == state);
    ...
}
```

For postconditions that prefer not to write a lambda, `Spec.Ensures(Spec.Result<int>() >= 0)` is also supported.

### Type Invariants

Mark parameterless `bool` instance methods or properties on your immutable structs/records with `[Invariant]`:

```csharp
public readonly record struct TodoBoard(
    TodoId NextId,
    ImmutableDictionary<TodoId, TodoItem> Items,
    NonNegativeInt ActiveCount,
    NonNegativeInt CompletedCount,
    NonNegativeInt DeletedCount)
{
    [Invariant]
    public bool IsConsistent() =>
        Items.Count == ActiveCount + CompletedCount + DeletedCount
        && Items.Count == NextId - 1;
}
```

Every `[Invariant]` member is:
1. Assumed on incoming parameters (and on elements retrieved from `ImmutableDictionary`/`ImmutableList`/`ImmutableHashSet`).
2. Proved by Z3 on every returned value (including `Result<T, E>.Ok(...)` payloads).
3. Evaluated at runtime by generated `[InboundPort]` gates!

### Inductive Loops (`Spec.LoopInvariant` & `Spec.Decreases`)

Place `Spec.LoopInvariant(...)` and `Spec.Decreases(...)` directly inside the loop body—supporting single loops, sequential loops, and nested loops in the same method:

```csharp
public static int FindJournalIndex(ImmutableList<PostedJournalEntry> journal, JournalEntryId entryId)
{
    Spec.Ensures<int>(result => result >= -1 && result < journal.Count);

    var i = 0;
    while (i < journal.Count)
    {
        Spec.LoopInvariant(i >= 0 && i <= journal.Count);
        Spec.Decreases(journal.Count - i);

        if (journal[i].Id == entryId)
        {
            return i;
        }
        i = i + 1;
    }
    return -1;
}
```

Z3 proves:
- Every indexer access `journal[i]` is strictly within `0 <= i < journal.Count` (zero out-of-bounds possibility!).
- `Spec.LoopInvariant` holds on entry and is preserved across every iteration.
- `Spec.Decreases` is `>= 0` and strictly decreases on every iteration (termination proof).

### Exact Money Math & Overflow Checking

- **`decimal`** lowers to exact arbitrary-precision `Real` rationals (`0.1m + 0.2m == 0.3m` proves cleanly).
- **`[CheckedArithmetic]`** switches `int`/`long` from mathematical integers to exact 32-bit / 64-bit BitVectors and verifies no `+`, `-`, `*`, or unary `-` can ever overflow.

---

## 5. Safe I/O Boundaries (`[InboundPort]`)

Tag entry-point domain methods with `[InboundPort]`:

```csharp
[InboundPort]
public static TodoTransition Add(TodoBoard board, TodoTitle title, UnixTimestamp now) => ...
```

`InboundPortGenerator` emits:
1. **`RawTodoBoard`, `RawTodoItem`**: Plain mutable POCO DTOs with primitive/list/dictionary properties (properties marked `[Ghost]` or `[Invariant]` are excluded).
2. **`TodoBoardGate.TryMarshal(RawTodoBoard? raw)`**: Returns `MarshalResult<TodoBoard>`, validating every leaf `IsValid`, enum (`Enum.IsDefined`), non-null string, nested object, and every `[Invariant]` member.
3. **`TodoEnginePort.Add(RawTodoBoard? rawBoard, string? rawTitle, long rawNow)`**: Marshals all parameters, checks any leading `Spec.Requires(...)` preconditions, and calls `TodoEngine.Add` only when zero `ContractViolation`s occurred.

---

## 6. Never Drop a Command or Commit (`[MustConsume]`)

Mark linear protocol tokens with `[MustConsume]`:

```csharp
[MustConsume]
public readonly record struct PendingTodoCommit(...);
```

`LinearObligationAnalyzer` (`VER007`) tracks every control-flow path across **all methods in your compilation** (including `async` ASP.NET Core handlers and Blazor components):
- Every token must be passed to a `[ConsumesObligation]` sink method (e.g., `repo.CommitAsync(pending)` or `dispatcher.DispatchAsync(cmd)`), returned to a caller, or explicitly released with `Obligation.Discard(token, "reason")`.
- Dropping, overwriting, double-consuming, or leaking a token on an error branch fails the build with `VER007`.

---

## 7. Migrating Existing Code Incrementally (`[VerifiableCandidate]`)

You don't need a big-bang rewrite:
1. Tag legacy classes/methods/assemblies with **`[VerifiableCandidate]`**. Violations `VER001`–`VER006` emit **Warnings** instead of breaking your build.
2. Run the automated **`Verifiable.CodeFixes`** Roslyn refactorings:
   - `HoistAmbientAuthority` (`VER004`)
   - `FreezeMutableState` (`VER001`, `VER002`)
   - `SandwichIoSplit` (`VER005`, `VER006`)
   - `ExceptionToUnion` (`VER005`)
3. Promote `[VerifiableCandidate]` -> `[Verifiable]` and add `Spec.*` + `[TypeAlias<T>]` contracts.

---

## 8. Testing Persistence Under Faults with GalaxyCheck

Verifiable.NET uses **[GalaxyCheck](https://github.com/ptetau/galactic-heck)** across all test suites (`Verifiable.Testing` + `GalaxyCheck.Xunit`):
- **`VerifiableGen`**: Property generators (`PositiveInt`, `PositiveDecimal`, `NonEmptyString`, `CurrencyCode`, `NonEmptyGuid`, `Refined<TRefined, TUnderlying>`, `PortRejectsInvalid`).
- **`FaultInjectedSqliteHarness` & `DifferentialPersistenceProperty`**: Runs stateful command sequences against real WAL-mode SQLite while injecting `PreCommitCrash`, `PostCommitCrashBeforeAck`, and `DuplicateDelivery` faults, proving persistence matches the `[Verifiable]` domain model step-by-step.

---

## 9. Sample Applications (`TodoApp` & `AccountingApp`)

| Sample | What It Showcases | Location |
| --- | --- | --- |
| **TodoApp** | `[assembly: Verifiable]`, `[TypeAlias<T>]`, `[Unverifiable]` HTTP DTOs, `[InboundPort]`, `[MustConsume]` end-to-end across Blazor WebAssembly -> Minimal API -> idempotent WAL SQLite | [`samples/TodoApp`](samples/TodoApp) |
| **AccountingApp** | Class-level `[Verifiable]`, double-entry ledger, Z3 exact `decimal` conservation (`TotalDebits == TotalCredits`), multi-line loop invariants (`Spec.LoopInvariant` / `Spec.Decreases`), append-only reversal immutability, `ReplayLedgerAsync()` differential parity | [`samples/AccountingApp`](samples/AccountingApp) |

---

## 10. Diagnostic Reference (`VER001`–`VER202`) & Agent UX

Every diagnostic message includes an actionable `Fix:` clause.

- **`VER001`–`VER006`**: Gatekeeper rules (Error in `[Verifiable]`, Warning in `[VerifiableCandidate]`).
- **`VER007`**: Linear `[MustConsume]` token violation (Error everywhere).
- **`VER101`–`VER104`**: SMT contract lowering (`VER101`), Z3 `SAT` counterexample (`VER102`), solver `unknown`/timeout (`VER103`), missing Z3 binary warning (`VER104`).
- **`VER201`–`VER202`**: `[TypeAlias<T>]` / `[Refined<T>]` must be `readonly partial record struct` (`VER201`) and declare `public static bool IsValid(T value)` (`VER202`).

### Agent UX & Deep Reference
- Set `VERIFIABLE_AGENT=1` for single-line `<verifiable-counterexample>{...}</verifiable-counterexample>` JSON blocks on `VER102` errors.
- Set `GALAXYCHECK_AGENT=1` for seed-0 GalaxyCheck property tests with `<galaxycheck-failure>{...}</galaxycheck-failure>` JSON blocks.
- **Full Manual**: [`docs/manual.md`](docs/manual.md)
- **Agent Guides**: [`AGENTS.md`](AGENTS.md), [`docs/agents.md`](docs/agents.md), and [`llms.txt`](llms.txt) (also packed at `\` inside every `Verifiable.*` NuGet package).
