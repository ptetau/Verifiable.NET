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
The first console line of every diagnostic (`VER001`–`VER302`) carries a concise, actionable summary (including the obligation, method, and counterexample for `VER102`, or the failing contract/reason for `VER101`–`VER103`), followed by the full Elm-style block.
For the complete multiline message and JSON when MSBuild truncates diagnostics to one console line, either run `verifiable-query build MyProject.csproj` / `verifiable-query check --project MyProject.csproj` (or `verifiable-query check --sarif proof.sarif`), or run
`dotnet build MyProject.csproj -p:VerifiableAgent=1 -p:ErrorLog=proof.sarif%2Cversion=2.1`
and read the diagnostic entry's `message.text` under `runs[].results[]` in `proof.sarif`.

All diagnostics (`VER001`–`VER302`) use an Elm-style layout (single-line console summary, `-- TITLE ---- File.cs` banner, numbered source excerpt with `^` carets or multi-line `|>` gutter markers, `"Why this rule exists:"`, and a concrete `Fix:` line).
When Z3 refutes a contract (`SAT`) with `VERIFIABLE_AGENT=1`, `Verifiable.Analyzers` outputs:
- Single-line console summary, Elm-style banner, source caret (plus linked contract declaration snippet when applicable), optional `"Why this rule exists:"` (from `because` / attribute `description`), `Contract:`, `Counterexample:` (`fromBal = 0.0`, `amount = 1.0`, `result = ...`), and failed-clause pinpointing with evaluated `Left`/`Right` values
- A `Fix:` line naming the exact remediation (plus boundary/off-by-one hints when applicable)
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
All Gatekeeper (`VER001`–`VER007`), SMT (`VER101`–`VER104`), Source Generator (`VER201`–`VER203`), and Spec Quality (`VER301`–`VER302`, default `Info`) diagnostics include a single-line console summary, an Elm-style explanation, and a concrete `Fix:` guidance clause. Inside `[Verifiable]` methods, `VerifiableNullabilitySuppressor` (`VER_SUP001`/`VER_SUP002`) also suppresses redundant C# `CS8602`/`CS8604` warnings when Z3 proves non-null along every reaching path. See [`docs/manual.md`](manual.md) and [`README.md`](../README.md) for full details and examples.

## 2. Z3 Discovery & Caching

`Z3SolverRunner` resolves Z3 in order:
1. `VERIFIABLE_Z3_PATH` environment variable
2. MSBuild `VerifiableZ3Path` property (`-p:VerifiableZ3Path=...`)
3. `.tools/z3/z3(.exe)` walking up from source file directory
4. `~/.verifiable/z3/z3(.exe)` (default target of `scripts/install-z3.ps1` and `scripts/install-z3.sh`)
5. `PATH`

Queries run over persistent `z3 -in` worker processes with `(echo "__VERIFIABLE_DONE__")` sentinels, `(set-option :timeout 2500)`, and SHA-256 in-memory + `%TEMP%/verifiable-smt-cache-v1` caching.

## 3. GalaxyCheck Integration (`external/GalaxyCheck`)

All test suites use **GalaxyCheck** (`https://github.com/ptetau/GalaxyCheck`) via `GalaxyCheck.Xunit` and `Verifiable.Testing`:
- Property-based tests (`[Property]`) for refined types, `[InboundPort]` gates, Gatekeeper severity parity, CodeFixes, and Z3 counterexample soundness.
- Stateful differential + fault-injected SQLite persistence properties (`DifferentialPersistenceProperty.Create`, `FaultInjectedSqliteHarness`) testing `PreCommitCrash`, `PostCommitCrashBeforeAck`, and `DuplicateDelivery` against WAL SQLite and append-only journal replay.

For full GalaxyCheck agent documentation, see `external/GalaxyCheck/docs/agents.md` and `external/GalaxyCheck/docs/llms.txt`.

## 4. `Verifiable.Query` CLI Agent Schema

`Verifiable.Query` (`verifiable-query`, pure .NET Core `net8.0`–`net11.0`) exposes `ask`, `check-invariant`, `list`, `check` (over `--project`, `--file`, `--stdin`, or `--sarif`), and `build` (`<project-or-sln>`).

- **Exit codes**: `0` proved/passed, `1` refuted/check errors, `2` inconclusive, `3` usage/compilation/binding error, `4` Z3 not found.
- **Color**: `--color auto|always|never` (ANSI color is automatically disabled under `VERIFIABLE_AGENT=1`, `NO_COLOR`, `--format json`, or redirected stdout).
- **`--format json`** writes structured JSON to stdout; **`--format elm` + `VERIFIABLE_AGENT=1`** appends:
  - `<verifiable-query-result>{...}</verifiable-query-result>` (`ask` / `check-invariant`):
    - Top-level fields: `command`, `status` (`"proved" | "refuted" | "inconclusive" | "error" | "z3_not_found"`), `exitCode`, `claim`, optional `assume`, `because`, `targetType`, `error`, `fix`, and `results[]`.
    - Per-method fields: `method`, `returnType`, `status`, `vacuous`, `pathsChecked`, `smtHash`, `reasons[]` (`{kind, label, spirit?, file?, line?}` from Z3 unsat core), `counterexamples[]` (`{file, line, values}`), optional `error`, `hint`.
  - `<verifiable-symbols>{...}</verifiable-symbols>` (`list`): discovered `[Verifiable]` types, `[Invariant]` members, and methods with contract counts.
  - `<verifiable-check-result>{...}</verifiable-check-result>` (`check` / `build`):
    - Top-level fields: `command`, `errorCount`, `warningCount`, `suppressedNullableCount`, and `diagnostics[]` (`{id, severity, file, line, column, summary}`).
