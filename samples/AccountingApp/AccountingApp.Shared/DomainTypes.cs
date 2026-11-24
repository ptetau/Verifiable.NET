using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Verifiable.Contracts;

[assembly: TypeAlias<int>("AccountId", "Value >= 1", Namespace = "AccountingApp.Shared")]
[assembly: TypeAlias<int>("JournalEntryId", "Value >= 1", Namespace = "AccountingApp.Shared")]
[assembly: TypeAlias<decimal>("PositiveAmount", "Value > 0m && Value <= 1000000000m", Namespace = "AccountingApp.Shared")]
[assembly: TypeAlias<decimal>("NonNegativeDecimal", "Value >= 0m", Namespace = "AccountingApp.Shared")]
[assembly: TypeAlias<decimal>("PositiveDecimal", "Value > 0m", Namespace = "AccountingApp.Shared")]
[assembly: TypeAlias<string>("CurrencyCode", "Length is >= 3 and <= 3", Namespace = "AccountingApp.Shared")]
[assembly: TypeAlias<string>("AccountName", "Length is >= 1 and <= 100", Namespace = "AccountingApp.Shared")]
[assembly: TypeAlias<long>("UnixTimestamp", "Value >= 0", Namespace = "AccountingApp.Shared")]
[assembly: TypeAlias<Guid>("CommandId", "Value != Guid.Empty", Namespace = "AccountingApp.Shared")]

namespace AccountingApp.Shared;

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
[Invariant("EntryDebitTotal == EntryCreditTotal && Lines.Count >= 2")]
public readonly record struct PostedJournalEntry(
    JournalEntryId Id,
    ImmutableList<JournalLine> Lines,
    PositiveDecimal EntryDebitTotal,
    PositiveDecimal EntryCreditTotal,
    string Memo,
    UnixTimestamp PostedAt,
    bool IsReversed,
    JournalEntryId? ReversesEntryId);

[Verifiable]
[Invariant("TotalDebits == TotalCredits")]
public readonly record struct LedgerState(
    ImmutableDictionary<AccountId, Account> Accounts,
    ImmutableList<PostedJournalEntry> Journal,
    JournalEntryId NextEntryId,
    NonNegativeDecimal TotalDebits,
    NonNegativeDecimal TotalCredits)
{
    [Ensures("result.TotalDebits == 0m && result.TotalCredits == 0m && result.NextEntryId == 1 && result.Accounts.Count == 0 && result.Journal.Count == 0")]
    public static LedgerState CreateEmpty() => new(
        ImmutableDictionary<AccountId, Account>.Empty,
        ImmutableList<PostedJournalEntry>.Empty,
        JournalEntryId.From(1),
        NonNegativeDecimal.From(0m),
        NonNegativeDecimal.From(0m));
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
