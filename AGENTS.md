# AGENTS.md — Agent UX Guide for Verifiable.NET

Verifiable.NET is designed so LLM coding agents can write, migrate, verify, and test formally proved C# code in tight, deterministic feedback loops using 100% type-safe contracts (`Spec.*`, `[Invariant]`, `[TypeAlias<T>]`).

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
When `VERIFIABLE_AGENT=1` is set, every `VER102` build error includes a `Fix:` hint and a single-line JSON block:

```text
<verifiable-counterexample>{"rule":"VER102","symbol":"LedgerEngine.Withdraw","kind":"Postcondition","contract":"Spec.Ensures<decimal>(result => result >= 0m)","counterexample":{"fromBal":"0.0","amount":"1.0"},"smtHash":"...","fix":"Fix: Strengthen Spec.Requires(...) preconditions or correct the return value to satisfy Spec.Ensures(...)."}</verifiable-counterexample>
```

### GalaxyCheck Property Failure
Every failing `[Property]` test includes a single-line `<galaxycheck-failure>` JSON block with `counterexample`, `csharp`, `replay`, `seed`, and an exact `rerun` command. See `docs/agents.md` and `external/galactic-heck/docs/agents.md` for full schemas.

## 4. Incremental Migration Workflow (`[VerifiableCandidate]`)

1. Annotate a legacy assembly, class, or method with `[VerifiableCandidate]` (emits Warnings, never breaks build).
2. Fix `VER001`–`VER006` warnings mechanically (manually or via `Verifiable.CodeFixes`):
   - `VER004` -> hoist `UtcNow`/`NewGuid()` into parameters
   - `VER001`/`VER002` -> freeze to `readonly record struct` / `Immutable*` + `with`
   - `VER006`/`VER005` -> split pure logic via Sandwich I/O and replace `throw` with `Result<T, TError>`
3. Promote `[VerifiableCandidate]` -> `[Verifiable]` at the highest appropriate level (`[assembly: Verifiable]` or class/struct level; opt out boundary types/methods with `[Unverifiable]`). Push invariants into `[TypeAlias<T>]` / `[Refined<T>]` (`public static bool IsValid(...)`), add `[Invariant]` bool methods/properties, and attach `Spec.Requires` / `Spec.Ensures<T>` / `Spec.LoopInvariant` / `Spec.Decreases` for Z3 verification.
4. Keep I/O boundaries safe by exposing `[InboundPort]` and wiring `[MustConsume]` obligation tokens across UI -> API -> DB.

## 5. Documentation & Compact Reference

- User guide: `README.md`
- Reference manual: `docs/manual.md`
- Compact agent reference: `llms.txt` at the repository root (also packed at `\` inside every `Verifiable.*` NuGet package).
