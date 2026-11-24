using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Verifiable.Contracts;

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

[Refined<int>("Value >= 1")]
public readonly partial record struct AccountId;

[Refined<int>("Value >= 1")]
public readonly partial record struct JournalEntryId;

[Refined<decimal>("Value > 0m && Value <= 1000000000m")]
public readonly partial record struct PositiveAmount;

[Refined<string>("Length is >= 3 and <= 3")]
public readonly partial record struct CurrencyCode;

[Refined<string>("Length is >= 1 and <= 100")]
public readonly partial record struct AccountName;

[Refined<long>("Value >= 0")]
public readonly partial record struct UnixTimestamp;

[Refined<Guid>("Value != Guid.Empty")]
public readonly partial record struct CommandId;

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
[Invariant("EntryDebitTotal == EntryCreditTotal && EntryDebitTotal > 0m && Lines.Count >= 2")]
public readonly record struct PostedJournalEntry(
    JournalEntryId Id,
    ImmutableList<JournalLine> Lines,
    decimal EntryDebitTotal,
    decimal EntryCreditTotal,
    string Memo,
    UnixTimestamp PostedAt,
    bool IsReversed,
    JournalEntryId? ReversesEntryId);

[Verifiable]
[Invariant("TotalDebits == TotalCredits")]
[Invariant("TotalDebits >= 0m")]
[Invariant("NextEntryId.Value >= 1")]
public readonly record struct LedgerState(
    ImmutableDictionary<AccountId, Account> Accounts,
    ImmutableList<PostedJournalEntry> Journal,
    JournalEntryId NextEntryId,
    decimal TotalDebits,
    decimal TotalCredits)
{
    [Verifiable]
    [Ensures("result.TotalDebits == 0m && result.TotalCredits == 0m && result.NextEntryId.Value == 1 && result.Accounts.Count == 0 && result.Journal.Count == 0")]
    public static LedgerState CreateEmpty() => new(
        ImmutableDictionary<AccountId, Account>.Empty,
        ImmutableList<PostedJournalEntry>.Empty,
        JournalEntryId.From(1),
        0m,
        0m);
}

[Verifiable]
public readonly record struct LedgerTransition(
    LedgerState State,
    bool IsSuccess,
    string? Error,
    JournalEntryId? CreatedEntryId);

[Verifiable]
public readonly record struct EntryTotals(decimal DebitTotal, decimal CreditTotal);

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
    [Verifiable]
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
