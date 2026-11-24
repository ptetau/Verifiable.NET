# Verifiable.NET (`csharpest`)

**Verifiable.NET** is a compile-time formal verification toolchain and incremental refactoring suite for standard C# (.NET 9 / .NET 10). Instead of introducing a separate verification language, developers mark deterministic domain boundaries as **Verifiable Cages** (`[Verifiable]`) inside ordinary C# solutions—enforced at compile time by Roslyn Source Generators, Roslyn Analyzers, Roslyn CodeFixes, and an out-of-process **Z3** SMT prover, with **zero runtime overhead**.

Tested end-to-end with **[GalaxyCheck](https://github.com/ptetau/galactic-heck)** property-based and stateful differential persistence tests.

---

## Core Architecture

1. **`Verifiable.Contracts`** (`net9.0;net10.0`, zero dependencies): Attributes (`[Verifiable]`, `[VerifiableCandidate]`, `[Refined<T>]`, `[Invariant]`, `[Requires]`, `[Ensures]`, `[LoopInvariant]`, `[Decreases]`, `[CheckedArithmetic]`, `[MustConsume]`, `[ConsumesObligation]`, `[InboundPort]`, `[Ghost]`, `[PureAxiom]`) and immutable runtime types (`Result<T, TError>`, `MarshalResult<T>`, `ContractViolation`, `Unit`, `Obligation`).
2. **`Verifiable.Generators`** (`netstandard2.0`):
   - `[Refined<T>]` expands single-line `readonly partial record struct` declarations into full nominal types with `IComparable<T>`, `IsValid`, `[PureAxiom] From`, `TryFrom`, `TryMarshal`, `System.Text.Json.Serialization.JsonConverter<T>` (including `ReadAsPropertyName`/`WriteAsPropertyName` for dictionary keys), and `[PureAxiom] ReadUnchecked<T>(ReadOnlySpan<T>)` for zero-based bounded integer indices.
   - `[InboundPort]` recursively generates `Raw*` DTOs, `*Gate.TryMarshal` invariant validators, and `*Port.*` wrappers for untrusted I/O boundaries.
3. **`Verifiable.Analyzers`** (`netstandard2.0`):
   - **Gatekeeper (`VER001`–`VER006`)**: Emits build **Errors** on `[Verifiable]` and migration **Warnings** on `[VerifiableCandidate]`.
   - **Linear Obligation Checker (`VER007`)**: Path-sensitive Roslyn `ControlFlowGraph` dataflow analysis across all methods enforcing `[MustConsume]` -> `[ConsumesObligation]` / return / `Obligation.Discard`.
   - **Z3 SMT Prover (`VER101`–`VER104`)**: Lowers Roslyn `ControlFlowGraph` to SSA SMT-LIB2 queries (`Int`, BitVec under `[CheckedArithmetic]`, exact `Real` rationals for `decimal`, `VerStr` strings + `str.len`, extensional `ImmutableDictionary`/`ImmutableList`/`ImmutableHashSet` arrays, datatypes, Occurrence Typing path conditions, and inductive `[LoopInvariant]`/`[Decreases]`), backed by a pooled `z3 -in` runner and SHA-256 cache.
4. **`Verifiable.CodeFixes`** (`netstandard2.0`): Automated Roslyn CodeFix providers for `[VerifiableCandidate]` migration (`HoistAmbientAuthority`, `FreezeMutableState`, `SandwichIoSplit`, `ExceptionToUnion`).
5. **`Verifiable.Testing`** (`net10.0`): GalaxyCheck generators (`VerifiableGen`) and `FaultInjectedSqliteHarness` (`PreCommitCrash`, `PostCommitCrashBeforeAck`, `DuplicateDelivery`) + `DifferentialPersistenceProperty`.

---

## Diagnostic Rules

| ID | Category | Enforces |
| --- | --- | --- |
| `VER001` | Heap Aliasing | Bans mutable classes, non-readonly structs, `set;` properties, `ref`/`out`, and member mutations inside cages. |
| `VER002` | Collections | Bans `List<T>`, `Dictionary<K,V>`, `HashSet<T>`, and mutable `T[]`; requires `System.Collections.Immutable`. |
| `VER003` | Unchecked Init | Bans `default(T)` / parameterless `new T()` on `[Invariant]` or `[Refined<T>]` structs. |
| `VER004` | Nondeterminism | Bans `DateTime.Now`/`UtcNow`, `DateTimeOffset.Now`/`UtcNow`, `Guid.NewGuid()`, `Random`, `Stopwatch`, mutable statics, I/O. |
| `VER005` | Control Flow | Bans unannotated `while`/`do`/`foreach`, `goto`, `async`/`await`/`Task`, `lock`, `throw`, `try`/`catch`. |
| `VER006` | Closed World | Bans calls outside `[Verifiable]`/`[PureAxiom]`/trusted pure BCL, `System.Reflection`, `dynamic`, unanalyzed delegates. |
| `VER007` | Linear Types | Enforces `[MustConsume]` obligation discharge across all execution paths. |
| `VER101`–`VER104` | SMT Prover | Invalid contracts (`VER101`), Z3 `SAT` counterexamples (`VER102`), `UNKNOWN`/timeout (`VER103`), missing Z3 (`VER104`). |
| `VER201`–`VER202` | Generators | Invalid `[Refined<T>]` struct declaration (`VER201`) or predicate (`VER202`). |

---

## Reference Applications (`samples/`)

1. **`samples/TodoApp`** (`TodoApp.Shared`, `TodoApp.Server`, `TodoApp.Client`):
   - Full-stack Blazor WebAssembly MVU -> ASP.NET Core Minimal API -> WAL-mode SQLite with `processed_commands` idempotency.
   - Z3-verified conservation, idempotency, and resurrection prevention laws, plus compile-time `VER007` obligation tracking across `UnsentTodoCommand` and `PendingTodoCommit`.
2. **`samples/AccountingApp`** (`AccountingApp.Shared`, `AccountingApp.Server`, `AccountingApp.Client`):
   - Full-stack Blazor WebAssembly double-entry ledger -> ASP.NET Core Minimal API -> append-only SQLite journal store.
   - Z3-verified exact decimal arithmetic, `TotalDebits == TotalCredits`, overdraft guard, multi-line balanced postings, and append-only reversal immutability, plus GalaxyCheck differential tests proving `ReplayLedgerAsync()` matches the verified `LedgerState`.

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

## Agent UX

Verifiable.NET supports first-class agent workflows out of the box (see [`AGENTS.md`](AGENTS.md), [`docs/agents.md`](docs/agents.md), and [`llms.txt`](llms.txt)):
- Set `VERIFIABLE_AGENT=1` for `<verifiable-counterexample>{...}</verifiable-counterexample>` single-line JSON payloads on `VER102` failures.
- Set `GALAXYCHECK_AGENT=1` for deterministic seed-0 GalaxyCheck property execution and `<galaxycheck-failure>{...}</galaxycheck-failure>` JSON payloads.
- Every diagnostic and generator error ends with an actionable `Fix:` instruction.
- Every NuGet package ships `llms.txt` at the package root `\`.
