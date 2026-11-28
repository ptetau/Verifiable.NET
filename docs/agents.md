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
  - `kind`: `"Precondition" | "Postcondition" | "TypeInvariant" | "Overflow" | "DivisionByZero" | "CollectionBounds" | "LoopInvariantEntry" | "LoopInvariantStep" | "LoopDecreasesEntry" | "LoopDecreasesStep" | "RecursionDecreases" | "SwitchExpressionException"`
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

`Verifiable.Query` (`verifiable-query`, pure .NET Core) exposes `ask`, `check-invariant`, `check`, `build`, and `list` over `--project`, `--file`, or `--stdin`.

Every command follows one output contract (requirement R12):

- **Never interactive.** No prompts; stdin is read only with `--stdin`.
- **Exit codes, the same on every command** (JSON `status` carries the same word):

  | Exit | `status` | Meaning |
  | --- | --- | --- |
  | 0 | `proved` | Everything asked was proved; `check` found no errors. |
  | 1 | `refuted` | A claim was disproved, or `check` / `build` found proof or rule errors. |
  | 2 | `undecided` | The solver could not decide (`VER103`, `VER106`) and nothing was refuted. |
  | 3 | `input_error` | Bad arguments, missing files, or code that does not compile (`CS*`). |
  | 4 | `tool_missing` | Z3 was not found (`VER104`). |

- **`--format json`** writes one JSON document to stdout. Its first property is `schema`, e.g. `"verifiable-query/check@1"`. The schemas are published in [`docs/schemas`](schemas/) (`check`, `query`, `list`), are closed (no extra properties), and fix the property order. The version after `@` changes only on a breaking change.
- **Diagnostics** (`check`, `build`, `check --sarif`) each carry `rule`, `severity`, `clause`, `symbol`, `file`, `line`, `column`, `contract`, `reason`, `counterexample` (an object of input to value), `fix`, and a one-line `summary`. A field an analyzer cannot fill is `null`, never missing.
- **Query results** (`ask`, `check-invariant`) carry `claim`, `assume`, `because`, `target`, `error` (`{title, message, fix}` or `null`) and `results[]`, each with `method`, `returnType`, `status`, `vacuous`, `pathsChecked`, `file`, `line`, `smtHash`, `reasons[]` (from the Z3 unsat core), `counterexamples[]`, `error` and `fix`.
- **Bounded output.** List-shaped results (`diagnostics`, `list`) come in pages of `--limit` items (default 50, max 1000). Pass `page.nextCursor` back as `--cursor` for the next page; it is `null` on the last page.
- **Same input, same output.** File paths are relative to the current directory with forward slashes; there are no timestamps or random ordering. Two runs on the same input give byte-identical JSON.
- **No colour for agents.** JSON never has ANSI escapes, and neither does Elm output under `VERIFIABLE_AGENT=1`, whatever `--color` says.
- `--format elm` + `VERIFIABLE_AGENT=1` appends the same JSON on one line inside `<verifiable-query-result>`, `<verifiable-check-result>` or `<verifiable-symbols>` tags.

### Clause IDs and coverage (`clauses`)

Every contract and rule can carry the ID of the spec clause it came from (requirement R1):

```csharp
[TypeAlias<decimal>("Prices are never negative", Clause = "C1")]
public readonly partial record struct Price { public static bool IsValid(decimal v) => v >= 0m; }

Spec.Requires(start >= 0, "Times are in range.", clause: "C2");
Spec.Ensures<int>(r => r > 0, "A booking ends after it starts", clause: "C2");
[Invariant("A booking ends after it starts", Clause = "C2")] public bool EndsAfterStart => End > Start;
```

- Every `Spec.*` contract takes an optional `clause` argument; `[TypeAlias]`, `[Refined]` and `[Invariant]` take `Clause =`. The ID must be a string literal.
- Put the spec's clause manifest next to the project as `*.clauses.json` (or set `VerifiableClauseManifest`), shaped `{"clauses":[{"id":"C1","text":"..."}]}`. The build then fails on a clause no code carries (`VER401`) or on code naming a clause the spec lacks (`VER403`), and warns on a contract or rule with no clause (`VER402`). A manifest it cannot read is `VER404`. With no manifest these rules are silent.
- Every diagnostic's `clause` field is filled from the contract it is about. A check with no contract of its own (an overflow, say) takes the clause when all the method's contracts share one.
- `verifiable-query clauses --project App.csproj [--clauses spec.clauses.json] --format json` maps each clause to the contracts and rules that carry it, with `missing`, `unknownClauses` and `withoutClause` lists (schema `clauses@1`). It exits `1` when a clause is missing or unknown.
- `verifiable-query check --clause C2` proves only the symbols that carry clause `C2`, and `check --clauses <file>` runs the coverage rules in-process.

### Holes for unwritten bodies (`Spec.Hole`, `holes`)

A generator writes the signature and contracts, and leaves the body to the LLM (requirement R4):

```csharp
public static Result<Booking, BookingError> Book(Schedule s, Booking b)
{
    Spec.Ensures<Result<Booking, BookingError>>(r => r.IsSuccess == !s.Overlaps(b), "...", clause: "C3");
    return Spec.Hole<Result<Booking, BookingError>>("C3");
}
```

- `Spec.Hole<T>("C3")` (or `Spec.Hole("C3")` in a `void` method) compiles, breaks no determinism rule, and fails the build with `VER108` naming the clause. Set `VerifiableHolesAsWarnings=true` to make it a warning while working (open question 6 suggests warning locally and error in CI).
- A method with a hole is never proved: the prover skips it (no misleading `VER102`), and `ask` on it is `undecided`.
- Under `VERIFIABLE_AGENT=1` each `VER108` carries `<verifiable-hole>{...}</verifiable-hole>` with `symbol`, `clause`, `clauseText` (from the manifest), `signature` and `contracts`.
- `verifiable-query holes --project App.csproj [--clauses spec.clauses.json] --format json` is the whole work list (schema `holes@1`); it exits `1` while any hole remains.

### Explain, fix and describe (`explain`, `fix`, `describe`)

Agents fix faster from an exact edit than from prose (requirement R15).

- `verifiable-query explain [VER102 ...] --format json` gives each rule's `title`, `category`, `severity`, `why`, `fix`, a worked `example` (`before` / `after`) and the `codeFixes` that handle it (schema `explain@1`). A test runs each worked example: `before` triggers the rule and `after` does not.
- `verifiable-query fix --project App.csproj [--rule VER004] [--target T] --format json` runs the IDE code fixes (VER001, VER002, VER004, VER005, VER006, VER301) and returns each as text edits: `file`, 1-based `start` / `end` (end exclusive, positions in the text before the fix) and `newText` (schema `fix@1`). `--apply` applies them one at a time, re-checking after each, and writes the files.
- `verifiable-query describe --format json` lists every command with its options, its output schema and the schema file, plus the exit codes (schema `describe@1`).

### Does the contract fix one answer? (`one-answer`)

A weak contract lets the LLM return any of several answers and still pass its proof (requirement R5). For each method, Z3 looks for one input with two different results that both meet every postcondition:

```bash
verifiable-query one-answer --project App.csproj [--target T.M] [--lock generated.contracts.lock.json] --format json
```

- Each method (schema `one-answer@1`) is `fixed` (any body that passes is correct), `open` with `counterexample: {input, result1, result2}`, or `inconclusive` with a `note` (solver unknown, results that cannot be compared with `==`, an unsupported contract). A method with no postcondition is `open`.
- Mark a method `[AnyValidResult("why")]` when more than one answer really is fine; it may then be open.
- Exit `1` while an unmarked method is open, `2` while one is inconclusive.
- `check --one-answer` reports each unmarked open method as `VER111` (an error). With `--lock`, only locked (generated) methods are checked. It runs in the CLI, not in `dotnet build`: on the samples it takes 4–10 s per project, so CI is the natural place (open question 4).
- How: a probe with the method's parameters, two candidate results, its preconditions and both postconditions is added to the method's own type and proved by the ordinary prover, so the answer is only as trusted as any other proof.

### Contract export (`export`)

Every contract, constrained type and invariant, as data (requirement R2). The round trip back to ACE, SQL `CHECK` constraints, UI messages and change impact read this instead of C#.

```bash
verifiable-query export --project App.csproj --out contracts.export.json
```

- Each entry (schema `export@1`) has `symbol` (`Type.Member(param types)` for method contracts), `kind` (`requires`, `ensures`, `loopInvariant`, `decreases`, `typeAlias`, `refined`, `invariant`), `clause`, `reason`, `text`, `result` (the name an `Ensures` lambda gives the return value), `guard` (for `RequiresWhen` / `EnsuresWhen`) and `expr`.
- `expr` is a tree of `{"op": ...}` nodes, each with its C# `type`: `and`, `or`, `not`, `eq` … `ge`, `add` … `mod`, `cond`, `implies`, `forall` / `exists` (`variable`, `from`, `to`, `body`), `member`, `index`, `call`, `cast`, `isNull` / `notNull`, and leaves `const`, `param`, `local`, `result`, `static`. Property patterns such as `value is { Length: >= 1 and <= 100 }` become plain comparisons. A form with no node yet is `{"op":"csharp","text":...}`; the samples have none.
- `--out` writes every entry, unpaged; the same source gives a byte-identical file.
- Not done yet: the cross-assembly contract metadata the analyzer reads is still its own format.

### Context packs (`pack`)

One command gives an agent everything it needs to work on a method, and nothing else (requirement R13):

```bash
verifiable-query pack --project App.csproj --holes --clauses spec.clauses.json --format json
verifiable-query pack --project App.csproj --target Schedule.CanBook --max-tokens 4000 --format json
verifiable-query pack --project App.csproj --clause C3 --format json
```

Each pack (schema `pack@1`) holds:

- the method's `signature`, `contracts`, whether it is a `hole`, and the `clauses` with their text from the manifest;
- `types`: every hand-written type in the signature, contracts or containing type, and every type reachable from their data members or from the closer helpers' signatures, each cut down to its data members and rules (`IsValid`, `[Invariant]`, with reason and clause);
- `helpers`: the methods it may call, with their contracts, ranked 0 (named in its contracts), 1 (same type), 2 (a pack type's method), 3 (any other contracted method);
- `callers`: the methods that call it, with their contracts and the call site.

`--max-tokens <n>` (about four characters a token) drops the least relevant parts first — rank-3 helpers, callers, the deepest types, rank-2 and rank-1 helpers, then other first-level types — and lists each in `omitted`. The method's own signature, contracts and clause text are never dropped.

A test holds every `[Verifiable]` method in `samples/` to this: every hand-written type and method its real body uses is in its pack.

### Contract lock (`lock`)

Contracts sit at the top of the body, so the LLM filling a body edits the same method that holds its spec. The lock stops it weakening a contract until the proof passes (requirement R3; this is the hash-check option from open question 1).

```bash
# The generator, after writing signatures, contracts and holes:
verifiable-query lock --project App.csproj --out generated.contracts.lock.json
```

- The lock records a SHA-256 of each method's signature and leading contract block, keyed `Type.Member(param types)`, with its clauses. Formatting is normalised, so reflowing a contract is fine.
- A `*.contracts.lock.json` next to the project is picked up by the build (or pass `check --lock <file>`). Any other change to a locked method — a contract weakened, strengthened, removed, added, reworded, moved below other code, or a changed signature — is `VER109`, naming the method and its clauses. Only the body may change. A lock that cannot be read is `VER110`.
- Keep the lock file out of the LLM's reach (or check in CI that it only changes when the generator runs). `--target` locks just some methods.

### Fast, scoped checks (`check --target`)

The fill loop checks one method many times, so `check` can prove just what an agent is working on (requirement R14):

```bash
verifiable-query check --project App.csproj --target LedgerEngine.PostTransfer --format json
verifiable-query check --project App.csproj --target "LedgerEngine,LedgerState" --format json
```

- `--target` (alias `--symbol`) takes `Type` or `Type.Member` names, comma-separated, simple or namespace-qualified. Only those methods are sent to Z3, and only diagnostics about them are reported. A name that matches nothing is an input error (exit `3`) with suggestions.
- Proof answers are cached by query hash, in memory and in a signed disk cache, so an unchanged proof is never re-solved. `--stats` adds `elapsedMs`, `queries`, `memoryCacheHits`, `diskCacheHits` and `solverRuns`; it is off by default because it differs between runs.
- `--timeout <ms>` sets the per-obligation solver timeout (default 2500). A timeout is `VER103`, status `undecided` (exit `2`), and the diagnostic names the `symbol`, the `obligation` kind (`Postcondition`, `Overflow`, `LoopInvariantStep`, ...), the `contract` and its `line`, so the agent can split that one step or add a helper.
- Measured on `samples/AccountingApp` (4 cores): one method is 71 proof queries against 398 for the project. Warm, it takes about 0.7 s in-process and about 3.3 s as a fresh process, most of which is .NET and compiler start-up. The time target is open question 7.

### Native tools (`tools`)

Agents only ever call `dotnet` commands; native tools sit behind `verifiable-query` (requirement R16). Today that is Z3; APE (SWI-Prolog) for the pipeline's ACE step will join it once its packaging is decided.

On a machine with only the .NET SDK, two commands set everything up:

```bash
dotnet tool install -g Verifiable.Query
verifiable-query tools install          # downloads Z3 5.3.3, checks its SHA-256, unpacks to ~/.verifiable/z3
```

- `verifiable-query tools status [--format json]` reports each tool's pinned version, checksum, whether it is installed, its version, and where it was found (`managed`, `VERIFIABLE_Z3_PATH`, `repository`, `PATH`, `--z3`). Exit `0` when all are installed, `4` when one is missing.
- `verifiable-query tools install [z3] [--to <dir>] [--force]` installs the pinned build for this platform. A download whose checksum does not match the manifest is refused and nothing is installed. Re-running is a no-op when the pinned version is already there.
- Set `VERIFIABLE_TOOLS_MIRROR` to an `https://` mirror or a `file://` folder holding the same archives for offline or air-gapped machines.
- Every "Z3 not found" result (`VER104`, query exit code `4`) quotes `verifiable-query tools install z3`.

