# Verifiable.NET Reference Manual

**Verifiable.NET** brings compile-time formal verification, type-safe specification contracts, constrained nominal types, protocol obligation tracking, and incremental refactoring directly into standard C# (.NET 9 and .NET 10). Everything compiles cleanly with zero third-party runtime overhead.

---

## Table of Contents

1. [Core Philosophy: Verifiable Cages](#1-core-philosophy-verifiable-cages)
2. [Getting Started & Installing Z3](#2-getting-started--installing-z3)
3. [Constrained Type Aliases (`[TypeAlias<T>]` & `[Refined<T>]`)](#3-constrained-type-aliases-typealiast--refinedt)
4. [Hierarchical `[Verifiable]` Scopes & The Gatekeeper (`VER001`–`VER006`)](#4-hierarchical-verifiable-scopes--the-gatekeeper-ver001ver006)
5. [Type-Safe Contracts & The Z3 SMT Prover (`Spec.*` & `[Invariant]`)](#5-type-safe-contracts--the-z3-smt-prover-spec--invariant)
6. [Safe I/O Boundaries with `[InboundPort]`](#6-safe-io-boundaries-with-inboundport)
7. [Linear Obligation Tokens (`[MustConsume]`)](#7-linear-obligation-tokens-mustconsume)
8. [Incremental Migration with `[VerifiableCandidate]` & CodeFixes](#8-incremental-migration-with-verifiablecandidate--codefixes)
9. [Property-Based & Fault-Injected Persistence Testing with GalaxyCheck](#9-property-based--fault-injected-persistence-testing-with-galaxycheck)
10. [Walkthroughs: `TodoApp` and `AccountingApp`](#10-walkthroughs-todoapp-and-accountingapp)
11. [Agent UX (`VERIFIABLE_AGENT=1` & `GALAXYCHECK_AGENT=1`)](#11-agent-ux-verifiable_agent1--galaxycheck_agent1)
12. [Complete Diagnostic Reference (`VER001`–`VER202`)](#12-complete-diagnostic-reference-ver001ver202)

---

## 1. Core Philosophy: Verifiable Cages

Real applications deal with HTTP requests, SQLite databases, clocks (`DateTimeOffset.UtcNow`), random GUIDs, and UI state. Formal verification works best when you isolate **untrusted I/O shells** from **deterministic domain cores** (*Sandwich Architecture*):

1. **Untrusted I/O Shell** parses JSON / SQL rows / UI input.
2. **Inbound Port (`[InboundPort]`)** validates and marshals raw primitives into constrained domain types (`MarshalResult<T>`).
3. **Verifiable Cage (`[Verifiable]`)** executes pure, deterministic domain functions whose preconditions, postconditions, loop invariants, and type invariants (`Spec.*`, `[Invariant]`) are proved at compile time by **Z3**.
4. **Linear Obligation Tokens (`[MustConsume]`)** enforce end-to-end protocol safety across UI -> API -> DB (`VER007`).

---

## 2. Getting Started & Installing Z3

### Prerequisites
- .NET SDK 9 or .NET SDK 10
- Z3 SMT Solver binary (`z3.exe` on Windows, `z3` on Linux/macOS)

```powershell
# Windows (installs into %USERPROFILE%\.verifiable\z3\)
powershell -ExecutionPolicy Bypass -File scripts/install-z3.ps1
```

```bash
# Linux / macOS (installs into ~/.verifiable/z3/)
bash scripts/install-z3.sh
```

Discovery order: `VERIFIABLE_Z3_PATH` -> MSBuild `-p:VerifiableZ3Path=...` -> `.tools/z3/` -> `~/.verifiable/z3/` -> `PATH`.

Run full verification across all libraries, Blazor Wasm clients, Z3 proofs, and GalaxyCheck tests:

```powershell
.\scripts\verify.ps1   # or ./scripts/verify.sh
```

---

## 3. Constrained Type Aliases (`[TypeAlias<T>]` & `[Refined<T>]`)

Push domain invariants into nominal `readonly partial record struct` types by writing a type-checked `public static bool IsValid(T value)` predicate:

```csharp
using System;
using Verifiable.Contracts;

[TypeAlias<int>]
public readonly partial record struct TodoId
{
    public static bool IsValid(int value) => value >= 1;
}

[TypeAlias<int>]
public readonly partial record struct NonNegativeInt
{
    public static bool IsValid(int value) => value >= 0;
}

[TypeAlias<decimal>]
public readonly partial record struct PositiveAmount
{
    public static bool IsValid(decimal value) => value > 0m && value <= 1000000000m;
}

[TypeAlias<string>]
public readonly partial record struct TodoTitle
{
    public static bool IsValid(string value) => value is { Length: >= 1 and <= 200 };
}

[TypeAlias<Guid>]
public readonly partial record struct CommandId
{
    public static bool IsValid(Guid value) => value != Guid.Empty;
}
```

`[Refined<T>]` behaves identically (generating `*.Refined.g.cs` instead of `*.TypeAlias.g.cs`) when you want to add custom instance members alongside `IsValid`.

### Generated Members
Every `[TypeAlias<T>]` / `[Refined<T>]` generates:
- Constructor `Name(TUnderlying Value)`, `IComparable<Name>.CompareTo`, `ToString()` (delegating to `Value`), `ToString(string? format)` / `ToString(string?, IFormatProvider?)` on numeric types & `Guid`, and `Length` on `string`.
- `[PureAxiom] public static implicit operator TUnderlying(Name alias) => alias.Value` for seamless widening without `.Value` clutter.
- `[PureAxiom]` relational operators `<`, `<=`, `>`, `>=` on numeric types and `char`.
- `[PureAxiom] From(TUnderlying value)` with `Spec.Requires(IsValid(value))` and `Spec.Ensures<Name>(result => result.Value == value)` proved by Z3 at call sites.
- `TryFrom(value, out result)`, `TryFrom(value) -> MarshalResult<Name>`, and `TryMarshal(value, fieldName)` for non-throwing boundary checks.
- `JsonConverter<Name>` enforcing `IsValid` on `Read` AND `ReadAsPropertyName` / `WriteAsPropertyName` so `ImmutableDictionary<AliasKey, V>` round-trips cleanly via `System.Text.Json`.
- Zero-based integer range elimination (`value >= 0 && value < N` or `value is >= 0 and < N`) generating `UpperBoundExclusive` and `[PureAxiom] ReadUnchecked<T>(ReadOnlySpan<T> buffer)` with `Spec.Requires(buffer.Length >= N)`.

`IsValid(...)` may use an expression body (`=> ...`) or a single-statement `{ return ...; }` body. When the underlying type is a reference type and the parameter is non-nullable (`string value`), the generator automatically adds a `value is not null` guard at runtime and in `From`.

---

## 4. Hierarchical `[Verifiable]` Scopes & The Gatekeeper (`VER001`–`VER006`)

Apply `[Verifiable]`, `[VerifiableCandidate]`, and `[CheckedArithmetic]` at the **highest** appropriate level:
- `[assembly: Verifiable]` (project-wide)
- `[Verifiable]` on a `class`, `static class`, `record`, or `struct` (type-wide + nested types)
- `[Verifiable]` on an individual method

Opt out specific boundary types or helper methods via `[Unverifiable]`. The nearest attribute along `method -> enclosing type -> assembly` determines scope, and calling `[Unverifiable]` from `[Verifiable]` triggers `VER006` unless marked `[PureAxiom]`.

### Gatekeeper Rules (`VER001`–`VER006`)
- **`VER001` (Heap Aliasing / Mutation)**: No mutable classes, non-readonly structs, `set;` properties, `ref`/`out`, or member mutation. Use `readonly record struct` / `record` with `init;` and `with`.
- **`VER002` (Collections)**: No `List<T>`, `Dictionary<K,V>`, `HashSet<T>`, or `T[]`. Use `System.Collections.Immutable`.
- **`VER003` (Unchecked Init)**: No `default(T)` or `new T()` on structs with `[Invariant]`, `[TypeAlias<T>]`, or `[Refined<T>]`.
- **`VER004` (Ambient Authority)**: No `DateTime.Now`/`UtcNow`, `DateTimeOffset.Now`/`UtcNow`, `Guid.NewGuid()`, `Random`, `Stopwatch`, mutable statics, or I/O. Pass explicit parameters.
- **`VER005` (Control Flow)**: No unannotated `while`/`do`/`foreach` (must contain `Spec.LoopInvariant` or `Spec.Decreases`), `goto`, `async`/`await`/`Task`, `lock`, `throw`, `try`/`catch`. Return `Result<T, TError>`.
- **`VER006` (Closed World)**: No unverified calls, reflection, or `dynamic`.

---

## 5. Type-Safe Contracts & The Z3 SMT Prover (`Spec.*` & `[Invariant]`)

### Method Specifications (`Spec`)
`Verifiable.Contracts.Spec` provides type-checked C# contracts decorated with `[Conditional("VERIFIABLE_SPEC")]`:

- **`Spec.Requires(bool condition)`**: Assumed on entry inside the verified method, proved under the caller's path condition (**Occurrence Typing**) at `[Verifiable]` call sites, and enforced at runtime by `[InboundPort]` wrappers when placed at the start of an `[InboundPort]` method.
- **`Spec.Ensures<TResult>(Func<TResult, bool> predicate)`** (or **`Spec.Ensures(bool condition)`** with **`Spec.Result<TResult>()`**): Proved on every `return` branch and assumed by modular `[Verifiable]` callers. The type parameter `TResult` is checked against the enclosing method's return type (`VER101` on mismatch).
- **`Spec.LoopInvariant(bool condition)`** & **`Spec.Decreases(int/long measure)`**: Placed inside `while`/`for`/`foreach` bodies; Z3 verifies entry, inductive preservation on back-edges, non-negativity, and strict decrease. Each loop in a method can carry its own independent `Spec.LoopInvariant` / `Spec.Decreases`.

Because `VERIFIABLE_SPEC` is not defined in normal builds, **the C# compiler omits all `Spec.*` calls and lambdas from emitted IL**—giving full IDE IntelliSense, rename refactoring, and zero runtime overhead.

### Typed Invariants (`[Invariant]`)
Attach `[Invariant]` to any non-static parameterless `bool` method or `bool` property returning a single pure expression:

```csharp
public readonly record struct PostedJournalEntry(...)
{
    [Invariant]
    public bool IsBalanced() => EntryDebitTotal == EntryCreditTotal && Lines.Count >= 2;
}
```

### Supported SMT Theories
- `decimal` -> exact arbitrary-precision `Real` rationals.
- `int` / `long` -> mathematical `Int`, or 32/64-bit `(_ BitVec 32)` / `(_ BitVec 64)` overflow proofs under `[CheckedArithmetic]`.
- `string` -> `VerStr` (`str-null`, `str-val`, `str.len`, concatenation, `IsNullOrEmpty`, property pattern `{ Length: ... }`).
- `ImmutableDictionary<K,V>`, `ImmutableList<T>`, `ImmutableArray<T>`, `ImmutableHashSet<T>` -> extensional array + count models with automatic bounds/key verification.
- Records, `readonly record struct`, `with` updates, tuples, `Nullable<T>`, and `Result<T, TError>`.

---

## 6. Safe I/O Boundaries with `[InboundPort]`

Putting `[InboundPort]` on a domain method generates:
- `Raw<Type>` DTOs (`*.Gate.g.cs`) excluding `[Ghost]` and `[Invariant]` members.
- `<Type>Gate.TryMarshal(Raw<Type>? raw, string path = "")` checking nested types, `[TypeAlias<T>]`/`[Refined<T>]` `IsValid`, enums, non-null strings, and all `[Invariant]` members, returning `MarshalResult<T>`.
- `<DeclaringType>Port.<Method>(...)` (`*.InboundPort.g.cs`) marshaling raw arguments and running leading `Spec.Requires(...)` preconditions before invoking the domain method.

---

## 7. Linear Obligation Tokens (`[MustConsume]`)

Annotate linear command/commit structs with `[MustConsume]`. `LinearObligationAnalyzer` (`VER007`) analyzes every method in the compilation via CFG dataflow analysis, ensuring every live obligation token reaches a `[ConsumesObligation]` method, a return statement, or `Obligation.Discard(token, reason)` on every execution path.

---

## 8. Incremental Migration with `[VerifiableCandidate]` & CodeFixes

1. Start with `[VerifiableCandidate]` (on assembly, class, or method) for Warning-level Gatekeeper diagnostics.
2. Apply `Verifiable.CodeFixes` refactorings (`HoistAmbientAuthority`, `FreezeMutableState`, `SandwichIoSplit`, `ExceptionToUnion`).
3. Promote to `[Verifiable]`, push constraints into `[TypeAlias<T>]` + `[Invariant]` types, and add `Spec.*` contracts.

---

## 9. Property-Based & Fault-Injected Persistence Testing with GalaxyCheck

`Verifiable.Testing` integrates **GalaxyCheck** (`external/galactic-heck`) via `VerifiableGen`, `FaultInjectedSqliteHarness` (`PreCommitCrash`, `PostCommitCrashBeforeAck`, `DuplicateDelivery`), and `DifferentialPersistenceProperty.Create` to verify SQLite durability and `[InboundPort]` rejection against your pure domain models.

---

## 10. Walkthroughs: `TodoApp` and `AccountingApp`

- **`samples/TodoApp`**: Showcases `[assembly: Verifiable]` + `[TypeAlias<T>]` (`TodoId`, `NonNegativeInt`, `TodoTitle`, `UnixTimestamp`, `CommandId`) + `[Invariant] public bool IsConsistent()` + `[Unverifiable]` wire DTOs + Blazor Wasm MVU + idempotent SQLite persistence.
- **`samples/AccountingApp`**: Showcases class/struct `[Verifiable]` + `[TypeAlias<T>]` (`PositiveAmount`, `NonNegativeDecimal`, `PositiveDecimal`, `CurrencyCode`, etc.) + Z3-proved double-entry conservation, inductive `Spec.LoopInvariant` / `Spec.Decreases` loops, and SQLite `ReplayLedgerAsync()` parity.

---

## 11. Agent UX (`VERIFIABLE_AGENT=1` & `GALAXYCHECK_AGENT=1`)

See [`AGENTS.md`](../AGENTS.md), [`docs/agents.md`](agents.md), and [`llms.txt`](../llms.txt) for machine-readable `<verifiable-counterexample>` and `<galaxycheck-failure>` schemas.

---

## 12. Complete Diagnostic Reference (`VER001`–`VER202`)

Every diagnostic message ends with a `Fix:` instruction.

| ID | Severity | Trigger |
| --- | --- | --- |
| `VER001` | Error (`[Verifiable]`) / Warning (`[VerifiableCandidate]`) | Mutable class, non-readonly struct, `set;` property, `ref`/`out`, member mutation |
| `VER002` | Error / Warning | Mutable collection (`List<T>`, `Dictionary<K,V>`, `HashSet<T>`, `T[]`, etc.) |
| `VER003` | Error / Warning | Unchecked `default(T)` or `new T()` on `[Invariant]`, `[TypeAlias<T>]`, or `[Refined<T>]` struct |
| `VER004` | Error / Warning | Nondeterminism / ambient authority (`UtcNow`, `NewGuid()`, `Random`, `Stopwatch`, mutable statics, I/O) |
| `VER005` | Error / Warning | Unannotated loops (no `Spec.LoopInvariant`/`Spec.Decreases`), `goto`, `async`/`await`/`Task`, `lock`, `throw`, `try`/`catch` |
| `VER006` | Error / Warning | Unverified call outside `[Verifiable]`/`[PureAxiom]`/trusted BCL, reflection, `dynamic`, unanalyzed delegates |
| `VER007` | Error (everywhere) | Dropped, overwritten, double-consumed, or leaked `[MustConsume]` token |
| `VER101` | Error | Invalid or unsupported expression in `Spec.*`, `IsValid`, or `[Invariant]` (or mismatched `Spec.Ensures<T>`) |
| `VER102` | Error | Z3 found a counterexample (`SAT`) violating a contract, invariant, overflow check, or collection index |
| `VER103` | Error | Z3 returned `unknown`, timed out, or failed SMT lowering |
| `VER104` | Warning | Z3 binary not found; SMT proof checking skipped |
| `VER201` | Error | `[TypeAlias<T>]` or `[Refined<T>]` target is not `readonly partial record struct` |
| `VER202` | Error | `[TypeAlias<T>]` or `[Refined<T>]` missing a valid `public static bool IsValid(T value)` expression |
