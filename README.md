# Verifiable.NET

**Write plain C#. Explain why your rules exist. Let the compiler prove they hold before the code ever runs.**

Verifiable.NET adds compile-time verification and bug-free boundary validation to standard .NET 9 / .NET 10 C# solutions. You don't learn a new proof language, and you don't pay any runtime or WebAssembly payload overhead—just standard C# attributes and `Spec.*` contracts checked at build time by Roslyn and [Z3](https://github.com/Z3Prover/z3).

---

## What problem are you trying to solve?

### 1. "Stop `0`, negative numbers, or blank strings from sneaking into my domain"

In normal C#, a `TodoId`, `AccountId`, or `PositiveAmount` is just an `int` or `decimal`, so anyone can accidentally pass `0`, `-5`, or swap two IDs.

With `[TypeAlias<T>]`, you define a tiny immutable struct, write one `IsValid` rule, and explain *why* in plain English:

```csharp
using Verifiable.Contracts;

[assembly: Verifiable]

[TypeAlias<decimal>("Order amounts must be strictly positive so transfers never move zero or negative money.")]
public readonly partial record struct PositiveAmount
{
    public static bool IsValid(decimal value) => value > 0m && value <= 1_000_000_000m;
}
```

Verifiable.NET generates `.From(...)`, `.TryFrom(...)`, `.TryMarshal(...)`, ordering operators, and a validating `System.Text.Json` converter automatically—all at zero allocation cost.

---

### 2. "Prove a business rule holds for every possible input—not just 3 unit tests"

Even good unit tests only check a handful of examples. Inside a `[Verifiable]` class or assembly, `Spec.Requires`, `Spec.Ensures`, and `[Invariant]` let the compiler check **all** valid execution paths using Z3:

```csharp
[Verifiable]
public static class LedgerEngine
{
    public static decimal Withdraw(decimal balance, PositiveAmount amount)
    {
        Spec.Requires(
            balance >= amount,
            "A withdrawal cannot exceed the account balance.");
        Spec.Ensures<decimal>(
            result => result >= 0m,
            "Balances must stay non-negative after every withdrawal.");

        return balance - amount;
    }
}
```

If you write a bug—say you forget the `Spec.Requires` check above—the build fails immediately with an **Elm-style error** that shows the exact line, reminds you *why* the rule exists, and gives concrete inputs that break it:

```text
LedgerEngine.cs(12,9): error VER102: Postcondition of LedgerEngine.Withdraw fails: Balances must stay non-negative after every withdrawal.; balance = 0.0, amount = 1.0
-- PROOF FAILED ------------------------------------------- LedgerEngine.cs

Z3 found inputs that break a postcondition in `LedgerEngine.Withdraw`.

   |
12 |         return balance - amount;
   |         ^^^^^^^^^^^^^^^^^^^^^^^^

Why this rule exists:
    Balances must stay non-negative after every withdrawal.

Contract:
    Postcondition: Spec.Ensures<decimal>(result => result >= 0m)

Counterexample:
    balance = 0.0
    amount = 1.0

Fix: Strengthen Spec.Requires(...) preconditions or correct the return value to satisfy Spec.Ensures(...).
```

`dotnet build` may show only the first line. To capture the full diagnostic and counterexample JSON, see the [agent guide](docs/agents.md#structured-counterexample-json-ver102--elm-output).

For contracts that use `??`, call helpers, or project a DTO into a record, see [Writing contracts with helpers](docs/manual.md#writing-contracts-with-helpers). If your build agent already has Z3, see [Bring your own Z3](docs/manual.md#bring-your-own-z3).

---

### 3. "Reject bad HTTP / JSON payloads cleanly at the door"

Validation belongs at the edge of your app so your core domain never sees half-baked objects. Put `[InboundPort]` on any entry method:

```csharp
[InboundPort]
public static TodoTransition Add(TodoBoard board, TodoTitle title, UnixTimestamp now)
```

Verifiable.NET generates a matching wire DTO and `TryMarshal(...)` gate (`TodoEnginePort.Add(...)`) that:

1. Checks every `[TypeAlias<T>]` rule (using your plain-English description on failure),
2. Checks every `[Invariant("...")]` on input records,
3. Checks every leading `Spec.Requires(..., "...")`, and
4. Returns `MarshalResult<T>` with clean `ContractViolation` messages ready for a `400 BadRequest` response.

---

### 4. "Stop forgetting to save changes or send queued commands"

When an action creates work that **must** be finished—like submitting an API request or committing a database transaction—mark the token struct `[MustConsume]`:

```csharp
[MustConsume]
public readonly record struct PendingTodoCommit(/* ... */);

[ConsumesObligation]
public static void Commit(PendingTodoCommit pending) { /* write to SQLite */ }
```

If any branch returns early, drops the return value, overwrites it, or discards it (`_ = ...`), the compiler stops with `VER007 -- UNCONSUMED OBLIGATION` until every path consumes, returns, or calls `Obligation.Discard(...)`.

---

### 5. "Keep verified code honest (no hidden `DateTime.UtcNow`, mutable lists, or throws)"

Inside `[Verifiable]`, the compiler enforces determinism (`VER001`–`VER006`) so proofs mean what they say and bugs are trivial to reproduce:

- Pass clocks and IDs explicitly (`UnixTimestamp now`, `CommandId commandId`) instead of reading `DateTime.UtcNow` or `Guid.NewGuid()`.
- Use `readonly record struct`, `ImmutableList<T>`, and `ImmutableDictionary<K, V>` instead of mutable classes or `List<T>`.
- Return explicit `Result<T, TError>` or transition values instead of throwing exceptions for domain errors.

Need an escape hatch for an I/O controller, logging, or SQLite adapter? Leave `[Verifiable]` off the outer shell—or put `[assembly: Verifiable]` on your shared domain project and opt individual boundary helpers out with `[Unverifiable]`.

---

### 6. "Adopt this slowly on an existing C# codebase without breaking the build"

You do not have to rewrite everything at once:

1. Add `[VerifiableCandidate]` to a class, method, or assembly. Violations appear as **warnings**, not build errors.
2. Apply the available `Verifiable.CodeFixes` for `VER001`–`VER006`. For `VER004`, the fix can move an eager clock or GUID read into a private method parameter when all callers can be updated in the same document. If a loop creates one GUID per item, keep that allocation per item; passing one GUID for the whole batch changes the result.
3. Flip `[VerifiableCandidate]` to `[Verifiable]` when the warnings reach zero.

---

### 7. "Ask whether a business rule holds (e.g., credits == debits)—and see *why* or *why not*"

Sometimes you want to ask an interactive question about your domain—without editing source files to add a new contract first. `Verifiable.Query` (`verifiable-query`) is a pure **.NET Core** CLI tool built on the same Roslyn + Z3 prover:

```powershell
# Ask across every method producing LedgerState whether debits always equal credits
dotnet run --project src/Verifiable.Query -c Release -- check-invariant `
  --project samples/AccountingApp/AccountingApp.Shared/AccountingApp.Shared.csproj `
  --type LedgerState `
  --expr "s => s.TotalDebits == s.TotalCredits"

# Ask why a specific transfer preserves balance
dotnet run --project src/Verifiable.Query -c Release -- ask `
  --project samples/AccountingApp/AccountingApp.Shared/AccountingApp.Shared.csproj `
  --target LedgerEngine.PostTransfer `
  --assert "result.State.TotalDebits == result.State.TotalCredits"
```

- **If it holds:** Z3 returns the minimal proof core (`-- QUERY HOLDS`), listing the exact `[Invariant]`, `[TypeAlias<T>]`, and `Spec.*` rules responsible—along with their `file:line` and plain-English spirit strings.
- **If it fails:** Z3 returns `-- QUERY REFUTED` with a concrete counterexample (`from = ...`, `amount = ...`, `result = ...`) and the exact `return` line where it happens.
- **Agent-friendly:** Add `--format json` or set `VERIFIABLE_AGENT=1` for single-line `<verifiable-query-result>{...}</verifiable-query-result>` output and deterministic exit codes (`0` holds, `1` refuted, `2` inconclusive, `3` expression/load error, `4` Z3 missing).

---

## Quick Start

### Prerequisites

- [.NET 9 or .NET 10 SDK](https://dotnet.microsoft.com/download)
- [Z3 SMT Solver](https://github.com/Z3Prover/z3) (auto-discovered from `VERIFIABLE_Z3_PATH`, `.tools/z3/`, `~/.verifiable/z3/`, or `PATH`)

Install Z3 in one command:

```powershell
# Windows
powershell -ExecutionPolicy Bypass -File scripts/install-z3.ps1
```

```bash
# Linux / macOS
bash scripts/install-z3.sh
```

### Build & Run All Tests

```powershell
# Windows
.\scripts\verify.ps1
```

```bash
# Linux / macOS
./scripts/verify.sh
```

Verifiable.NET uses **GalaxyCheck** (`external/GalaxyCheck`, via `GalaxyCheck.Xunit`) for property-based and stateful testing across generators, Z3 counterexample soundness, and SQLite crash/replay tests.

---

## Package Architecture

| Package | What it ships |
| --- | --- |
| `Verifiable.Contracts` | Zero-dependency attributes (`[Verifiable]`, `[TypeAlias<T>]`, `[Invariant]`, `[InboundPort]`, `[MustConsume]`), `Spec.*` helpers (stripped from Release IL), `Result<T, TError>`, and `MarshalResult<T>`. |
| `Verifiable.Generators` | Source generator that expands `[TypeAlias<T>]` / `[Refined<T>]` structs, JSON converters, and `[InboundPort]` validation wrappers. |
| `Verifiable.Analyzers` | Gatekeeper (`VER001`–`VER006`), linear obligation checker (`VER007`), and Z3 proof engine (`VER101`–`VER104`) with Elm-style diagnostics. |
| `Verifiable.CodeFixes` | Automated IDE/Roslyn refactorings for `[VerifiableCandidate]` migration. |
| `Verifiable.Testing` | Deterministic state-machine drivers and fault-injected persistence test helpers. |
| `Verifiable.Query` | Pure .NET Core CLI tool (`verifiable-query`) for asking ad-hoc `ask`, `check-invariant`, and `list` verification queries with Z3 unsat-core explanations. |

---

## Working Sample Applications

See [**`samples/README.md`**](samples/README.md) for a cross-sample pattern matrix, runnable `verifiable-query` commands, and bug counterexamples.

| Sample | Core Library | Client & Server | What It Demonstrates |
| --- | --- | --- | --- |
| [**TodoApp**](samples/TodoApp/README.md) | [`samples/TodoApp/TodoApp.Shared`](samples/TodoApp/TodoApp.Shared) | Blazor Wasm + ASP.NET Core + SQLite | `[assembly: Verifiable]`, constrained types with spirit messages, MVU state transitions, `[InboundPort]` validation (`Add`/`Complete`/`Delete`/`Rename`/`Reopen`), `Spec.LoopInvariant` + `Spec.Decreases`, `[MustConsume]` obligations, idempotent SQLite persistence. |
| [**AccountingApp**](samples/AccountingApp/README.md) | [`samples/AccountingApp/AccountingApp.Shared`](samples/AccountingApp/AccountingApp.Shared) | Blazor Wasm + ASP.NET Core + SQLite | Double-entry ledger where `TotalDebits == TotalCredits` is proved by Z3 across multi-line entries (`Spec.LoopInvariant` / `Spec.Decreases`), 2-leg and 3-leg fee-split transfers, trial balance computation, and reversals. |
| [**InventoryPricing**](samples/InventoryPricing/README.md) | [`samples/InventoryPricing`](samples/InventoryPricing) | Pure domain library | `[CheckedArithmetic]` 32-bit BitVec overflow/underflow proofs, `WarehouseBin.ReadUnchecked` fixed-buffer bounds, `BasisPoints` share bounds, and `Result<T, PricingError>` cart pricing loops. |

---

## Agent & AI Integration (`VERIFIABLE_AGENT=1`)

Verifiable.NET works cleanly in automated coding loops:

- Set `VERIFIABLE_AGENT=1` so `VER102` diagnostics include a single-line `<verifiable-counterexample>{...}</verifiable-counterexample>` JSON payload (with `"rule"`, `"symbol"`, `"kind"`, `"contract"`, `"reason"`, `"counterexample"`, `"smtHash"`, and `"fix"`).
- Set `GALAXYCHECK_AGENT=1` for deterministic seed `0`, concise failure output, and `<galaxycheck-failure>` JSON.
- Every `Verifiable.*` NuGet package bundles `llms.txt` at the package root (`\`).

---

## Further Reading

- **[User Manual & Cookbook](docs/manual.md)** — Problem-by-problem recipes, scoping rules, loop proofs, and full diagnostic index.
- **[Agent Guide](AGENTS.md)** & **[Machine Schema Reference](docs/agents.md)** — Exact JSON schemas and automation workflows.
- **[Compact Reference (`llms.txt`)](llms.txt)** — Single-file quick reference for LLMs.
