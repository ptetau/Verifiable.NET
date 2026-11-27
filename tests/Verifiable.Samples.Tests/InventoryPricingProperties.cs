using System.Collections.Generic;
using System.Collections.Immutable;
using GalaxyCheck;
using GalaxyCheck.Gens;
using InventoryPricing;
using Xunit;

namespace Verifiable.Samples.Tests;

public sealed class InventoryPricingProperties
{
    private static IGen<CartLine> GenCartLine() =>
        from bin in Gen.Int32().Between(0, 63)
        from price in Gen.Int64().Between(1, 50_000)
        from qty in Gen.Int32().Between(1, 200)
        select new CartLine(WarehouseBin.From(bin), PriceCents.From(price), OrderQuantity.From(qty));

    [Property(Iterations = 30)]
    public Property PriceCart_AlwaysSatisfiesPriceBreakdownInvariantAndBounds() =>
        (from lines in GenCartLine().ListOf().WithCountBetween(1, 20)
         from discountBps in Gen.Int32().Between(0, 10_000)
         from taxBps in Gen.Int32().Between(0, 10_000)
         select (lines, discountBps, taxBps)).ForAll(x =>
        {
            var res = PricingEngine.PriceCart(
                x.lines.ToImmutableList(),
                BasisPoints.From(x.discountBps),
                BasisPoints.From(x.taxBps));

            if (!res.IsSuccess) return false;
            var p = res.Value;
            return p.IsConsistent()
                && p.SubtotalCents > 0
                && p.GrandTotalCents == p.SubtotalCents - p.DiscountCents + p.TaxCents;
        });

    [Property(Iterations = 25)]
    public Property HigherDiscountBps_NeverIncreasesGrandTotal() =>
        (from lines in GenCartLine().ListOf().WithCountBetween(1, 10)
         from bps1 in Gen.Int32().Between(0, 10_000)
         from bps2 in Gen.Int32().Between(0, 10_000)
         from taxBps in Gen.Int32().Between(0, 3_000)
         select (lines, low: System.Math.Min(bps1, bps2), high: System.Math.Max(bps1, bps2), taxBps)).ForAll(x =>
        {
            var list = x.lines.ToImmutableList();
            var tax = BasisPoints.From(x.taxBps);
            var lowDisc = PricingEngine.PriceCart(list, BasisPoints.From(x.low), tax).Value;
            var highDisc = PricingEngine.PriceCart(list, BasisPoints.From(x.high), tax).Value;

            return highDisc.DiscountCents >= lowDisc.DiscountCents
                && highDisc.GrandTotalCents <= lowDisc.GrandTotalCents;
        });

    [Property(Iterations = 25)]
    public Property RestockThenReserve_RoundTripsExactBinStockAndEnforcesCapacity() =>
        (from binIdx in Gen.Int32().Between(0, 63)
         from initial in Gen.Int32().Between(0, 990_000)
         from delta in Gen.Int32().Between(1, 10_000)
         select (binIdx, initial, delta)).ForAll(x =>
        {
            var bin = WarehouseBin.From(x.binIdx);
            var inv0 = new WarehouseInventory(
                ImmutableDictionary<WarehouseBin, StockCount>.Empty.Add(bin, StockCount.From(x.initial)));
            var qty = OrderQuantity.From(x.delta);

            var restocked = PricingEngine.RestockBin(inv0, bin, qty);
            if (!restocked.IsSuccess) return false;

            var reserved = PricingEngine.ReserveStock(restocked.Value, bin, qty);
            return reserved.IsSuccess
                && reserved.Value.Stock[bin].Value == x.initial;
        });

    [Property(Iterations = 20)]
    public Property ReserveMoreThanAvailable_FailsSafelyWithoutThrowing() =>
        (from binIdx in Gen.Int32().Between(0, 63)
         from stock in Gen.Int32().Between(0, 500)
         from extra in Gen.Int32().Between(1, 500)
         select (binIdx, stock, extra)).ForAll(x =>
        {
            var bin = WarehouseBin.From(x.binIdx);
            var inv = new WarehouseInventory(
                ImmutableDictionary<WarehouseBin, StockCount>.Empty.Add(bin, StockCount.From(x.stock)));
            var req = OrderQuantity.From(x.stock + x.extra);

            var res = PricingEngine.ReserveStock(inv, bin, req);
            return !res.IsSuccess && res.Error == PricingError.InsufficientStock;
        });

    [Property(Iterations = 25)]
    public Property ReadBinStockFast_MatchesIndexedLookupForEveryValidBin() =>
        (from values in Gen.Int32().Between(0, 1000).ListOf().WithCountBetween(64, 64)
         from binIdx in Gen.Int32().Between(0, 63)
         select (arr: values.ToArray(), binIdx)).ForAll(x =>
        {
            var bin = WarehouseBin.From(x.binIdx);
            var ok = PricingEngine.TryReadBinStockFast(x.arr, bin, out var fast);
            return ok && fast == x.arr[x.binIdx];
        });

    [Fact]
    public void InboundPort_RejectsOutOfRangeBinAndBasisPointsWithSpiritMessages()
    {
        var emptyRaw = new RawWarehouseInventory { Stock = new Dictionary<int, int>() };
        var badBin = PricingEnginePort.ReserveStock(emptyRaw, 64, 1);
        Assert.False(badBin.IsValid);
        Assert.Contains(badBin.Violations, v => v.Message.Contains("bounded to [0, 63]"));

        var badBps = PricingEnginePort.PriceCart([], 12_000, 1_500);
        Assert.False(badBps.IsValid);
        Assert.Contains(badBps.Violations, v => v.Message.Contains("basis points"));
    }

    [Property(Iterations = 25)]
    public Property RemoveCartLine_PreservesOrderBeforeAndAfterRemovedIndex() =>
        (from lines in GenCartLine().ListOf().WithCountBetween(1, 10)
         from rawIdx in Gen.Int32().Between(0, 9)
         select (arr: lines.ToImmutableArray(), idx: rawIdx % lines.Count)).ForAll(x =>
        {
            var updated = PricingEngine.RemoveCartLine(x.arr, x.idx);
            return !updated.IsDefault
                && updated.Length == x.arr.Length - 1
                && Verifiable.Contracts.Spec.ForAll(0, x.idx, i => updated[i] == x.arr[i])
                && Verifiable.Contracts.Spec.ForAll(x.idx, updated.Length, i => updated[i] == x.arr[i + 1]);
        });
}
