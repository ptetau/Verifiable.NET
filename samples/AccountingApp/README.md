# AccountingApp — Full-Stack Verifiable Double-Entry Ledger

A full-stack double-entry accounting application (`AccountingApp.Shared` + Blazor WebAssembly `AccountingApp.Client` + ASP.NET Core Minimal API `AccountingApp.Server` + SQLite) where Z3 proves `TotalDebits == TotalCredits` across account creation, 2-leg transfers, 3-leg fee splits, arbitrary multi-line journal entries, and reversals.

## The Domain & Rules

[`samples/AccountingApp/AccountingApp.Shared`](AccountingApp.Shared) marks its domain types (`Account`, `JournalLine`, `PostedJournalEntry`, `LedgerState`, `LedgerTransition`) and engines (`LedgerEngine`, `LedgerUiEngine`) with type-level `[Verifiable]`, leaving serialization and HTTP/SQLite adapters cleanly outside the verified core:

- **Constrained value types (`[TypeAlias<T>]` with spirit messages)** in [DomainTypes.cs](AccountingApp.Shared/DomainTypes.cs):
  - `AccountId` (`int`, `value >= 1`) and `JournalEntryId` (`int`, `value >= 1`) — positive nominal identifiers.
  - `AccountName` (`string`, `1..100` chars) and `CurrencyCode` (`string`, length `3`) — validated labels and ISO-style currency codes.
  - `PositiveAmount` (`decimal`, `value > 0m && value <= 1000000000m`), `PositiveDecimal` (`decimal`, `value > 0m`), and `NonNegativeDecimal` (`decimal`, `value >= 0m`) — non-negative running totals and positive transfer/line amounts.
- **Type `[Invariant]`s:**
  - `PostedJournalEntry.IsBalanced`: every posted entry has at least two lines and `TotalDebits == TotalCredits`.
  - `LedgerState.IsBalanced`: across the entire ledger, `TotalDebits == TotalCredits` always holds (*"Across the entire ledger, total debits must always equal total credits."*).
- **Verified operations in [LedgerEngine.cs](AccountingApp.Shared/LedgerEngine.cs)** (using `Spec.Implies` for conditional transition rules):
  - `OpenAccount` (`[InboundPort]`): initializes an account with zero balance.
  - `PostTransfer` (`[InboundPort]`): debits destination and credits source while enforcing currency match and overdraft rules.
  - `PostFeeSplitTransfer` (`[InboundPort]`): 3-leg split entry — credits source by `grossAmount`, debits destination by `grossAmount - feeAmount`, and debits fee account by `feeAmount`, conserving `TotalDebits == TotalCredits`.
  - `PostCheckedAssetTransfer`: asset-to-asset transfer with explicit balance sufficiency precondition.
  - `PostBalancedEntry` (`[InboundPort]`): multi-line entry validated via `SumEntrySides` and applied via `ApplyLinesToAccounts`, both proved with `Spec.LoopInvariant` + `Spec.Decreases`.
  - `ReverseEntry` (`[InboundPort]`): locates an entry via `FindJournalIndex`, flips debits/credits via `FlipEntryLines`, and marks the original reversed.
  - `ComputeTrialBalance`: sums account asset/liability/equity/revenue/expense balances with `Spec.LoopInvariant` + `Spec.Decreases` into a `TrialBalanceSummary`.
- **Linear obligations & I/O boundaries:**
  - `LedgerUiEngine` creates `UnsentLedgerCommand` values; [LedgerCommandDispatcher.cs](AccountingApp.Client/LedgerCommandDispatcher.cs) (`DispatchAsync`) and [SqliteLedgerRepository.cs](AccountingApp.Server/SqliteLedgerRepository.cs) (`CommitAsync`) carry `[ConsumesObligation]` over `PendingLedgerCommit` tokens.
  - [Home.razor](AccountingApp.Client/Pages/Home.razor) exposes 2-leg transfer, 3-leg fee split, and reversal actions plus a "Verify Journal Replay" client audit button.
  - [Program.cs](AccountingApp.Server/Program.cs) and [SqliteLedgerRepository.cs](AccountingApp.Server/SqliteLedgerRepository.cs) wrap HTTP endpoints (`/api/accounts`, `/api/transfers`, `/api/journal/fee-split`, `/api/journal/entry`, `/api/journal/reverse`, `/api/ledger`, `/api/ledger/reset`) in SQLite transactions guarded by an idempotent `command_log`.

## GalaxyCheck Properties & Integration Tests

- **Domain `[Property]` tests** in [AccountingDomainProperties.cs](../../tests/Verifiable.Samples.Tests/AccountingDomainProperties.cs):
  - `DoubleEntryOperations_AlwaysPreserveBalanceAndNonNegativeDebits` — runs randomized sequences of `OpenAccount`, `PostTransfer`, `PostFeeSplitTransfer`, and `ReverseEntry` and checks `LedgerState.IsBalanced` and `ComputeTrialBalance` after every step.
  - `FeeSplitTransfer_DistributesNetAndFeeWithoutRoundingLoss` — checks across generated gross amounts and fee ratios that `to` receives `gross - fee`, `feeAccount` receives `fee`, and total debits/credits grow by `gross`.
  - `CurrencyMismatch_IsRejectedWithoutChangingState` — verifies cross-currency transfers are rejected without mutating accounts or totals.
  - `NonOverdraftAccount_NeverDropsBelowZero` — confirms asset/non-overdraft accounts cannot go negative.
  - `Reversal_RestoresOriginalAccountBalancesAndRejectsDoubleReversal` — proves reversing an entry restores net account balances and blocks duplicate reversal.
  - `InboundPort_RejectsZeroFeeAndShortCurrencyWithSpiritMessages` (`[Fact]`) — verifies `[InboundPort]` boundary rejections report spirit reasons cleanly.
- **Stateful SQLite crash/replay & HTTP tests:**
  - `SqliteLedgerRepository_AndAppendOnlyReplay_MatchVerifiedLedgerState` and `LedgerInboundPort_RejectsNonPositiveAmounts` in [AccountingAppStatefulTests.cs](../../tests/Verifiable.Samples.Tests/AccountingAppStatefulTests.cs) — tests SQLite commit replay and fault injection against the pure model.
  - `AccountingAppServer_EndToEndDoubleEntry_ReplayParity_AndGuards` in [SampleHttpIntegrationTests.cs](../../tests/Verifiable.Samples.Tests/SampleHttpIntegrationTests.cs) — covers accounts, transfers, 3-leg fee splits, reversals, replay parity, and reset over HTTP.

Run the suite:

```powershell
dotnet test tests/Verifiable.Samples.Tests/Verifiable.Samples.Tests.csproj -c Release --filter "FullyQualifiedName~Accounting|FullyQualifiedName~Ledger|FullyQualifiedName~FeeSplit|FullyQualifiedName~DoubleEntry|FullyQualifiedName~Reversal|FullyQualifiedName~Currency|FullyQualifiedName~Overdraft"
```

## Asking `verifiable-query` About `LedgerState`

Check whether **every** method in `AccountingApp.Shared` returning `LedgerState` preserves `TotalDebits == TotalCredits`:

```powershell
dotnet run --project src/Verifiable.Query -f net10.0 -c Release -- check-invariant `
  --project samples/AccountingApp/AccountingApp.Shared/AccountingApp.Shared.csproj `
  --type LedgerState `
  --expr "s => s.TotalDebits == s.TotalCredits"
```

Actual output from Z3:

```text
-- INVARIANT HOLDS (10/10 methods) ------------------------- LedgerState

Claim: s => s.TotalDebits == s.TotalCredits

[HOLDS] LedgerState.CreateEmpty  (1 path)
[HOLDS] LedgerUiState.Initial  (1 path)  -- because: [Invariant] LedgerState.IsBalanced ("Across the entire ledger, total debits must always equal total credits.") [DomainTypes.cs:89]
[HOLDS] LedgerEngine.PostCheckedAssetTransfer  (1 path)  -- because: [Invariant] LedgerState.IsBalanced ("Across the entire ledger, total debits must always equal total credits.") [DomainTypes.cs:89]
[HOLDS] LedgerEngine.OpenAccount  (2 paths)  -- because: [Invariant] LedgerState.IsBalanced ("Across the entire ledger, total debits must always equal total credits.") [DomainTypes.cs:89]
[HOLDS] LedgerEngine.PostTransfer  (6 paths)  -- because: [Invariant] LedgerState.IsBalanced ("Across the entire ledger, total debits must always equal total credits.") [DomainTypes.cs:89]
[HOLDS] LedgerEngine.PostFeeSplitTransfer  (7 paths)  -- because: [Invariant] LedgerState.IsBalanced ("Across the entire ledger, total debits must always equal total credits.") [DomainTypes.cs:89]
[HOLDS] LedgerEngine.PostBalancedEntry  (5 paths)  -- because: [Invariant] LedgerState.IsBalanced ("Across the entire ledger, total debits must always equal total credits.") [DomainTypes.cs:89]; branch condition reaching return (LedgerEngine.cs:435) [LedgerEngine.cs:435]
[HOLDS] LedgerEngine.ReverseEntry  (6 paths)  -- because: [Invariant] LedgerState.IsBalanced ("Across the entire ledger, total debits must always equal total credits.") [DomainTypes.cs:89]; LedgerEngine.FindJournalIndex: Spec.Ensures<int>(result => result >= -1 && result < journal.Count, "Returns either -1 (not found) or a valid index into the journal list.") ("Returns either -1 (not found) or a valid index into the journal list.") [LedgerEngine.cs:440]; [Invariant] PostedJournalEntry.IsBalanced ("Every posted journal entry must have equal debits and credits across at least two lines.") [DomainTypes.cs:77]; branch condition reaching return (LedgerEngine.cs:503) [LedgerEngine.cs:503]; branch condition reaching return (LedgerEngine.cs:543) [LedgerEngine.cs:543]
[HOLDS] LedgerUiEngine.OnCommitConfirmed  (1 path)  -- because: [Invariant] LedgerState.IsBalanced ("Across the entire ledger, total debits must always equal total credits.") [DomainTypes.cs:89]
[HOLDS] LedgerUiEngine.OnCommitFailed  (1 path)  -- because: [Invariant] LedgerState.IsBalanced ("Across the entire ledger, total debits must always equal total credits.") [DomainTypes.cs:89]

All 10 checked methods satisfy the claim.
```

## A True Failure

Suppose someone writes a 3-leg fee split and forgets to subtract `fee` from `net` on the debit side (`var net = gross;` instead of `gross - fee`). Z3 produces an exact fractional decimal counterexample immediately:

```powershell
@'
using Verifiable.Contracts; [assembly: Verifiable] public readonly record struct Ledger(decimal Debits, decimal Credits) { public static Ledger SplitFeeBug(Ledger s, decimal gross, decimal fee) { Spec.Requires(s.Debits == s.Credits && gross > 0m && fee > 0m && fee < gross); var net = gross; return new Ledger(s.Debits + net + fee, s.Credits + gross); } }
'@ | dotnet run --project src/Verifiable.Query -f net10.0 -c Release -- ask --stdin --target Ledger.SplitFeeBug --assert "result.Debits == result.Credits" --because "3-leg fee splits must keep debits equal to credits."
```

Actual counterexample from Z3 (exit code `1`):

```text
-- QUERY REFUTED ----------------------------------------- StdinInput.cs

Z3 found inputs where your claim is false in `Ledger.SplitFeeBug`.

  |
1 | using Verifiable.Contracts; [assembly: Verifiable] public readonly record struct Ledger(decimal Debits, decimal Credits) { public static Ledger SplitFeeBug(Ledger s, decimal gross, decimal fee) { Spec.Requires(s.Debits == s.Credits && gross > 0m && fee > 0m && fee < gross); var net = gross; return new Ledger(s.Debits + net + fee, s.Credits + gross); } }
  |                                                                                                                                                                                                                                                                                                            ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

Why this question matters:
    3-leg fee splits must keep debits equal to credits.

Claim:
    result.Debits == result.Credits

Counterexample (reaching StdinInput.cs:1):
    s = Ledger_1(0.0, 0.0)
    gross = 0.5m
    fee = 0.25m
    result = Ledger_1(0.75m, 0.5m)

Fix: Strengthen `Spec.Requires(...)` / `--assume`, enforce a `[TypeAlias<T>]` / `[Invariant]`, or fix the implementation.
```
