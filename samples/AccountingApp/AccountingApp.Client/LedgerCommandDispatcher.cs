using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using AccountingApp.Shared;
using Verifiable.Contracts;

namespace AccountingApp.Client;

public sealed class LedgerCommandDispatcher(HttpClient http)
{
    public async Task<Result<LedgerState, string>> LoadLedgerAsync(CancellationToken ct = default)
    {
        try
        {
            var state = await http.GetFromJsonAsync<LedgerState>("/api/ledger", ct);
            return Result<LedgerState, string>.Ok(state);
        }
        catch (Exception ex)
        {
            return Result<LedgerState, string>.Fail(ex.Message);
        }
    }

    [ConsumesObligation]
    public async Task<Result<LedgerCommitReceipt, string>> DispatchAsync(
        UnsentLedgerCommand command,
        CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage response = command.OperationKind switch
            {
                "open" when command.OpenAccount is not null =>
                    await http.PostAsJsonAsync("/api/accounts/open", command.OpenAccount, ct),
                "transfer" when command.Transfer is not null =>
                    await http.PostAsJsonAsync("/api/journal/transfer", command.Transfer, ct),
                "post" when command.BalancedEntry is not null =>
                    await http.PostAsJsonAsync("/api/journal/post", command.BalancedEntry, ct),
                "reverse" when command.Reverse is not null =>
                    await http.PostAsJsonAsync("/api/journal/reverse", command.Reverse, ct),
                _ => throw new InvalidOperationException($"Invalid ledger command '{command.OperationKind}'.")
            };

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                return Result<LedgerCommitReceipt, string>.Fail(err);
            }

            var receipt = await response.Content.ReadFromJsonAsync<LedgerCommitReceipt>(ct);
            return Result<LedgerCommitReceipt, string>.Ok(receipt);
        }
        catch (Exception ex)
        {
            return Result<LedgerCommitReceipt, string>.Fail(ex.Message);
        }
    }
}
