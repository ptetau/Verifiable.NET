extern alias TodoSrv;

using System;
using System.Linq;
using System.Threading.Tasks;
using GalaxyCheck;
using TodoApp.Shared;
using Verifiable.Testing;
using Xunit;

namespace Verifiable.Samples.Tests;

/// <summary>
/// Requirement R11: the fault-injected model-versus-database harness works for any domain. Here the
/// TodoApp's commands are registered once, and DomainModel generates the command sequences, injects
/// crashes and duplicate deliveries, and compares the model with the database after every step.
/// </summary>
public sealed class DomainModelTests
{
    private sealed record AddArgs(string Title, Guid Command, long At);

    private sealed record TargetArgs(int Id, Guid Command, long At);

    private static bool SameBoard(TodoBoard a, TodoBoard b) =>
        a.NextId == b.NextId &&
        a.ActiveCount == b.ActiveCount &&
        a.CompletedCount == b.CompletedCount &&
        a.DeletedCount == b.DeletedCount &&
        a.Items.Count == b.Items.Count &&
        a.Items.All(kv => b.Items.TryGetValue(kv.Key, out var other) && kv.Value == other);

    /// <summary>Builds the pending commit for an accepted transition, and returns it for the harness to run.</summary>
    private static Func<Task> Commit(TodoSqliteSystem db, string kind, Guid command, long at, bool accepted, TodoTransition transition)
    {
        if (!accepted || !transition.IsSuccess || transition.AffectedId is null) return () => Task.CompletedTask;
        var item = transition.Board.Items[transition.AffectedId.Value];
        var pending = TodoEngine.CreatePendingCommit(CommandId.From(command), kind, transition.Board, item, UnixTimestamp.From(at));
        return () => db.Repo.CommitAsync(pending);
    }

    private static IGen<int>? AnyItem(TodoBoard board) =>
        board.Items.IsEmpty ? null : Gen.Element(board.Items.Keys.Select(k => k.Value).ToList());

    private static DomainModel<TodoSqliteSystem, TodoBoard> Todo(bool forgetDeletes = false) =>
        new DomainModel<TodoSqliteSystem, TodoBoard>()
            .Command("Add",
                _ => from title in VerifiableGen.NonEmptyString(40)
                     from cmd in VerifiableGen.NonEmptyGuid()
                     from at in Gen.Int64().Between(1, 1_000_000)
                     select new AddArgs(title, cmd, at),
                (board, a) => TodoEngine.Add(board, TodoTitle.From(a.Title), UnixTimestamp.From(a.At)).Board,
                (db, a) =>
                {
                    var r = TodoEnginePort.Add(db.Repo.LoadRawBoardAsync().GetAwaiter().GetResult(), a.Title, a.At);
                    return Commit(db, "add", a.Command, a.At, r.IsValid, r.Value);
                },
                weight: 3)
            .Command("Complete",
                board => AnyItem(board) is { } ids
                    ? from id in ids from cmd in VerifiableGen.NonEmptyGuid() from at in Gen.Int64().Between(1, 1_000_000) select new TargetArgs(id, cmd, at)
                    : null,
                (board, a) => TodoEngine.Complete(board, TodoId.From(a.Id), UnixTimestamp.From(a.At)).Board,
                (db, a) =>
                {
                    var r = TodoEnginePort.Complete(db.Repo.LoadRawBoardAsync().GetAwaiter().GetResult(), a.Id, a.At);
                    return Commit(db, "complete", a.Command, a.At, r.IsValid, r.Value);
                },
                weight: 2)
            .Command("Delete",
                board => AnyItem(board) is { } ids
                    ? from id in ids from cmd in VerifiableGen.NonEmptyGuid() from at in Gen.Int64().Between(1, 1_000_000) select new TargetArgs(id, cmd, at)
                    : null,
                (board, a) => forgetDeletes ? board : TodoEngine.Delete(board, TodoId.From(a.Id), UnixTimestamp.From(a.At)).Board,
                (db, a) =>
                {
                    var r = TodoEnginePort.Delete(db.Repo.LoadRawBoardAsync().GetAwaiter().GetResult(), a.Id, a.At);
                    return Commit(db, "delete", a.Command, a.At, r.IsValid, r.Value);
                },
                weight: 2);

    private static Property TodoProperty(bool forgetDeletes = false) =>
        Todo(forgetDeletes).Property(
            () => new TodoSqliteSystem(),
            TodoBoard.CreateEmpty(),
            db => db.LoadBoard(),
            db => db.Harness,
            SameBoard);

    [Property(Iterations = 30)]
    public Property TodoRepository_MatchesTheModel_UnderCrashesAndDuplicates() => TodoProperty();

    [Fact]
    public void AModelThatDisagreesWithTheDatabase_IsCaught()
    {
        var result = TodoProperty(forgetDeletes: true).Check(iterations: 200, seed: 0);
        Assert.True(result.Falsified, "Dropping deletes from the model must make the property fail.");
    }

    [Fact]
    public void Registration_IsValidated()
    {
        Assert.Equal(new[] { "Add", "Complete", "Delete" }, Todo().CommandNames);
        Assert.Throws<InvalidOperationException>(() =>
            new DomainModel<TodoSqliteSystem, TodoBoard>().Property(() => new TodoSqliteSystem(), TodoBoard.CreateEmpty(), db => db.LoadBoard(), db => db.Harness));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DomainModel<TodoSqliteSystem, TodoBoard>().Command<int>("X", _ => Gen.Int32(), (s, _) => s, (_, _) => () => Task.CompletedTask, weight: 0));
    }
}
