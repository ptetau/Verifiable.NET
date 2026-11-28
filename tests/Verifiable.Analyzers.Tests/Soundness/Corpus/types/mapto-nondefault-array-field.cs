// The [MapTo] mapper copies a possibly default ImmutableArray into a [NonDefault] field, and Count then throws.
// @expect: rejected
// @witness: Corpus.Types.MapToArray.Handle(new Corpus.Types.ListDto())
using System.Collections.Immutable;
using Verifiable.Contracts;

namespace Corpus.Types;

[MapTo(typeof(ListState))]
public class ListDto
{
    public ImmutableArray<int> Items { get; set; }
}

public readonly record struct ListState([property: NonDefault] ImmutableArray<int> Items);

[Verifiable]
public static class MapToArray
{
    public static int Handle(ListDto dto)
    {
        Spec.Ensures<int>(r => r >= 0, "A length is never negative.");
        var s = dto.ToListState();
        return s.Items.Length;
    }
}
