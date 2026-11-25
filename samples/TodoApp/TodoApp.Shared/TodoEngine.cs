using Verifiable.Contracts;

namespace TodoApp.Shared;

public static class TodoEngine
{
    [InboundPort]
    public static TodoTransition Add(TodoBoard board, TodoTitle title, UnixTimestamp now)
    {
        Spec.Ensures<TodoTransition>(
            result => !result.IsSuccess || (result.Board.NextId == board.NextId + 1 && result.Board.ActiveCount == board.ActiveCount + 1),
            "Adding a todo advances NextId by 1 and adds one active item.");
        Spec.Ensures<TodoTransition>(
            result => !result.IsSuccess || (result.Board.CompletedCount == board.CompletedCount && result.Board.DeletedCount == board.DeletedCount),
            "Adding a new todo does not change completed or deleted counts.");
        Spec.Ensures<TodoTransition>(
            result => result.IsSuccess || result.Board == board,
            "A rejected Add leaves the board completely unchanged.");

        if (board.Items.ContainsKey(board.NextId))
        {
            return new TodoTransition(board, false, "Duplicate next TodoId detected.", null);
        }

        var item = new TodoItem(board.NextId, title, TodoStatus.Active, now, now);
        var nextId = TodoId.From(board.NextId + 1);
        var nextBoard = board with
        {
            NextId = nextId,
            Items = board.Items.Add(board.NextId, item),
            ActiveCount = NonNegativeInt.From(board.ActiveCount + 1)
        };
        return new TodoTransition(nextBoard, true, null, item.Id);
    }

    [InboundPort]
    public static TodoTransition Complete(TodoBoard board, TodoId id, UnixTimestamp now)
    {
        Spec.Ensures<TodoTransition>(
            result => result.IsSuccess || result.Board == board,
            "A failed Complete leaves the board unchanged.");
        Spec.Ensures<TodoTransition>(
            result => !board.Items.ContainsKey(id) || board.Items[id].Status != TodoStatus.Deleted || (!result.IsSuccess && result.Board == board),
            "A deleted todo cannot be resurrected by completing it.");
        Spec.Ensures<TodoTransition>(
            result => !board.Items.ContainsKey(id) || board.Items[id].Status != TodoStatus.Completed || (result.IsSuccess && result.Board == board),
            "Completing an already-completed item is idempotent.");

        if (!board.Items.ContainsKey(id))
        {
            return new TodoTransition(board, false, "Todo item not found.", null);
        }

        var item = board.Items[id];
        if (item.Status == TodoStatus.Deleted)
        {
            return new TodoTransition(board, false, "Cannot complete a deleted todo item.", id);
        }

        if (item.Status == TodoStatus.Completed)
        {
            return new TodoTransition(board, true, null, id);
        }

        if (board.ActiveCount <= 0)
        {
            return new TodoTransition(board, false, "Board active count is inconsistent.", id);
        }

        var updated = item with { Status = TodoStatus.Completed, UpdatedAt = now };
        var nextBoard = board with
        {
            Items = board.Items.SetItem(id, updated),
            ActiveCount = NonNegativeInt.From(board.ActiveCount - 1),
            CompletedCount = NonNegativeInt.From(board.CompletedCount + 1)
        };
        return new TodoTransition(nextBoard, true, null, id);
    }

    [InboundPort]
    public static TodoTransition Delete(TodoBoard board, TodoId id, UnixTimestamp now)
    {
        Spec.Ensures<TodoTransition>(
            result => result.IsSuccess || result.Board == board,
            "A failed Delete leaves the board unchanged.");
        Spec.Ensures<TodoTransition>(
            result => !board.Items.ContainsKey(id) || board.Items[id].Status != TodoStatus.Deleted || (result.IsSuccess && result.Board == board),
            "Deleting an already-deleted item is idempotent.");

        if (!board.Items.ContainsKey(id))
        {
            return new TodoTransition(board, false, "Todo item not found.", null);
        }

        var item = board.Items[id];
        if (item.Status == TodoStatus.Deleted)
        {
            return new TodoTransition(board, true, null, id);
        }

        var updated = item with { Status = TodoStatus.Deleted, UpdatedAt = now };
        if (item.Status == TodoStatus.Active)
        {
            if (board.ActiveCount <= 0)
            {
                return new TodoTransition(board, false, "Board active count is inconsistent.", id);
            }

            var nextActiveBoard = board with
            {
                Items = board.Items.SetItem(id, updated),
                ActiveCount = NonNegativeInt.From(board.ActiveCount - 1),
                DeletedCount = NonNegativeInt.From(board.DeletedCount + 1)
            };
            return new TodoTransition(nextActiveBoard, true, null, id);
        }

        if (board.CompletedCount <= 0)
        {
            return new TodoTransition(board, false, "Board completed count is inconsistent.", id);
        }

        var nextCompletedBoard = board with
        {
            Items = board.Items.SetItem(id, updated),
            CompletedCount = NonNegativeInt.From(board.CompletedCount - 1),
            DeletedCount = NonNegativeInt.From(board.DeletedCount + 1)
        };
        return new TodoTransition(nextCompletedBoard, true, null, id);
    }

    [InboundPort]
    public static TodoTransition Rename(TodoBoard board, TodoId id, TodoTitle newTitle, UnixTimestamp now)
    {
        Spec.Ensures<TodoTransition>(
            result => result.IsSuccess || result.Board == board,
            "A failed Rename leaves the board completely unchanged.");
        Spec.Ensures<TodoTransition>(
            result => !board.Items.ContainsKey(id) || board.Items[id].Status != TodoStatus.Deleted || (!result.IsSuccess && result.Board == board),
            "Deleted todo items cannot be renamed.");
        Spec.Ensures<TodoTransition>(
            result => !result.IsSuccess || (
                result.Board.NextId == board.NextId &&
                result.Board.ActiveCount == board.ActiveCount &&
                result.Board.CompletedCount == board.CompletedCount &&
                result.Board.DeletedCount == board.DeletedCount),
            "Renaming a todo preserves NextId and all status counts.");
        Spec.Ensures<TodoTransition>(
            result => !result.IsSuccess || (result.Board.Items.ContainsKey(id) && result.Board.Items[id].Title == newTitle),
            "A successful Rename stores the exact validated new title.");

        if (!board.Items.ContainsKey(id))
        {
            return new TodoTransition(board, false, "Todo item not found.", null);
        }

        var item = board.Items[id];
        if (item.Status == TodoStatus.Deleted)
        {
            return new TodoTransition(board, false, "Cannot rename a deleted todo item.", id);
        }

        if (item.Title == newTitle)
        {
            return new TodoTransition(board, true, null, id);
        }

        var updated = item with { Title = newTitle, UpdatedAt = now };
        var nextBoard = board with { Items = board.Items.SetItem(id, updated) };
        return new TodoTransition(nextBoard, true, null, id);
    }

    [InboundPort]
    public static TodoTransition Reopen(TodoBoard board, TodoId id, UnixTimestamp now)
    {
        Spec.Ensures<TodoTransition>(
            result => result.IsSuccess || result.Board == board,
            "A failed Reopen leaves the board completely unchanged.");
        Spec.Ensures<TodoTransition>(
            result => !board.Items.ContainsKey(id) || board.Items[id].Status != TodoStatus.Deleted || (!result.IsSuccess && result.Board == board),
            "Deleted todo items are terminal and cannot be reopened.");
        Spec.Ensures<TodoTransition>(
            result => !board.Items.ContainsKey(id) || board.Items[id].Status != TodoStatus.Active || (result.IsSuccess && result.Board == board),
            "Reopening an already-active todo is idempotent.");

        if (!board.Items.ContainsKey(id))
        {
            return new TodoTransition(board, false, "Todo item not found.", null);
        }

        var item = board.Items[id];
        if (item.Status == TodoStatus.Deleted)
        {
            return new TodoTransition(board, false, "Cannot reopen a deleted todo item.", id);
        }

        if (item.Status == TodoStatus.Active)
        {
            return new TodoTransition(board, true, null, id);
        }

        if (board.CompletedCount <= 0)
        {
            return new TodoTransition(board, false, "Board completed count is inconsistent.", id);
        }

        var updated = item with { Status = TodoStatus.Active, UpdatedAt = now };
        var nextBoard = board with
        {
            Items = board.Items.SetItem(id, updated),
            ActiveCount = NonNegativeInt.From(board.ActiveCount + 1),
            CompletedCount = NonNegativeInt.From(board.CompletedCount - 1)
        };
        return new TodoTransition(nextBoard, true, null, id);
    }

    public static int CountItemsByStatus(System.Collections.Immutable.ImmutableList<TodoItem> items, TodoStatus status)
    {
        Spec.Ensures<int>(
            result => result >= 0 && result <= items.Count,
            "Filtered count can never be negative or exceed total items.");

        var count = 0;
        var i = 0;
        while (i < items.Count)
        {
            Spec.LoopInvariant(
                i >= 0 && i <= items.Count && count >= 0 && count <= i,
                "Matched count never exceeds the number of items inspected so far.");
            Spec.Decreases(items.Count - i, "Remaining items shrink by one on each iteration.");

            if (items[i].Status == status)
            {
                count = count + 1;
            }
            i = i + 1;
        }
        return count;
    }

    public static PendingTodoCommit CreatePendingCommit(
        CommandId commandId,
        string operationKind,
        TodoBoard nextBoard,
        TodoItem? mutatedItem,
        UnixTimestamp timestamp) =>
        new(commandId, operationKind, nextBoard, mutatedItem, timestamp);
}

public static class TodoUiEngine
{
    public static (TodoUiState State, UnsentTodoCommand Command) OnSubmitAdd(
        TodoUiState state,
        TodoTitle title,
        CommandId commandId,
        UnixTimestamp now)
    {
        var nextState = state with
        {
            IsSyncing = true,
            PendingCommandId = commandId,
            StatusText = "Syncing...",
            LastError = null
        };
        var cmd = new UnsentTodoCommand(commandId, "add", title, null, now);
        return (nextState, cmd);
    }

    public static (TodoUiState State, UnsentTodoCommand Command) OnSubmitComplete(
        TodoUiState state,
        TodoId id,
        CommandId commandId,
        UnixTimestamp now)
    {
        var nextState = state with
        {
            IsSyncing = true,
            PendingCommandId = commandId,
            StatusText = "Syncing...",
            LastError = null
        };
        var cmd = new UnsentTodoCommand(commandId, "complete", null, id, now);
        return (nextState, cmd);
    }

    public static (TodoUiState State, UnsentTodoCommand Command) OnSubmitDelete(
        TodoUiState state,
        TodoId id,
        CommandId commandId,
        UnixTimestamp now)
    {
        var nextState = state with
        {
            IsSyncing = true,
            PendingCommandId = commandId,
            StatusText = "Syncing...",
            LastError = null
        };
        var cmd = new UnsentTodoCommand(commandId, "delete", null, id, now);
        return (nextState, cmd);
    }

    public static (TodoUiState State, UnsentTodoCommand Command) OnSubmitRename(
        TodoUiState state,
        TodoId id,
        TodoTitle newTitle,
        CommandId commandId,
        UnixTimestamp now)
    {
        var nextState = state with
        {
            IsSyncing = true,
            PendingCommandId = commandId,
            StatusText = "Syncing...",
            LastError = null
        };
        var cmd = new UnsentTodoCommand(commandId, "rename", newTitle, id, now);
        return (nextState, cmd);
    }

    public static (TodoUiState State, UnsentTodoCommand Command) OnSubmitReopen(
        TodoUiState state,
        TodoId id,
        CommandId commandId,
        UnixTimestamp now)
    {
        var nextState = state with
        {
            IsSyncing = true,
            PendingCommandId = commandId,
            StatusText = "Syncing...",
            LastError = null
        };
        var cmd = new UnsentTodoCommand(commandId, "reopen", null, id, now);
        return (nextState, cmd);
    }

    public static TodoUiState OnCommitConfirmed(TodoUiState state, TodoCommitReceipt receipt)
    {
        Spec.Requires(
            state.IsSyncing && state.PendingCommandId.HasValue && receipt.CommandId == state.PendingCommandId.Value,
            "A confirmation receipt is only valid when the UI is syncing that exact pending command.");
        Spec.Ensures<TodoUiState>(
            result => !result.IsSyncing && !result.PendingCommandId.HasValue,
            "Confirming a commit clears the in-flight syncing flag and pending command ID.");

        return state with
        {
            Board = receipt.Board,
            IsSyncing = false,
            PendingCommandId = null,
            StatusText = "Saved",
            LastError = null
        };
    }

    public static TodoUiState OnCommitFailed(TodoUiState state, string error) =>
        state with
        {
            IsSyncing = false,
            PendingCommandId = null,
            StatusText = "Error",
            LastError = error
        };
}
