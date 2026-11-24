# Verifiable.NET (`csharpest`)

**Verifiable.NET** is a compile-time formal verification toolchain and incremental refactoring suite for standard C# (.NET 9 / .NET 10). Instead of introducing a separate verification language, developers declare **Constrained Type Aliases** (`[TypeAlias<T>]` / `[Refined<T>]`) and mark deterministic domain assemblies, classes, or methods as **Verifiable Cages** (`[Verifiable]`) inside ordinary C# solutions—enforced at compile time by Roslyn Source Generators, Roslyn Analyzers, Roslyn CodeFixes, and an out-of-process **Z3** SMT prover, with **zero runtime overhead**.

Tested end-to-end with **[GalaxyCheck](https://github.com/ptetau/galactic-heck)** property-based and stateful differential persistence tests.

> **New to Verifiable.NET?** Read the **[User Manual (`docs/manual.md`)](docs/manual.md)** for a complete, example-driven guide.

---

## Core Architecture

1. **`Verifiable.Contracts`** (`net9.0;net10.0`, zero dependencies): Attributes (`[Verifiable]`, `[Unverifiable]`, `[VerifiableCandidate]`, `[TypeAlias<T>]`, `[Refined<T>]`, `[Invariant]`, `[Requires]`, `[Ensures]`, `[LoopInvariant]`, `[Decreases]`, `[CheckedArithmetic]`, `[MustConsume]`, `[ConsumesObligation]`, `[InboundPort]`, `[Ghost]`, `[PureAxiom]`) and immutable runtime types (`Result<T, TError>`, `MarshalResult<T>`, `ContractViolation`, `Unit`, `Obligation`).
2. **`Verifiable.Generators`** (`netstandard2.0`):
   - `[TypeAlias<T>]` & `[Refined<T>]`: Single-line constrained nominal types (`[assembly: TypeAlias<int>("TodoId", "Value >= 1", Namespace = "...")]` or `[Refined<T>]` `readonly partial record struct`) generated with `IComparable<T>`, `implicit operator TUnderlying`, relational operators `<`/`<=`/`>`/`>=`, `ToString()`, `IsValid`, `[PureAxiom] From`, `TryFrom`, `TryMarshal`, `JsonConverter<T>` (including `ReadAsPropertyName`/`WriteAsPropertyName` for dictionary keys), and `[PureAxiom] ReadUnchecked<T>(ReadOnlySpan<T>)` for zero-based bounded integer indices.
   - `[InboundPort]`: Recursively generates `Raw*` DTOs, `*Gate.TryMarshal` invariant validators, and `*Port.*` wrappers for untrusted I/O boundaries (with full support for same-compilation `[TypeAlias<T>]` types).
3. **`Verifiable.Analyzers`** (`netstandard2.0`):
   - **Hierarchical `[Verifiable]` Scopes**: Annotate once at the **assembly**, **class/struct**, or **method** level (opt out specific boundary types/methods via `[Unverifiable]`).
   - **Gatekeeper (`VER001`–`VER006`)**: Emits build **Errors** on `[Verifiable]` and migration **Warnings** on `[VerifiableCandidate]`.
   - **Linear Obligation Checker (`VER007`)**: Path-sensitive Roslyn `ControlFlowGraph` dataflow analysis across all methods enforcing `[MustConsume]` -> `[ConsumesObligation]` / return / `Obligation.Discard`.
   - **Z3 SMT Prover (`VER101`–`VER104`)**: Lowers Roslyn `ControlFlowGraph` to SSA SMT-LIB2 queries (`Int`, BitVec under `[CheckedArithmetic]`, exact `Real` rationals for `decimal`, `VerStr` strings + `str.len`, extensional `ImmutableDictionary`/`ImmutableList`/`ImmutableHashSet` arrays, datatypes, Occurrence Typing path conditions, implicit alias widening, and inductive `[LoopInvariant]`/`[Decreases]`), backed by a pooled `z3 -in` runner and SHA-256 cache.
4. **`Verifiable.CodeFixes`** (`netstandard2.0`): Automated Roslyn CodeFix providers for `[VerifiableCandidate]` migration (`HoistAmbientAuthority`, `FreezeMutableState`, `SandwichIoSplit`, `ExceptionToUnion`).
5. **`Verifiable.Testing`** (`net10.0`): GalaxyCheck generators (`VerifiableGen`) and `FaultInjectedSqliteHarness` (`PreCommitCrash`, `PostCommitCrashBeforeAck`, `DuplicateDelivery`) + `DifferentialPersistenceProperty`.

---

## Diagnostic Rules

| ID | Category | Enforces |
| --- | --- | --- |
| `VER001` | Heap Aliasing | Bans mutable classes, non-readonly structs, `set;` properties, `ref`/`out`, and member mutations inside cages. |
| `VER002` | Collections | Bans `List<T>`, `Dictionary<K,V>`, `HashSet<T>`, and mutable `T[]`; requires `System.Collections.Immutable`. |
| `VER003` | Unchecked Init | Bans `default(T)` / parameterless `new T()` on `[Invariant]`, `[Refined<T>]`, or `[TypeAlias<T>]` structs. |
| `VER004` | Nondeterminism | Bans `DateTime.Now`/`UtcNow`, `DateTimeOffset.Now`/`UtcNow`, `Guid.NewGuid()`, `Random`, `Stopwatch`, mutable statics, I/O. |
| `VER005` | Control Flow | Bans unannotated `while`/`do`/`foreach`, `goto`, `async`/`await`/`Task`, `lock`, `throw`, `try`/`catch`. |
| `VER006` | Closed World | Bans calls outside `[Verifiable]`/`[PureAxiom]`/trusted pure BCL, `System.Reflection`, `dynamic`, unanalyzed delegates. |
| `VER007` | Linear Types | Enforces `[MustConsume]` obligation discharge across all execution paths. |
| `VER101`–`VER104` | SMT Prover | Invalid contracts (`VER101`), Z3 `SAT` counterexamples (`VER102`), `UNKNOWN`/timeout (`VER103`), missing Z3 (`VER104`). |
| `VER201`–`VER204` | Generators | Invalid `[Refined<T>]` declaration (`VER201`), invalid predicate (`VER202`), invalid `[TypeAlias<T>]` identifier (`VER203`), duplicate alias (`VER204`). |

---

## Reference Applications (`samples/`)

1. **`samples/TodoApp`** (`TodoApp.Shared`, `TodoApp.Server`, `TodoApp.Client`):
   - Demonstrates assembly-level `[assembly: Verifiable]`, `[assembly: TypeAlias<...>]` (`TodoId`, `NonNegativeInt`, `TodoTitle`, `UnixTimestamp`, `CommandId`), and `[Unverifiable]` wire DTOs across Blazor WebAssembly MVU -> ASP.NET Core Minimal API -> WAL-mode SQLite with `processed_commands` idempotency.
2. **`samples/AccountingApp`** (`AccountingApp.Shared`, `AccountingApp.Server`, `AccountingApp.Client`):
   - Demonstrates class/struct-level `[Verifiable]` cascading and constrained type aliases (`PositiveAmount`, `NonNegativeDecimal`, `PositiveDecimal`, `CurrencyCode`, etc.) with Z3-verified exact decimal arithmetic, `TotalDebits == TotalCredits`, overdraft guard, multi-line balanced postings, append-only reversals, and `ReplayLedgerAsync()` parity.

---

## Quick Start

```powershell
# 1. Clone with GalaxyCheck submodule
git clone --recursive https://github.com/ptetau/csharpest.git
cd csharpest

# 2. Install Z3
powershell -ExecutionPolicy Bypass -File scripts/install-z3.ps1
# (On Linux/macOS: bash scripts/install-z3.sh)

# 3. Build & verify all libraries, Blazor Wasm clients, Z3 proofs, and GalaxyCheck tests
.\scripts\verify.ps1
# (On Linux/macOS: ./scripts/verify.sh)
```

---

## Documentation & Agent UX

- **[User Manual (`docs/manual.md`)](docs/manual.md)** — Complete developer guide, tutorials, and diagnostic reference.
- **[`AGENTS.md`](AGENTS.md)** & **[`docs/agents.md`](docs/agents.md)** — Agent workflows (`VERIFIABLE_AGENT=1`, `GALAXYCHECK_AGENT=1`, JSON counterexamples, Z3 discovery).
- **[`llms.txt`](llms.txt)** — Compact single-file LLM reference (also packed at `\` inside every `Verifiable.*` NuGet package).
