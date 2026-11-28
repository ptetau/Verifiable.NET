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
The first console line of every diagnostic (`VER001`–`VER203`) carries a concise, actionable summary (including the obligation, method, and counterexample for `VER102`, or the failing contract/reason for `VER101`–`VER103`), followed by the full Elm-style block.
For the complete multiline message and JSON when MSBuild truncates diagnostics to one console line, run
`dotnet build MyProject.csproj -p:VerifiableAgent=1 -p:ErrorLog=proof.sarif%2Cversion=2.1`
and read the diagnostic entry's `message.text` under `runs[].results[]` in `proof.sarif`.

All diagnostics (`VER001`–`VER203`) use an Elm-style layout (single-line console summary, `-- TITLE ---- File.cs` banner, numbered source excerpt with `^` caret underline, `"Why this rule exists:"`, and a concrete `Fix:` line).
When Z3 refutes a contract (`SAT`) with `VERIFIABLE_AGENT=1`, `Verifiable.Analyzers` outputs:
- Single-line console summary, Elm-style banner, source caret, optional `"Why this rule exists:"` (from `because` / attribute `description`), `Contract:`, and `Counterexample:` (`fromBal = 0.0`, `amount = 1.0`)
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
All Gatekeeper (`VER001`–`VER007`), SMT (`VER101`–`VER106`), and Source Generator (`VER201`–`VER203`) diagnostics include a single-line console summary, an Elm-style explanation, and a concrete `Fix:` guidance clause. See [`docs/manual.md`](manual.md) and [`README.md`](../README.md) for full details and examples.

## 2. Z3 Discovery & Caching

`Z3SolverRunner` resolves Z3 in order:
1. `VERIFIABLE_Z3_PATH` environment variable
2. MSBuild `VerifiableZ3Path` property (`-p:VerifiableZ3Path=...`)
3. `.tools/z3/z3(.exe)` walking up from source file directory
4. `~/.verifiable/z3/z3(.exe)` (default target of `scripts/install-z3.ps1` and `scripts/install-z3.sh`)
5. `PATH`

If none is found, `VER104` stops the build: nothing was proved, so nothing may pass as verified. Set `<VerifiableAllowMissingZ3>true</VerifiableAllowMissingZ3>` to build without proofs on purpose (it becomes a warning).

Queries run over persistent `z3 -in` worker processes with `(echo "__VERIFIABLE_DONE__")` sentinels, `(set-option :timeout 2500)`, and SHA-256 in-memory caching plus a signed disk cache in `<LocalApplicationData>/Verifiable/smt-cache-v2` (owner-only folder built before its key is written; a folder or key open to group/others, or a link, is never trusted; on Windows the folder gets a protected ACL for you, SYSTEM and Administrators, and a folder or key owned by anyone else or with an ACL entry for anyone else is moved aside and rebuilt; each entry carries an HMAC-SHA256 over its query hash and answer, keyed with a per-user secret; entries that fail to verify are ignored). The query hash includes the Z3 binary's resolved path, size and timestamp, checked on every query, so replacing Z3 (even behind a symlink) invalidates old answers.

## 3. GalaxyCheck Integration (`external/GalaxyCheck`)

All test suites use **GalaxyCheck** (`https://github.com/ptetau/GalaxyCheck`) via `GalaxyCheck.Xunit` and `Verifiable.Testing`:
- Property-based tests (`[Property]`) for refined types, `[InboundPort]` gates, Gatekeeper severity parity, CodeFixes, and Z3 counterexample soundness.
- Stateful differential + fault-injected SQLite persistence properties (`DifferentialPersistenceProperty.Create`, `FaultInjectedSqliteHarness`) testing `PreCommitCrash`, `PostCommitCrashBeforeAck`, and `DuplicateDelivery` against WAL SQLite and append-only journal replay.

For full GalaxyCheck agent documentation, see `external/GalaxyCheck/docs/agents.md` and `external/GalaxyCheck/docs/llms.txt`.

## 4. `Verifiable.Query` CLI Agent Schema

`Verifiable.Query` (`verifiable-query`, pure .NET Core) exposes `ask`, `check-invariant`, and `list` over `--project`, `--file`, or `--stdin`.

- **Exit codes**: `0` proved, `1` refuted, `2` inconclusive, `3` usage/compilation/binding error, `4` Z3 not found.
- **`--format json`** writes structured JSON to stdout; **`--format elm` + `VERIFIABLE_AGENT=1`** appends `<verifiable-query-result>{...}</verifiable-query-result>` (or `<verifiable-symbols>{...}</verifiable-symbols>` on `list`):
  - Top-level fields: `command`, `status` (`"proved" | "refuted" | "inconclusive" | "error" | "z3_not_found"`), `exitCode`, `claim`, optional `assume`, `because`, `targetType`, `error`, `fix`, and `results[]`.
  - Per-method fields: `method`, `returnType`, `status`, `vacuous`, `pathsChecked`, `smtHash`, `reasons[]` (`{kind, label, spirit?, file?, line?}` from Z3 unsat core), `counterexamples[]` (`{file, line, values}`), optional `error`, `hint`.
