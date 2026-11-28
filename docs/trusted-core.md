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
| `ProofKernel.BlocksBuild` | Decides the severity of `VER104`: a missing solver stops the build unless the project explicitly opts out with `VerifiableAllowMissingZ3`. (Refuted and undecided obligations stop the build through the default Error severity of `VER102`/`VER103`, which a project can still lower in `.editorconfig`.) |
| `ProofKernel.IsCacheable` | Only definitive answers (`unsat`, `sat`) are cached. Timeouts and errors are always retried. |
| `ProofKernel.AcceptCachedAnswer` | A cached answer replaces a Z3 call only if its signature checked out and it is a definitive `unsat` or `sat`. (The signature check itself is in `SignedAnswerCache`, which is tested, not proved.) |
| `ProofKernel.PremisesContradict` | Premises are called contradictory only when Z3 answers `unsat` to them alone; an undecided check is not evidence either way. When they contradict, the analyzer rejects the method's proofs with `VER105`. |
| `VacuityKernel.GuaranteeQuery`, `ReachabilityQuery` | The two checks of a callee guarantee are **exactly** the assumptions made before the call, then the call's context (path, guard, callee preconditions), then (first check only) the guarantee. |
| `VacuityKernel.NeedsReachabilityCheck`, `ClassifyGuarantee` | A callee guarantee is impossible at a call (`VER105`) only when Z3 answers `unsat` with the guarantee and `sat` without it. `unsat` both times is dead code, not a contradiction. An undecided check never counts as either. |
| `VacuityKernel.CheckUndecided` | Any premise-check answer other than `sat` or `unsat` leaves vacuity open and is reported (`VER106`), never treated as fine. |
| `QueryKernel.Aggregate` | One counterexample refutes a query. An undecided path blocks a proof. A proof that never used the goal is flagged as vacuous. |
| `ObligationLattice.Join` | When two paths merge, a `[MustConsume]` token that may be live on either path is still flagged, unless one side is a sink-method parameter (which may hold the token). A token counts as consumed only if it was consumed on both paths. |
| `Proofs/LatticeLaws.cs` | The merge is commutative, idempotent, associative and an upper bound. Together with the per-block transfer rules (which are not proved), this is what lets the `VER007` analysis give the same answer in any visiting order. |

## How the core is checked

1. **Build.** `src/Verifiable.Core` is compiled with `[assembly: Verifiable]` and Z3 attached. A failed proof is a `VER102` build error, so `dotnet build Verifiable.NET.sln` fails if a rule is broken. This needs Z3 (see [Rules for contributors](#rules-for-contributors)). The analyzer that does this is not the one that ships. It is `Verifiable.Analyzers.Bootstrap`: the same analyzer sources, built with a frozen, earlier copy of the kernel in [`src/Verifiable.Analyzers.Bootstrap/Kernel`](../src/Verifiable.Analyzers.Bootstrap/Kernel). So a change to the live kernel is proved by the previous kernel, not by itself. This is stage-0 bootstrapping, as compilers do it.
2. **Break tests.** `KernelSelfVerificationTests` re-runs the proof on the kernel sources, then breaks the kernel on purpose, one rule at a time, and checks each break is rejected. This shows the contracts are strong enough to catch the bugs they exist for.
3. **Independent checks.** If the analyzer that proves the kernel were built from that same kernel, a kernel bug that blinded the prover would approve itself. For example, an extra `(assert false)` in `BuildRefutationQuery` makes every query `unsat`, including the query for its own contract. With the bootstrap, the frozen kernel builds those queries, so that bug fails the `Verifiable.Core` build with `VER102`. Three checks do not depend on the live kernel's prover:
   - The bootstrap build above. `BootstrapKernelTests` loads the same bootstrap DLL, checks that it proves the live kernel and rejects the `(assert false)` bug, and writes which kernel files differ from the frozen copy to the test output.
   - `Verifiable.Core.Tests` runs the compiled kernel against simple reference implementations, exhaustively for the small enums and with GalaxyCheck for strings and output lines. It needs no Z3.
   - Canary tests (`KernelSelfVerificationTests.Canary_*`, `BootstrapKernelTests.Canary_*` and the wider analyzer suite) require each analyzer as built to reject contracts that are plainly false.

   The bootstrap separates only the kernel. Both analyzers share the C# to SMT translation, so a translation bug that hid a goal would still pass the kernel's own proof (see [What is still trusted](#what-is-still-trusted-not-proved)). And a bug that is in the frozen copy too, because it was promoted, is caught only by the tests.

## How the proved code ships

`Verifiable.Analyzers` compiles the same kernel `.cs` files into its own DLL (see `Verifiable.Analyzers.csproj`). The analyzer targets `netstandard2.0` and cannot reference `Verifiable.Contracts`, so the kernel's `Spec.*` calls bind to a small stand-in (`KernelShim.cs`). They are `[Conditional]` and removed from the compiled code, just as in user code. Every kernel file ships except `AssemblyInfo.cs` and the `Proofs/` lemmas. The bootstrap analyzer and its frozen kernel are never packed: `Verifiable.Analyzers` and `Verifiable.NET` ship the analyzer built from the live kernel.

## What is still trusted, not proved

The core is small on purpose. These parts are outside it and are trusted:

- **Z3** gives correct `sat`/`unsat` answers.
- **Roslyn** gives a correct control-flow graph and types.
- **The C# to SMT translation** (`CfgSsaLowerer` and friends) turns your code and contracts into formulas that mean the same thing. This is the largest trusted part. It is covered by the analyzer test suite, not by proof.
- **Choosing what to check.** `VerifiableSmtAnalyzer.MethodNeedsSmtCheck` and the lowerer decide which methods and obligations reach Z3. A method that is skipped produces no error, so it looks proved. The vacuity checks also skip some cases: a callee guarantee is not checked on its own when no goal follows it, or when it is assumed with no path condition, guard or callee precondition (it is then part of the whole premise set, which was checked).
- **The query CLI script.** `QueryEngine.BuildNamedQueryScript` assembles the premises, path conditions and `--assume` for `verifiable-query`. Only its final, negated-goal line comes from the core.
- **The `VER007` transfer rules.** How each statement changes a token's state, and the check at method exit, live in `LinearObligationAnalyzer`. Only the merge rule is in the core.
- **The Z3 process plumbing** (`Z3SolverRunner`) sends the query, returns the output lines it read, and computes the cache key (`BuildHashInput`).
- **The disk cache** (`SignedAnswerCache`) signs each entry with a per-user key, binding it to its query. The folder is built owner-only before the key is written, and an existing folder or key that group or others could access, or that is a link, is never trusted. On Windows it relies on the default private ACL of `LocalApplicationData`. It is as safe as your user account: code running as you (or as root) can still forge entries.

## Known gaps found while drawing this line

These are open, and are listed so they are not forgotten:

- **Vacuous proofs on one path.** `VER105` checks the whole premise set, and each callee guarantee at the call that assumes it. Two cases are still open. The translation's internal axioms (decimal rounding, strings, collections, calendars) are only checked as part of the whole premise set: each guarantee is checked against the assumptions made before it, so a guarantee that clashes only with a later axiom on its path is missed. And when the whole-method check is undecided, the per-call checks are skipped (`VER106` is reported instead). Premise checks use a fixed Z3 work budget rather than a clock, so the undecided result is the same on every machine (a 10 s wall-clock backstop applies only on extremely slow machines). `verifiable-query` reports per-path vacuity through its unsat core.

## Rules for contributors

- Building the solution proves the kernel, so it needs Z3: run `scripts/install-z3.sh` (or `.ps1`) first. Without it the build stops with `VER104` in `Verifiable.Core`.
- Anything that decides a verdict from a solver answer belongs in `src/Verifiable.Core`.
- The core stays free of Roslyn, I/O, mutable state and exceptions (the `[Verifiable]` gatekeeper enforces this).
- Every core rule is a `Spec.Ensures` with a plain-English `because`. Prefer exact contracts (`r == ...`) over partial ones (`r.StartsWith(...)`): a partial contract can let extra text, such as an `(assert false)`, slip through.
- When you add a rule, add a matching break to `KernelSelfVerificationTests.KernelBugs` so we know the proof can fail, and a reference check to `Verifiable.Core.Tests` so it is also checked without the prover.
- A kernel change is proved by the frozen kernel, so do not promote in the same change. Once it is merged, promote it in its own pull request: `scripts/promote-bootstrap-kernel.sh` (or `.ps1`) first builds `Verifiable.Core` to prove the live kernel with the current frozen copy, then copies the live kernel into `src/Verifiable.Analyzers.Bootstrap/Kernel`. It refuses to promote a kernel that is not proved. Never edit the frozen copy by hand.
- The bootstrap compiles the live analyzer sources against the frozen kernel. A kernel file with no frozen copy yet (a new rule) is compiled in as it is, so analyzer code can use a new rule straight away; that new rule is proved only by itself until it is promoted, and its reference tests in `Verifiable.Core.Tests` are what check it independently. Changing the signature of an existing kernel member that the analyzer calls breaks `Verifiable.Analyzers.Bootstrap`: add the new member alongside, promote, then remove the old one. If you cannot split the change, run the promote script with `--no-proof` (`-NoProof`) and say in the pull request that this kernel was not proved by an earlier one.
