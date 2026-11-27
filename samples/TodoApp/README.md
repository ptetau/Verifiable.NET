# TodoApp — Full-Stack Verifiable Workflow Manager

A full-stack todo board (`TodoApp.Shared` + Blazor WebAssembly `TodoApp.Client` + ASP.NET Core Minimal API `TodoApp.Server` + SQLite) where state-count consistency and UI-to-database command delivery are checked at compile time by Z3 and verified under randomized sequences and crash replay by GalaxyCheck.

## The Domain & Rules

[`samples/TodoApp/TodoApp.Shared`](TodoApp.Shared) marks the entire assembly `[assembly: Verifiable]` so every domain type and engine method stays inside the deterministic subset:

- **Constrained value types (`[TypeAlias<T>]` with spirit messages)** in [DomainTypes.cs](TodoApp.Shared/DomainTypes.cs):
  - `TodoId` (`int`, `value >= 1`) — positive sequential item IDs.
  - `TodoTitle` (`string`, `value is { Length: >= 1 and <= 200 }`) — non-empty, length-capped titles.
  - `NonNegativeInt` (`int`, `value >= 0`) — bucket counters that can never dip below zero.
  - `UnixTimestamp` (`long`, `value >= 0`) and `CommandId` (`Guid`, `value != Guid.Empty`) — deterministic event metadata.
- **Type `[Invariant]` on `TodoBoard`:**
  - `TodoBoard.IsConsistent`: `Items.Count == ActiveCount + CompletedCount + DeletedCount && Items.Count == NextId - 1` (*"Total stored items must equal the sum of active, completed, and deleted counts and match NextId - 1."*).
- **Verified operations in [TodoEngine.cs](TodoApp.Shared/TodoEngine.cs):**
  - `[InboundPort]` entrypoints (`Add`, `Complete`, `Delete`, `Rename`, `Reopen`) generate safe boundary wrappers (`AddFromRaw`, etc.) that validate external HTTP/UI primitives and return `TodoTransition`, expressing conditional transition rules cleanly with `Spec.Implies` and `Spec.EnsuresWhen<TodoTransition>` while preserving `TodoBoard.IsConsistent` on every path.
  - `CountItemsByStatus` iterates with explicit `Spec.LoopInvariant` and `Spec.Decreases` contracts, proving loop safety and termination.
- **End-to-end linear obligations (`[MustConsume]`):**
  - Submitting a UI action (`TodoUiEngine.OnSubmitAdd`, `OnSubmitComplete`, `OnSubmitDelete`, `OnSubmitRename`, `OnSubmitReopen`) returns an `UnsentTodoCommand` wrapper consumed across the HTTP boundary.
  - Client dispatch ([TodoCommandDispatcher.cs](TodoApp.Client/TodoCommandDispatcher.cs) `DispatchAsync`) and server persistence ([SqliteTodoRepository.cs](TodoApp.Server/SqliteTodoRepository.cs) `CommitAsync`) carry `[ConsumesObligation]` over `[MustConsume]` tokens (`PendingTodoCommit`), while [TodoUiEngine](TodoApp.Shared/TodoEngine.cs) settles client UI state via `OnCommitConfirmed` and `OnCommitFailed`.
- **I/O boundaries (`[Unverifiable]`):**
  - [TodoCommandDispatcher.cs](TodoApp.Client/TodoCommandDispatcher.cs) and [Home.razor](TodoApp.Client/Pages/Home.razor) drive the MVU state machine with filter tabs (`All`, `Active`, `Completed`, `Deleted`), inline renaming, and item reopening.
  - [Program.cs](TodoApp.Server/Program.cs) and [SqliteTodoRepository.cs](TodoApp.Server/SqliteTodoRepository.cs) expose HTTP endpoints (`POST /api/todos`, `/api/todos/complete`, `/api/todos/delete`, `/api/todos/rename`, `/api/todos/reopen`, `GET /api/todos`, `POST /api/todos/reset`) backed by an idempotent `command_log` table (`CommitWithReplayCheck`).

## GalaxyCheck Properties & Integration Tests

- **Domain `[Property]` tests** in [TodoDomainProperties.cs](../../tests/Verifiable.Samples.Tests/TodoDomainProperties.cs):
  - `RandomLifecycleSequences_PreserveCountsNextIdAndStatusConsistency` — runs randomized sequences of add/complete/reopen/rename/delete transitions and verifies `TodoBoard.IsConsistent` and `CountItemsByStatus` after every step.
  - `CompleteThenReopen_RoundTripsActiveTodoAndCounts` — checks `Complete` followed by `Reopen` restores active status and bucket counts identically.
  - `DeletedTodos_CannotBeCompletedReopenedOrRenamed` — verifies terminal `Deleted` state blocks subsequent mutations cleanly.
  - `CompleteDeleteAndReopen_AreIdempotentInStableStates` — asserts repeated `Complete`, `Delete`, or `Reopen` in stable states leaves counts and board unchanged.
  - `Rename_UpdatesTitleWithoutChangingCountsOrNextId` — checks renaming updates only the title while preserving ID, status, and bucket counts.
  - `InboundPort_RejectsBlankRenameTitleWithSpiritMessage` (`[Fact]`) — verifies `RenameFromRaw` returns clear boundary errors for empty titles.
- **Stateful SQLite crash/replay & HTTP integration tests:**
  - `SqliteTodoRepository_MatchesVerifiedModel_UnderCrashAndDuplicateFaults` and `TodoInboundPort_RejectsAllEmptyOrOverlongTitles` in [TodoAppStatefulTests.cs](../../tests/Verifiable.Samples.Tests/TodoAppStatefulTests.cs) — injects duplicate-command and crash faults against SQLite and compares against the pure `TodoEngine` model.
  - `TodoAppServer_EndToEndLifecycle_Idempotency_AndValidation` in [SampleHttpIntegrationTests.cs](../../tests/Verifiable.Samples.Tests/SampleHttpIntegrationTests.cs) — runs `create → rename → complete → reopen → delete → reset` over live ASP.NET Core HTTP endpoints.

Run the suite:

```powershell
dotnet test tests/Verifiable.Samples.Tests/Verifiable.Samples.Tests.csproj -c Release --filter "FullyQualifiedName~Todo"
```

## Asking `verifiable-query` About `TodoBoard`

Check whether **every** method in `TodoApp.Shared` returning `TodoBoard` preserves `Items.Count == ActiveCount + CompletedCount + DeletedCount`:

```powershell
dotnet run --project src/Verifiable.Query -f net10.0 -c Release -- check-invariant `
  --project samples/TodoApp/TodoApp.Shared/TodoApp.Shared.csproj `
  --type TodoBoard `
  --expr "b => b.Items.Count == b.ActiveCount + b.CompletedCount + b.DeletedCount"
```

Actual output from Z3:

```text
-- INVARIANT HOLDS (10/10 methods) --------------------------- TodoBoard

Claim: b => b.Items.Count == b.ActiveCount + b.CompletedCount + b.DeletedCount

[HOLDS] TodoBoard.CreateEmpty  (1 path)
[HOLDS] TodoUiState.Initial  (1 path)  -- because: CreateEmpty [DomainTypes.cs:118]; TodoBoard.CreateEmpty: Spec.Ensures<TodoBoard>(
            result => result.ActiveCount == 0 && result.CompletedCount == 0 && result.DeletedCount == 0,
            "A brand-new board starts with zero items in every status bucket.") ("A brand-new board starts with zero items in every status bucket.") [DomainTypes.cs:70]
[HOLDS] TodoEngine.Add  (2 paths)  -- because: [Invariant] TodoBoard.IsConsistent ("Total stored items must equal the sum of active, completed, and deleted counts and match NextId - 1.") [DomainTypes.cs:61]; branch condition reaching return (TodoEngine.cs:33) [TodoEngine.cs:33]
[HOLDS] TodoEngine.Complete  (5 paths)  -- because: [Invariant] TodoBoard.IsConsistent ("Total stored items must equal the sum of active, completed, and deleted counts and match NextId - 1.") [DomainTypes.cs:61]; branch condition reaching return (TodoEngine.cs:79) [TodoEngine.cs:79]
[HOLDS] TodoEngine.Delete  (6 paths)  -- because: [Invariant] TodoBoard.IsConsistent ("Total stored items must equal the sum of active, completed, and deleted counts and match NextId - 1.") [DomainTypes.cs:61]; branch condition reaching return (TodoEngine.cs:118) [TodoEngine.cs:118]; branch condition reaching return (TodoEngine.cs:132) [TodoEngine.cs:132]
[HOLDS] TodoEngine.Rename  (4 paths)  -- because: [Invariant] TodoBoard.IsConsistent ("Total stored items must equal the sum of active, completed, and deleted counts and match NextId - 1.") [DomainTypes.cs:61]; branch condition reaching return (TodoEngine.cs:173) [TodoEngine.cs:173]
[HOLDS] TodoEngine.Reopen  (5 paths)  -- because: [Invariant] TodoBoard.IsConsistent ("Total stored items must equal the sum of active, completed, and deleted counts and match NextId - 1.") [DomainTypes.cs:61]; branch condition reaching return (TodoEngine.cs:219) [TodoEngine.cs:219]
[HOLDS] TodoEngine.CreatePendingCommit  (1 path)  -- because: [Invariant] TodoBoard.IsConsistent ("Total stored items must equal the sum of active, completed, and deleted counts and match NextId - 1.") [DomainTypes.cs:61]
[HOLDS] TodoUiEngine.OnCommitConfirmed  (1 path)  -- because: [Invariant] TodoBoard.IsConsistent ("Total stored items must equal the sum of active, completed, and deleted counts and match NextId - 1.") [DomainTypes.cs:61]
[HOLDS] TodoUiEngine.OnCommitFailed  (1 path)  -- because: [Invariant] TodoBoard.IsConsistent ("Total stored items must equal the sum of active, completed, and deleted counts and match NextId - 1.") [DomainTypes.cs:61]

All 10 checked methods satisfy the claim.
```

## A True Failure

If a developer increments `Completed` on completion but forgets to decrement `Active`, `verifiable-query --stdin` catches the bug immediately without writing a test:

```powershell
@'
using System.Collections.Immutable; using Verifiable.Contracts; [assembly: Verifiable] public readonly record struct Board(int Active, int Completed, int Total) { public static Board CompleteBug(Board b) { Spec.Requires(b.Total == b.Active + b.Completed && b.Active >= 1); return b with { Completed = b.Completed + 1 }; } }
'@ | dotnet run --project src/Verifiable.Query -f net10.0 -c Release -- ask --stdin --target Board.CompleteBug --assert "result.Total == result.Active + result.Completed" --because "Completing a todo must conserve total item count."
```

Actual counterexample from Z3 (exit code `1`):

```text
-- QUERY REFUTED ----------------------------------------- StdinInput.cs

Z3 found inputs where your claim is false in `Board.CompleteBug`.

  |
1 | using System.Collections.Immutable; using Verifiable.Contracts; [assembly: Verifiable] public readonly record struct Board(int Active, int Completed, int Total) { public static Board CompleteBug(Board b) { Spec.Requires(b.Total == b.Active + b.Completed && b.Active >= 1); return b with { Completed = b.Completed + 1 }; } }
  |                                                                                                                                                                                                                                                                                         ^

Why this question matters:
    Completing a todo must conserve total item count.

Claim:
    result.Total == result.Active + result.Completed

Counterexample (reaching StdinInput.cs:1):
    b = Board_1(8856, 11797, 20653)
    result = Board_1(8856, 11798, 20653)

Fix: Strengthen `Spec.Requires(...)` / `--assume`, enforce a `[TypeAlias<T>]` / `[Invariant]`, or fix the implementation.
```
