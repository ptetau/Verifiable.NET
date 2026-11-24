using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AccountingApp.Shared;
using Microsoft.Data.Sqlite;
using Verifiable.Contracts;

namespace AccountingApp.Server;

public sealed class SqliteLedgerRepository
{
    private readonly string _connectionString;

    public SqliteLedgerRepository(string connectionString)
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

            CREATE TABLE IF NOT EXISTS ledger_meta (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                next_entry_id INTEGER NOT NULL,
                total_debits TEXT NOT NULL,
                total_credits TEXT NOT NULL
            );
            INSERT OR IGNORE INTO ledger_meta (id, next_entry_id, total_debits, total_credits)
            VALUES (1, 1, '0', '0');

            CREATE TABLE IF NOT EXISTS accounts (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                kind INTEGER NOT NULL,
                currency TEXT NOT NULL,
                balance TEXT NOT NULL,
                allow_overdraft INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS journal_entries (
                id INTEGER PRIMARY KEY,
                memo TEXT NOT NULL,
                posted_at INTEGER NOT NULL,
                entry_debit_total TEXT NOT NULL,
                entry_credit_total TEXT NOT NULL,
                is_reversed INTEGER NOT NULL,
                reverses_entry_id INTEGER NULL
            );

            CREATE TABLE IF NOT EXISTS journal_lines (
                line_pk INTEGER PRIMARY KEY AUTOINCREMENT,
                entry_id INTEGER NOT NULL,
                line_order INTEGER NOT NULL,
                account_id INTEGER NOT NULL,
                side INTEGER NOT NULL,
                amount TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS processed_commands (
                command_id TEXT PRIMARY KEY,
                operation_kind TEXT NOT NULL,
                applied_at INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task<RawLedgerState> LoadRawLedgerAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        int nextEntryId = 1;
        decimal totalDebits = 0m, totalCredits = 0m;

        await using (var metaCmd = conn.CreateCommand())
        {
            metaCmd.CommandText = "SELECT next_entry_id, total_debits, total_credits FROM ledger_meta WHERE id = 1;";
            await using var r = await metaCmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                nextEntryId = r.GetInt32(0);
                totalDebits = ParseDec(r.GetString(1));
                totalCredits = ParseDec(r.GetString(2));
            }
        }

        var accounts = new Dictionary<int, RawAccount>();
        await using (var accCmd = conn.CreateCommand())
        {
            accCmd.CommandText = "SELECT id, name, kind, currency, balance, allow_overdraft FROM accounts ORDER BY id;";
            await using var r = await accCmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = r.GetInt32(0);
                accounts[id] = new RawAccount
                {
                    Id = id,
                    Name = r.GetString(1),
                    Kind = (AccountType)r.GetInt32(2),
                    Currency = r.GetString(3),
                    Balance = ParseDec(r.GetString(4)),
                    AllowOverdraft = r.GetInt32(5) != 0
                };
            }
        }

        var linesByEntry = await LoadLinesByEntryAsync(conn, ct);
        var journal = await LoadJournalEntriesAsync(conn, linesByEntry, ct);

        return new RawLedgerState
        {
            Accounts = accounts,
            Journal = journal,
            NextEntryId = nextEntryId,
            TotalDebits = totalDebits,
            TotalCredits = totalCredits
        };
    }

    public async Task<LedgerState> LoadLedgerAsync(CancellationToken ct = default)
    {
        var raw = await LoadRawLedgerAsync(ct);
        var marshaled = LedgerStateGate.TryMarshal(raw, "sqlite");
        if (!marshaled.IsValid)
        {
            throw new InvalidOperationException("Persisted SQLite LedgerState failed inbound invariant gate.");
        }
        return marshaled.Value;
    }

    /// <summary>
    /// Reconstructs the entire <see cref="LedgerState"/> solely from the append-only SQLite journal table
    /// (ignoring materialized account balances and meta totals) and validates it through <see cref="LedgerStateGate"/>.
    /// </summary>
    public async Task<LedgerState> ReplayLedgerAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var accounts = new Dictionary<int, RawAccount>();
        await using (var accCmd = conn.CreateCommand())
        {
            accCmd.CommandText = "SELECT id, name, kind, currency, allow_overdraft FROM accounts ORDER BY id;";
            await using var r = await accCmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = r.GetInt32(0);
                accounts[id] = new RawAccount
                {
                    Id = id,
                    Name = r.GetString(1),
                    Kind = (AccountType)r.GetInt32(2),
                    Currency = r.GetString(3),
                    Balance = 0m, // Reset to zero; recomputed strictly from append-only journal_lines
                    AllowOverdraft = r.GetInt32(4) != 0
                };
            }
        }

        var linesByEntry = await LoadLinesByEntryAsync(conn, ct);
        var journal = await LoadJournalEntriesAsync(conn, linesByEntry, ct);

        decimal totalDebits = 0m, totalCredits = 0m;
        int maxEntryId = 0;

        foreach (var entry in journal)
        {
            if (entry.Id > maxEntryId) maxEntryId = entry.Id;
            totalDebits += entry.EntryDebitTotal;
            totalCredits += entry.EntryCreditTotal;

            if (entry.Lines is not null)
            {
                foreach (var line in entry.Lines)
                {
                    var acc = accounts[line.AccountId];
                    acc.Balance += LedgerEngine.SignedBalanceDelta(acc.Kind, line.Side, line.Amount);
                }
            }
        }

        var rawReplayed = new RawLedgerState
        {
            Accounts = accounts,
            Journal = journal,
            NextEntryId = maxEntryId + 1,
            TotalDebits = totalDebits,
            TotalCredits = totalCredits
        };

        var marshaled = LedgerStateGate.TryMarshal(rawReplayed, "replay");
        if (!marshaled.IsValid)
        {
            throw new InvalidOperationException("Replayed LedgerState violated invariant gate.");
        }
        return marshaled.Value;
    }

    [ConsumesObligation]
    public async Task<LedgerCommitReceipt> CommitAsync(PendingLedgerCommit pending, CancellationToken ct = default)
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
                var current = await LoadLedgerAsync(ct);
                return new LedgerCommitReceipt(pending.CommandId, current, WasDuplicate: true);
            }
        }

        if (pending.OpenedAccount is { } opened)
        {
            await using var accIns = conn.CreateCommand();
            accIns.Transaction = tx;
            accIns.CommandText = """
                INSERT INTO accounts (id, name, kind, currency, balance, allow_overdraft)
                VALUES ($id, $name, $kind, $curr, $bal, $od);
                """;
            accIns.Parameters.AddWithValue("$id", opened.Id.Value);
            accIns.Parameters.AddWithValue("$name", opened.Name.Value);
            accIns.Parameters.AddWithValue("$kind", (int)opened.Kind);
            accIns.Parameters.AddWithValue("$curr", opened.Currency.Value);
            accIns.Parameters.AddWithValue("$bal", FormatDec(opened.Balance));
            accIns.Parameters.AddWithValue("$od", opened.AllowOverdraft ? 1 : 0);
            await accIns.ExecuteNonQueryAsync(ct);
        }

        if (pending.ReversedEntryId is { } revId)
        {
            await using var revUpd = conn.CreateCommand();
            revUpd.Transaction = tx;
            revUpd.CommandText = "UPDATE journal_entries SET is_reversed = 1 WHERE id = $id;";
            revUpd.Parameters.AddWithValue("$id", revId.Value);
            await revUpd.ExecuteNonQueryAsync(ct);
        }

        if (pending.AppendedEntry is { } entry)
        {
            await using (var entryIns = conn.CreateCommand())
            {
                entryIns.Transaction = tx;
                entryIns.CommandText = """
                    INSERT INTO journal_entries (id, memo, posted_at, entry_debit_total, entry_credit_total, is_reversed, reverses_entry_id)
                    VALUES ($id, $memo, $at, $deb, $cred, $rev, $revOf);
                    """;
                entryIns.Parameters.AddWithValue("$id", entry.Id.Value);
                entryIns.Parameters.AddWithValue("$memo", entry.Memo);
                entryIns.Parameters.AddWithValue("$at", entry.PostedAt.Value);
                entryIns.Parameters.AddWithValue("$deb", FormatDec(entry.EntryDebitTotal));
                entryIns.Parameters.AddWithValue("$cred", FormatDec(entry.EntryCreditTotal));
                entryIns.Parameters.AddWithValue("$rev", entry.IsReversed ? 1 : 0);
                entryIns.Parameters.AddWithValue("$revOf", entry.ReversesEntryId.HasValue ? entry.ReversesEntryId.Value.Value : DBNull.Value);
                await entryIns.ExecuteNonQueryAsync(ct);
            }

            for (var i = 0; i < entry.Lines.Count; i++)
            {
                var line = entry.Lines[i];
                await using (var lineIns = conn.CreateCommand())
                {
                    lineIns.Transaction = tx;
                    lineIns.CommandText = """
                        INSERT INTO journal_lines (entry_id, line_order, account_id, side, amount)
                        VALUES ($entryId, $ord, $accId, $side, $amt);
                        """;
                    lineIns.Parameters.AddWithValue("$entryId", entry.Id.Value);
                    lineIns.Parameters.AddWithValue("$ord", i);
                    lineIns.Parameters.AddWithValue("$accId", line.AccountId.Value);
                    lineIns.Parameters.AddWithValue("$side", (int)line.Side);
                    lineIns.Parameters.AddWithValue("$amt", FormatDec(line.Amount.Value));
                    await lineIns.ExecuteNonQueryAsync(ct);
                }

                var updatedAcc = pending.NextState.Accounts[line.AccountId];
                await using (var balUpd = conn.CreateCommand())
                {
                    balUpd.Transaction = tx;
                    balUpd.CommandText = "UPDATE accounts SET balance = $bal WHERE id = $id;";
                    balUpd.Parameters.AddWithValue("$bal", FormatDec(updatedAcc.Balance));
                    balUpd.Parameters.AddWithValue("$id", updatedAcc.Id.Value);
                    await balUpd.ExecuteNonQueryAsync(ct);
                }
            }
        }

        await using (var metaUpd = conn.CreateCommand())
        {
            metaUpd.Transaction = tx;
            metaUpd.CommandText = "UPDATE ledger_meta SET next_entry_id = $nextId, total_debits = $deb, total_credits = $cred WHERE id = 1;";
            metaUpd.Parameters.AddWithValue("$nextId", pending.NextState.NextEntryId.Value);
            metaUpd.Parameters.AddWithValue("$deb", FormatDec(pending.NextState.TotalDebits));
            metaUpd.Parameters.AddWithValue("$cred", FormatDec(pending.NextState.TotalCredits));
            await metaUpd.ExecuteNonQueryAsync(ct);
        }

        await using (var idemp = conn.CreateCommand())
        {
            idemp.Transaction = tx;
            idemp.CommandText = "INSERT INTO processed_commands (command_id, operation_kind, applied_at) VALUES ($id, $kind, $at);";
            idemp.Parameters.AddWithValue("$id", cmdIdStr);
            idemp.Parameters.AddWithValue("$kind", pending.OperationKind);
            idemp.Parameters.AddWithValue("$at", pending.Timestamp.Value);
            await idemp.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new LedgerCommitReceipt(pending.CommandId, pending.NextState, WasDuplicate: false);
    }

    private static async Task<Dictionary<int, List<RawJournalLine>>> LoadLinesByEntryAsync(SqliteConnection conn, CancellationToken ct)
    {
        var map = new Dictionary<int, List<RawJournalLine>>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT entry_id, account_id, side, amount FROM journal_lines ORDER BY entry_id, line_order;";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var entryId = r.GetInt32(0);
            if (!map.TryGetValue(entryId, out var list))
            {
                list = new List<RawJournalLine>();
                map[entryId] = list;
            }
            list.Add(new RawJournalLine
            {
                AccountId = r.GetInt32(1),
                Side = (EntrySide)r.GetInt32(2),
                Amount = ParseDec(r.GetString(3))
            });
        }
        return map;
    }

    private static async Task<List<RawPostedJournalEntry>> LoadJournalEntriesAsync(
        SqliteConnection conn,
        Dictionary<int, List<RawJournalLine>> linesByEntry,
        CancellationToken ct)
    {
        var list = new List<RawPostedJournalEntry>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, memo, posted_at, entry_debit_total, entry_credit_total, is_reversed, reverses_entry_id FROM journal_entries ORDER BY id;";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = r.GetInt32(0);
            list.Add(new RawPostedJournalEntry
            {
                Id = id,
                Memo = r.GetString(1),
                PostedAt = r.GetInt64(2),
                EntryDebitTotal = ParseDec(r.GetString(3)),
                EntryCreditTotal = ParseDec(r.GetString(4)),
                IsReversed = r.GetInt32(5) != 0,
                ReversesEntryId = r.IsDBNull(6) ? null : r.GetInt32(6),
                Lines = linesByEntry.TryGetValue(id, out var lines) ? lines : new List<RawJournalLine>()
            });
        }
        return list;
    }

    private static string FormatDec(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    private static decimal ParseDec(string value) => decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);
}
