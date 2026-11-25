# Agent UX Guide (`Verifiable.NET`)

Verifiable.NET pairs **Roslyn compile-time verification** with **GalaxyCheck property-based testing** so agents receive exact, actionable diagnostics on every build and test run.

## 1. Agent Mode (`VERIFIABLE_AGENT=1` + `GALAXYCHECK_AGENT=1`)

Run builds and tests with both variables enabled:

```powershell
$env:VERIFIABLE_AGENT = "1"
$env:GALAXYCHECK_AGENT = "1"
dotnet build Verifiable.NET.sln -c Release
dotnet test Verifiable.NET.sln -c Release --no-build
```

### Structured Counterexample JSON (`VER102`) & Elm Output
All diagnostics (`VER001`–`VER202`) use an Elm-style layout (`-- TITLE ---- File.cs`, numbered source excerpt with `^` caret underline, `"Why this rule exists:"`, and a concrete `Fix:` line).
When Z3 refutes a contract (`SAT`) with `VERIFIABLE_AGENT=1`, `Verifiable.Analyzers` outputs:
- Elm-style banner, source caret, optional `"Why this rule exists:"` (from `because` / attribute `description`), `Contract:`, and `Counterexample:` (`fromBal = 0.0`, `amount = 1.0`)
- A `Fix:` line naming the exact remediation
- A single-line `<verifiable-counterexample>{...}</verifiable-counterexample>` payload containing:
  - `rule`: `"VER102"`
  - `symbol`: `"DeclaringType.Method"`
  - `kind`: `"Precondition" | "Postcondition" | "TypeInvariant" | "Overflow" | "DivisionByZero" | "CollectionBounds" | "LoopInvariantEntry" | "LoopInvariantStep" | "LoopDecreasesEntry" | "LoopDecreasesStep"`
  - `contract`: exact contract or check description
  - `reason` *(optional)*: human-readable `because` or attribute `description` string when present
  - `counterexample`: key-value map of parameter names to Z3 counterexample values
  - `smtHash`: 12-char prefix of the cached SMT-LIB2 SHA-256 query hash
  - `fix`: actionable fix string

### Every Diagnostic Ends With `Fix:`
All Gatekeeper (`VER001`–`VER007`), SMT (`VER101`–`VER104`), and Source Generator (`VER201`–`VER202`) diagnostics include an Elm-style explanation and a concrete `Fix:` guidance clause. See [`docs/manual.md`](manual.md) and [`README.md`](../README.md) for full details and examples.

## 2. Z3 Discovery & Caching

`Z3SolverRunner` resolves Z3 in order:
1. `VERIFIABLE_Z3_PATH` environment variable
2. MSBuild `VerifiableZ3Path` property (`-p:VerifiableZ3Path=...`)
3. `.tools/z3/z3(.exe)` walking up from source file directory
4. `~/.verifiable/z3/z3(.exe)` (default target of `scripts/install-z3.ps1` and `scripts/install-z3.sh`)
5. `PATH`

Queries run over persistent `z3 -in` worker processes with `(echo "__VERIFIABLE_DONE__")` sentinels, `(set-option :timeout 2500)`, and SHA-256 in-memory + `%TEMP%/verifiable-smt-cache-v1` caching.

## 3. GalaxyCheck Integration (`external/galactic-heck`)

All test suites use **GalaxyCheck** (`https://github.com/ptetau/galactic-heck`) via `GalaxyCheck.Xunit` and `Verifiable.Testing`:
- Property-based tests (`[Property]`) for refined types, `[InboundPort]` gates, Gatekeeper severity parity, CodeFixes, and Z3 counterexample soundness.
- Stateful differential + fault-injected SQLite persistence properties (`DifferentialPersistenceProperty.Create`, `FaultInjectedSqliteHarness`) testing `PreCommitCrash`, `PostCommitCrashBeforeAck`, and `DuplicateDelivery` against WAL SQLite and append-only journal replay.

For full GalaxyCheck agent documentation, see `external/galactic-heck/docs/agents.md` and `external/galactic-heck/docs/llms.txt`.

## 4. `Verifiable.Query` CLI Agent Schema

`Verifiable.Query` (`verifiable-query`, pure .NET Core) exposes `ask`, `check-invariant`, and `list` over `--project`, `--file`, or `--stdin`.

- **Exit codes**: `0` proved, `1` refuted, `2` inconclusive, `3` usage/compilation/binding error, `4` Z3 not found.
- **`--format json`** writes structured JSON to stdout; **`--format elm` + `VERIFIABLE_AGENT=1`** appends `<verifiable-query-result>{...}</verifiable-query-result>` (or `<verifiable-symbols>{...}</verifiable-symbols>` on `list`):
  - Top-level fields: `command`, `status` (`"proved" | "refuted" | "inconclusive" | "error" | "z3_not_found"`), `exitCode`, `claim`, optional `assume`, `because`, `targetType`, `error`, `fix`, and `results[]`.
  - Per-method fields: `method`, `returnType`, `status`, `vacuous`, `pathsChecked`, `smtHash`, `reasons[]` (`{kind, label, spirit?, file?, line?}` from Z3 unsat core), `counterexamples[]` (`{file, line, values}`), optional `error`, `hint`.
