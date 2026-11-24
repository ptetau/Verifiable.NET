using System.Collections.Immutable;
using Verifiable.Contracts;

namespace AccountingApp.Shared;

[Verifiable]
public static class LedgerEngine
{
    public static decimal SignedBalanceDelta(AccountType kind, EntrySide side, decimal amount)
    {
        if (kind == AccountType.Asset || kind == AccountType.Expense)
        {
            return side == EntrySide.Debit ? amount : -amount;
        }
        return side == EntrySide.Credit ? amount : -amount;
    }

    public static LedgerState PostCheckedAssetTransfer(
        LedgerState state,
        AccountId from,
        AccountId to,
        PositiveAmount amount,
        string memo,
        UnixTimestamp now)
    {
        Spec.Requires(
            from != to && state.Accounts.ContainsKey(from) && state.Accounts.ContainsKey(to) && state.Accounts[from].Balance >= amount,
            "Transfers require two distinct existing accounts and enough balance in the source account to avoid overdraft.");
        Spec.Ensures<LedgerState>(
            result => result.Accounts.ContainsKey(from) && result.Accounts[from].Balance >= 0m,
            "After the transfer, the source account remains non-negative.");
        Spec.Ensures<LedgerState>(
            result => result.TotalDebits == result.TotalCredits,
            "Total debits still equal total credits across the ledger.");
        Spec.Ensures<LedgerState>(
            result => result.TotalDebits == state.TotalDebits + amount,
            "Total debits grow by the exact transfer amount.");

        var fromAcc = state.Accounts[from];
        var toAcc = state.Accounts[to];

        var updatedFrom = fromAcc with { Balance = fromAcc.Balance - amount };
        var updatedTo = toAcc with { Balance = toAcc.Balance + amount };
        var nextAccounts = state.Accounts
            .SetItem(from, updatedFrom)
            .SetItem(to, updatedTo);

        var debitLine = new JournalLine(to, EntrySide.Debit, amount);
        var creditLine = new JournalLine(from, EntrySide.Credit, amount);
        var lines = ImmutableList<JournalLine>.Empty.Add(debitLine).Add(creditLine);

        var entryTotal = PositiveDecimal.From(amount);
        var entry = new PostedJournalEntry(
            state.NextEntryId,
            lines,
            entryTotal,
            entryTotal,
            memo,
            now,
            false,
            null);

        return state with
        {
            Accounts = nextAccounts,
            Journal = state.Journal.Add(entry),
            NextEntryId = JournalEntryId.From(state.NextEntryId + 1),
            TotalDebits = NonNegativeDecimal.From(state.TotalDebits + amount),
            TotalCredits = NonNegativeDecimal.From(state.TotalCredits + amount)
        };
    }

    [InboundPort]
    public static LedgerTransition OpenAccount(
        LedgerState state,
        AccountId id,
        AccountName name,
        AccountType kind,
        CurrencyCode currency,
        bool allowOverdraft)
    {
        Spec.Ensures<LedgerTransition>(
            result => !result.IsSuccess || (result.State.TotalDebits == state.TotalDebits && result.State.TotalCredits == state.TotalCredits),
            "Opening an account preserves total debits and credits.");
        Spec.Ensures<LedgerTransition>(
            result => result.IsSuccess || result.State == state,
            "A rejected OpenAccount leaves the ledger unchanged.");

        if (state.Accounts.ContainsKey(id))
        {
            return new LedgerTransition(state, false, "Account already exists.", null);
        }

        var acc = new Account(id, name, kind, currency, 0m, allowOverdraft);
        var nextState = state with { Accounts = state.Accounts.Add(id, acc) };
        return new LedgerTransition(nextState, true, null, null);
    }

    [InboundPort]
    public static LedgerTransition PostTransfer(
        LedgerState state,
        AccountId from,
        AccountId to,
        PositiveAmount amount,
        string memo,
        UnixTimestamp now)
    {
        Spec.Ensures<LedgerTransition>(
            result => result.State.TotalDebits == result.State.TotalCredits,
            "Every transfer keeps total debits equal to total credits.");
        Spec.Ensures<LedgerTransition>(
            result => !result.IsSuccess || result.State.TotalDebits == state.TotalDebits + amount,
            "A successful transfer increases total debits by the exact transfer amount.");
        Spec.Ensures<LedgerTransition>(
            result => result.IsSuccess || result.State == state,
            "A rejected transfer leaves the ledger completely unchanged.");

        if (from == to)
        {
            return new LedgerTransition(state, false, "Source and destination accounts must differ.", null);
        }

        if (!state.Accounts.ContainsKey(from) || !state.Accounts.ContainsKey(to))
        {
            return new LedgerTransition(state, false, "Account not found.", null);
        }

        var fromAcc = state.Accounts[from];
        var toAcc = state.Accounts[to];

        if (fromAcc.Currency != toAcc.Currency)
        {
            return new LedgerTransition(state, false, "Currency mismatch between accounts.", null);
        }

        var fromDelta = SignedBalanceDelta(fromAcc.Kind, EntrySide.Credit, amount);
        var toDelta = SignedBalanceDelta(toAcc.Kind, EntrySide.Debit, amount);
        var newFromBalance = fromAcc.Balance + fromDelta;
        var newToBalance = toAcc.Balance + toDelta;

        if (!fromAcc.AllowOverdraft && newFromBalance < 0m)
        {
            return new LedgerTransition(state, false, "Insufficient funds in source account.", null);
        }

        if (!toAcc.AllowOverdraft && newToBalance < 0m)
        {
            return new LedgerTransition(state, false, "Overdraft guard violated on destination account.", null);
        }

        var nextAccounts = state.Accounts
            .SetItem(from, fromAcc with { Balance = newFromBalance })
            .SetItem(to, toAcc with { Balance = newToBalance });

        var lines = ImmutableList<JournalLine>.Empty
            .Add(new JournalLine(to, EntrySide.Debit, amount))
            .Add(new JournalLine(from, EntrySide.Credit, amount));

        var entryTotal = PositiveDecimal.From(amount);
        var entry = new PostedJournalEntry(
            state.NextEntryId,
            lines,
            entryTotal,
            entryTotal,
            memo,
            now,
            false,
            null);

        var nextState = state with
        {
            Accounts = nextAccounts,
            Journal = state.Journal.Add(entry),
            NextEntryId = JournalEntryId.From(state.NextEntryId + 1),
            TotalDebits = NonNegativeDecimal.From(state.TotalDebits + amount),
            TotalCredits = NonNegativeDecimal.From(state.TotalCredits + amount)
        };

        return new LedgerTransition(nextState, true, null, entry.Id);
    }

    public static EntryTotals SumEntrySides(ImmutableList<JournalLine> lines)
    {
        var debits = 0m;
        var credits = 0m;
        var i = 0;
        while (i < lines.Count)
        {
            Spec.LoopInvariant(i >= 0 && i <= lines.Count && debits >= 0m && credits >= 0m, "Index stays in range and running debit/credit sums never go negative.");
            Spec.Decreases(lines.Count - i, "Remaining journal lines shrink by one on each iteration.");

            var line = lines[i];
            if (line.Amount > 0m)
            {
                if (line.Side == EntrySide.Debit)
                {
                    debits = debits + line.Amount;
                }
                else
                {
                    credits = credits + line.Amount;
                }
            }
            i = i + 1;
        }
        return new EntryTotals(NonNegativeDecimal.From(debits), NonNegativeDecimal.From(credits));
    }

    public static AccountUpdateOutcome ApplyLinesToAccounts(
        ImmutableDictionary<AccountId, Account> accounts,
        ImmutableList<JournalLine> lines,
        CurrencyCode expectedCurrency)
    {
        var cur = accounts;
        var i = 0;
        while (i < lines.Count)
        {
            Spec.LoopInvariant(i >= 0 && i <= lines.Count, "Loop index stays within the journal lines collection.");
            Spec.Decreases(lines.Count - i, "Remaining lines shrink by one on each iteration.");

            var line = lines[i];
            if (!cur.ContainsKey(line.AccountId))
            {
                return new AccountUpdateOutcome(accounts, false, "Account referenced in journal line does not exist.");
            }

            var acc = cur[line.AccountId];
            if (acc.Currency != expectedCurrency)
            {
                return new AccountUpdateOutcome(accounts, false, "Journal entry mixes currencies.");
            }

            var nextBal = acc.Balance + SignedBalanceDelta(acc.Kind, line.Side, line.Amount);
            if (!acc.AllowOverdraft && nextBal < 0m)
            {
                return new AccountUpdateOutcome(accounts, false, "Overdraft guard violated on account.");
            }

            cur = cur.SetItem(line.AccountId, acc with { Balance = nextBal });
            i = i + 1;
        }

        return new AccountUpdateOutcome(cur, true, null);
    }

    [InboundPort]
    public static LedgerTransition PostBalancedEntry(
        LedgerState state,
        ImmutableList<JournalLine> lines,
        string memo,
        UnixTimestamp now)
    {
        Spec.Ensures<LedgerTransition>(
            result => result.State.TotalDebits == result.State.TotalCredits,
            "Any multi-line entry leaves total debits equal to total credits.");
        Spec.Ensures<LedgerTransition>(
            result => result.IsSuccess || result.State == state,
            "A rejected entry leaves the ledger completely unchanged.");

        if (lines.Count < 2)
        {
            return new LedgerTransition(state, false, "Journal entry requires at least 2 lines.", null);
        }

        var firstAccountId = lines[0].AccountId;
        if (!state.Accounts.ContainsKey(firstAccountId))
        {
            return new LedgerTransition(state, false, "Primary account does not exist.", null);
        }

        var totals = SumEntrySides(lines);
        if (totals.DebitTotal <= 0m || totals.DebitTotal != totals.CreditTotal)
        {
            return new LedgerTransition(state, false, "Journal entry debits and credits must balance and be positive.", null);
        }

        var currency = state.Accounts[firstAccountId].Currency;
        var applied = ApplyLinesToAccounts(state.Accounts, lines, currency);
        if (!applied.IsSuccess)
        {
            return new LedgerTransition(state, false, applied.Error, null);
        }

        var entry = new PostedJournalEntry(
            state.NextEntryId,
            lines,
            PositiveDecimal.From(totals.DebitTotal),
            PositiveDecimal.From(totals.CreditTotal),
            memo,
            now,
            false,
            null);

        var nextState = state with
        {
            Accounts = applied.Accounts,
            Journal = state.Journal.Add(entry),
            NextEntryId = JournalEntryId.From(state.NextEntryId + 1),
            TotalDebits = NonNegativeDecimal.From(state.TotalDebits + totals.DebitTotal),
            TotalCredits = NonNegativeDecimal.From(state.TotalCredits + totals.CreditTotal)
        };

        return new LedgerTransition(nextState, true, null, entry.Id);
    }

    public static int FindJournalIndex(ImmutableList<PostedJournalEntry> journal, JournalEntryId entryId)
    {
        Spec.Ensures<int>(result => result >= -1 && result < journal.Count, "Returns either -1 (not found) or a valid index into the journal list.");

        var i = 0;
        while (i < journal.Count)
        {
            Spec.LoopInvariant(i >= 0 && i <= journal.Count, "Search index stays within [0, journal.Count].");
            Spec.Decreases(journal.Count - i, "Remaining journal entries to inspect shrink by one per step.");

            if (journal[i].Id == entryId)
            {
                return i;
            }
            i = i + 1;
        }
        return -1;
    }

    public static ImmutableList<JournalLine> FlipEntryLines(ImmutableList<JournalLine> lines)
    {
        Spec.Ensures<ImmutableList<JournalLine>>(result => result.Count == lines.Count, "Flipping debit/credit sides preserves the exact number of lines.");

        var flipped = ImmutableList<JournalLine>.Empty;
        var i = 0;
        while (i < lines.Count)
        {
            Spec.LoopInvariant(i >= 0 && i <= lines.Count && flipped.Count == i, "Flipped output length matches loop index i within bounds.");
            Spec.Decreases(lines.Count - i, "Remaining lines shrink by one on each iteration.");

            var line = lines[i];
            var oppSide = line.Side == EntrySide.Debit ? EntrySide.Credit : EntrySide.Debit;
            flipped = flipped.Add(line with { Side = oppSide });
            i = i + 1;
        }
        return flipped;
    }

    [InboundPort]
    public static LedgerTransition ReverseEntry(
        LedgerState state,
        JournalEntryId entryId,
        UnixTimestamp now)
    {
        Spec.Ensures<LedgerTransition>(
            result => result.State.TotalDebits == result.State.TotalCredits,
            "Reversing an entry keeps total debits equal to total credits.");
        Spec.Ensures<LedgerTransition>(
            result => result.IsSuccess || result.State == state,
            "A rejected reversal leaves the ledger completely unchanged.");

        var idx = FindJournalIndex(state.Journal, entryId);
        if (idx < 0)
        {
            return new LedgerTransition(state, false, "Journal entry not found.", null);
        }

        var orig = state.Journal[idx];
        if (orig.IsReversed)
        {
            return new LedgerTransition(state, false, "Journal entry has already been reversed.", null);
        }

        if (orig.Lines.Count < 2)
        {
            return new LedgerTransition(state, false, "Original journal entry is invalid.", null);
        }

        var firstAccId = orig.Lines[0].AccountId;
        if (!state.Accounts.ContainsKey(firstAccId))
        {
            return new LedgerTransition(state, false, "Original account not found.", null);
        }

        var flippedLines = FlipEntryLines(orig.Lines);
        var currency = state.Accounts[firstAccId].Currency;
        var applied = ApplyLinesToAccounts(state.Accounts, flippedLines, currency);
        if (!applied.IsSuccess)
        {
            return new LedgerTransition(state, false, applied.Error, null);
        }

        var reversalEntry = new PostedJournalEntry(
            state.NextEntryId,
            flippedLines,
            orig.EntryCreditTotal,
            orig.EntryDebitTotal,
            "Reversal of #" + orig.Id.Value,
            now,
            false,
            orig.Id);

        var updatedJournal = state.Journal
            .SetItem(idx, orig with { IsReversed = true })
            .Add(reversalEntry);

        var nextState = state with
        {
            Accounts = applied.Accounts,
            Journal = updatedJournal,
            NextEntryId = JournalEntryId.From(state.NextEntryId + 1),
            TotalDebits = NonNegativeDecimal.From(state.TotalDebits + orig.EntryCreditTotal),
            TotalCredits = NonNegativeDecimal.From(state.TotalCredits + orig.EntryDebitTotal)
        };

        return new LedgerTransition(nextState, true, null, reversalEntry.Id);
    }

    [Unverifiable]
    public static PendingLedgerCommit CreatePendingCommit(
        CommandId commandId,
        string operationKind,
        LedgerState nextState,
        Account? openedAccount,
        PostedJournalEntry? appendedEntry,
        JournalEntryId? reversedEntryId,
        UnixTimestamp timestamp) =>
        new(commandId, operationKind, nextState, openedAccount, appendedEntry, reversedEntryId, timestamp);
}

[Verifiable]
public static class LedgerUiEngine
{
    [Unverifiable]
    public static (LedgerUiState State, UnsentLedgerCommand Command) OnSubmitOpenAccount(
        LedgerUiState state,
        OpenAccountHttpRequest req,
        CommandId commandId)
    {
        var next = state with { IsSyncing = true, PendingCommandId = commandId, StatusText = "Syncing...", LastError = null };
        var cmd = new UnsentLedgerCommand(commandId, "open", req with { CommandId = commandId.Value }, null, null, null);
        return (next, cmd);
    }

    [Unverifiable]
    public static (LedgerUiState State, UnsentLedgerCommand Command) OnSubmitTransfer(
        LedgerUiState state,
        PostTransferHttpRequest req,
        CommandId commandId)
    {
        var next = state with { IsSyncing = true, PendingCommandId = commandId, StatusText = "Syncing...", LastError = null };
        var cmd = new UnsentLedgerCommand(commandId, "transfer", null, req with { CommandId = commandId.Value }, null, null);
        return (next, cmd);
    }

    [Unverifiable]
    public static (LedgerUiState State, UnsentLedgerCommand Command) OnSubmitReverse(
        LedgerUiState state,
        ReverseEntryHttpRequest req,
        CommandId commandId)
    {
        var next = state with { IsSyncing = true, PendingCommandId = commandId, StatusText = "Syncing...", LastError = null };
        var cmd = new UnsentLedgerCommand(commandId, "reverse", null, null, null, req with { CommandId = commandId.Value });
        return (next, cmd);
    }

    public static LedgerUiState OnCommitConfirmed(LedgerUiState state, LedgerCommitReceipt receipt)
    {
        Spec.Requires(
            state.IsSyncing && state.PendingCommandId.HasValue && receipt.CommandId == state.PendingCommandId.Value,
            "Confirmation receipts must match the currently syncing command ID.");
        Spec.Ensures<LedgerUiState>(
            result => !result.IsSyncing && !result.PendingCommandId.HasValue,
            "Confirming a commit clears syncing state and pending command ID.");

        return state with
        {
            State = receipt.State,
            IsSyncing = false,
            PendingCommandId = null,
            StatusText = "Saved",
            LastError = null
        };
    }

    public static LedgerUiState OnCommitFailed(LedgerUiState state, string error) =>
        state with
        {
            IsSyncing = false,
            PendingCommandId = null,
            StatusText = "Error",
            LastError = error
        };
}
