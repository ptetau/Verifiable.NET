# The Trusted Core

Verifiable.NET tells you a contract is **proved**. That claim is only as good as the code that makes it. This page explains which code that is, how it is kept apart from everything else, and how we prove it is correct.

## Core vs utility

- **Core** is code that decides whether a proof passes. If it is wrong, Verifiable.NET can report a bug-free method that has a bug.
- **Utility** is code that finds, runs, formats or explains. If it is wrong, you get a bad message or a crash, but never a false "proved".

The core lives in [`src/Verifiable.Core`](../src/Verifiable.Core). It has no Roslyn, file, process or network access. Everything else (`Verifiable.Analyzers`, `Verifiable.Query`, Elm formatting, SARIF, Z3 discovery and process management) is utility and calls into the core for every verdict.

## What the core decides, and what is proved

Each rule is written as a `Spec.Ensures` contract next to the code. Z3 checks it every time the project builds.

| Core function | Rule proved by Z3 |
| --- | --- |
| `ProofKernel.Decide` | An obligation is proved **if and only if** Z3 answered `unsat`. `sat` is the only way to refute. A missing solver is never a pass. |
| `ProofKernel.BuildRefutationQuery` | The query keeps every premise unchanged, asserts the path condition, and ends with the **negated** goal. |
| `ProofKernel.NamedNegatedGoal` | The labelled goal used for "why" explanations is exactly the negated goal. |
| `ProofKernel.ClassifyStatusLine` | Only the exact token `unsat` reads as a proof. An `(error ...)` line is never ignored. |
| `ProofKernel.ReadCheckSatAnswer` | `unsat` is only reported if Z3 printed exactly `unsat` and nothing before it was another verdict or an error. Empty output is an error. |
| `ProofKernel.BlocksBuild` | Only a proved obligation lets the build pass. A missing solver passes only with the explicit `VerifiableAllowMissingZ3` opt-out. |
| `ProofKernel.IsCacheable` | Only definitive answers (`unsat`, `sat`) are cached. Timeouts and errors are always retried. |
| `QueryKernel.Aggregate` | One counterexample refutes a query. An undecided path blocks a proof. A proof that never used the goal is flagged as vacuous. |
| `ObligationLattice.Join` | A `[MustConsume]` token that may be live on either path is still flagged after the paths merge. |
| `Proofs/LatticeLaws.cs` | The merge is commutative, idempotent, associative and an upper bound, so the `VER007` analysis reaches the same answer in any order and always stops. |

## How the proof runs

1. **Build.** `src/Verifiable.Core` is compiled with `[assembly: Verifiable]` and Verifiable's own analyzers and Z3 attached. A failed proof is a `VER102` build error. `dotnet build Verifiable.NET.sln` fails if the core is wrong.
2. **Tests.** `KernelSelfVerificationTests` (in `Verifiable.Analyzers.Tests`) re-runs the proof on the kernel sources. It also breaks the kernel on purpose, one rule at a time, and checks that each break is rejected. This shows the proofs are not passing vacuously.
3. **Runtime checks.** `Verifiable.Core.Tests` runs the compiled kernel exhaustively (small enums) and with GalaxyCheck (strings and output lines), comparing it to simple reference models.

## How the proved code ships

`Verifiable.Analyzers` compiles the same kernel `.cs` files into its own DLL (see `Verifiable.Analyzers.csproj`). The analyzer targets `netstandard2.0` and cannot reference `Verifiable.Contracts`, so the kernel's `Spec.*` calls bind to a small stand-in (`KernelShim.cs`). They are `[Conditional]` and removed from the compiled code, just as in user code. The files that ship are the files that were proved; only `AssemblyInfo.cs` and the `Proofs/` lemmas stay out.

## What is still trusted, not proved

The core is small on purpose. These parts are outside it and are trusted:

- **Z3** gives correct `sat`/`unsat` answers.
- **Roslyn** gives a correct control-flow graph and types.
- **The C# to SMT translation** (`CfgSsaLowerer` and friends) turns your code and contracts into formulas that mean the same thing. This is the largest trusted part. It is covered by the analyzer test suite, not by proof.
- **The Z3 process plumbing** (`Z3SolverRunner`) sends the query and returns the output lines it read.

## Known gaps found while drawing this line

These are open, and are listed so they are not forgotten:

- **Shared disk cache.** `Z3SolverRunner` caches answers in `verifiable-smt-cache-v1` under the system temp folder, keyed by a hash of the query. Entries are not signed, so anyone who can write to that folder can plant an `Unsat` answer. The cache now rejects any entry the kernel would not have cached (`IsCacheable`), but a planted `Unsat` still passes.
- **Vacuous proofs in the analyzer.** If a method's premises contradict each other, every goal is "proved". `verifiable-query` detects this through the unsat core; the build-time analyzer does not.

## Rules for contributors

- Anything that can change a verdict belongs in `src/Verifiable.Core`.
- The core stays free of Roslyn, I/O, mutable state and exceptions (the `[Verifiable]` gatekeeper enforces this).
- Every core function states its rule as a `Spec.Ensures` with a plain-English `because`.
- When you add a rule, add a matching break to `KernelSelfVerificationTests.KernelBugs` so we know the proof can fail.
