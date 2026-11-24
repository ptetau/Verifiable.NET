# Verifiable.NET User Manual

**Verifiable.NET** brings compile-time formal verification, constrained type generation, protocol obligation tracking, and incremental refactoring directly into standard C# (.NET 9 and .NET 10). Instead of adopting a new programming language or paying a runtime overhead penalty, you carve out **Verifiable Cages** inside ordinary `.NET` solutions—from Blazor WebAssembly UIs to ASP.NET Core Minimal APIs and SQLite repositories.

---

## Table of Contents

1. [Core Philosophy: Verifiable Cages](#1-core-philosophy-verifiable-cages)
2. [Getting Started & Installing Z3](#2-getting-started--installing-z3)
3. [Constrained Type Aliases (`[TypeAlias<T>]` & `[Refined<T>]`)](#3-constrained-type-aliases-typealiast--refinedt)
4. [Hierarchical `[Verifiable]` Scopes & The Gatekeeper (`VER001`–`VER006`)](#4-hierarchical-verifiable-scopes--the-gatekeeper-ver001ver006)
5. [Formal Contracts & The Z3 SMT Prover (`VER101`–`VER104`)](#5-formal-contracts--the-z3-smt-prover-ver101ver104)
6. [Safe I/O Boundaries with `[InboundPort]`](#6-safe-io-boundaries-with-inboundport)
7. [Linear Obligation Tokens (`[MustConsume]`)](#7-linear-obligation-tokens-mustconsume)
8. [Incremental Migration with `[VerifiableCandidate]` & CodeFixes](#8-incremental-migration-with-verifiablecandidate--codefixes)
9. [Property-Based & Fault-Injected Persistence Testing with GalaxyCheck](#9-property-based--fault-injected-persistence-testing-with-galaxycheck)
10. [Walkthroughs: `TodoApp` and `AccountingApp`](#10-walkthroughs-todoapp-and-accountingapp)
11. [Agent UX (`VERIFIABLE_AGENT=1` & `GALAXYCHECK_AGENT=1`)](#11-agent-ux-verifiable_agent1--galaxycheck_agent1)
12. [Complete Diagnostic Reference (`VER001`–`VER204`)](#12-complete-diagnostic-reference-ver001ver204)

---

## 1. Core Philosophy: Verifiable Cages

Real applications have messy edges: HTTP endpoints, databases, clocks (`DateTimeOffset.UtcNow`), random IDs (`Guid.NewGuid()`), and UI state. Formal verification shines when you separate **untrusted I/O shells** from **deterministic domain cores** (often called the *Sandwich Architecture*):

1. **Untrusted I/O Shell** reads HTTP JSON / SQLite rows / user input.
2. **Inbound Port (`[InboundPort]`)** validates and marshals raw primitives into constrained domain types (`MarshalResult<T>`).
3. **Verifiable Cage (`[Verifiable]`)** executes pure, deterministic domain logic whose contracts (`[Invariant]`, `[Requires]`, `[Ensures]`) are mathematically proved at compile time by **Z3**.
4. **Linear Obligation Tokens (`[MustConsume]`)** ensure the UI and API cannot silently drop a command or forget to commit or discard a database transaction (`VER007`).

Everything runs at compile time using Roslyn Source Generators, Roslyn Diagnostic Analyzers, Roslyn CodeFixes, and an out-of-process Z3 worker pool—leaving **zero verification runtime libraries** in your deployment payload beyond tiny marker attributes in `Verifiable.Contracts`.

---

## 2. Getting Started & Installing Z3

### Prerequisites
- .NET SDK 9 or .NET SDK 10
- Z3 SMT Solver binary (`z3.exe` on Windows, `z3` on Linux/macOS)

### Installing Z3

Run the provided helper script once from the repository root:

```powershell
# Windows (installs into %USERPROFILE%\.verifiable\z3\)
powershell -ExecutionPolicy Bypass -File scripts/install-z3.ps1
```

```bash
# Linux / macOS (installs into ~/.verifiable/z3/)
bash scripts/install-z3.sh
```

`Verifiable.Analyzers` automatically searches for Z3 in this order:
1. `VERIFIABLE_Z3_PATH` environment variable
2. MSBuild `-p:VerifiableZ3Path=...` property
3. `.tools/z3/z3(.exe)` walking up from your source files
4. `~/.verifiable/z3/z3(.exe)`
5. System `PATH`

If Z3 is missing, Gatekeeper rules (`VER001`–`VER007`) still run, and `VER104` emits a build warning explaining how to install Z3.

### Building & Testing Everything

```powershell
# Windows
.\scripts\verify.ps1
```

```bash
# Linux / macOS
./scripts/verify.sh
```

---

## 3. Constrained Type Aliases (`[TypeAlias<T>]` & `[Refined<T>]`)

Instead of repeating preconditions like `[Requires("id >= 1 && amount > 0m")]` on every method in your codebase, **push constraints directly into your types**.

### Option A: Single-Line `[TypeAlias<T>]` (Recommended)

Declare constrained nominal types in one line at the **assembly** or **class/struct** level—zero `partial record struct` boilerplate required:

```csharp
using System;
using Verifiable.Contracts;

[assembly: TypeAlias<int>("TodoId", "Value >= 1", Namespace = "TodoApp.Shared")]
[assembly: TypeAlias<int>("NonNegativeInt", "Value >= 0", Namespace = "TodoApp.Shared")]
[assembly: TypeAlias<decimal>("PositiveAmount", "Value > 0m && Value <= 1000000000m", Namespace = "TodoApp.Shared")]
[assembly: TypeAlias<string>("TodoTitle", "Length is >= 1 and <= 200", Namespace = "TodoApp.Shared")]
[assembly: TypeAlias<Guid>("CommandId", "Value != Guid.Empty", Namespace = "TodoApp.Shared")]
```

You can also place `[TypeAlias<T>]` on a class or struct, in which case `Namespace` defaults automatically to that enclosing type's namespace:

```csharp
namespace MyApp.Domain;

[TypeAlias<decimal>("PositiveDecimal", "Value > 0m")]
[TypeAlias<string>("CurrencyCode", "Length is >= 3 and <= 3")]
public static class DomainTypes;
```

Set `IsInternal = true` if you want an `internal` type alias instead of `public`.

### Option B: `[Refined<T>]` on a Custom `readonly partial record struct`

When you want to attach custom helper methods or properties directly onto the constrained type itself, use `[Refined<T>]`:

```csharp
[Refined<int>("Value >= 0 && Value < 64")]
public readonly partial record struct BufferIndex
{
    public bool IsFirst => Value == 0;
}
```

### What Both `[TypeAlias<T>]` and `[Refined<T>]` Generate

Both attributes share the same code generator engine and produce a zero-allocation `readonly partial record struct <Name>(TUnderlying Value)` carrying:

- **Compile-time sealing**: `[Verifiable]` and `[Invariant("...")]` (plus `[Refined<T>]` so Z3, Gatekeeper, and `[InboundPort]` understand the type across project boundaries).
- **Safe checked construction**:
  - `T.From(value)` — marked `[PureAxiom]` with `[Requires(...)]` and `[Ensures("result.Value == value")]`. Inside `[Verifiable]` code, Z3 proves at compile time that your caller path guarantees the predicate holds!
  - `T.TryFrom(value, out T result)` and `MarshalResult<T> T.TryFrom(value)` — non-throwing validation for UI/API boundaries.
  - `T.IsValid(value)` and `T.TryMarshal(value, fieldName)` — used by `[InboundPort]` and unit checks.
- **Ergonomic widening & operators (minimal `.Value` noise!)**:
  - `implicit operator TUnderlying(Alias value) => value.Value` — pass `PositiveAmount` anywhere a `decimal` is expected, or use `board.NextId + 1` in C# and Z3 contracts without writing `.Value` everywhere!
  - Relational operators `<`, `<=`, `>`, `>=` (on numeric types and `char`), plus `IComparable<T>.CompareTo`.
  - Clean `ToString()` (returns `Value.ToString()` instead of `TodoId { Value = 1 }`), `ToString(string? format)`, and `Length` (on `string`-backed aliases).
- **JSON safety (including dictionary keys!)**:
  - `JsonConverter<T>` validates the invariant during deserialization and throws a descriptive `JsonException` on tampered payloads.
  - Implements `ReadAsPropertyName` and `WriteAsPropertyName` so `ImmutableDictionary<TodoId, TodoItem>` round-trips cleanly via `System.Text.Json`.
- **Zero-bounds-check span indexing**:
  - When an `int` refinement expresses a zero-based constant upper bound (such as `"Value >= 0 && Value < 64"` or `"Value is >= 0 and < 64"`), the generator emits `public const int UpperBoundExclusive = 64;` and a `[PureAxiom] [Requires("buffer.Length >= 64")] ReadUnchecked<T>(ReadOnlySpan<T> buffer)` helper.

---

## 4. Hierarchical `[Verifiable]` Scopes & The Gatekeeper (`VER001`–`VER006`)

You do **not** need to repeat `[Verifiable]` on every method or nested type. Apply `[Verifiable]` (or `[VerifiableCandidate]` / `[CheckedArithmetic]`) at the **highest level** that fits your module:

1. **Assembly level**: `[assembly: Verifiable]` seals every class, struct, and method in the entire project.
2. **Type level**: `[Verifiable]` on a `static class`, `record`, or `readonly record struct` seals every method and member inside it (and any nested types).
3. **Method level**: `[Verifiable]` on individual pure core functions inside a mixed class.

If an assembly or class is `[Verifiable]`, you can opt a specific boundary DTO or factory helper out with `[Unverifiable]`:

```csharp
[assembly: Verifiable]

namespace MyApp.Shared;

public static class DomainEngine
{
    // Automatically verified (inherits [assembly: Verifiable]):
    public static int PureCalculation(int x) => x * 2;
}

// Explicitly excluded from the cage (HTTP DTOs or boundary helpers):
[Unverifiable]
public sealed record CreateOrderHttpRequest(Guid CommandId, decimal Amount);
```

When scopes overlap, the **nearest** attribute on the declaration chain (`method -> enclosing type -> assembly`) wins. Calling an `[Unverifiable]` method from a `[Verifiable]` method is blocked with `VER006` unless the target is marked `[PureAxiom]`.

### What the Gatekeeper Enforces (`VER001`–`VER006`)

| Rule | Disallowed in `[Verifiable]` | Required Deterministic Alternative |
| --- | --- | --- |
| `VER001` | Mutable `class`, non-`readonly struct`, settable `set;` properties, `ref`/`out` params, member reassignment | `readonly record struct` or immutable `record` with `init;` and `with` expressions |
| `VER002` | `List<T>`, `Dictionary<K,V>`, `HashSet<T>`, `Queue<T>`, `Stack<T>`, mutable `T[]` | `ImmutableList<T>`, `ImmutableDictionary<K,V>`, `ImmutableHashSet<T>`, `ImmutableArray<T>` |
| `VER003` | `default(T)` or parameterless `new T()` on any struct carrying `[Invariant]` or `[Refined<T>]` / `[TypeAlias<T>]` | `T.From(...)`, `T.TryFrom(...)`, or `[InboundPort]` |
| `VER004` | `DateTime.Now`/`UtcNow`/`Today`, `DateTimeOffset.Now`/`UtcNow`, `Guid.NewGuid()`, `Random`, `Stopwatch`, mutable `static` fields, `Environment.*`, `Console.*`, `System.IO.*`, `HttpClient` | Pass deterministic inputs explicitly (`UnixTimestamp now`, `CommandId commandId`) |
| `VER005` | Unannotated `while`/`do`/`foreach` loops, `goto`, `async`/`await`, `Task`/`ValueTask`, `lock`, `throw`, `try`/`catch` | Loops with `[LoopInvariant]`/`[Decreases]`; explicit `Result<T, TError>` union returns |
| `VER006` | Calling unverified methods outside `[Verifiable]`/`[PureAxiom]`/trusted pure BCL, `System.Reflection`, `dynamic` | Seal the callee in `[Verifiable]` or split I/O using Sandwich Refactoring |

---

## 5. Formal Contracts & The Z3 SMT Prover (`VER101`–`VER104`)

Inside every `[Verifiable]` method, `VerifiableSmtAnalyzer` lowers Roslyn's `ControlFlowGraph` (CFG) and Static Single Assignment (SSA) values into SMT-LIB2 queries solved by pooled `z3 -in` processes.

### Contract Attributes

- **`[Invariant("expr")]`** on a `record` / `struct`: assumed on entry parameters and collection elements retrieved from immutable collections, and **proved** on every return value (and inside `Result<T, E>.Ok(...)` payloads).
- **`[Requires("expr")]`**: assumed at entry of the method being verified, and **proved** at every call site inside `[Verifiable]` callers using that caller's dominant path condition (**Occurrence Typing**).
- **`[Ensures("expr")]`**: **proved** on every `return` branch of the method (`result` refers to the returned value; parameters refer to their entry values).
- **`[LoopInvariant("expr")]` + `[Decreases("expr")]`**: verifies inductive `while` / `for` loops:
  - `[LoopInvariant]` must hold before the loop starts and be preserved at every back-edge.
  - `[Decreases]` must be non-negative on entry and strictly decrease toward zero on every iteration (proving termination!).
- **`[CheckedArithmetic]`**: switches `int` and `long` sorts from mathematical integers to exact 32-bit / 64-bit BitVectors (`(_ BitVec 32)` / `(_ BitVec 64)`) and proves `+`, `-`, `*`, and unary `-` cannot overflow!

### Supported SMT Theories & Operations

- **Exact Money Math**: `decimal` lowers to exact arbitrary-precision `Real` rationals (`0.1m + 0.2m == 0.3m` proves without binary floating-point rounding drift!).
- **Strings**: `VerStr` datatype supporting `null`, string literals, `string.IsNullOrEmpty(s)`, `s.Length`, and string concatenation.
- **Immutable Collections**: Extensional array + count models for `ImmutableDictionary<K, V>` (`Add`, `SetItem`, `Remove`, `ContainsKey`, `GetValueOrDefault`, `this[k]`, `Count`), `ImmutableList<T>` / `ImmutableArray<T>` (`Add`, `SetItem`, `this[i]`, `Count`), and `ImmutableHashSet<T>` (`Add`, `Remove`, `Contains`, `Count`). Indexing `dict[k]` automatically emits a Z3 proof obligation that `ContainsKey(k)` holds!
- **Records, Tuples, `with` expressions, `Nullable<T>`, `Result<T, E>`**: Full algebraic datatype lowering, including `with` clones.

### Reading Z3 Counterexamples (`VER102`)

Suppose you omit an overdraft check:

```csharp
[Verifiable]
public static class LedgerCheck
{
    [Requires("amount > 0m")]
    [Ensures("result >= 0m")]
    public static decimal Withdraw(decimal fromBal, decimal amount) => fromBal - amount;
}
```

Compiling gives an immediate Z3 counterexample and fix hint:

```text
error VER102: Proof obligation '[Ensures("result >= 0m")]' failed for 'LedgerCheck.Withdraw' (counterexample { fromBal = 0.0, amount = 1.0 }) - Fix: Strengthen [Requires] preconditions or correct the return value to satisfy [Ensures].
```

---

## 6. Safe I/O Boundaries with `[InboundPort]`

Untrusted wire JSON or database rows should never construct domain invariants blindly. Put `[InboundPort]` on your domain entry points:

```csharp
[Verifiable]
public static class TodoEngine
{
    [InboundPort]
    public static TodoTransition Add(TodoBoard board, TodoTitle title, UnixTimestamp now)
        => ...
}
```

`Verifiable.Generators` inspects the parameter graph and emits:
1. **`RawTodoBoard` & `RawTodoItem` DTO classes** with primitive properties (`int`, `string?`, `List<T>`, `Dictionary<K, V>`), ready for JSON binding or SQLite hydration.
2. **`TodoBoardGate.TryMarshal(RawTodoBoard? raw, string path = "")`** returning `MarshalResult<TodoBoard>`, validating every leaf `[TypeAlias<T>]` / `[Refined<T>]`, every enum via `Enum.IsDefined`, non-null strings, and all composite `[Invariant]` predicates, collecting structured `ContractViolation` errors with field paths (e.g., `Items[1].Title`).
3. **`TodoEnginePort.Add(RawTodoBoard? __ver_raw_board, string? __ver_raw_title, long __ver_raw_now)`** which marshals every parameter, checks method `[Requires]`, and invokes `TodoEngine.Add(...)` **only** if zero violations exist.

Fields annotated with `[Ghost]` (proof-only specification properties) are automatically excluded from `Raw*` DTOs.

---

## 7. Linear Obligation Tokens (`[MustConsume]`)

Runtime correctness isn't just pure math—protocol bugs happen when a UI button handler builds a command and forgets to send it, or an API handler commits nothing on an early exit.

Define linear obligation tokens using `[MustConsume]` on a `struct`:

```csharp
[MustConsume]
public readonly record struct PendingTodoCommit(...);
```

`LinearObligationAnalyzer` (`VER007`) performs path-sensitive CFG dataflow analysis across **every method in the compilation** (whether inside `[Verifiable]` or inside async controllers/Razor components!). Every `[MustConsume]` value must follow affine/linear rules on every control-flow path:
- Pass it to a `[ConsumesObligation]` sink method (e.g., `await repo.CommitAsync(pending)` or `await dispatcher.DispatchAsync(cmd)`),
- **OR** return it (transferring ownership to the caller),
- **OR** explicitly discharge it on an error/cancellation branch with `Obligation.Discard(pending, "reason")`.

Dropping a token, overwriting a live variable, returning without consuming on one `if` branch, or consuming the same token twice triggers compile-time error **`VER007`**.

---

## 8. Incremental Migration with `[VerifiableCandidate]` & CodeFixes

You don't have to rewrite a legacy service overnight:

1. **Tag legacy code with `[VerifiableCandidate]`** (on a method, class, or assembly). Violations of `VER001`–`VER006` are emitted as **Build Warnings** so your CI stays green while you migrate.
2. **Apply automated Roslyn CodeFixes** (`Verifiable.CodeFixes`):
   - **`HoistAmbientAuthority`** (`VER004`): Moves `DateTime.UtcNow` / `DateTimeOffset.UtcNow` / `Guid.NewGuid()` out into an explicit method parameter and updates call sites.
   - **`FreezeMutableState`** (`VER001` / `VER002`): Converts mutable classes/structs into immutable `record`s with `init;` properties, rewrites property assignments into `with` expressions, and migrates `List<T>` / `Dictionary<K,V>` / `HashSet<T>` / `T[]` to `System.Collections.Immutable`.
   - **`SandwichIoSplit`** (`VER005` / `VER006`): Extracts the pure computational block between I/O `await` calls into a separate `[Verifiable]` static core method.
   - **`ExceptionToUnion`** (`VER005`): Rewrites domain `throw` statements into explicit `Result<T, string>.Fail(...)` / `.Ok(...)` returns.
3. **Promote `[VerifiableCandidate]` -> `[Verifiable]`** and add `[TypeAlias<T>]` types and Z3 contracts.

---

## 9. Property-Based & Fault-Injected Persistence Testing with GalaxyCheck

Compile-time Z3 proofs prove your pure domain model obeys its axioms; **GalaxyCheck** (`external/galactic-heck` + `Verifiable.Testing`) tests that your **real SQLite persistence adapter and `[InboundPort]` gates** never drift from that verified model—even under crashes and duplicate network deliveries!

### Generators & Harness (`Verifiable.Testing`)

- `VerifiableGen`: Ready-to-use GalaxyCheck generators (`PositiveInt`, `PositiveDecimal`, `NonEmptyString`, `CurrencyCode`, `NonEmptyGuid`, `Refined<TRefined, TUnderlying>`, `PortRejectsInvalid`).
- `FaultInjectedSqliteHarness`: Spins up isolated WAL-mode SQLite databases per property test sequence and injects realistic failures via `PersistenceFaultKind`:
  - `None`
  - `PreCommitCrash` (fails before commit)
  - `PostCommitCrashBeforeAck` (commits to SQLite, then throws to simulate lost HTTP ack)
  - `DuplicateDelivery` (replays the same `CommandId` twice to prove idempotency!)
- `DifferentialPersistenceProperty.Create`: Compares actual persisted SQLite state against your pure `[Verifiable]` model step-by-step, shrinking any divergence to a minimal command trace.

---

## 10. Walkthroughs: `TodoApp` and `AccountingApp`

### `samples/TodoApp` (Assembly-Level `[assembly: Verifiable]`)
- `TodoApp.Shared`: Declares `[assembly: Verifiable]` once plus `[assembly: TypeAlias<...>]` for `TodoId`, `NonNegativeInt`, `TodoTitle`, `UnixTimestamp`, and `CommandId`. `TodoBoard` only needs cross-field `[Invariant]`s (`Items.Count == ActiveCount + CompletedCount + DeletedCount` and `Items.Count == NextId - 1`) because positivity/non-negativity live inside `TodoId` and `NonNegativeInt`. Wire DTOs use `[Unverifiable]`.
- `TodoApp.Client`: Blazor WebAssembly UI driven by `TodoUiEngine` (pure MVU state machine) + `TodoCommandDispatcher` (`[ConsumesObligation]`).
- `TodoApp.Server`: ASP.NET Core Minimal API invoking `TodoEnginePort.*` and persisting atomically to WAL SQLite via `SqliteTodoRepository.CommitAsync(PendingTodoCommit)`.

### `samples/AccountingApp` (Class-Level `[Verifiable]` + Exact Double-Entry Ledger)
- `AccountingApp.Shared`: Uses `[assembly: TypeAlias<...>]` for `AccountId`, `JournalEntryId`, `PositiveAmount`, `NonNegativeDecimal`, `PositiveDecimal`, `CurrencyCode`, `AccountName`, `UnixTimestamp`, `CommandId`, and puts `[Verifiable]` once on each domain struct and `LedgerEngine` / `LedgerUiEngine`. Z3 proves exact rational conservation (`TotalDebits == TotalCredits`), non-negative overdraft-guarded asset transfers, inductive loop balance checks (`SumEntrySides`, `ApplyLinesToAccounts`, `FlipEntryLines`), and append-only journal reversals (`ReverseEntry`).
- `AccountingApp.Server`: Includes `ReplayLedgerAsync()`, reconstructing the entire ledger state from `journal_entries` + `journal_lines` alone and running `LedgerStateGate.TryMarshal`, verified against live state in `AccountingAppStatefulTests`.

---

## 11. Agent UX (`VERIFIABLE_AGENT=1` & `GALAXYCHECK_AGENT=1`)

Verifiable.NET is engineered for fast, zero-guesswork LLM agent loops:
- Set **`VERIFIABLE_AGENT=1`** (or pass `-p:VerifiableAgent=1`) to append a single-line `<verifiable-counterexample>{...}</verifiable-counterexample>` JSON payload to every `VER102` failure, containing exact parameter counterexamples, SMT hash, and `Fix:` advice.
- Set **`GALAXYCHECK_AGENT=1`** (and optionally `GALAXYCHECK_SEED=n`) for deterministic seed-0 property test runs with `<galaxycheck-failure>{...}</galaxycheck-failure>` JSON blocks.
- Read **`llms.txt`** (located at the repo root and packed at `\` inside every `Verifiable.*` NuGet package) and [`docs/agents.md`](agents.md).

---

## 12. Complete Diagnostic Reference (`VER001`–`VER204`)

Every diagnostic message ends with a `Fix:` instruction.

| ID | Default Severity | Trigger |
| --- | --- | --- |
| `VER001` | Error (`[Verifiable]`) / Warning (`[VerifiableCandidate]`) | Mutable `class`, non-`readonly struct`, `set;` property, `ref`/`out` parameter, or member assignment |
| `VER002` | Error / Warning | Mutable collection (`List<T>`, `Dictionary<K,V>`, `HashSet<T>`, `T[]`, etc.) |
| `VER003` | Error / Warning | Unchecked `default(T)` or `new T()` on `[Invariant]`, `[Refined<T>]`, or `[TypeAlias<T>]` struct |
| `VER004` | Error / Warning | Nondeterminism / ambient authority (`UtcNow`, `NewGuid()`, `Random`, `Stopwatch`, mutable statics, I/O) |
| `VER005` | Error / Warning | Disallowed control flow (unannotated loops, `goto`, `async`/`await`/`Task`, `lock`, `throw`, `try`/`catch`) |
| `VER006` | Error / Warning | Unverified call outside `[Verifiable]`/`[PureAxiom]`/trusted BCL, `System.Reflection`, `dynamic`, unanalyzed delegates |
| `VER007` | Error (everywhere) | Dropped, overwritten, double-consumed, or leaked `[MustConsume]` linear obligation token |
| `VER101` | Error | Invalid or unsupported C# expression syntax in `[Requires]` / `[Ensures]` / `[Invariant]` / `[LoopInvariant]` / `[Decreases]` |
| `VER102` | Error | Z3 found a concrete counterexample (`SAT`) violating a contract, invariant, overflow check, or collection index |
| `VER103` | Error | Z3 returned `unknown`, timed out, or hit an SMT lowering error |
| `VER104` | Warning | Z3 executable not found; SMT proof checking was skipped |
| `VER201` | Error | `[Refined<T>]` applied to a type not declared as `readonly partial record struct` |
| `VER202` | Error | Empty or syntactically invalid predicate in `[Refined<T>]` or `[TypeAlias<T>]` |
| `VER203` | Error | Invalid C# identifier name in `[TypeAlias<T>]` |
| `VER204` | Error | Duplicate `[TypeAlias<T>]` full type name (`Namespace.Name`) in compilation |
