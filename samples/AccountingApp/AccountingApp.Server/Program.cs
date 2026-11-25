using System.Collections.Generic;
using System.Collections.Immutable;
using AccountingApp.Server;
using AccountingApp.Shared;
using Verifiable.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var connStr = cfg["LedgerDb:ConnectionString"] ?? "Data Source=accounting.db";
    return new SqliteLedgerRepository(connStr);
});

var app = builder.Build();

app.MapGet("/api/ledger", async (SqliteLedgerRepository repo, CancellationToken ct) =>
{
    var state = await repo.LoadLedgerAsync(ct);
    return Results.Ok(state);
});

app.MapGet("/api/ledger/replay", async (SqliteLedgerRepository repo, CancellationToken ct) =>
{
    var replayed = await repo.ReplayLedgerAsync(ct);
    return Results.Ok(replayed);
});

app.MapPost("/api/accounts/open", async (OpenAccountHttpRequest req, SqliteLedgerRepository repo, CancellationToken ct) =>
{
    var cmdCheck = CommandId.TryMarshal(req.CommandId, nameof(req.CommandId));
    if (!cmdCheck.IsValid)
    {
        return Results.BadRequest(new LedgerErrorResponse("Invalid CommandId.", cmdCheck.Violations));
    }

    var rawState = await repo.LoadRawLedgerAsync(ct);
    var portRes = LedgerEnginePort.OpenAccount(rawState, req.AccountId, req.Name!, req.Kind, req.Currency!, req.AllowOverdraft);
    if (!portRes.IsValid)
    {
        return Results.BadRequest(new LedgerErrorResponse("Inbound port validation failed.", portRes.Violations));
    }

    var transition = portRes.Value;
    if (!transition.IsSuccess)
    {
        return Results.Conflict(new LedgerErrorResponse(transition.Error ?? "OpenAccount failed.", ImmutableArray<ContractViolation>.Empty));
    }

    var openedAcc = transition.State.Accounts[AccountId.From(req.AccountId)];
    var pending = LedgerEngine.CreatePendingCommit(
        cmdCheck.Value, "open", transition.State, openedAcc, null, null, UnixTimestamp.From(req.Timestamp));
    var receipt = await repo.CommitAsync(pending, ct);
    return Results.Ok(receipt);
});

app.MapPost("/api/journal/transfer", async (PostTransferHttpRequest req, SqliteLedgerRepository repo, CancellationToken ct) =>
{
    var cmdCheck = CommandId.TryMarshal(req.CommandId, nameof(req.CommandId));
    if (!cmdCheck.IsValid)
    {
        return Results.BadRequest(new LedgerErrorResponse("Invalid CommandId.", cmdCheck.Violations));
    }

    var rawState = await repo.LoadRawLedgerAsync(ct);
    var portRes = LedgerEnginePort.PostTransfer(rawState, req.FromAccountId, req.ToAccountId, req.Amount, req.Memo ?? string.Empty, req.Timestamp);
    if (!portRes.IsValid)
    {
        return Results.BadRequest(new LedgerErrorResponse("Inbound port validation failed.", portRes.Violations));
    }

    var transition = portRes.Value;
    if (!transition.IsSuccess || transition.CreatedEntryId is null)
    {
        return Results.Conflict(new LedgerErrorResponse(transition.Error ?? "Transfer failed.", ImmutableArray<ContractViolation>.Empty));
    }

    var entry = transition.State.Journal[^1];
    var pending = LedgerEngine.CreatePendingCommit(
        cmdCheck.Value, "transfer", transition.State, null, entry, null, UnixTimestamp.From(req.Timestamp));
    var receipt = await repo.CommitAsync(pending, ct);
    return Results.Ok(receipt);
});

app.MapPost("/api/journal/post", async (PostBalancedEntryHttpRequest req, SqliteLedgerRepository repo, CancellationToken ct) =>
{
    var cmdCheck = CommandId.TryMarshal(req.CommandId, nameof(req.CommandId));
    if (!cmdCheck.IsValid)
    {
        return Results.BadRequest(new LedgerErrorResponse("Invalid CommandId.", cmdCheck.Violations));
    }

    var rawState = await repo.LoadRawLedgerAsync(ct);
    var rawLines = req.Lines?.ConvertAll(l => new RawJournalLine
    {
        AccountId = l.AccountId,
        Side = l.Side,
        Amount = l.Amount
    }) ?? new List<RawJournalLine>();

    var portRes = LedgerEnginePort.PostBalancedEntry(rawState, rawLines, req.Memo ?? string.Empty, req.Timestamp);
    if (!portRes.IsValid)
    {
        return Results.BadRequest(new LedgerErrorResponse("Inbound port validation failed.", portRes.Violations));
    }

    var transition = portRes.Value;
    if (!transition.IsSuccess || transition.CreatedEntryId is null)
    {
        return Results.Conflict(new LedgerErrorResponse(transition.Error ?? "Post entry failed.", ImmutableArray<ContractViolation>.Empty));
    }

    var entry = transition.State.Journal[^1];
    var pending = LedgerEngine.CreatePendingCommit(
        cmdCheck.Value, "post", transition.State, null, entry, null, UnixTimestamp.From(req.Timestamp));
    var receipt = await repo.CommitAsync(pending, ct);
    return Results.Ok(receipt);
});

app.MapPost("/api/journal/reverse", async (ReverseEntryHttpRequest req, SqliteLedgerRepository repo, CancellationToken ct) =>
{
    var cmdCheck = CommandId.TryMarshal(req.CommandId, nameof(req.CommandId));
    if (!cmdCheck.IsValid)
    {
        return Results.BadRequest(new LedgerErrorResponse("Invalid CommandId.", cmdCheck.Violations));
    }

    var rawState = await repo.LoadRawLedgerAsync(ct);
    var portRes = LedgerEnginePort.ReverseEntry(rawState, req.EntryId, req.Timestamp);
    if (!portRes.IsValid)
    {
        return Results.BadRequest(new LedgerErrorResponse("Inbound port validation failed.", portRes.Violations));
    }

    var transition = portRes.Value;
    if (!transition.IsSuccess || transition.CreatedEntryId is null)
    {
        return Results.Conflict(new LedgerErrorResponse(transition.Error ?? "Reverse failed.", ImmutableArray<ContractViolation>.Empty));
    }

    var reversalEntry = transition.State.Journal[^1];
    var pending = LedgerEngine.CreatePendingCommit(
        cmdCheck.Value, "reverse", transition.State, null, reversalEntry, JournalEntryId.From(req.EntryId), UnixTimestamp.From(req.Timestamp));
    var receipt = await repo.CommitAsync(pending, ct);
    return Results.Ok(receipt);
});

app.MapPost("/api/journal/fee-split", async (PostFeeSplitHttpRequest req, SqliteLedgerRepository repo, CancellationToken ct) =>
{
    var cmdCheck = CommandId.TryMarshal(req.CommandId, nameof(req.CommandId));
    if (!cmdCheck.IsValid)
    {
        return Results.BadRequest(new LedgerErrorResponse("Invalid CommandId.", cmdCheck.Violations));
    }

    var rawState = await repo.LoadRawLedgerAsync(ct);
    var portRes = LedgerEnginePort.PostFeeSplitTransfer(
        rawState,
        req.FromAccountId,
        req.ToAccountId,
        req.FeeAccountId,
        req.GrossAmount,
        req.FeeAmount,
        req.Memo ?? string.Empty,
        req.Timestamp);
    if (!portRes.IsValid)
    {
        return Results.BadRequest(new LedgerErrorResponse("Inbound port validation failed.", portRes.Violations));
    }

    var transition = portRes.Value;
    if (!transition.IsSuccess || transition.CreatedEntryId is null)
    {
        return Results.Conflict(new LedgerErrorResponse(transition.Error ?? "Fee-split transfer failed.", ImmutableArray<ContractViolation>.Empty));
    }

    var entry = transition.State.Journal[^1];
    var pending = LedgerEngine.CreatePendingCommit(
        cmdCheck.Value, "fee-split", transition.State, null, entry, null, UnixTimestamp.From(req.Timestamp));
    var receipt = await repo.CommitAsync(pending, ct);
    return Results.Ok(receipt);
});

app.Run();

public partial class Program;
