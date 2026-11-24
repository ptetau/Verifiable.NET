extern alias TodoSrv;

using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyCheck;
using GalaxyCheck.Gens;
using GalaxyCheck.Stateful;
using TodoApp.Shared;
using Verifiable.Testing;
using Xunit;

namespace Verifiable.Samples.Tests;

public sealed class TodoSqliteSystem : IDisposable
{
    public FaultInjectedSqliteHarness Harness { get; } = new("todo-stateful");
    public TodoSrv::TodoApp.Server.SqliteTodoRepository Repo { get; }

    public TodoSqliteSystem()
    {
        Repo = new TodoSrv::TodoApp.Server.SqliteTodoRepository(Harness.ConnectionString);
    }

    public TodoBoard LoadBoard() => Repo.LoadBoardAsync().GetAwaiter().GetResult();

    public void Dispose() => Harness.Dispose();
}

public sealed class TodoModel
{
    public TodoBoard Board { get; set; } = TodoBoard.CreateEmpty();
}

public abstract class TodoCommandBase(PersistenceFaultKind fault) : Command<TodoSqliteSystem, TodoModel>
{
    protected PersistenceFaultKind Fault { get; } = fault;

    public override bool Expects(Exception exception) =>
        Fault is PersistenceFaultKind.PreCommitCrash or PersistenceFaultKind.PostCommitCrashBeforeAck
        && exception is SimulatedPersistenceCrashException;

    public override bool Postcondition(TodoSqliteSystem system, TodoModel modelBefore, TodoModel modelAfter)
    {
        var persisted = system.LoadBoard();
        if (!BoardEquals(persisted, modelAfter.Board))
        {
            return false;
        }

        return persisted.NextId.Value >= 1
            && persisted.Items.Count == persisted.ActiveCount + persisted.CompletedCount + persisted.DeletedCount
            && persisted.Items.Count == persisted.NextId.Value - 1;
    }

    protected static bool BoardEquals(TodoBoard a, TodoBoard b)
    {
        if (a.NextId != b.NextId ||
            a.ActiveCount != b.ActiveCount ||
            a.CompletedCount != b.CompletedCount ||
            a.DeletedCount != b.DeletedCount ||
            a.Items.Count != b.Items.Count)
        {
            return false;
        }

        foreach (var kv in a.Items)
        {
            if (!b.Items.TryGetValue(kv.Key, out var bv) || kv.Value != bv)
            {
                return false;
            }
        }
        return true;
    }
}

public sealed class AddTodoCmd(string title, Guid cmdGuid, long ts, PersistenceFaultKind fault) : TodoCommandBase(fault)
{
    public override void Run(TodoSqliteSystem system)
    {
        var rawBoard = system.Repo.LoadRawBoardAsync().GetAwaiter().GetResult();
        var portRes = TodoEnginePort.Add(rawBoard, title, ts);
        if (!portRes.IsValid || !portRes.Value.IsSuccess || portRes.Value.AffectedId is null)
        {
            return;
        }

        var tr = portRes.Value;
        var item = tr.Board.Items[tr.AffectedId.Value];
        var pending = TodoEngine.CreatePendingCommit(CommandId.From(cmdGuid), "add", tr.Board, item, UnixTimestamp.From(ts));

        try
        {
            system.Harness.ExecuteWithFaultAsync(Fault, async () =>
            {
                await system.Repo.CommitAsync(pending);
            }).GetAwaiter().GetResult();
        }
        catch
        {
            Verifiable.Contracts.Obligation.Discard(pending, "Simulated fault before/after commit");
            throw;
        }
    }

    public override TodoModel NextModel(TodoModel model)
    {
        if (Fault == PersistenceFaultKind.PreCommitCrash)
        {
            return model;
        }

        var tr = TodoEngine.Add(model.Board, TodoTitle.From(title), UnixTimestamp.From(ts));
        return new TodoModel { Board = tr.Board };
    }

    public override string ToString() => $"Add({title}, fault={Fault})";
}

public sealed class CompleteTodoCmd(int targetId, Guid cmdGuid, long ts, PersistenceFaultKind fault) : TodoCommandBase(fault)
{
    public override bool Precondition(TodoModel model) => model.Board.Items.Count > 0;

    public override void Run(TodoSqliteSystem system)
    {
        var rawBoard = system.Repo.LoadRawBoardAsync().GetAwaiter().GetResult();
        var portRes = TodoEnginePort.Complete(rawBoard, targetId, ts);
        if (!portRes.IsValid || !portRes.Value.IsSuccess || portRes.Value.AffectedId is null)
        {
            return;
        }

        var tr = portRes.Value;
        var item = tr.Board.Items[tr.AffectedId.Value];
        var pending = TodoEngine.CreatePendingCommit(CommandId.From(cmdGuid), "complete", tr.Board, item, UnixTimestamp.From(ts));

        try
        {
            system.Harness.ExecuteWithFaultAsync(Fault, async () =>
            {
                await system.Repo.CommitAsync(pending);
            }).GetAwaiter().GetResult();
        }
        catch
        {
            Verifiable.Contracts.Obligation.Discard(pending, "Simulated fault");
            throw;
        }
    }

    public override TodoModel NextModel(TodoModel model)
    {
        if (Fault == PersistenceFaultKind.PreCommitCrash)
        {
            return model;
        }

        var tr = TodoEngine.Complete(model.Board, TodoId.From(targetId), UnixTimestamp.From(ts));
        return new TodoModel { Board = tr.Board };
    }

    public override string ToString() => $"Complete(#{targetId}, fault={Fault})";
}

public sealed class DeleteTodoCmd(int targetId, Guid cmdGuid, long ts, PersistenceFaultKind fault) : TodoCommandBase(fault)
{
    public override bool Precondition(TodoModel model) => model.Board.Items.Count > 0;

    public override void Run(TodoSqliteSystem system)
    {
        var rawBoard = system.Repo.LoadRawBoardAsync().GetAwaiter().GetResult();
        var portRes = TodoEnginePort.Delete(rawBoard, targetId, ts);
        if (!portRes.IsValid || !portRes.Value.IsSuccess || portRes.Value.AffectedId is null)
        {
            return;
        }

        var tr = portRes.Value;
        var item = tr.Board.Items[tr.AffectedId.Value];
        var pending = TodoEngine.CreatePendingCommit(CommandId.From(cmdGuid), "delete", tr.Board, item, UnixTimestamp.From(ts));

        try
        {
            system.Harness.ExecuteWithFaultAsync(Fault, async () =>
            {
                await system.Repo.CommitAsync(pending);
            }).GetAwaiter().GetResult();
        }
        catch
        {
            Verifiable.Contracts.Obligation.Discard(pending, "Simulated fault");
            throw;
        }
    }

    public override TodoModel NextModel(TodoModel model)
    {
        if (Fault == PersistenceFaultKind.PreCommitCrash)
        {
            return model;
        }

        var tr = TodoEngine.Delete(model.Board, TodoId.From(targetId), UnixTimestamp.From(ts));
        return new TodoModel { Board = tr.Board };
    }

    public override string ToString() => $"Delete(#{targetId}, fault={Fault})";
}

public class TodoAppStatefulTests
{
    private static readonly IGen<PersistenceFaultKind> FaultGen = Gen.Choose(
        (Gen.Constant(PersistenceFaultKind.None), 5),
        (Gen.Constant(PersistenceFaultKind.DuplicateDelivery), 2),
        (Gen.Constant(PersistenceFaultKind.PreCommitCrash), 1),
        (Gen.Constant(PersistenceFaultKind.PostCommitCrashBeforeAck), 1));

    [Property(Iterations = 30)]
    public Property SqliteTodoRepository_MatchesVerifiedModel_UnderCrashAndDuplicateFaults() =>
        DifferentialPersistenceProperty.Create(
            () => new TodoSqliteSystem(),
            () => new TodoModel(),
            NextCommand);

    [Property]
    public Property TodoInboundPort_RejectsAllEmptyOrOverlongTitles() =>
        VerifiableGen.PortRejectsInvalid(
            Gen.Choose(
                (Gen.Constant(string.Empty), 1),
                (Gen.String().WithLengthBetween(201, 240), 1)),
            title => TodoEnginePort.Add(
                TodoSrv::TodoApp.Server.SqliteTodoRepository.ToRaw(TodoBoard.CreateEmpty()),
                title,
                1000L));

    private static IGen<Command<TodoSqliteSystem, TodoModel>> NextCommand(TodoModel model)
    {
        var add =
            from title in VerifiableGen.NonEmptyString(40)
            from cmd in VerifiableGen.NonEmptyGuid()
            from ts in Gen.Int64().Between(1, 1_000_000)
            from fault in FaultGen
            select (Command<TodoSqliteSystem, TodoModel>)new AddTodoCmd(title, cmd, ts, fault);

        if (model.Board.Items.IsEmpty)
        {
            return add;
        }

        var existingIds = model.Board.Items.Keys.Select(k => k.Value).ToList();
        var complete =
            from id in Gen.Element(existingIds)
            from cmd in VerifiableGen.NonEmptyGuid()
            from ts in Gen.Int64().Between(1, 1_000_000)
            from fault in FaultGen
            select (Command<TodoSqliteSystem, TodoModel>)new CompleteTodoCmd(id, cmd, ts, fault);

        var delete =
            from id in Gen.Element(existingIds)
            from cmd in VerifiableGen.NonEmptyGuid()
            from ts in Gen.Int64().Between(1, 1_000_000)
            from fault in FaultGen
            select (Command<TodoSqliteSystem, TodoModel>)new DeleteTodoCmd(id, cmd, ts, fault);

        return Gen.Choose(
            (add, 3),
            (complete, 2),
            (delete, 2));
    }
}
