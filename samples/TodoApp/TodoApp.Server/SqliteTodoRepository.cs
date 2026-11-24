using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TodoApp.Shared;
using Verifiable.Contracts;

namespace TodoApp.Server;

public sealed class SqliteTodoRepository
{
    private readonly string _connectionString;

    public SqliteTodoRepository(string connectionString)
    {
        _connectionString = connectionString;
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS board_meta (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                next_id INTEGER NOT NULL
            );
            INSERT OR IGNORE INTO board_meta (id, next_id) VALUES (1, 1);

            CREATE TABLE IF NOT EXISTS todos (
                id INTEGER PRIMARY KEY,
                title TEXT NOT NULL,
                status INTEGER NOT NULL,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS processed_commands (
                command_id TEXT PRIMARY KEY,
                operation_kind TEXT NOT NULL,
                applied_at INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task<RawTodoBoard> LoadRawBoardAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        int nextId = 1;
        await using (var metaCmd = conn.CreateCommand())
        {
            metaCmd.CommandText = "SELECT next_id FROM board_meta WHERE id = 1;";
            var scalar = await metaCmd.ExecuteScalarAsync(ct);
            if (scalar is not null && scalar != DBNull.Value)
            {
                nextId = Convert.ToInt32(scalar);
            }
        }

        var items = new Dictionary<int, RawTodoItem>();
        int active = 0, completed = 0, deleted = 0;

        await using (var itemsCmd = conn.CreateCommand())
        {
            itemsCmd.CommandText = "SELECT id, title, status, created_at, updated_at FROM todos ORDER BY id;";
            await using var reader = await itemsCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt32(0);
                var title = reader.GetString(1);
                var status = (TodoStatus)reader.GetInt32(2);
                var createdAt = reader.GetInt64(3);
                var updatedAt = reader.GetInt64(4);

                items[id] = new RawTodoItem
                {
                    Id = id,
                    Title = title,
                    Status = status,
                    CreatedAt = createdAt,
                    UpdatedAt = updatedAt
                };

                switch (status)
                {
                    case TodoStatus.Active: active++; break;
                    case TodoStatus.Completed: completed++; break;
                    case TodoStatus.Deleted: deleted++; break;
                }
            }
        }

        return new RawTodoBoard
        {
            NextId = nextId,
            Items = items,
            ActiveCount = active,
            CompletedCount = completed,
            DeletedCount = deleted
        };
    }

    public async Task<TodoBoard> LoadBoardAsync(CancellationToken ct = default)
    {
        var raw = await LoadRawBoardAsync(ct);
        var marshaled = TodoBoardGate.TryMarshal(raw, "sqlite");
        if (!marshaled.IsValid)
        {
            throw new InvalidOperationException("Persisted SQLite TodoBoard failed inbound invariant gate.");
        }
        return marshaled.Value;
    }

    [ConsumesObligation]
    public async Task<TodoCommitReceipt> CommitAsync(PendingTodoCommit pending, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        var cmdIdStr = pending.CommandId.Value.ToString("D");
        await using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.Transaction = tx;
            checkCmd.CommandText = "SELECT COUNT(1) FROM processed_commands WHERE command_id = $id;";
            checkCmd.Parameters.AddWithValue("$id", cmdIdStr);
            var exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(ct)) > 0;
            if (exists)
            {
                await tx.RollbackAsync(ct);
                var current = await LoadBoardAsync(ct);
                return new TodoCommitReceipt(pending.CommandId, current, WasDuplicate: true);
            }
        }

        if (pending.MutatedItem is { } item)
        {
            await using var upsert = conn.CreateCommand();
            upsert.Transaction = tx;
            upsert.CommandText = """
                INSERT INTO todos (id, title, status, created_at, updated_at)
                VALUES ($id, $title, $status, $createdAt, $updatedAt)
                ON CONFLICT(id) DO UPDATE SET
                    title = excluded.title,
                    status = excluded.status,
                    updated_at = excluded.updated_at;
                """;
            upsert.Parameters.AddWithValue("$id", item.Id.Value);
            upsert.Parameters.AddWithValue("$title", item.Title.Value);
            upsert.Parameters.AddWithValue("$status", (int)item.Status);
            upsert.Parameters.AddWithValue("$createdAt", item.CreatedAt.Value);
            upsert.Parameters.AddWithValue("$updatedAt", item.UpdatedAt.Value);
            await upsert.ExecuteNonQueryAsync(ct);
        }

        await using (var metaUpd = conn.CreateCommand())
        {
            metaUpd.Transaction = tx;
            metaUpd.CommandText = "UPDATE board_meta SET next_id = $nextId WHERE id = 1;";
            metaUpd.Parameters.AddWithValue("$nextId", pending.NextBoard.NextId.Value);
            await metaUpd.ExecuteNonQueryAsync(ct);
        }

        await using (var idemp = conn.CreateCommand())
        {
            idemp.Transaction = tx;
            idemp.CommandText = """
                INSERT INTO processed_commands (command_id, operation_kind, applied_at)
                VALUES ($id, $kind, $at);
                """;
            idemp.Parameters.AddWithValue("$id", cmdIdStr);
            idemp.Parameters.AddWithValue("$kind", pending.OperationKind);
            idemp.Parameters.AddWithValue("$at", pending.Timestamp.Value);
            await idemp.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new TodoCommitReceipt(pending.CommandId, pending.NextBoard, WasDuplicate: false);
    }

    public static RawTodoBoard ToRaw(TodoBoard board)
    {
        var items = new Dictionary<int, RawTodoItem>(board.Items.Count);
        foreach (var kv in board.Items)
        {
            items[kv.Key.Value] = new RawTodoItem
            {
                Id = kv.Value.Id.Value,
                Title = kv.Value.Title.Value,
                Status = kv.Value.Status,
                CreatedAt = kv.Value.CreatedAt.Value,
                UpdatedAt = kv.Value.UpdatedAt.Value
            };
        }

        return new RawTodoBoard
        {
            NextId = board.NextId.Value,
            Items = items,
            ActiveCount = board.ActiveCount,
            CompletedCount = board.CompletedCount,
            DeletedCount = board.DeletedCount
        };
    }
}
