using System;
using System.Collections.Immutable;
using Verifiable.Contracts;

namespace InventoryPricing;

public static class PricingEngine
{
    [CheckedArithmetic]
    public static Result<StockCount, PricingError> TryDeductStock(
        StockCount current,
        OrderQuantity requested)
    {
        Spec.Ensures<Result<StockCount, PricingError>>(
            result => !result.IsSuccess || result.Value == current - requested,
            "Deducting requested units reduces stock by the exact quantity without underflow.");

        if (current < requested)
        {
            return Result<StockCount, PricingError>.Fail(PricingError.InsufficientStock);
        }

        return Result<StockCount, PricingError>.Ok(StockCount.From(current - requested));
    }

    [CheckedArithmetic]
    public static Result<StockCount, PricingError> TryAddStock(
        StockCount current,
        OrderQuantity added)
    {
        Spec.Ensures<Result<StockCount, PricingError>>(
            result => !result.IsSuccess || result.Value == current + added,
            "Adding units increases stock up to 1,000,000 without 32-bit overflow.");

        if (current > 1_000_000 - added)
        {
            return Result<StockCount, PricingError>.Fail(PricingError.CapacityExceeded);
        }

        return Result<StockCount, PricingError>.Ok(StockCount.From(current + added));
    }

    [InboundPort]
    public static Result<WarehouseInventory, PricingError> ReserveStock(
        WarehouseInventory inventory,
        WarehouseBin bin,
        OrderQuantity requested)
    {
        Spec.Ensures<Result<WarehouseInventory, PricingError>>(
            result => !result.IsSuccess || (
                result.Value.Stock.ContainsKey(bin) &&
                inventory.Stock.ContainsKey(bin) &&
                result.Value.Stock[bin] == inventory.Stock[bin] - requested),
            "Reserving stock strictly decreases only the requested bin by the exact quantity.");

        if (!inventory.Stock.ContainsKey(bin))
        {
            return Result<WarehouseInventory, PricingError>.Fail(PricingError.BinNotTracked);
        }

        var current = inventory.Stock[bin];
        var nextStock = TryDeductStock(current, requested);
        if (!nextStock.IsSuccess)
        {
            return Result<WarehouseInventory, PricingError>.Fail(nextStock.Error);
        }

        return Result<WarehouseInventory, PricingError>.Ok(
            inventory with { Stock = inventory.Stock.SetItem(bin, nextStock.Value) });
    }

    [InboundPort]
    public static Result<WarehouseInventory, PricingError> RestockBin(
        WarehouseInventory inventory,
        WarehouseBin bin,
        OrderQuantity added)
    {
        var existing = inventory.Stock.ContainsKey(bin) ? inventory.Stock[bin] : StockCount.From(0);
        var nextStock = TryAddStock(existing, added);
        if (!nextStock.IsSuccess)
        {
            return Result<WarehouseInventory, PricingError>.Fail(nextStock.Error);
        }

        return Result<WarehouseInventory, PricingError>.Ok(
            inventory with { Stock = inventory.Stock.SetItem(bin, nextStock.Value) });
    }

    [CheckedArithmetic]
    public static int CombineQuantities(OrderQuantity a, OrderQuantity b)
    {
        Spec.Ensures<int>(
            result => result == a + b && result >= 2 && result <= 20_000,
            "Combining two order quantities never overflows a 32-bit int.");

        return a + b;
    }

    public static long CalculateLineSubtotal(PriceCents unitPrice, OrderQuantity quantity)
    {
        Spec.Ensures<long>(
            result => result >= 0L,
            "Line subtotals are always non-negative cents.");

        return unitPrice.Value * quantity.Value;
    }

    public static long ComputeBasisPointsShare(long amountCents, BasisPoints rateBps)
    {
        Spec.Requires(
            amountCents >= 0L,
            "Percentage shares apply only to non-negative amounts.");
        Spec.Ensures<long>(
            result => result >= 0L && result <= amountCents,
            "A basis-point share (0% to 100%) is non-negative and never exceeds the base amount.");

        return (amountCents * rateBps.Value) / 10_000L;
    }

    [InboundPort]
    public static Result<PriceBreakdown, PricingError> PriceCart(
        ImmutableList<CartLine> lines,
        BasisPoints discountBps,
        BasisPoints taxBps)
    {
        if (lines.Count == 0)
        {
            return Result<PriceBreakdown, PricingError>.Fail(PricingError.EmptyCart);
        }

        if (lines.Count > 100)
        {
            return Result<PriceBreakdown, PricingError>.Fail(PricingError.CartTooLarge);
        }

        long subtotal = 0L;
        var i = 0;

        while (i < lines.Count)
        {
            Spec.LoopInvariant(
                i >= 0 && i <= lines.Count && subtotal >= 0L,
                "Running subtotal stays non-negative across every line item.");
            Spec.Decreases(lines.Count - i, "Remaining cart lines shrink by one on each iteration.");

            var line = lines[i];
            subtotal = subtotal + CalculateLineSubtotal(line.UnitPrice, line.Quantity);
            i = i + 1;
        }

        var discount = ComputeBasisPointsShare(subtotal, discountBps);
        var discountedSubtotal = subtotal - discount;
        var tax = ComputeBasisPointsShare(discountedSubtotal, taxBps);
        var grandTotal = discountedSubtotal + tax;

        return Result<PriceBreakdown, PricingError>.Ok(
            new PriceBreakdown(subtotal, discount, tax, grandTotal));
    }

    [Unverifiable]
    public static bool TryReadBinStockFast(ReadOnlySpan<int> binSnapshot, WarehouseBin bin, out int count)
    {
        if (binSnapshot.Length < WarehouseBin.UpperBoundExclusive)
        {
            count = 0;
            return false;
        }

        count = bin.ReadUnchecked(binSnapshot);
        return true;
    }
}
