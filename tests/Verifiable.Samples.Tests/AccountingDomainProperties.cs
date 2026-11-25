using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using AccountingApp.Shared;
using GalaxyCheck;
using GalaxyCheck.Gens;
using Xunit;

namespace Verifiable.Samples.Tests;

public sealed class AccountingDomainProperties
{
    private static LedgerState CreateFundedCashAndRevenueAccounts(decimal initialCashCents)
    {
        var s = LedgerState.CreateEmpty();
        s = LedgerEngine.OpenAccount(s, AccountId.From(1), AccountName.From("Cash"), AccountType.Asset, CurrencyCode.From("USD"), false).State;
        s = LedgerEngine.OpenAccount(s, AccountId.From(2), AccountName.From("Owner Equity"), AccountType.Equity, CurrencyCode.From("USD"), true).State;
        s = LedgerEngine.OpenAccount(s, AccountId.From(3), AccountName.From("Merchant Payout"), AccountType.Asset, CurrencyCode.From("USD"), false).State;
        s = LedgerEngine.OpenAccount(s, AccountId.From(4), AccountName.From("Platform Fee"), AccountType.Revenue, CurrencyCode.From("USD"), true).State;

        if (initialCashCents > 0m)
        {
            s = LedgerEngine.PostTransfer(
                s,
                AccountId.From(2),
                AccountId.From(1),
                PositiveAmount.From(initialCashCents),
                "Initial equity capitalization",
                UnixTimestamp.From(1)).State;
        }
        return s;
    }

    [Property(Iterations = 25)]
    public Property DoubleEntryOperations_AlwaysPreserveBalanceAndNonNegativeDebits() =>
        (from seedCash in Gen.Int32().Between(100, 5000)
         from amounts in Gen.Int32().Between(10, 500).ListOf().WithCountBetween(1, 8)
         select (seedCash, amounts)).ForAll(x =>
        {
            var s = CreateFundedCashAndRevenueAccounts(x.seedCash);
            long ts = 10;

            foreach (var rawAmt in x.amounts)
            {
                ts++;
                var now = UnixTimestamp.From(ts);
                var tr = LedgerEngine.PostTransfer(s, AccountId.From(1), AccountId.From(3), PositiveAmount.From(rawAmt), "Payout", now);
                if (!tr.State.IsBalanced()) return false;
                if (!tr.IsSuccess && tr.State != s) return false;
                s = tr.State;
            }

            if (s.Journal.Count > 1)
            {
                var rev = LedgerEngine.ReverseEntry(s, s.Journal[^1].Id, UnixTimestamp.From(ts + 1));
                if (!rev.State.IsBalanced()) return false;
                s = rev.State;
            }

            var trial = LedgerEngine.ComputeTrialBalance(s.Accounts.Values.ToImmutableList());
            return s.IsBalanced()
                && trial.AccountCount == s.Accounts.Count
                && trial.AssetExpenseTotal == trial.LiabilityEquityRevenueTotal;
        });

    [Property(Iterations = 25)]
    public Property FeeSplitTransfer_DistributesNetAndFeeWithoutRoundingLoss() =>
        (from gross in Gen.Int32().Between(10, 2000)
         from fee in Gen.Int32().Between(1, 2000)
         select (gross, fee)).ForAll(x =>
        {
            var s = CreateFundedCashAndRevenueAccounts(5000m);
            var revCreditBefore = s.Accounts[AccountId.From(4)].Balance;
            var recipientBefore = s.Accounts[AccountId.From(3)].Balance;
            var cashBefore = s.Accounts[AccountId.From(1)].Balance;

            var tr = LedgerEngine.PostFeeSplitTransfer(
                s,
                AccountId.From(1),
                AccountId.From(3),
                AccountId.From(4),
                PositiveAmount.From(x.gross),
                PositiveAmount.From(x.fee),
                "Marketplace split",
                UnixTimestamp.From(50));

            if (x.fee >= x.gross)
            {
                return !tr.IsSuccess && tr.State == s;
            }

            var expectedNet = (decimal)x.gross - x.fee;
            var feeDelta = s.Accounts[AccountId.From(4)].Balance - tr.State.Accounts[AccountId.From(4)].Balance;
            return tr.IsSuccess
                && tr.State.IsBalanced()
                && tr.State.TotalDebits == s.TotalDebits + x.gross
                && tr.State.Accounts[AccountId.From(1)].Balance == cashBefore - x.gross
                && tr.State.Accounts[AccountId.From(3)].Balance == recipientBefore + expectedNet
                && feeDelta == x.fee
                && revCreditBefore == s.Accounts[AccountId.From(4)].Balance;
        });

    [Property(Iterations = 20)]
    public Property CurrencyMismatch_IsRejectedWithoutChangingState() =>
        (from amount in Gen.Int32().Between(1, 500)
         select amount).ForAll(amount =>
        {
            var s = LedgerState.CreateEmpty();
            s = LedgerEngine.OpenAccount(s, AccountId.From(1), AccountName.From("USD Account"), AccountType.Equity, CurrencyCode.From("USD"), true).State;
            s = LedgerEngine.OpenAccount(s, AccountId.From(2), AccountName.From("EUR Account"), AccountType.Asset, CurrencyCode.From("EUR"), false).State;
            s = LedgerEngine.OpenAccount(s, AccountId.From(3), AccountName.From("USD Fees"), AccountType.Expense, CurrencyCode.From("USD"), false).State;

            var twoLeg = LedgerEngine.PostTransfer(s, AccountId.From(1), AccountId.From(2), PositiveAmount.From(amount), "FX fail", UnixTimestamp.From(1));
            var threeLeg = LedgerEngine.PostFeeSplitTransfer(s, AccountId.From(1), AccountId.From(2), AccountId.From(3), PositiveAmount.From(amount + 10m), PositiveAmount.From(1m), "FX split fail", UnixTimestamp.From(2));

            return !twoLeg.IsSuccess && twoLeg.State == s
                && !threeLeg.IsSuccess && threeLeg.State == s;
        });

    [Property(Iterations = 20)]
    public Property NonOverdraftAccount_NeverDropsBelowZero() =>
        (from funded in Gen.Int32().Between(1, 500)
         from attempt in Gen.Int32().Between(1, 1000)
         select (funded, attempt)).ForAll(x =>
        {
            var s = CreateFundedCashAndRevenueAccounts(x.funded);
            var tr = LedgerEngine.PostTransfer(s, AccountId.From(1), AccountId.From(3), PositiveAmount.From(x.attempt), "Withdraw", UnixTimestamp.From(5));

            if (x.attempt > x.funded)
            {
                return !tr.IsSuccess && tr.State == s && tr.State.Accounts[AccountId.From(1)].Balance == x.funded;
            }

            return tr.IsSuccess && tr.State.Accounts[AccountId.From(1)].Balance == x.funded - x.attempt;
        });

    [Property(Iterations = 20)]
    public Property Reversal_RestoresOriginalAccountBalancesAndRejectsDoubleReversal() =>
        (from amt in Gen.Int32().Between(1, 500)
         select amt).ForAll(amt =>
        {
            var s0 = CreateFundedCashAndRevenueAccounts(1000m);
            var tr = LedgerEngine.PostTransfer(s0, AccountId.From(1), AccountId.From(3), PositiveAmount.From(amt), "Pay", UnixTimestamp.From(10));
            var entryId = tr.CreatedEntryId!.Value;
            var rev1 = LedgerEngine.ReverseEntry(tr.State, entryId, UnixTimestamp.From(11));
            var rev2 = LedgerEngine.ReverseEntry(rev1.State, entryId, UnixTimestamp.From(12));

            return rev1.IsSuccess
                && rev1.State.IsBalanced()
                && rev1.State.Accounts[AccountId.From(1)].Balance == s0.Accounts[AccountId.From(1)].Balance
                && rev1.State.Accounts[AccountId.From(3)].Balance == s0.Accounts[AccountId.From(3)].Balance
                && !rev2.IsSuccess
                && rev2.State == rev1.State;
        });

    [Fact]
    public void InboundPort_RejectsZeroFeeAndShortCurrencyWithSpiritMessages()
    {
        var raw = new RawLedgerState
        {
            NextEntryId = 1,
            TotalDebits = 0m,
            TotalCredits = 0m,
            Accounts = new Dictionary<int, RawAccount>(),
            Journal = new List<RawPostedJournalEntry>()
        };

        var badCurr = LedgerEnginePort.OpenAccount(raw, 1, "Cash", AccountType.Asset, "US", false);
        Assert.False(badCurr.IsValid);
        Assert.Contains(badCurr.Violations, v => v.Message.Contains("3-letter ISO codes"));

        var badSplit = LedgerEnginePort.PostFeeSplitTransfer(raw, 1, 2, 3, 100m, 0m, "bad", 1);
        Assert.False(badSplit.IsValid);
        Assert.Contains(badSplit.Violations, v => v.Message.Contains("strictly positive"));
    }
}
