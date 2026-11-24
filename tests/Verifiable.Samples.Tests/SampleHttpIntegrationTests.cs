extern alias TodoSrv;
extern alias AccountingSrv;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using AccountingApp.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using TodoApp.Shared;
using Xunit;

namespace Verifiable.Samples.Tests;

public class SampleHttpIntegrationTests
{
    [Fact]
    public async Task TodoAppServer_EndToEndLifecycle_Idempotency_AndValidation()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"todo-http-{Guid.NewGuid():N}.db");
        try
        {
            await using var factory = new WebApplicationFactory<TodoSrv::Program>()
                .WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TodoDb:ConnectionString"] = $"Data Source={dbPath}"
                    })));

            using var client = factory.CreateClient();

            // 1. Reject invalid wire input via generated [InboundPort]
            var badResp = await client.PostAsJsonAsync("/api/todos/add", new AddTodoHttpRequest(Guid.NewGuid(), "", 100));
            Assert.Equal(HttpStatusCode.BadRequest, badResp.StatusCode);
            var badBody = await badResp.Content.ReadAsStringAsync();
            Assert.Contains("between 1 and 200 characters", badBody);

            // 2. Add item
            var addCmd = Guid.NewGuid();
            var addResp = await client.PostAsJsonAsync("/api/todos/add", new AddTodoHttpRequest(addCmd, "Ship Verifiable.NET", 100));
            addResp.EnsureSuccessStatusCode();
            var addReceipt = await addResp.Content.ReadFromJsonAsync<TodoCommitReceipt>();
            Assert.False(addReceipt.WasDuplicate);
            Assert.Equal(1, addReceipt.Board.ActiveCount);

            // 3. Idempotent replay with same CommandId
            var replayResp = await client.PostAsJsonAsync("/api/todos/add", new AddTodoHttpRequest(addCmd, "Ship Verifiable.NET", 100));
            replayResp.EnsureSuccessStatusCode();
            var replayReceipt = await replayResp.Content.ReadFromJsonAsync<TodoCommitReceipt>();
            Assert.True(replayReceipt.WasDuplicate);
            Assert.Equal(1, replayReceipt.Board.ActiveCount);

            // 4. Delete item, then attempt resurrection via Complete -> 409 Conflict
            var delResp = await client.PostAsJsonAsync("/api/todos/delete", new DeleteTodoHttpRequest(Guid.NewGuid(), 1, 200));
            delResp.EnsureSuccessStatusCode();

            var resurrectResp = await client.PostAsJsonAsync("/api/todos/complete", new CompleteTodoHttpRequest(Guid.NewGuid(), 1, 300));
            Assert.Equal(HttpStatusCode.Conflict, resurrectResp.StatusCode);

            // 5. GET board reflects exact state
            var board = await client.GetFromJsonAsync<TodoBoard>("/api/board");
            Assert.Equal(0, board.ActiveCount);
            Assert.Equal(1, board.DeletedCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (File.Exists(dbPath + "-wal")) File.Delete(dbPath + "-wal");
            if (File.Exists(dbPath + "-shm")) File.Delete(dbPath + "-shm");
        }
    }

    [Fact]
    public async Task AccountingAppServer_EndToEndDoubleEntry_ReplayParity_AndGuards()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"acct-http-{Guid.NewGuid():N}.db");
        try
        {
            await using var factory = new WebApplicationFactory<AccountingSrv::Program>()
                .WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["LedgerDb:ConnectionString"] = $"Data Source={dbPath}"
                    })));

            using var client = factory.CreateClient();

            // 1. Open Cash (Asset, no overdraft) & Equity (overdraft allowed to fund Cash)
            (await client.PostAsJsonAsync("/api/accounts/open",
                new OpenAccountHttpRequest(Guid.NewGuid(), 1, "Cash", AccountType.Asset, "USD", false, 1))).EnsureSuccessStatusCode();
            (await client.PostAsJsonAsync("/api/accounts/open",
                new OpenAccountHttpRequest(Guid.NewGuid(), 2, "Owner Equity", AccountType.Equity, "USD", true, 2))).EnsureSuccessStatusCode();
            (await client.PostAsJsonAsync("/api/accounts/open",
                new OpenAccountHttpRequest(Guid.NewGuid(), 3, "Cloud Hosting", AccountType.Expense, "USD", false, 3))).EnsureSuccessStatusCode();

            // 2. Fund Cash from Owner Equity ($500.00): Debit Cash, Credit Owner Equity
            var fundResp = await client.PostAsJsonAsync("/api/journal/post", new PostBalancedEntryHttpRequest(
                Guid.NewGuid(),
                [
                    new RawJournalLineInput(1, EntrySide.Debit, 500.00m),
                    new RawJournalLineInput(2, EntrySide.Credit, 500.00m)
                ],
                "Initial funding",
                10));
            fundResp.EnsureSuccessStatusCode();

            // 3. Reject invalid amount via [InboundPort]
            var badTransfer = await client.PostAsJsonAsync("/api/journal/transfer",
                new PostTransferHttpRequest(Guid.NewGuid(), 1, 3, -50m, "bad", 11));
            Assert.Equal(HttpStatusCode.BadRequest, badTransfer.StatusCode);

            // 4. Reject overdraft on Cash (> 500)
            var overdraft = await client.PostAsJsonAsync("/api/journal/transfer",
                new PostTransferHttpRequest(Guid.NewGuid(), 1, 3, 600m, "too much", 12));
            Assert.Equal(HttpStatusCode.Conflict, overdraft.StatusCode);

            // 5. Valid transfer ($125.50 from Cash -> Cloud Hosting)
            var txResp = await client.PostAsJsonAsync("/api/journal/transfer",
                new PostTransferHttpRequest(Guid.NewGuid(), 1, 3, 125.50m, "Hosting bill", 13));
            txResp.EnsureSuccessStatusCode();

            // 6. Reverse entry #2
            var revResp = await client.PostAsJsonAsync("/api/journal/reverse",
                new ReverseEntryHttpRequest(Guid.NewGuid(), 2, 14));
            revResp.EnsureSuccessStatusCode();

            // 7. Double-reversal must fail with 409 Conflict
            var revAgain = await client.PostAsJsonAsync("/api/journal/reverse",
                new ReverseEntryHttpRequest(Guid.NewGuid(), 2, 15));
            Assert.Equal(HttpStatusCode.Conflict, revAgain.StatusCode);

            // 8. Compare live ledger vs append-only journal replay
            var live = await client.GetFromJsonAsync<LedgerState>("/api/ledger");
            var replayed = await client.GetFromJsonAsync<LedgerState>("/api/ledger/replay");

            Assert.Equal(live.TotalDebits, live.TotalCredits);
            Assert.Equal(500.00m, live.Accounts[AccountId.From(1)].Balance);
            Assert.Equal(0m, live.Accounts[AccountId.From(3)].Balance);
            Assert.Equal(live.TotalDebits, replayed.TotalDebits);
            Assert.Equal(live.TotalCredits, replayed.TotalCredits);
            Assert.Equal(live.Journal.Count, replayed.Journal.Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (File.Exists(dbPath + "-wal")) File.Delete(dbPath + "-wal");
            if (File.Exists(dbPath + "-shm")) File.Delete(dbPath + "-shm");
        }
    }
}
