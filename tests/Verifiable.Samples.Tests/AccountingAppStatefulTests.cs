extern alias AccountingSrv;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using AccountingApp.Shared;
using GalaxyCheck;
using GalaxyCheck.Gens;
using GalaxyCheck.Stateful;
using Verifiable.Contracts;
using Verifiable.Testing;
using Xunit;

namespace Verifiable.Samples.Tests;

public sealed class LedgerSqliteSystem : IDisposable
{
    public FaultInjectedSqliteHarness Harness { get; } = new("ledger-stateful");
    public AccountingSrv::AccountingApp.Server.SqliteLedgerRepository Repo { get; }

    public LedgerSqliteSystem()
    {
        Repo = new AccountingSrv::AccountingApp.Server.SqliteLedgerRepository(Harness.ConnectionString);
    }

    public void Dispose() => Harness.Dispose();
}

public sealed class LedgerModel
{
    public LedgerState State { get; set; } = LedgerState.CreateEmpty();
}

public abstract class LedgerCommandBase(PersistenceFaultKind fault) : Command<LedgerSqliteSystem, LedgerModel>
{
    protected PersistenceFaultKind Fault { get; } = fault;

    public override bool Expects(Exception exception) =>
        Fault is PersistenceFaultKind.PreCommitCrash or PersistenceFaultKind.PostCommitCrashBeforeAck
        && exception is SimulatedPersistenceCrashException;

    public override bool Postcondition(LedgerSqliteSystem system, LedgerModel modelBefore, LedgerModel modelAfter)
    {
        var loaded = system.Repo.LoadLedgerAsync().GetAwaiter().GetResult();
        var replayed = system.Repo.ReplayLedgerAsync().GetAwaiter().GetResult();

        return StatesMatch(loaded, modelAfter.State)
            && StatesMatch(replayed, modelAfter.State)
            && loaded.TotalDebits == loaded.TotalCredits
            && loaded.Accounts.Values.All(a => a.AllowOverdraft || a.Balance >= 0m);
    }

    private static bool StatesMatch(LedgerState a, LedgerState b)
    {
        if (a.TotalDebits != b.TotalDebits ||
            a.TotalCredits != b.TotalCredits ||
            a.NextEntryId != b.NextEntryId ||
            a.Accounts.Count != b.Accounts.Count ||
            a.Journal.Count != b.Journal.Count)
        {
            return false;
        }

        foreach (var kv in a.Accounts)
        {
            if (!b.Accounts.TryGetValue(kv.Key, out var bAcc) || kv.Value != bAcc)
            {
                return false;
            }
        }

        for (var i = 0; i < a.Journal.Count; i++)
        {
            var ea = a.Journal[i];
            var eb = b.Journal[i];
            if (ea.Id != eb.Id ||
                ea.EntryDebitTotal != eb.EntryDebitTotal ||
                ea.EntryCreditTotal != eb.EntryCreditTotal ||
                ea.IsReversed != eb.IsReversed ||
                ea.ReversesEntryId != eb.ReversesEntryId ||
                !ea.Lines.SequenceEqual(eb.Lines))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class OpenAccountCmd(
    int id,
    string name,
    AccountType kind,
    string currency,
    bool allowOverdraft,
    Guid cmdGuid,
    long ts,
    PersistenceFaultKind fault) : LedgerCommandBase(fault)
{
    public override bool Precondition(LedgerModel model) => !model.State.Accounts.ContainsKey(AccountId.From(id));

    public override void Run(LedgerSqliteSystem system)
    {
        var raw = system.Repo.LoadRawLedgerAsync().GetAwaiter().GetResult();
        var portRes = LedgerEnginePort.OpenAccount(raw, id, name, kind, currency, allowOverdraft);
        if (!portRes.IsValid || !portRes.Value.IsSuccess)
        {
            return;
        }

        var tr = portRes.Value;
        var opened = tr.State.Accounts[AccountId.From(id)];
        var pending = LedgerEngine.CreatePendingCommit(
            CommandId.From(cmdGuid), "open", tr.State, opened, null, null, UnixTimestamp.From(ts));

        try
        {
            system.Harness.ExecuteWithFaultAsync(Fault, async () =>
            {
                await system.Repo.CommitAsync(pending);
            }).GetAwaiter().GetResult();
        }
        catch
        {
            Obligation.Discard(pending, "Simulated fault");
            throw;
        }
    }

    public override LedgerModel NextModel(LedgerModel model)
    {
        if (Fault == PersistenceFaultKind.PreCommitCrash) return model;

        var tr = LedgerEngine.OpenAccount(
            model.State,
            AccountId.From(id),
            AccountName.From(name),
            kind,
            CurrencyCode.From(currency),
            allowOverdraft);
        return new LedgerModel { State = tr.State };
    }

    public override string ToString() => $"OpenAccount(#{id} {name} {kind}, od={allowOverdraft}, fault={Fault})";
}

public sealed class TransferCmd(
    int fromId,
    int toId,
    decimal amount,
    string memo,
    Guid cmdGuid,
    long ts,
    PersistenceFaultKind fault) : LedgerCommandBase(fault)
{
    public override bool Precondition(LedgerModel model) =>
        model.State.Accounts.ContainsKey(AccountId.From(fromId)) &&
        model.State.Accounts.ContainsKey(AccountId.From(toId));

    public override void Run(LedgerSqliteSystem system)
    {
        var raw = system.Repo.LoadRawLedgerAsync().GetAwaiter().GetResult();
        var portRes = LedgerEnginePort.PostTransfer(raw, fromId, toId, amount, memo, ts);
        if (!portRes.IsValid || !portRes.Value.IsSuccess || portRes.Value.CreatedEntryId is null)
        {
            return;
        }

        var tr = portRes.Value;
        var entry = tr.State.Journal[^1];
        var pending = LedgerEngine.CreatePendingCommit(
            CommandId.From(cmdGuid), "transfer", tr.State, null, entry, null, UnixTimestamp.From(ts));

        try
        {
            system.Harness.ExecuteWithFaultAsync(Fault, async () =>
            {
                await system.Repo.CommitAsync(pending);
            }).GetAwaiter().GetResult();
        }
        catch
        {
            Obligation.Discard(pending, "Simulated fault");
            throw;
        }
    }

    public override LedgerModel NextModel(LedgerModel model)
    {
        if (Fault == PersistenceFaultKind.PreCommitCrash) return model;

        var tr = LedgerEngine.PostTransfer(
            model.State,
            AccountId.From(fromId),
            AccountId.From(toId),
            PositiveAmount.From(amount),
            memo,
            UnixTimestamp.From(ts));
        return new LedgerModel { State = tr.State };
    }

    public override string ToString() => $"Transfer({fromId}->{toId} {amount:N2}m, fault={Fault})";
}

public sealed class ReverseEntryCmd(
    int entryId,
    Guid cmdGuid,
    long ts,
    PersistenceFaultKind fault) : LedgerCommandBase(fault)
{
    public override bool Precondition(LedgerModel model) =>
        model.State.Journal.Any(e => e.Id.Value == entryId);

    public override void Run(LedgerSqliteSystem system)
    {
        var raw = system.Repo.LoadRawLedgerAsync().GetAwaiter().GetResult();
        var portRes = LedgerEnginePort.ReverseEntry(raw, entryId, ts);
        if (!portRes.IsValid || !portRes.Value.IsSuccess || portRes.Value.CreatedEntryId is null)
        {
            return;
        }

        var tr = portRes.Value;
        var reversal = tr.State.Journal[^1];
        var pending = LedgerEngine.CreatePendingCommit(
            CommandId.From(cmdGuid), "reverse", tr.State, null, reversal, JournalEntryId.From(entryId), UnixTimestamp.From(ts));

        try
        {
            system.Harness.ExecuteWithFaultAsync(Fault, async () =>
            {
                await system.Repo.CommitAsync(pending);
            }).GetAwaiter().GetResult();
        }
        catch
        {
            Obligation.Discard(pending, "Simulated fault");
            throw;
        }
    }

    public override LedgerModel NextModel(LedgerModel model)
    {
        if (Fault == PersistenceFaultKind.PreCommitCrash) return model;

        var tr = LedgerEngine.ReverseEntry(
            model.State,
            JournalEntryId.From(entryId),
            UnixTimestamp.From(ts));
        return new LedgerModel { State = tr.State };
    }

    public override string ToString() => $"Reverse(#{entryId}, fault={Fault})";
}

public class AccountingAppStatefulTests
{
    private static readonly IGen<PersistenceFaultKind> FaultGen = Gen.Choose(
        (Gen.Constant(PersistenceFaultKind.None), 5),
        (Gen.Constant(PersistenceFaultKind.DuplicateDelivery), 2),
        (Gen.Constant(PersistenceFaultKind.PreCommitCrash), 1),
        (Gen.Constant(PersistenceFaultKind.PostCommitCrashBeforeAck), 1));

    [Property(Iterations = 30)]
    public Property SqliteLedgerRepository_AndAppendOnlyReplay_MatchVerifiedLedgerState() =>
        DifferentialPersistenceProperty.Create(
            () => new LedgerSqliteSystem(),
            () => new LedgerModel(),
            NextCommand);

    [Property]
    public Property LedgerInboundPort_RejectsNonPositiveAmounts() =>
        VerifiableGen.PortRejectsInvalid(
            Gen.Decimal().Between(-1000m, 0m),
            badAmt => LedgerEnginePort.PostTransfer(
                new RawLedgerState { NextEntryId = 1, TotalDebits = 0m, TotalCredits = 0m },
                1, 2, badAmt, "bad", 10L));

    private static IGen<Command<LedgerSqliteSystem, LedgerModel>> NextCommand(LedgerModel model)
    {
        var openIds = new[] { 1, 2, 3, 4 }.Where(id => !model.State.Accounts.ContainsKey(AccountId.From(id))).ToList();
        var openGen = openIds.Count > 0
            ? from id in Gen.Element(openIds)
              from kind in Gen.Element(new[] { AccountType.Asset, AccountType.Equity, AccountType.Revenue })
              from od in Gen.Constant(kind == AccountType.Equity || kind == AccountType.Revenue)
              from cmd in VerifiableGen.NonEmptyGuid()
              from ts in Gen.Int64().Between(1, 100_000)
              from fault in FaultGen
              select (Command<LedgerSqliteSystem, LedgerModel>)new OpenAccountCmd(id, $"Acc{id}", kind, "USD", od, cmd, ts, fault)
            : null;

        if (model.State.Accounts.Count < 2)
        {
            return openGen!;
        }

        var existingAccIds = model.State.Accounts.Keys.Select(k => k.Value).ToList();
        var transferGen =
            from fromId in Gen.Element(existingAccIds)
            from toId in Gen.Element(existingAccIds)
            from amt in VerifiableGen.PositiveDecimal(50_000L)
            from cmd in VerifiableGen.NonEmptyGuid()
            from ts in Gen.Int64().Between(1, 100_000)
            from fault in FaultGen
            select (Command<LedgerSqliteSystem, LedgerModel>)new TransferCmd(fromId, toId, amt, "tx", cmd, ts, fault);

        var choices = Gen.Choose<Command<LedgerSqliteSystem, LedgerModel>>()
            .WithChoice(transferGen, 5);

        if (openGen is not null)
        {
            choices = choices.WithChoice(openGen, 2);
        }

        if (!model.State.Journal.IsEmpty)
        {
            var entryIds = model.State.Journal.Select(j => j.Id.Value).ToList();
            var reverseGen =
                from eid in Gen.Element(entryIds)
                from cmd in VerifiableGen.NonEmptyGuid()
                from ts in Gen.Int64().Between(1, 100_000)
                from fault in FaultGen
                select (Command<LedgerSqliteSystem, LedgerModel>)new ReverseEntryCmd(eid, cmd, ts, fault);
            choices = choices.WithChoice(reverseGen, 2);
        }

        return choices;
    }
}
