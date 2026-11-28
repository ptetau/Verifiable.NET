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
| `SolverPlumbingKernel.NamedAssertion` | Each labelled premise of a `verifiable-query` script asserts exactly its formula, under its label, and nothing else. |
| `SolverPlumbingKernel.CacheKeyField`, `CacheKeyEvalEntry`, `CacheKeyMaterial` | The cache key is exactly the solver identity, mode, timeout, script and evaluated expressions, each as one length-prefixed field. The length is written as two UTF-16 code units, so a field's text can never run into the next field. |
| `Proofs/CacheKeyLaws.cs` | The cache key is one-to-one: equal keys come only from equal solver, mode, timeout, script and eval list. Two step lemmas (a field, or an eval entry, followed by more text decodes in only one way) cover the eval list one entry at a time; the induction over its length is argued, not machine-checked. |
| `ProofKernel.ClassifyStatusLine` | Only the exact token `unsat` reads as a proof. An `(error ...)` line is never ignored. |
| `ProofKernel.ReadCheckSatAnswer` | `unsat` is only reported if Z3 printed exactly `unsat` and nothing before it was another verdict or an error. Empty output is an error. |
| `ProofKernel.BlocksBuild` | Decides the severity of `VER104`: a missing solver stops the build unless the project explicitly opts out with `VerifiableAllowMissingZ3`. (Refuted and undecided obligations stop the build through the default Error severity of `VER102`/`VER103`, which a project can still lower in `.editorconfig`.) |
| `ProofKernel.IsCacheable` | Only definitive answers (`unsat`, `sat`) are cached. Timeouts and errors are always retried. |
| `ProofKernel.AcceptCachedAnswer` | A cached answer replaces a Z3 call only if its signature checked out and it is a definitive `unsat` or `sat`. (The signature check itself is in `SignedAnswerCache`, which is tested, not proved.) |
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
- **The query CLI script.** `QueryEngine.BuildNamedQueryScript` loops over the path conditions and the `--assume` premise and writes each with `SolverPlumbingKernel.NamedAssertion`, then ends with `ProofKernel.NamedNegatedGoal`. The order and the labels are utility code. A label must never equal a declared name: Z3 accepts the clash, and later formulas then read the name as the label (true or false, as 1 or 0). Labels (`path_cond_N`, `assume_result_N`, `p_N_Kind`, `query_negated_goal`) use prefixes that no parameter (`p_` plus the escaped C# name) or local (`ssa_N_...`) can produce.
- **The `VER007` transfer rules.** How each statement changes a token's state, and the check at method exit, live in `LinearObligationAnalyzer`. Only the merge rule is in the core.
- **The Z3 process plumbing** (`Z3SolverRunner`) sends the query and returns the output lines it read. Each read ends at a sentinel that carries a fresh random nonce, so no model value can end a read early, even one that spells a sentinel's text. The cache key's layout comes from the core (`CacheKeyMaterial`); collecting its parts (`Z3Identity`, the timeout as decimal text) and hashing it are utility. The hash covers the UTF-16 code units directly, because UTF-8 would map every unpaired surrogate to the same bytes. Counterexample parsing (`ParseModel`) reads `""` as the only escape inside a string, as SMT-LIB 2.6 does; it can produce a wrong counterexample, not a wrong verdict.
- **Names in the SMT script.** C# identifiers are written with `SmtTypeSystem.SymbolPart`: ASCII letters, digits and `_` are kept, and every other code unit becomes `$` and four hex digits. This is one-to-one, so two C# names never share a symbol, and Z3 never sees a non-ASCII symbol. String literals are escaped by `ContractExpressionTranslator.EscapeSmtString` (`""` for a quote, `\u{...}` for a backslash and everything outside printable ASCII). Both are tested against hostile input (`SmtInjectionTests`), not proved.
- **The disk cache** (`SignedAnswerCache`) signs each entry with a per-user key, binding it to its query. The folder is built owner-only before the key is written, and an existing folder or key that group or others could access, or that is a link, is never trusted. On Windows it relies on the default private ACL of `LocalApplicationData`. It is as safe as your user account: code running as you (or as root) can still forge entries.

## Known gaps found while drawing this line

These are open, and are listed so they are not forgotten:

- **Vacuous proofs on one path.** `VER105` catches premises that contradict for the whole method. It does not catch a contradiction that only appears on one branch, for example a callee guarantee or `[PureAxiom]` that is impossible but only used inside an `if`: goals on that branch are still "proved". It also stays silent when Z3 cannot decide whether the premises can hold. `verifiable-query` reports per-path vacuity through its unsat core.

## Rules for contributors

- Building the solution proves the kernel, so it needs Z3: run `scripts/install-z3.sh` (or `.ps1`) first. Without it the build stops with `VER104` in `Verifiable.Core`.
- Anything that decides a verdict from a solver answer belongs in `src/Verifiable.Core`.
- The core stays free of Roslyn, I/O, mutable state and exceptions (the `[Verifiable]` gatekeeper enforces this).
- Every core rule is a `Spec.Ensures` with a plain-English `because`. Prefer exact contracts (`r == ...`) over partial ones (`r.StartsWith(...)`): a partial contract can let extra text, such as an `(assert false)`, slip through.
- When you add a rule, add a matching break to `KernelSelfVerificationTests.KernelBugs` so we know the proof can fail, and a reference check to `Verifiable.Core.Tests` so it is also checked without the prover.
