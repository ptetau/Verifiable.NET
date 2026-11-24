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

### Structured Counterexample JSON (`VER102`)
When Z3 refutes a contract (`SAT`), `Verifiable.Analyzers` outputs:
- Concrete counterexample parameter values (`fromBal = 0m, amount = 1m`)
- A `Fix:` line naming the exact remediation
- A single-line `<verifiable-counterexample>{...}</verifiable-counterexample>` payload containing:
  - `rule`: `"VER102"`
  - `symbol`: `"DeclaringType.Method"`
  - `kind`: `"Precondition" | "Postcondition" | "TypeInvariant" | "Overflow" | "CollectionBounds" | "LoopInvariantEntry" | "LoopInvariantStep" | "LoopDecreasesEntry" | "LoopDecreasesStep"`
  - `contract`: exact attribute or check description
  - `counterexample`: key-value map of parameter names to Z3 counterexample values
  - `smtHash`: 12-char prefix of the cached SMT-LIB2 SHA-256 query hash
  - `fix`: actionable fix string

### Every Diagnostic Ends With `Fix:`
All Gatekeeper (`VER001`–`VER007`), SMT (`VER101`–`VER104`), and Source Generator (`VER201`–`VER204`) diagnostics include a concrete `Fix:` guidance clause. See [`docs/manual.md`](manual.md) for full details and examples.

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
