using System;
using System.Collections.Immutable;
using Verifiable.Contracts;

namespace TodoApp.Shared;

public enum TodoStatus
{
    Active = 0,
    Completed = 1,
    Deleted = 2
}

[Refined<int>("Value >= 1")]
public readonly partial record struct TodoId;

[Refined<string>("Length is >= 1 and <= 200")]
public readonly partial record struct TodoTitle;

[Refined<long>("Value >= 0")]
public readonly partial record struct UnixTimestamp;

[Refined<Guid>("Value != Guid.Empty")]
public readonly partial record struct CommandId;

[Verifiable]
public readonly record struct TodoItem(
    TodoId Id,
    TodoTitle Title,
    TodoStatus Status,
    UnixTimestamp CreatedAt,
    UnixTimestamp UpdatedAt);

[Verifiable]
[Invariant("NextId.Value >= 1")]
[Invariant("ActiveCount >= 0 && CompletedCount >= 0 && DeletedCount >= 0")]
[Invariant("Items.Count == ActiveCount + CompletedCount + DeletedCount")]
[Invariant("Items.Count == NextId.Value - 1")]
public readonly record struct TodoBoard(
    TodoId NextId,
    ImmutableDictionary<TodoId, TodoItem> Items,
    int ActiveCount,
    int CompletedCount,
    int DeletedCount)
{
    [Verifiable]
    [Ensures("result.NextId.Value == 1 && result.Items.Count == 0 && result.ActiveCount == 0 && result.CompletedCount == 0 && result.DeletedCount == 0")]
    public static TodoBoard CreateEmpty() => new(
        TodoId.From(1),
        ImmutableDictionary<TodoId, TodoItem>.Empty,
        0, 0, 0);
}

[Verifiable]
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

[Verifiable]
public readonly record struct TodoCommitReceipt(
    CommandId CommandId,
    TodoBoard Board,
    bool WasDuplicate);

[Verifiable]
public readonly record struct TodoUiState(
    TodoBoard Board,
    bool IsSyncing,
    CommandId? PendingCommandId,
    string StatusText,
    string? LastError)
{
    [Verifiable]
    public static TodoUiState Initial() =>
        new(TodoBoard.CreateEmpty(), false, null, "Ready", null);
}

public sealed record AddTodoHttpRequest(Guid CommandId, string? Title, long Timestamp);
public sealed record CompleteTodoHttpRequest(Guid CommandId, int TodoId, long Timestamp);
public sealed record DeleteTodoHttpRequest(Guid CommandId, int TodoId, long Timestamp);
public sealed record ApiErrorResponse(string Message, ImmutableArray<ContractViolation> Violations);
