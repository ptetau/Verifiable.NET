# The break-it corpus

Small C# programs written to catch the analyzer out. `SoundnessCorpusTests` runs each one through the analyzers and through the CLR.

## How a program is checked

1. **Verdict.** The analyzers (gatekeeper, SMT, `VER007`, suppression audit) run on the program. It counts as **rejected** if they report any `VER` error, or `VER106`. Compile errors, analyzer crashes and a missing Z3 fail the test outright.
2. **Witness.** A rejected program must show it really is wrong. An `@witness` call is run with the contracts checked at run time (see `RuntimeSpec`). Its preconditions must hold, and it must break a postcondition, a callee's precondition, a loop invariant or `Decreases` measure, or throw. This keeps mistaken entries out of the corpus.
3. **Cross-check.** Every method the analyzer accepted is run on about 300 GalaxyCheck inputs, biased towards edges (0, ±1, `MinValue`, `MaxValue`, empty strings, odd characters). If a run meets the preconditions but breaks a contract, the analyzer proved something false, and the test fails.

## Directives

Comment lines of the form `// @name: value`:

| Directive | Meaning |
| --- | --- |
| `@expect: rejected` | The program is wrong; the analyzer must report an error. |
| `@expect: proved` | The program is right; the analyzer must accept it, and the cross-check must find nothing. |
| `@witness: Ns.Type.Method(args)` | A static call that shows the program is wrong. Repeatable. |
| `@no-witness: reason` | For bugs a run cannot show, such as a leaked `[MustConsume]` token. |
| `@known-bug: description` | The analyzer gets this wrong today. The test passes while that stays true and fails once it is fixed, so the marker is removed with the fix. |
| `@runtime: skip reason` | Skip the cross-check, for example when a loop is too slow to run. |

## Writing a program

- One idea per file, in a folder for its area. Start with a one-line comment saying why the program is wrong (or right).
- Put the code in a `[Verifiable]` static class with static methods, so the witness and the cross-check can call it.
- Make the bug real in C#, not just surprising: the witness must break a contract when it runs.
- Contracts written with `Spec.Result<T>()` are not checked at run time; use `Spec.Ensures<T>(r => ...)` where you can.
- A lambda postcondition sees a parameter's value when the method returns, not when it was called, so do not assign to parameters in corpus programs.
