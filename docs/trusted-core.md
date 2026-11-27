# The Trusted Core

Verifiable.NET tells you a contract is **proved**. That claim is only as good as the code that makes it. This page explains which code that is, how it is kept apart from everything else, how we check it, and what is still taken on trust.

## Core vs utility

- **Core** is the small set of rules that turn a solver answer into a verdict. If one is wrong, Verifiable.NET can report a method with a bug as proved.
- **Utility** is code that finds, runs, formats or explains. Most of it can only produce a bad message or a crash. Some utility code can still affect a verdict; it is listed under [What is still trusted](#what-is-still-trusted-not-proved).

The core lives in [`src/Verifiable.Core`](../src/Verifiable.Core). It has no Roslyn, file, process or network access. The analyzers, the nullability suppressor and `verifiable-query` call into it for every verdict instead of keeping their own copies of these rules.

## What the core decides, and what is proved

Each rule is written as a `Spec.Ensures` contract next to the code, and Z3 checks it every time the project builds. Two tiny helpers (`IsOutstanding`, `JoinCore`) are single expressions used by the contracts and lemmas, and carry no contract of their own.

| Core function | Rule proved by Z3 |
| --- | --- |
| `ProofKernel.Decide` | An obligation is proved **if and only if** Z3 answered `unsat`. `sat` is the only way to refute. A missing solver is never a pass. |
| `ProofKernel.BuildRefutationQuery` | The build-time query is **exactly** the premises, then the path condition (if any), then the negated goal. Nothing else can be slipped in. |
| `ProofKernel.NamedNegatedGoal` | The labelled goal at the end of a `verifiable-query` script is exactly the negated goal. (The rest of that script is built by `QueryEngine`; see below.) |
| `ProofKernel.ClassifyStatusLine` | Only the exact token `unsat` reads as a proof. An `(error ...)` line is never ignored. |
| `ProofKernel.ReadCheckSatAnswer` | `unsat` is only reported if Z3 printed exactly `unsat` and nothing before it was another verdict or an error. Empty output is an error. |
| `ProofKernel.IsCacheable` | Only definitive answers (`unsat`, `sat`) are cached. Timeouts and errors are always retried. |
| `ProofKernel.PremisesContradict` | Premises are called contradictory only when Z3 answers `unsat` to them alone; an undecided check is not evidence either way. When they contradict, the analyzer rejects the method's proofs with `VER105`. |
| `QueryKernel.Aggregate` | One counterexample refutes a query. An undecided path blocks a proof. A proof that never used the goal is flagged as vacuous. |
| `ObligationLattice.Join` | When two paths merge, a `[MustConsume]` token that may be live on either path is still flagged, unless one side is a sink-method parameter (which may hold the token). A token counts as consumed only if it was consumed on both paths. |
| `Proofs/LatticeLaws.cs` | The merge is commutative, idempotent, associative and an upper bound. Together with the per-block transfer rules (which are not proved), this is what lets the `VER007` analysis give the same answer in any visiting order. |

## How the core is checked

1. **Build.** `src/Verifiable.Core` is compiled with `[assembly: Verifiable]` and Verifiable's own analyzers and Z3 attached. A failed proof is a `VER102` build error, so `dotnet build Verifiable.NET.sln` fails if a rule is broken. This needs Z3 (see [Rules for contributors](#rules-for-contributors)).
2. **Break tests.** `KernelSelfVerificationTests` re-runs the proof on the kernel sources, then breaks the kernel on purpose, one rule at a time, and checks each break is rejected. This shows the contracts are strong enough to catch the bugs they exist for.
3. **Independent checks.** The analyzer that proves the kernel is itself built from the kernel. A kernel bug that blinded the prover (for example one that made every query `unsat`) would therefore also approve itself. Two checks do not depend on the prover:
   - `Verifiable.Core.Tests` runs the compiled kernel against simple reference implementations, exhaustively for the small enums and with GalaxyCheck for strings and output lines. It needs no Z3.
   - Canary tests (`KernelSelfVerificationTests.Canary_*`, and the wider analyzer suite) require the analyzer as built to reject contracts that are plainly false.

## How the proved code ships

`Verifiable.Analyzers` compiles the same kernel `.cs` files into its own DLL (see `Verifiable.Analyzers.csproj`). The analyzer targets `netstandard2.0` and cannot reference `Verifiable.Contracts`, so the kernel's `Spec.*` calls bind to a small stand-in (`KernelShim.cs`). They are `[Conditional]` and removed from the compiled code, just as in user code. Every kernel file ships except `AssemblyInfo.cs` and the `Proofs/` lemmas.

## What is still trusted, not proved

The core is small on purpose. These parts are outside it and are trusted:

- **Z3** gives correct `sat`/`unsat` answers.
- **Roslyn** gives a correct control-flow graph and types.
- **The C# to SMT translation** (`CfgSsaLowerer` and friends) turns your code and contracts into formulas that mean the same thing. This is the largest trusted part. It is covered by the analyzer test suite, not by proof.
- **Choosing what to check.** `VerifiableSmtAnalyzer.MethodNeedsSmtCheck` and the lowerer decide which methods and obligations reach Z3. A method that is skipped produces no error, so it looks proved.
- **The query CLI script.** `QueryEngine.BuildNamedQueryScript` assembles the premises, path conditions and `--assume` for `verifiable-query`. Only its final, negated-goal line comes from the core.
- **The `VER007` transfer rules.** How each statement changes a token's state, and the check at method exit, live in `LinearObligationAnalyzer`. Only the merge rule is in the core.
- **The Z3 process plumbing** (`Z3SolverRunner`) sends the query, returns the output lines it read, and computes the cache key (`BuildHashInput`).

## Known gaps found while drawing this line

These are open, and are listed so they are not forgotten:

- **Shared disk cache.** `Z3SolverRunner` caches answers in `verifiable-smt-cache-v1` under the system temp folder, keyed by a hash of the query. Entries are not signed, so anyone who can write to that folder can plant an `Unsat` answer. The cache now rejects any entry the kernel would not have cached (`IsCacheable`), but a planted `Unsat` still passes.
- **No solver, no failure.** `VER104` (Z3 not found) is a warning. A build without Z3 succeeds with no proofs checked. (The kernel's own build is the exception: `Verifiable.Core` treats warnings as errors.)
- **Vacuous proofs on one path.** `VER105` catches premises that contradict for the whole method. It does not catch a contradiction that only appears on one branch, for example a callee guarantee or `[PureAxiom]` that is impossible but only used inside an `if`: goals on that branch are still "proved". It also stays silent when Z3 cannot decide whether the premises can hold. `verifiable-query` reports per-path vacuity through its unsat core.

## Rules for contributors

- Building the solution proves the kernel, so it needs Z3: run `scripts/install-z3.sh` (or `.ps1`) first. Without it the build stops with `VER104` in `Verifiable.Core`.
- Anything that decides a verdict from a solver answer belongs in `src/Verifiable.Core`.
- The core stays free of Roslyn, I/O, mutable state and exceptions (the `[Verifiable]` gatekeeper enforces this).
- Every core rule is a `Spec.Ensures` with a plain-English `because`. Prefer exact contracts (`r == ...`) over partial ones (`r.StartsWith(...)`): a partial contract can let extra text, such as an `(assert false)`, slip through.
- When you add a rule, add a matching break to `KernelSelfVerificationTests.KernelBugs` so we know the proof can fail, and a reference check to `Verifiable.Core.Tests` so it is also checked without the prover.
