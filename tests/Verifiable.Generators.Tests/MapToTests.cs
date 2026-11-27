using GalaxyCheck;
using GalaxyCheck.Gens;
using Verifiable.Contracts;
using Xunit;

namespace Verifiable.Generators.Tests;

[MapTo(typeof(MappedOrderState))]
[MapTo(typeof(MappedOrderSummary), MethodName = "ToSummary")]
public class MappedOrderDto
{
    public int Id { get; set; }
    public decimal Total { get; set; }
    public string? Notes { get; set; }
}

public readonly record struct MappedOrderState(int Id, decimal Total);

public readonly record struct MappedOrderSummary
{
    public int Id { get; init; }
    public string? Notes { get; init; }
}

public class MappedOrderResponse
{
    public int Id { get; set; }
    public decimal Total { get; set; }
    public string? Notes { get; set; }
}

[Verifiable]
public static partial class MappedOrderConverter
{
    [MapTo]
    public static partial MappedOrderResponse ToResponse(MappedOrderDto dto);

    public static MappedOrderState Adapt(MappedOrderDto dto)
    {
        Spec.Ensures<MappedOrderState>(s => s.Id == dto.Id && s.Total == dto.Total);
        return dto.ToMappedOrderState();
    }

    public static MappedOrderResponse AdaptResponse(MappedOrderDto dto)
    {
        Spec.Ensures<MappedOrderResponse>(r => r.Id == dto.Id && r.Total == dto.Total && r.Notes == dto.Notes);
        return ToResponse(dto);
    }
}

public class MapToTests
{
    [Fact]
    public void GeneratedMapToExtensionsAndPartialMethods_ProjectFieldsAccurately()
    {
        var dto = new MappedOrderDto { Id = 42, Total = 19.95m, Notes = "Express" };

        var state = dto.ToMappedOrderState();
        Assert.Equal(42, state.Id);
        Assert.Equal(19.95m, state.Total);

        var summary = dto.ToSummary();
        Assert.Equal(42, summary.Id);
        Assert.Equal("Express", summary.Notes);

        var response = MappedOrderConverter.ToResponse(dto);
        Assert.Equal(42, response.Id);
        Assert.Equal(19.95m, response.Total);
        Assert.Equal("Express", response.Notes);
    }

    [Property]
    public Property GeneratedMapTo_PreservesAllFieldsAcrossInputs() =>
        (from id in Gen.Int32()
         from total in Gen.Decimal().Between(-10000m, 10000m)
         from notes in Gen.String().WithLengthBetween(0, 20)
         select (id, total, notes)).ForAll(x =>
        {
            var dto = new MappedOrderDto { Id = x.id, Total = x.total, Notes = x.notes };
            var state = MappedOrderConverter.Adapt(dto);
            var summary = dto.ToSummary();
            var response = MappedOrderConverter.AdaptResponse(dto);

            return state.Id == x.id
                && state.Total == x.total
                && summary.Id == x.id
                && summary.Notes == x.notes
                && response.Id == x.id
                && response.Total == x.total
                && response.Notes == x.notes;
        });
}
