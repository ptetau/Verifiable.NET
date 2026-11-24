using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Verifiable.Contracts;

namespace AccountingApp.Shared;

[TypeAlias<int>("Every account ID must be at least 1.")]
public readonly partial record struct AccountId { public static bool IsValid(int value) => value >= 1; }

[TypeAlias<int>("Journal entry IDs start at 1 and increment monotonically.")]
public readonly partial record struct JournalEntryId { public static bool IsValid(int value) => value >= 1; }

[TypeAlias<decimal>("Line-item amounts must be strictly positive (up to 1,000,000,000) to avoid zero or overflowing postings.")]
public readonly partial record struct PositiveAmount { public static bool IsValid(decimal value) => value > 0m && value <= 1000000000m; }

[TypeAlias<decimal>("Running ledger totals cannot go negative.")]
public readonly partial record struct NonNegativeDecimal { public static bool IsValid(decimal value) => value >= 0m; }

[TypeAlias<decimal>("Entry totals must be strictly positive so zero-value entries never enter the journal.")]
public readonly partial record struct PositiveDecimal { public static bool IsValid(decimal value) => value > 0m; }

[TypeAlias<string>("Currency codes must be 3-letter ISO codes such as USD.")]
public readonly partial record struct CurrencyCode { public static bool IsValid(string value) => value is { Length: >= 3 and <= 3 }; }

[TypeAlias<string>("Account names must be between 1 and 100 characters.")]
public readonly partial record struct AccountName { public static bool IsValid(string value) => value is { Length: >= 1 and <= 100 }; }

[TypeAlias<long>("Ledger timestamps cannot be negative.")]
public readonly partial record struct UnixTimestamp { public static bool IsValid(long value) => value >= 0; }

[TypeAlias<Guid>("Every ledger command requires a non-empty UUID for idempotent persistence.")]
public readonly partial record struct CommandId { public static bool IsValid(Guid value) => value != Guid.Empty; }

public enum AccountType
{
    Asset = 0,
    Liability = 1,
    Equity = 2,
    Revenue = 3,
    Expense = 4
}

public enum EntrySide
{
    Debit = 0,
    Credit = 1
}

[Verifiable]
public readonly record struct Account(
    AccountId Id,
    AccountName Name,
    AccountType Kind,
    CurrencyCode Currency,
    decimal Balance,
    bool AllowOverdraft);

[Verifiable]
public readonly record struct JournalLine(
    AccountId AccountId,
    EntrySide Side,
    PositiveAmount Amount);

[Verifiable]
public readonly record struct PostedJournalEntry(
    JournalEntryId Id,
    ImmutableList<JournalLine> Lines,
    PositiveDecimal EntryDebitTotal,
    PositiveDecimal EntryCreditTotal,
    string Memo,
    UnixTimestamp PostedAt,
    bool IsReversed,
    JournalEntryId? ReversesEntryId)
{
    [Invariant("Every posted journal entry must have equal debits and credits across at least two lines.")]
    public bool IsBalanced() => EntryDebitTotal == EntryCreditTotal && Lines.Count >= 2;
}

[Verifiable]
public readonly record struct LedgerState(
    ImmutableDictionary<AccountId, Account> Accounts,
    ImmutableList<PostedJournalEntry> Journal,
    JournalEntryId NextEntryId,
    NonNegativeDecimal TotalDebits,
    NonNegativeDecimal TotalCredits)
{
    [Invariant("Across the entire ledger, total debits must always equal total credits.")]
    public bool IsBalanced() => TotalDebits == TotalCredits;

    public static LedgerState CreateEmpty()
    {
        Spec.Ensures<LedgerState>(result =>
            result.TotalDebits == 0m
            && result.TotalCredits == 0m
            && result.NextEntryId == 1
            && result.Accounts.Count == 0
            && result.Journal.Count == 0,
            "An empty ledger starts balanced at zero with no accounts or entries and NextEntryId = 1.");

        return new(
            ImmutableDictionary<AccountId, Account>.Empty,
            ImmutableList<PostedJournalEntry>.Empty,
            JournalEntryId.From(1),
            NonNegativeDecimal.From(0m),
            NonNegativeDecimal.From(0m));
    }
}

[Verifiable]
public readonly record struct LedgerTransition(
    LedgerState State,
    bool IsSuccess,
    string? Error,
    JournalEntryId? CreatedEntryId);

[Verifiable]
public readonly record struct EntryTotals(NonNegativeDecimal DebitTotal, NonNegativeDecimal CreditTotal);

[Verifiable]
public readonly record struct AccountUpdateOutcome(
    ImmutableDictionary<AccountId, Account> Accounts,
    bool IsSuccess,
    string? Error);

[MustConsume]
public readonly record struct PendingLedgerCommit(
    CommandId CommandId,
    string OperationKind,
    LedgerState NextState,
    Account? OpenedAccount,
    PostedJournalEntry? AppendedEntry,
    JournalEntryId? ReversedEntryId,
    UnixTimestamp Timestamp);

[MustConsume]
public readonly record struct UnsentLedgerCommand(
    CommandId CommandId,
    string OperationKind,
    OpenAccountHttpRequest? OpenAccount,
    PostTransferHttpRequest? Transfer,
    PostBalancedEntryHttpRequest? BalancedEntry,
    ReverseEntryHttpRequest? Reverse);

[Verifiable]
public readonly record struct LedgerCommitReceipt(
    CommandId CommandId,
    LedgerState State,
    bool WasDuplicate);

[Verifiable]
public readonly record struct LedgerUiState(
    LedgerState State,
    bool IsSyncing,
    CommandId? PendingCommandId,
    string StatusText,
    string? LastError)
{
    public static LedgerUiState Initial() =>
        new(LedgerState.CreateEmpty(), false, null, "Ready", null);
}

public sealed record OpenAccountHttpRequest(
    Guid CommandId,
    int AccountId,
    string? Name,
    AccountType Kind,
    string? Currency,
    bool AllowOverdraft,
    long Timestamp);

public sealed record PostTransferHttpRequest(
    Guid CommandId,
    int FromAccountId,
    int ToAccountId,
    decimal Amount,
    string? Memo,
    long Timestamp);

public sealed record RawJournalLineInput(int AccountId, EntrySide Side, decimal Amount);

public sealed record PostBalancedEntryHttpRequest(
    Guid CommandId,
    List<RawJournalLineInput>? Lines,
    string? Memo,
    long Timestamp);

public sealed record ReverseEntryHttpRequest(
    Guid CommandId,
    int EntryId,
    long Timestamp);

public sealed record LedgerErrorResponse(string Message, ImmutableArray<ContractViolation> Violations);
