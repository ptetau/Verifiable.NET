using System.Collections.Immutable;
using TodoApp.Server;
using TodoApp.Shared;
using Verifiable.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var connStr = cfg["TodoDb:ConnectionString"] ?? "Data Source=todoapp.db";
    return new SqliteTodoRepository(connStr);
});

var app = builder.Build();

app.MapGet("/api/board", async (SqliteTodoRepository repo, CancellationToken ct) =>
{
    var board = await repo.LoadBoardAsync(ct);
    return Results.Ok(board);
});

app.MapPost("/api/todos/add", async (AddTodoHttpRequest req, SqliteTodoRepository repo, CancellationToken ct) =>
{
    var cmdCheck = CommandId.TryMarshal(req.CommandId, nameof(req.CommandId));
    if (!cmdCheck.IsValid)
    {
        return Results.BadRequest(new ApiErrorResponse("Invalid CommandId.", cmdCheck.Violations));
    }

    var rawBoard = await repo.LoadRawBoardAsync(ct);
    var portRes = TodoEnginePort.Add(rawBoard, req.Title!, req.Timestamp);
    if (!portRes.IsValid)
    {
        return Results.BadRequest(new ApiErrorResponse("Inbound port validation failed.", portRes.Violations));
    }

    var transition = portRes.Value;
    if (!transition.IsSuccess || transition.AffectedId is null)
    {
        return Results.Conflict(new ApiErrorResponse(transition.Error ?? "Add failed.", ImmutableArray<ContractViolation>.Empty));
    }

    var item = transition.Board.Items[transition.AffectedId.Value];
    var pending = TodoEngine.CreatePendingCommit(cmdCheck.Value, "add", transition.Board, item, UnixTimestamp.From(req.Timestamp));
    var receipt = await repo.CommitAsync(pending, ct);
    return Results.Ok(receipt);
});

app.MapPost("/api/todos/complete", async (CompleteTodoHttpRequest req, SqliteTodoRepository repo, CancellationToken ct) =>
{
    var cmdCheck = CommandId.TryMarshal(req.CommandId, nameof(req.CommandId));
    if (!cmdCheck.IsValid)
    {
        return Results.BadRequest(new ApiErrorResponse("Invalid CommandId.", cmdCheck.Violations));
    }

    var rawBoard = await repo.LoadRawBoardAsync(ct);
    var portRes = TodoEnginePort.Complete(rawBoard, req.TodoId, req.Timestamp);
    if (!portRes.IsValid)
    {
        return Results.BadRequest(new ApiErrorResponse("Inbound port validation failed.", portRes.Violations));
    }

    var transition = portRes.Value;
    if (!transition.IsSuccess || transition.AffectedId is null)
    {
        return Results.Conflict(new ApiErrorResponse(transition.Error ?? "Complete failed.", ImmutableArray<ContractViolation>.Empty));
    }

    var item = transition.Board.Items[transition.AffectedId.Value];
    var pending = TodoEngine.CreatePendingCommit(cmdCheck.Value, "complete", transition.Board, item, UnixTimestamp.From(req.Timestamp));
    var receipt = await repo.CommitAsync(pending, ct);
    return Results.Ok(receipt);
});

app.MapPost("/api/todos/delete", async (DeleteTodoHttpRequest req, SqliteTodoRepository repo, CancellationToken ct) =>
{
    var cmdCheck = CommandId.TryMarshal(req.CommandId, nameof(req.CommandId));
    if (!cmdCheck.IsValid)
    {
        return Results.BadRequest(new ApiErrorResponse("Invalid CommandId.", cmdCheck.Violations));
    }

    var rawBoard = await repo.LoadRawBoardAsync(ct);
    var portRes = TodoEnginePort.Delete(rawBoard, req.TodoId, req.Timestamp);
    if (!portRes.IsValid)
    {
        return Results.BadRequest(new ApiErrorResponse("Inbound port validation failed.", portRes.Violations));
    }

    var transition = portRes.Value;
    if (!transition.IsSuccess || transition.AffectedId is null)
    {
        return Results.Conflict(new ApiErrorResponse(transition.Error ?? "Delete failed.", ImmutableArray<ContractViolation>.Empty));
    }

    var item = transition.Board.Items[transition.AffectedId.Value];
    var pending = TodoEngine.CreatePendingCommit(cmdCheck.Value, "delete", transition.Board, item, UnixTimestamp.From(req.Timestamp));
    var receipt = await repo.CommitAsync(pending, ct);
    return Results.Ok(receipt);
});

app.Run();

public partial class Program;
