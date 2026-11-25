using System.Collections.Immutable;
using Verifiable.Contracts;

[assembly: Verifiable]

namespace InventoryPricing;

[TypeAlias<int>("Warehouse bin indices are bounded to [0, 63] so snapshot lookups need no bounds check.")]
public readonly partial record struct WarehouseBin
{
    public static bool IsValid(int value) => value >= 0 && value < 64;
}

[TypeAlias<int>("Discount and tax rates are expressed in basis points (0 to 10,000, representing 0% to 100%).")]
public readonly partial record struct BasisPoints
{
    public static bool IsValid(int value) => value >= 0 && value <= 10_000;
}

[TypeAlias<long>("Item unit prices are stored in non-negative cents up to $1,000,000 (100,000,000 cents).")]
public readonly partial record struct PriceCents
{
    public static bool IsValid(long value) => value >= 0L && value <= 100_000_000L;
}

[TypeAlias<int>("Order line quantities must be between 1 and 10,000 units.")]
public readonly partial record struct OrderQuantity
{
    public static bool IsValid(int value) => value >= 1 && value <= 10_000;
}

[TypeAlias<int>("Warehouse stock counts stay in [0, 1,000,000] so stock arithmetic cannot overflow 32-bit integers.")]
public readonly partial record struct StockCount
{
    public static bool IsValid(int value) => value >= 0 && value <= 1_000_000;
}

public enum PricingError
{
    EmptyCart = 0,
    CartTooLarge = 1,
    BinNotTracked = 2,
    InsufficientStock = 3,
    CapacityExceeded = 4
}

public readonly record struct CartLine(
    WarehouseBin Bin,
    PriceCents UnitPrice,
    OrderQuantity Quantity);

public readonly record struct PriceBreakdown(
    long SubtotalCents,
    long DiscountCents,
    long TaxCents,
    long GrandTotalCents)
{
    [Invariant("Discount never exceeds subtotal, tax is non-negative, and grand total equals subtotal - discount + tax.")]
    public bool IsConsistent() =>
        SubtotalCents >= 0L
        && DiscountCents >= 0L
        && DiscountCents <= SubtotalCents
        && TaxCents >= 0L
        && GrandTotalCents == SubtotalCents - DiscountCents + TaxCents;
}

public readonly record struct WarehouseInventory(
    ImmutableDictionary<WarehouseBin, StockCount> Stock)
{
    public static WarehouseInventory CreateEmpty() =>
        new(ImmutableDictionary<WarehouseBin, StockCount>.Empty);
}
