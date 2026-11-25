using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using GalaxyCheck;
using GalaxyCheck.Gens;
using TodoApp.Shared;
using Xunit;

namespace Verifiable.Samples.Tests;

public sealed class TodoDomainProperties
{
    private enum TodoCmdKind { Add, Complete, Reopen, Rename, Delete }

    private sealed record TodoCmd(TodoCmdKind Kind, int TargetIndex, string Title);

    private static IGen<TodoCmd> GenTodoCmd() =>
        from kind in Gen.Element([TodoCmdKind.Add, TodoCmdKind.Complete, TodoCmdKind.Reopen, TodoCmdKind.Rename, TodoCmdKind.Delete])
        from idx in Gen.Int32().Between(1, 10)
        from title in Gen.String().WithLengthBetween(1, 30)
        select new TodoCmd(kind, idx, title);

    [Property(Iterations = 30)]
    public Property RandomLifecycleSequences_PreserveCountsNextIdAndStatusConsistency() =>
        GenTodoCmd().ListOf().WithCountBetween(1, 20).ForAll(cmds =>
        {
            var board = TodoBoard.CreateEmpty();
            long ts = 100;

            foreach (var cmd in cmds)
            {
                ts++;
                var now = UnixTimestamp.From(ts);
                var nextIdVal = cmd.TargetIndex <= Math.Max(1, board.NextId.Value - 1) ? cmd.TargetIndex : 1;

                var tr = cmd.Kind switch
                {
                    TodoCmdKind.Add => TodoEngine.Add(board, TodoTitle.From(cmd.Title), now),
                    TodoCmdKind.Complete when board.Items.Count > 0 =>
                        TodoEngine.Complete(board, TodoId.From(nextIdVal), now),
                    TodoCmdKind.Reopen when board.Items.Count > 0 =>
                        TodoEngine.Reopen(board, TodoId.From(nextIdVal), now),
                    TodoCmdKind.Rename when board.Items.Count > 0 =>
                        TodoEngine.Rename(board, TodoId.From(nextIdVal), TodoTitle.From(cmd.Title), now),
                    TodoCmdKind.Delete when board.Items.Count > 0 =>
                        TodoEngine.Delete(board, TodoId.From(nextIdVal), now),
                    _ => TodoEngine.Add(board, TodoTitle.From(cmd.Title), now)
                };

                if (!tr.Board.IsConsistent()) return false;
                if (!tr.IsSuccess && tr.Board != board) return false;

                var itemList = tr.Board.Items.Values.ToImmutableList();
                if (TodoEngine.CountItemsByStatus(itemList, TodoStatus.Active) != tr.Board.ActiveCount.Value) return false;
                if (TodoEngine.CountItemsByStatus(itemList, TodoStatus.Completed) != tr.Board.CompletedCount.Value) return false;
                if (TodoEngine.CountItemsByStatus(itemList, TodoStatus.Deleted) != tr.Board.DeletedCount.Value) return false;

                board = tr.Board;
            }

            return true;
        });

    [Property(Iterations = 25)]
    public Property CompleteThenReopen_RoundTripsActiveTodoAndCounts() =>
        (from title in Gen.String().WithLengthBetween(1, 30)
         select title).ForAll(title =>
        {
            var board0 = TodoBoard.CreateEmpty();
            var added = TodoEngine.Add(board0, TodoTitle.From(title), UnixTimestamp.From(1));
            var id = added.AffectedId!.Value;
            var completed = TodoEngine.Complete(added.Board, id, UnixTimestamp.From(2));
            var reopened = TodoEngine.Reopen(completed.Board, id, UnixTimestamp.From(3));

            return added.IsSuccess
                && completed.IsSuccess
                && completed.Board.CompletedCount.Value == 1
                && completed.Board.ActiveCount.Value == 0
                && reopened.IsSuccess
                && reopened.Board.ActiveCount.Value == 1
                && reopened.Board.CompletedCount.Value == 0
                && reopened.Board.Items[id].Status == TodoStatus.Active
                && reopened.Board.Items[id].Title == TodoTitle.From(title);
        });

    [Property(Iterations = 25)]
    public Property DeletedTodos_CannotBeCompletedReopenedOrRenamed() =>
        (from initialTitle in Gen.String().WithLengthBetween(1, 20)
        from renameTitle in Gen.String().WithLengthBetween(1, 20)
        from startCompleted in Gen.Element([false, true])
        select (initialTitle, renameTitle, startCompleted)).ForAll(x =>
        {
            var board = TodoEngine.Add(TodoBoard.CreateEmpty(), TodoTitle.From(x.initialTitle), UnixTimestamp.From(1)).Board;
            var id = TodoId.From(1);
            if (x.startCompleted)
            {
                board = TodoEngine.Complete(board, id, UnixTimestamp.From(2)).Board;
            }

            var deleted = TodoEngine.Delete(board, id, UnixTimestamp.From(3)).Board;

            var tryComplete = TodoEngine.Complete(deleted, id, UnixTimestamp.From(4));
            var tryReopen = TodoEngine.Reopen(deleted, id, UnixTimestamp.From(5));
            var tryRename = TodoEngine.Rename(deleted, id, TodoTitle.From(x.renameTitle), UnixTimestamp.From(6));

            return !tryComplete.IsSuccess && tryComplete.Board == deleted
                && !tryReopen.IsSuccess && tryReopen.Board == deleted
                && !tryRename.IsSuccess && tryRename.Board == deleted;
        });

    [Property(Iterations = 25)]
    public Property CompleteDeleteAndReopen_AreIdempotentInStableStates() =>
        (from title in Gen.String().WithLengthBetween(1, 20)
         select title).ForAll(title =>
        {
            var activeBoard = TodoEngine.Add(TodoBoard.CreateEmpty(), TodoTitle.From(title), UnixTimestamp.From(1)).Board;
            var id = TodoId.From(1);

            var reopenActive = TodoEngine.Reopen(activeBoard, id, UnixTimestamp.From(2));
            var complete1 = TodoEngine.Complete(activeBoard, id, UnixTimestamp.From(3));
            var complete2 = TodoEngine.Complete(complete1.Board, id, UnixTimestamp.From(4));
            var delete1 = TodoEngine.Delete(complete2.Board, id, UnixTimestamp.From(5));
            var delete2 = TodoEngine.Delete(delete1.Board, id, UnixTimestamp.From(6));

            return reopenActive.IsSuccess && reopenActive.Board == activeBoard
                && complete1.IsSuccess && complete2.IsSuccess && complete2.Board == complete1.Board
                && delete1.IsSuccess && delete2.IsSuccess && delete2.Board == delete1.Board;
        });

    [Property(Iterations = 25)]
    public Property Rename_UpdatesTitleWithoutChangingCountsOrNextId() =>
        (from original in Gen.String().WithLengthBetween(1, 25)
         from updated in Gen.String().WithLengthBetween(1, 25)
         select (original, updated)).ForAll(x =>
        {
            var b0 = TodoEngine.Add(TodoBoard.CreateEmpty(), TodoTitle.From(x.original), UnixTimestamp.From(1)).Board;
            var id = TodoId.From(1);
            var renamed = TodoEngine.Rename(b0, id, TodoTitle.From(x.updated), UnixTimestamp.From(2));

            return renamed.IsSuccess
                && renamed.Board.Items[id].Title.Value == x.updated
                && renamed.Board.NextId == b0.NextId
                && renamed.Board.ActiveCount == b0.ActiveCount
                && renamed.Board.CompletedCount == b0.CompletedCount
                && renamed.Board.DeletedCount == b0.DeletedCount;
        });

    [Fact]
    public void InboundPort_RejectsBlankRenameTitleWithSpiritMessage()
    {
        var board = TodoEngine.Add(TodoBoard.CreateEmpty(), TodoTitle.From("Initial"), UnixTimestamp.From(1)).Board;
        var raw = new RawTodoBoard
        {
            NextId = board.NextId.Value,
            ActiveCount = board.ActiveCount.Value,
            CompletedCount = board.CompletedCount.Value,
            DeletedCount = board.DeletedCount.Value,
            Items = new Dictionary<int, RawTodoItem>
            {
                [1] = new()
                {
                    Id = 1,
                    Title = "Initial",
                    Status = TodoStatus.Active,
                    CreatedAt = 1,
                    UpdatedAt = 1
                }
            }
        };

        var res = TodoEnginePort.Rename(raw, 1, "", 2);
        Assert.False(res.IsValid);
        Assert.Contains(res.Violations, v => v.Message.Contains("between 1 and 200 characters"));
    }
}
