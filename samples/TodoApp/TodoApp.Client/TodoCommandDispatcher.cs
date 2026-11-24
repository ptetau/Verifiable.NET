using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using TodoApp.Shared;
using Verifiable.Contracts;

namespace TodoApp.Client;

public sealed class TodoCommandDispatcher(HttpClient http)
{
    public async Task<Result<TodoBoard, string>> LoadBoardAsync(CancellationToken ct = default)
    {
        try
        {
            var board = await http.GetFromJsonAsync<TodoBoard>("/api/board", ct);
            return Result<TodoBoard, string>.Ok(board);
        }
        catch (Exception ex)
        {
            return Result<TodoBoard, string>.Fail(ex.Message);
        }
    }

    [ConsumesObligation]
    public async Task<Result<TodoCommitReceipt, string>> DispatchAsync(
        UnsentTodoCommand command,
        CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage response = command.OperationKind switch
            {
                "add" => await http.PostAsJsonAsync(
                    "/api/todos/add",
                    new AddTodoHttpRequest(command.CommandId.Value, command.TitleText, command.Timestamp.Value),
                    ct),
                "complete" => await http.PostAsJsonAsync(
                    "/api/todos/complete",
                    new CompleteTodoHttpRequest(command.CommandId.Value, command.TargetIdValue ?? 0, command.Timestamp.Value),
                    ct),
                "delete" => await http.PostAsJsonAsync(
                    "/api/todos/delete",
                    new DeleteTodoHttpRequest(command.CommandId.Value, command.TargetIdValue ?? 0, command.Timestamp.Value),
                    ct),
                _ => throw new InvalidOperationException($"Unknown operation kind '{command.OperationKind}'.")
            };

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                return Result<TodoCommitReceipt, string>.Fail(err);
            }

            var receipt = await response.Content.ReadFromJsonAsync<TodoCommitReceipt>(ct);
            return Result<TodoCommitReceipt, string>.Ok(receipt);
        }
        catch (Exception ex)
        {
            return Result<TodoCommitReceipt, string>.Fail(ex.Message);
        }
    }
}
