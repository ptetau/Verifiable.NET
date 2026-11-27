# InventoryPricing — Overflow-Safe Stock & Deterministic Cart Pricing

A pure, dependency-free domain library (`samples/InventoryPricing`) focused on numeric and collection safety: proving 32-bit stock arithmetic cannot overflow or underflow under `[CheckedArithmetic]`, eliminating buffer bounds checks on proven bin indices (`WarehouseBin.ReadUnchecked`), bounding discounts/taxes with `BasisPoints`, proving positional preservation on `ImmutableArray<CartLine>` via `[NonDefault]` and `Spec.ForAll`, and verifying a multi-line cart pricing loop with `Spec.LoopInvariant` and `Spec.Decreases`.

## The Domain & Rules

[`samples/InventoryPricing`](.) marks the assembly `[assembly: Verifiable]`:

- **Constrained value types (`[TypeAlias<T>]` with spirit messages)** in [DomainTypes.cs](DomainTypes.cs):
  - `WarehouseBin` (`int`, `value >= 0 && value < 64`) — slot index into a fixed 64-bin array. Because `0 <= value < 64` is a constant bounds guard, `Verifiable.Generators` emits a `[PureAxiom]` `ReadUnchecked` fixed-buffer helper directly on `WarehouseBin`.
  - `BasisPoints` (`int`, `value >= 0 && value <= 10_000`) — `0.00%` to `100.00%`, preventing negative fees or >100% discounts.
  - `PriceCents` (`long`, `value >= 0L && value <= 100_000_000L`) — integer cents up to \$1,000,000 per line or cart.
  - `OrderQuantity` (`int`, `value >= 1 && value <= 10_000`) — bounded per-line item quantity.
  - `StockCount` (`int`, `value >= 0 && value <= 1_000_000`) — non-negative stock level capped at 1,000,000 units per bin.
- **Type `[Invariant]` & explicit error union:**
  - `PriceBreakdown.IsConsistent`: `SubtotalCents >= 0L && DiscountCents >= 0L && DiscountCents <= SubtotalCents && TaxCents >= 0L && GrandTotalCents == SubtotalCents - DiscountCents + TaxCents` (*"Discount never exceeds subtotal, tax is non-negative, and grand total equals subtotal - discount + tax."*).
  - `PricingError` (`enum`: `EmptyCart`, `CartTooLarge`, `BinNotTracked`, `InsufficientStock`, `CapacityExceeded`).
- **Checked arithmetic, quantifiers, loops & fast-path access in [PricingEngine.cs](PricingEngine.cs):**
  - `[CheckedArithmetic]` arithmetic helpers (`TryDeductStock`, `TryAddStock`, `CombineQuantities`) are verified under Z3 32-bit BitVec overflow/underflow semantics using `Spec.Implies` contracts so `int` additions and subtractions cannot wrap around.
  - `[InboundPort]` methods (`ReserveStock`, `RestockBin`, `PriceCart`) generate `*FromRaw` boundary wrappers, marshal inputs cleanly, and return `Result<T, PricingError>`.
  - `ComputeBasisPointsShare` proves `result >= 0L && result <= amountCents` for any `BasisPoints` in `0..10_000`.
  - `RemoveCartLine` uses `[NonDefault]` on `ImmutableArray<CartLine>` parameters/returns and `Spec.ForAll` index quantifiers to prove length decreases by one, lines before `index` stay in place, and lines after `index` shift left by one.
  - `PriceCart` sums cart lines with explicit `Spec.LoopInvariant` and `Spec.Decreases` contracts and computes subtotal, discount, tax, and grand total.
  - `[Unverifiable]` `TryReadBinStockFast` demonstrates bridging proven `WarehouseBin` bounds into a `ReadOnlySpan<int>` lookup using `bin.ReadUnchecked(...)`.

## GalaxyCheck Properties & Query Tests

- **Domain `[Property]` tests** in [InventoryPricingProperties.cs](../../tests/Verifiable.Samples.Tests/InventoryPricingProperties.cs):
  - `PriceCart_AlwaysSatisfiesPriceBreakdownInvariantAndBounds` — generates arbitrary cart lines, discount basis points, and tax basis points and verifies `PriceBreakdown.IsConsistent` and `DiscountCents <= SubtotalCents`.
  - `HigherDiscountBps_NeverIncreasesGrandTotal` — asserts monotonicity of grand total as discount basis points increase.
  - `RestockThenReserve_RoundTripsExactBinStockAndEnforcesCapacity` — proves `RestockBin` followed by `ReserveStock` restores exact bin counts and enforces `StockCount` capacity limits.
  - `ReserveMoreThanAvailable_FailsSafelyWithoutThrowing` — confirms over-reserving returns `PricingError.InsufficientStock` without exceptions or stock mutation.
  - `ReadBinStockFast_MatchesIndexedLookupForEveryValidBin` — tests `TryReadBinStockFast` / `WarehouseBin.ReadUnchecked` against direct span indexing for all 64 bins.
  - `RemoveCartLine_PreservesOrderBeforeAndAfterRemovedIndex` — checks `RemoveCartLine` returns a non-default `ImmutableArray<CartLine>` preserving elements before `index` and shifting elements after `index` left by one.
  - `InboundPort_RejectsOutOfRangeBinAndBasisPointsWithSpiritMessages` (`[Fact]`) — checks `[InboundPort]` boundary rejections report constrained-type spirit messages.

Run the suite:

```powershell
dotnet test tests/Verifiable.Samples.Tests/Verifiable.Samples.Tests.csproj -c Release --filter "FullyQualifiedName~InventoryPricingProperties"
```

## Asking `verifiable-query` About `PriceCart`

Ask whether every successful `PriceCart` return satisfies the breakdown identity and keeps discounts bounded by the subtotal:

```powershell
dotnet run --project src/Verifiable.Query -f net10.0 -c Release -- ask `
  --project samples/InventoryPricing/InventoryPricing.csproj `
  --target PricingEngine.PriceCart `
  --assert "result => !result.IsSuccess || ((result.Value).GrandTotalCents == (result.Value).SubtotalCents - (result.Value).DiscountCents + (result.Value).TaxCents && (result.Value).DiscountCents <= (result.Value).SubtotalCents)"
```

Actual output from Z3:

```text
-- QUERY HOLDS ---------------------------------------- PricingEngine.cs

Proved for all reachable execution paths (3 checked) in
`PricingEngine.PriceCart`.

    |
143 |     public static Result<PriceBreakdown, PricingError> PriceCart(
    |                                                        ^^^^^^^^^

Claim:
    result => !result.IsSuccess || ((result.Value).GrandTotalCents == (result.Value).SubtotalCents - (result.Value).DiscountCents + (result.Value).TaxCents && (result.Value).DiscountCents <= (result.Value).SubtotalCents)

Why it holds (from Z3 proof core):
    1. [CalleeEnsures] PricingEngine.ComputeBasisPointsShare: Spec.Ensures<long>(
            result => result >= 0L && result <= amountCents,
            "A basis-point share (0% to 100%) is non-negative and never exceeds the base amount.")  (PricingEngine.cs:114)
       Spirit: "A basis-point share (0% to 100%) is non-negative and never exceeds the base amount."
    2. [PathCondition] branch condition reaching return (PricingEngine.cs:178)  (PricingEngine.cs:178)
```

## A True Failure

If fixed-dollar coupon logic subtracts `coupon` directly without clamping to `subtotal`, Z3 immediately produces the minimal counterexample (`subtotal = 0`, `coupon = 1`, `Total = -1`):

```powershell
@'
using Verifiable.Contracts; [assembly: Verifiable] public readonly record struct Quote(long Subtotal, long Discount, long Total) { public static Quote ApplyCouponBug(long subtotal, long coupon) { Spec.Requires(subtotal >= 0 && coupon >= 0); return new Quote(subtotal, coupon, subtotal - coupon); } }
'@ | dotnet run --project src/Verifiable.Query -f net10.0 -c Release -- ask --stdin --target Quote.ApplyCouponBug --assert "result.Total >= 0 && result.Discount <= result.Subtotal" --because "Discounts cannot turn an order total negative."
```

Actual counterexample from Z3 (exit code `1`):

```text
-- QUERY REFUTED ----------------------------------------- StdinInput.cs

Z3 found inputs where your claim is false in `Quote.ApplyCouponBug`.

  |
1 | using Verifiable.Contracts; [assembly: Verifiable] public readonly record struct Quote(long Subtotal, long Discount, long Total) { public static Quote ApplyCouponBug(long subtotal, long coupon) { Spec.Requires(subtotal >= 0 && coupon >= 0); return new Quote(subtotal, coupon, subtotal - coupon); } }
  |                                                                                                                                                                                                                                                         ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

Why this question matters:
    Discounts cannot turn an order total negative.

Claim:
    result.Total >= 0 && result.Discount <= result.Subtotal

Counterexample (reaching StdinInput.cs:1):
    subtotal = 0
    coupon = 1
    result = Quote_1(0, 1, -1)

Fix: Strengthen `Spec.Requires(...)` / `--assume`, enforce a `[TypeAlias<T>]` / `[Invariant]`, or fix the implementation.
```
