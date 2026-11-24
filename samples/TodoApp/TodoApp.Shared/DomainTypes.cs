using System;
using System.Collections.Immutable;
using Verifiable.Contracts;

[assembly: Verifiable]
[assembly: TypeAlias<int>("TodoId", "Value >= 1", Namespace = "TodoApp.Shared")]
[assembly: TypeAlias<int>("NonNegativeInt", "Value >= 0", Namespace = "TodoApp.Shared")]
[assembly: TypeAlias<string>("TodoTitle", "Length is >= 1 and <= 200", Namespace = "TodoApp.Shared")]
[assembly: TypeAlias<long>("UnixTimestamp", "Value >= 0", Namespace = "TodoApp.Shared")]
[assembly: TypeAlias<Guid>("CommandId", "Value != Guid.Empty", Namespace = "TodoApp.Shared")]

namespace TodoApp.Shared;

public enum TodoStatus
{
    Active = 0,
    Completed = 1,
    Deleted = 2
}

public readonly record struct TodoItem(
    TodoId Id,
    TodoTitle Title,
    TodoStatus Status,
    UnixTimestamp CreatedAt,
    UnixTimestamp UpdatedAt);

[Invariant("Items.Count == ActiveCount + CompletedCount + DeletedCount")]
[Invariant("Items.Count == NextId - 1")]
public readonly record struct TodoBoard(
    TodoId NextId,
    ImmutableDictionary<TodoId, TodoItem> Items,
    NonNegativeInt ActiveCount,
    NonNegativeInt CompletedCount,
    NonNegativeInt DeletedCount)
{
    [Ensures("result.NextId == 1 && result.Items.Count == 0 && result.ActiveCount == 0 && result.CompletedCount == 0 && result.DeletedCount == 0")]
    public static TodoBoard CreateEmpty() => new(
        TodoId.From(1),
        ImmutableDictionary<TodoId, TodoItem>.Empty,
        NonNegativeInt.From(0),
        NonNegativeInt.From(0),
        NonNegativeInt.From(0));
}

public readonly record struct TodoTransition(
    TodoBoard Board,
    bool IsSuccess,
    string? Error,
    TodoId? AffectedId);

[MustConsume]
public readonly record struct PendingTodoCommit(
    CommandId CommandId,
    string OperationKind,
    TodoBoard NextBoard,
    TodoItem? MutatedItem,
    UnixTimestamp Timestamp);

[MustConsume]
public readonly record struct UnsentTodoCommand(
    CommandId CommandId,
    string OperationKind,
    string? TitleText,
    int? TargetIdValue,
    UnixTimestamp Timestamp);

public readonly record struct TodoCommitReceipt(
    CommandId CommandId,
    TodoBoard Board,
    bool WasDuplicate);

public readonly record struct TodoUiState(
    TodoBoard Board,
    bool IsSyncing,
    CommandId? PendingCommandId,
    string StatusText,
    string? LastError)
{
    public static TodoUiState Initial() =>
        new(TodoBoard.CreateEmpty(), false, null, "Ready", null);
}

[Unverifiable]
public sealed record AddTodoHttpRequest(Guid CommandId, string? Title, long Timestamp);

[Unverifiable]
public sealed record CompleteTodoHttpRequest(Guid CommandId, int TodoId, long Timestamp);

[Unverifiable]
public sealed record DeleteTodoHttpRequest(Guid CommandId, int TodoId, long Timestamp);

[Unverifiable]
public sealed record ApiErrorResponse(string Message, ImmutableArray<ContractViolation> Violations);
