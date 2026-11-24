using System.Collections.Generic;
using System.Collections.Immutable;
using GalaxyCheck;
using GalaxyCheck.Gens;
using Verifiable.Contracts;
using Xunit;

namespace Verifiable.Generators.Tests;

[Verifiable]
public readonly record struct SampleItem(SampleTodoId Id, SampleCode Title, bool Completed);

[Verifiable]
public readonly record struct SampleBoard(
    SampleTodoId NextId,
    ImmutableDictionary<SampleTodoId, SampleItem> Items,
    int ActiveCount,
    int CompletedCount)
{
    [Invariant]
    public bool CheckCounts() =>
        NextId >= 1
        && ActiveCount >= 0
        && CompletedCount >= 0
        && Items.Count == ActiveCount + CompletedCount
        && Items.Count == NextId - 1;

    [Ghost]
    public int GhostTotal => ActiveCount + CompletedCount;
}

[Verifiable]
public static class SampleEngine
{
    [InboundPort]
    public static SampleBoard AddItem(SampleBoard board, SampleCode title)
    {
        Spec.Requires(board.NextId >= 1);
        var item = new SampleItem(board.NextId, title, false);
        return board with
        {
            NextId = SampleTodoId.From(board.NextId + 1),
            Items = board.Items.Add(board.NextId, item),
            ActiveCount = board.ActiveCount + 1
        };
    }
}

public class InboundPortPropertyTests
{
    [Property]
    public Property ValidRawBoardAndTitle_PassesPortAndMaintainsInvariant() =>
        (from activeCount in Gen.Int32().Between(0, 8)
         from completedCount in Gen.Int32().Between(0, 8)
         from newTitle in Gen.String().WithLengthBetween(1, 15)
         select (activeCount, completedCount, newTitle)).ForAll(x =>
        {
            var rawBoard = BuildConsistentRawBoard(x.activeCount, x.completedCount);
            var portResult = SampleEnginePort.AddItem(rawBoard, x.newTitle);

            if (!portResult.IsValid)
            {
                return false;
            }

            var nextBoard = portResult.Value;
            return nextBoard.ActiveCount == x.activeCount + 1
                && nextBoard.CompletedCount == x.completedCount
                && nextBoard.Items.Count == x.activeCount + x.completedCount + 1
                && nextBoard.NextId.Value == nextBoard.Items.Count + 1;
        });

    [Property]
    public Property MismatchedCountsOrBadLeafValues_AreRejectedByPort() =>
        (from activeCount in Gen.Int32().Between(0, 5)
         from completedCount in Gen.Int32().Between(0, 5)
         from delta in Gen.Element(new[] { -2, -1, 1, 2 })
         from useBadTitle in Gen.Boolean()
         select (activeCount, completedCount, delta, useBadTitle)).ForAll(x =>
        {
            var rawBoard = BuildConsistentRawBoard(x.activeCount, x.completedCount);
            rawBoard.NextId += x.delta;

            var title = x.useBadTitle ? "" : "Valid Title";
            var portResult = SampleEnginePort.AddItem(rawBoard, title);

            return !portResult.IsValid && !portResult.Violations.IsEmpty;
        });

    private static RawSampleBoard BuildConsistentRawBoard(int active, int completed)
    {
        var items = new Dictionary<int, RawSampleItem>();
        var id = 1;
        for (var i = 0; i < active; i++, id++)
        {
            items[id] = new RawSampleItem { Id = id, Title = $"A{id}", Completed = false };
        }
        for (var i = 0; i < completed; i++, id++)
        {
            items[id] = new RawSampleItem { Id = id, Title = $"C{id}", Completed = true };
        }
        return new RawSampleBoard
        {
            NextId = id,
            Items = items,
            ActiveCount = active,
            CompletedCount = completed
        };
    }
}
