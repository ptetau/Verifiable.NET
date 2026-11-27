# AGENTS.md — Agent UX Guide for Verifiable.NET

Verifiable.NET is designed so LLM coding agents can write, migrate, verify, and test formally proved C# code in tight, deterministic feedback loops using 100% type-safe contracts (`Spec.*`, `[Invariant]`, `[TypeAlias<T>]`) with plain-English spirit strings (`because` / `description`) and Elm-style diagnostics.

## 1. Environment Variables (Always Set These in Agent Runs)

| Variable | Effect |
| --- | --- |
| `VERIFIABLE_AGENT=1` | Emits single-line `<verifiable-counterexample>{...}</verifiable-counterexample>` JSON on Z3 proof failures (`VER102`). |
| `GALAXYCHECK_AGENT=1` | Fixes GalaxyCheck seed to `0`, suppresses passing output, caps failure output to 40 lines, and emits `<galaxycheck-failure>{...}</galaxycheck-failure>` JSON. |
| `GALAXYCHECK_SEED=n` | Overrides GalaxyCheck's default seed for reproducible property runs. |
| `VERIFIABLE_Z3_PATH` | Optional explicit path to `z3` / `z3.exe`. Auto-discovery also checks `.tools/z3/` and `~/.verifiable/z3/`. |

## 2. One-Command Verification

```powershell
# Windows
.\scripts\verify.ps1
```

```bash
# Linux / macOS
./scripts/verify.sh
```

If Z3 is not installed yet:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/install-z3.ps1
```

```bash
bash scripts/install-z3.sh
```

## 3. Reading Machine-Readable Failure Blocks

### Z3 SMT Proof Failure (`VER102`)
Every Verifiable diagnostic uses an Elm-style layout (`-- TITLE ---- File.cs`, source snippet with `^` caret underline, `"Why this rule exists:"`, `Fix:` hint). When `VERIFIABLE_AGENT=1` is set, every `VER102` build error also appends a single-line JSON block (including `"reason"` whenever a `because` or attribute `description` string was provided):

```text
<verifiable-counterexample>{"rule":"VER102","symbol":"LedgerEngine.Withdraw","kind":"Postcondition","contract":"Spec.Ensures<decimal>(result => result >= 0m)","reason":"Balances must stay non-negative after every withdrawal.","counterexample":{"fromBal":"0.0","amount":"1.0"},"smtHash":"...","fix":"Fix: Strengthen Spec.Requires(...) preconditions or correct the return value to satisfy Spec.Ensures(...)."}</verifiable-counterexample>
```

### GalaxyCheck Property Failure
Every failing `[Property]` test includes a single-line `<galaxycheck-failure>` JSON block with `counterexample`, `csharp`, `replay`, `seed`, and an exact `rerun` command. See `docs/agents.md` and `external/GalaxyCheck/docs/agents.md` for full schemas.

### Interactive Verification Query & Elm Check CLI (`Verifiable.Query`)
Run pure .NET Core formal queries or full multi-line Elm checks without modifying source files (`verifiable-query` or `dotnet run --project src/Verifiable.Query -f net10.0 -c Release -- ...`):
- `ask --project <p> --target <Type.Method|Type> --assert <expr> [--assume <expr>] [--because <text>]`
- `check-invariant --project <p> --type <TypeName> --expr <lambda> [--assume <expr>]`
- `list --project <p>` (or `--file` / `--stdin`)
- `check (--project <p> | --file <f> | --stdin | --sarif <log.sarif>) [--color auto|always|never] [--no-warnings] [--format elm|json]`
- `build <project-or-sln> [--color auto|always|never] [--no-warnings] [--format elm|json] [-- <extra dotnet build args>]`

Exit codes are deterministic (`0`=proved/passed, `1`=refuted/errors, `2`=inconclusive, `3`=load/bind error, `4`=Z3 missing). With `--format json`, pure JSON is emitted; with `--format elm` + `VERIFIABLE_AGENT=1`, ANSI colors are disabled and Elm output appends a single-line `<verifiable-query-result>{...}</verifiable-query-result>`, `<verifiable-symbols>{...}</verifiable-symbols>`, or `<verifiable-check-result>{...}</verifiable-check-result>`.

## 4. Incremental Migration Workflow (`[VerifiableCandidate]`)

1. Annotate a legacy assembly, class, or method with `[VerifiableCandidate]` (emits Warnings, never breaks build).
2. Fix `VER001`–`VER006` warnings mechanically (manually or via `Verifiable.CodeFixes`):
   - `VER004` -> use the code fix for eligible eager reads in private methods; move per-item clock reads or GUID allocations to the calling shell without changing how often they run
   - `VER001`/`VER002` -> freeze to `readonly record struct` / `Immutable*` + `with`
   - `VER006`/`VER005` -> split pure logic via Sandwich I/O and replace `throw` with `Result<T, TError>`
3. Promote `[VerifiableCandidate]` -> `[Verifiable]` at the highest appropriate level (`[assembly: Verifiable]` or class/struct level; opt out boundary types/methods with `[Unverifiable]`). Push invariants into `[TypeAlias<T>("why")]` / `[Refined<T>("why")]` (`public static bool IsValid(...)`), add `[Invariant("why")]` bool methods/properties, annotate initialized `ImmutableArray<T>` values with `[NonDefault]` (or `<VerifiableNonDefaultImmutableArrays>true</VerifiableNonDefaultImmutableArrays>`), and attach `Spec.Requires` / `Spec.RequiresWhen` / `Spec.Ensures<T>` / `Spec.EnsuresWhen<T>` / `Spec.Implies` / `Spec.ForAll` / `Spec.Exists` / `Spec.LoopInvariant` / `Spec.Decreases` (all with `because` strings; `VER301`/`VER302` nudge on dense clauses or missing `because`, with a `VER301` split CodeFix).
4. Keep I/O boundaries safe by exposing `[InboundPort]`, using `[MapTo]` for 1:1 DTO projections, and wiring `[MustConsume]` obligation tokens across UI -> API -> DB.

## 5. Documentation & Compact Reference

- User guide: `README.md`
- Reference manual: `docs/manual.md`
- Compact agent reference: `llms.txt` at the repository root (also packed at `\` inside every `Verifiable.*` NuGet package).
