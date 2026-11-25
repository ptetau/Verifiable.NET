# Verifiable.NET Worked Samples

Three full worked examples showing how Verifiable.NET proves business rules at compile time with Z3, enforces UI-to-database obligations (`[MustConsume]`), validates external boundaries (`[InboundPort]`), tests runtime invariants and crash recovery with **GalaxyCheck**, and answers live verification questions with **`verifiable-query`**.

| Sample | Domain & Stack | Key Verifiable.NET Features |
| --- | --- | --- |
| [**TodoApp**](TodoApp/README.md) | Todo workflow manager — Shared core + Blazor Wasm + ASP.NET Core + SQLite | `[assembly: Verifiable]`, `[TypeAlias<T>]` spirit rules, `[Invariant]`, `[InboundPort]`, `[MustConsume]` UI/API/DB protocol, `Spec.LoopInvariant` + `Spec.Decreases`, idempotent SQLite replay |
| [**AccountingApp**](AccountingApp/README.md) | Double-entry ledger — Shared core + Blazor Wasm + ASP.NET Core + SQLite | `TotalDebits == TotalCredits` proved across `OpenAccount`, `PostTransfer`, 3-leg `PostFeeSplitTransfer`, multi-line `PostBalancedEntry`, `ReverseEntry`, and `ComputeTrialBalance` |
| [**InventoryPricing**](InventoryPricing/README.md) | Warehouse stock & cart pricing — Pure verified domain library | `[CheckedArithmetic]` 32-bit BitVec overflow/underflow proofs, `Result<T, PricingError>`, `BasisPoints` share bounds, `WarehouseBin.ReadUnchecked` fixed-buffer bounds |

## Running Everything Together

From the repository root:

```powershell
# Full build (Release, VERIFIABLE_AGENT=1) + all 5 test suites (GALAXYCHECK_AGENT=1)
powershell -ExecutionPolicy Bypass -File scripts/verify.ps1
```

Or run just the sample property, HTTP integration, and query tests directly:

```powershell
# All GalaxyCheck [Property] suites + SQLite crash-recovery + ASP.NET Minimal API integration tests
dotnet test tests/Verifiable.Samples.Tests/Verifiable.Samples.Tests.csproj -c Release

# Verifiable.Query automated proof tests across the samples
dotnet test tests/Verifiable.Query.Tests/Verifiable.Query.Tests.csproj -c Release
```

## Where to Find Each Verifiable.NET Pattern

| Pattern | TodoApp | AccountingApp | InventoryPricing |
| --- | --- | --- | --- |
| `[assembly: Verifiable]` | `TodoApp.Shared/DomainTypes.cs` | `AccountingApp.Shared/DomainTypes.cs` | `InventoryPricing/DomainTypes.cs` |
| `[TypeAlias<T>("why")]` constrained value types | `TodoId`, `TodoTitle`, `NonNegativeInt`, `UnixTimestamp`, `CommandId` | `AccountId`, `JournalEntryId`, `AccountName`, `CurrencyCode`, `PositiveAmount`, `PositiveDecimal`, `NonNegativeDecimal` | `WarehouseBin`, `BasisPoints`, `PriceCents`, `OrderQuantity`, `StockCount` |
| Type `[Invariant("why")]` | `TodoBoard.IsConsistent` | `PostedJournalEntry.IsBalanced`, `LedgerState.IsBalanced` | `PriceBreakdown.IsConsistent` |
| `[CheckedArithmetic]` BitVec overflow/underflow proofs | — | — | `TryDeductStock`, `TryAddStock`, `CombineQuantities` |
| Fixed-buffer `.ReadUnchecked` via proven `[TypeAlias<int>]` bounds | — | — | `WarehouseBin` (`0..63`) in `PricingEngine.TryReadBinStockFast` |
| `Spec.LoopInvariant` + `Spec.Decreases` | `TodoEngine.CountItemsByStatus` | `SumEntrySides`, `ApplyLinesToAccounts`, `FindJournalIndex`, `FlipEntryLines`, `ComputeTrialBalance` | `PricingEngine.PriceCart` |
| `[InboundPort]` HTTP / untrusted input marshaling | `Add`, `Complete`, `Delete`, `Rename`, `Reopen` | `OpenAccount`, `PostTransfer`, `PostFeeSplitTransfer`, `PostBalancedEntry`, `ReverseEntry` | `ReserveStock`, `RestockBin`, `PriceCart` |
| `[MustConsume]` linear obligation tokens | `PendingTodoCommit` (`TodoUiEngine` → `TodoCommandDispatcher` → `SqliteTodoRepository`) | `PendingLedgerCommit` (`LedgerUiEngine` → `LedgerCommandDispatcher` → `SqliteLedgerRepository`) | — |
| Explicit error / transition returns | `TodoTransition` | `LedgerTransition` | `Result<T, PricingError>` |
| `[Unverifiable]` boundary adapters | `SqliteTodoRepository`, HTTP DTOs, Blazor components | `SqliteLedgerRepository`, HTTP DTOs, Blazor components | `TryReadBinStockFast` (`ReadUnchecked` Span bridge) |
| GalaxyCheck `[Property]` + SQLite crash/replay suite | `TodoDomainProperties.cs`, `TodoAppStatefulTests.cs` | `AccountingDomainProperties.cs`, `AccountingAppStatefulTests.cs` | `InventoryPricingProperties.cs` |

## Querying the Samples Live (`verifiable-query`)

You can ask Z3 about any invariant or postcondition in the sample projects without editing a single line of code:

```powershell
# Does every method returning TodoBoard preserve bucket sums == Items.Count?
dotnet run --project src/Verifiable.Query -c Release -- check-invariant `
  --project samples/TodoApp/TodoApp.Shared/TodoApp.Shared.csproj `
  --type TodoBoard `
  --expr "b => b.Items.Count == b.ActiveCount + b.CompletedCount + b.DeletedCount"

# Does every method returning LedgerState preserve TotalDebits == TotalCredits?
dotnet run --project src/Verifiable.Query -c Release -- check-invariant `
  --project samples/AccountingApp/AccountingApp.Shared/AccountingApp.Shared.csproj `
  --type LedgerState `
  --expr "s => s.TotalDebits == s.TotalCredits"

# Does PriceCart enforce GrandTotal == Subtotal - Discount + Tax and Discount <= Subtotal?
dotnet run --project src/Verifiable.Query -c Release -- ask `
  --project samples/InventoryPricing/InventoryPricing.csproj `
  --target PricingEngine.PriceCart `
  --assert "result => !result.IsSuccess || ((result.Value).GrandTotalCents == (result.Value).SubtotalCents - (result.Value).DiscountCents + (result.Value).TaxCents && (result.Value).DiscountCents <= (result.Value).SubtotalCents)"
```

Each sample's README walks through its domain rules, verified methods, GalaxyCheck properties, live `verifiable-query` proof output, and a concrete **"A true failure"** counterexample showing what Z3 catches when a contract is broken.
