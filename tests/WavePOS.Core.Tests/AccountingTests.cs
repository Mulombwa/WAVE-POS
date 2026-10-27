using WavePOS.Core.Accounting;
using WavePOS.Core.Inventory;
using WavePOS.Core.Models;
using WavePOS.Core.Security;

namespace WavePOS.Core.Tests;

public class AccountingTests
{
    // Fake account ids: each system key gets its own number.
    private static readonly Dictionary<string, long> Ids = new();
    private static long Acct(string key) => Ids.TryGetValue(key, out var id) ? id : Ids[key] = Ids.Count + 1;

    private static readonly DateOnly Today = new(2026, 10, 27);

    private static decimal Debits(JournalDraft e, string key) => e.Lines.Where(l => l.AccountId == Acct(key)).Sum(l => l.Debit - l.Credit);

    // ---------- Journal rules ----------

    [Fact]
    public void A_balanced_journal_passes()
    {
        var e = new JournalDraft { Date = Today, Lines = [new(1, 100m, 0), new(2, 0, 100m)] };
        Assert.Null(JournalValidator.Check(e));
    }

    [Theory]
    [InlineData(100, 0, 0, 90, "must be equal")]
    [InlineData(100, 100, 0, 0, "not both")]
    [InlineData(-5, 0, 0, -5, "negative")]
    [InlineData(10.005, 0, 0, 10.005, "two decimal")]
    public void Bad_journals_are_rejected(double d1, double c1, double d2, double c2, string expected)
    {
        var e = new JournalDraft { Date = Today, Lines = [new(1, (decimal)d1, (decimal)c1), new(2, (decimal)d2, (decimal)c2)] };
        Assert.Contains(expected, JournalValidator.Check(e));
    }

    [Fact]
    public void Journals_need_two_lines_and_respect_the_lock_date()
    {
        Assert.Contains("two lines", JournalValidator.Check(new JournalDraft { Date = Today, Lines = [new(1, 5, 0)] }));
        var e = new JournalDraft { Date = new(2026, 9, 30), Lines = [new(1, 5, 0), new(2, 0, 5)] };
        Assert.Contains("locked", JournalValidator.Check(e, lockDate: new DateOnly(2026, 9, 30)));
        Assert.Null(JournalValidator.Check(e, lockDate: new DateOnly(2026, 9, 29)));
    }

    [Fact]
    public void Reversing_swaps_debits_and_credits()
    {
        var reversed = JournalValidator.Reverse([new(1, 40m, 0), new(2, 0, 40m)]);
        Assert.Equal(new JournalLineDraft(1, 0, 40m), reversed[0]);
        Assert.Equal(new JournalLineDraft(2, 40m, 0), reversed[1]);
    }

    // ---------- Posting rules ----------

    [Fact]
    public void A_card_sale_posts_takings_sales_vat_and_cost_of_sales()
    {
        var e = PostingRules.Sale(7, Today, PaymentMethod.Card, total: 116m, vat: 16m, cost: 70m, Acct);

        Assert.Null(JournalValidator.Check(e));
        Assert.Equal(116m, Debits(e, SystemAccounts.CardClearing));
        Assert.Equal(-100m, Debits(e, SystemAccounts.Sales));
        Assert.Equal(-16m, Debits(e, SystemAccounts.VatOutput));
        Assert.Equal(70m, Debits(e, SystemAccounts.CostOfSales));
        Assert.Equal(-70m, Debits(e, SystemAccounts.Inventory));
        Assert.Equal(JournalSource.Sale, e.Source);
        Assert.Equal("7", e.SourceId);
    }

    [Fact]
    public void A_zero_rated_sale_with_no_cost_has_no_vat_or_stock_lines()
    {
        var e = PostingRules.Sale(1, Today, PaymentMethod.Cash, 25m, 0m, 0m, Acct);
        Assert.Equal(2, e.Lines.Count);
        Assert.Null(JournalValidator.Check(e));
    }

    [Fact]
    public void A_refund_reverses_the_sale_and_restocks_at_cost()
    {
        var e = PostingRules.Refund(3, 7, Today, PaymentMethod.MobileMoney, 58m, 8m, restockedCost: 35m, Acct);

        Assert.Null(JournalValidator.Check(e));
        Assert.Equal(-58m, Debits(e, SystemAccounts.MobileMoney));
        Assert.Equal(50m, Debits(e, SystemAccounts.SalesReturns));
        Assert.Equal(8m, Debits(e, SystemAccounts.VatOutput));
        Assert.Equal(35m, Debits(e, SystemAccounts.Inventory));
        Assert.Equal(-35m, Debits(e, SystemAccounts.CostOfSales));
    }

    [Fact]
    public void Stock_changes_post_at_cost_to_the_right_account()
    {
        var received = PostingRules.StockMovement(1, "Sugar", Today, StockReason.Received, 10, 40m, Acct)!;
        Assert.Equal(400m, Debits(received, SystemAccounts.Inventory));
        Assert.Equal(-400m, Debits(received, SystemAccounts.Payables));

        var damaged = PostingRules.StockMovement(1, "Sugar", Today, StockReason.Damaged, -2, 40m, Acct)!;
        Assert.Equal(80m, Debits(damaged, SystemAccounts.StockLosses));
        Assert.Equal(-80m, Debits(damaged, SystemAccounts.Inventory));

        var found = PostingRules.StockMovement(1, "Sugar", Today, StockReason.StockCount, 1, 40m, Acct)!;
        Assert.Equal(-40m, Debits(found, SystemAccounts.StockLosses));

        var opening = PostingRules.StockMovement(1, "Sugar", Today, StockReason.InitialStock, 5, 40m, Acct)!;
        Assert.Equal(-200m, Debits(opening, SystemAccounts.OpeningBalances));

        Assert.Null(PostingRules.StockMovement(1, "Free sample", Today, StockReason.Damaged, -3, 0m, Acct));
        Assert.Throws<ArgumentException>(() => PostingRules.StockMovement(1, "Sugar", Today, StockReason.Sale, -1, 40m, Acct));
    }

    [Fact]
    public void A_cost_change_revalues_the_stock_on_hand()
    {
        var up = PostingRules.Revaluation(1, "Rice", Today, 50m, "cost K40.00 to K45.00 on 10 in stock", Acct)!;
        Assert.Equal(50m, Debits(up, SystemAccounts.Inventory));
        Assert.Equal(-50m, Debits(up, SystemAccounts.StockLosses));
        var down = PostingRules.Revaluation(1, "Rice", Today, -0.5m, "rounding", Acct)!;
        Assert.Equal(-0.5m, Debits(down, SystemAccounts.Inventory));
        Assert.Null(PostingRules.Revaluation(1, "Rice", Today, 0m, "no change", Acct));
    }

    [Fact]
    public void Bank_spending_with_vat_splits_out_input_vat()
    {
        const long bank = 900, rent = 901;
        var e = PostingRules.BankLine(5, Today, "ZESCO token", -116m, bank, rent, includesVat: true, Acct);

        Assert.Null(JournalValidator.Check(e));
        Assert.Equal(-116m, e.Lines.Where(l => l.AccountId == bank).Sum(l => l.Debit - l.Credit));
        Assert.Equal(100m, e.Lines.Where(l => l.AccountId == rent).Sum(l => l.Debit - l.Credit));
        Assert.Equal(16m, Debits(e, SystemAccounts.VatInput));

        var income = PostingRules.BankLine(6, Today, "Interest", 25m, bank, rent, includesVat: false, Acct);
        Assert.Equal(2, income.Lines.Count);
        Assert.Equal(25m, income.Lines.Single(l => l.AccountId == bank).Debit);
    }

    [Fact]
    public void Vat_included_is_sixteen_over_one_hundred_and_sixteen() =>
        Assert.Equal(16m, PostingRules.VatIncluded(116m));

    // ---------- Reports ----------

    private static Account A(string code, AccountType type) => new() { Id = int.Parse(code), Code = code, Name = code, Type = type };

    private static List<AccountBalance> SampleBooks() =>
    [
        new(A("1000", AccountType.Asset), 1160m, 0),        // cash from sales
        new(A("1200", AccountType.Asset), 1000m, 700m),     // stock bought 1000, sold 700
        new(A("2000", AccountType.Liability), 0, 1000m),    // owed to supplier
        new(A("2100", AccountType.Liability), 0, 160m),     // VAT on sales
        new(A("4000", AccountType.Income), 0, 1000m),       // sales
        new(A("5000", AccountType.Expense), 700m, 0),       // cost of sales
        new(A("6000", AccountType.Expense), 0, 0),          // no rent: left out
    ];

    [Fact]
    public void Trial_balance_debits_equal_credits()
    {
        var tb = FinancialReports.TrialBalance(Today, SampleBooks());
        Assert.True(tb.Balances);
        Assert.Equal(2160m, tb.TotalDebit);
        Assert.DoesNotContain(tb.Rows, r => r.Account.Code == "6000");
        Assert.Equal(300m, tb.Rows.Single(r => r.Account.Code == "1200").Debit);
    }

    [Fact]
    public void Profit_and_loss_shows_gross_and_net_profit()
    {
        var books = SampleBooks();
        books.Add(new(A("6010", AccountType.Expense), 120m, 0)); // wages
        var pl = FinancialReports.ProfitAndLoss(Today, Today, books);

        Assert.Equal(1000m, pl.Income.Total);
        Assert.Equal(700m, pl.CostOfSales.Total);
        Assert.Equal(300m, pl.GrossProfit);
        Assert.Equal(180m, pl.NetProfit);
        Assert.Equal(0.3m, pl.GrossMargin);
    }

    [Fact]
    public void Balance_sheet_balances_with_profit_to_date_in_equity()
    {
        var bs = FinancialReports.BalanceSheet(Today, SampleBooks());
        Assert.Equal(1460m, bs.Assets.Total);
        Assert.Equal(1160m, bs.Liabilities.Total);
        Assert.Equal(300m, bs.ProfitToDate);
        Assert.True(bs.Balances);
    }

    // ---------- Bank statements ----------

    [Fact]
    public void Reads_a_debit_credit_statement_with_a_preamble()
    {
        const string csv = """
            Zanaco Business Account,,,,
            Account number: 0123456789,,,,
            Date,Value Date,Narration,Debit,Credit,Balance
            ,,Opening balance,,,"1,000.00"
            27/10/2026,27/10/2026,POS settlement VISA,,"2,320.00","3,320.00"
            28/10/2026,28/10/2026,"ZESCO prepaid, shop",116.00,,"3,204.00"
            29/10/2026 14:05,29/10/2026,Bank charges,(15.00),,"3,189.00"
            """;
        var result = BankStatementParser.Parse(csv);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(3, result.Lines.Count);
        Assert.Equal(1, result.SkippedRows);
        Assert.Equal(new StatementLine(new(2026, 10, 27), "POS settlement VISA", 2320m, "", 3320m), result.Lines[0]);
        Assert.Equal(-116m, result.Lines[1].Amount);
        Assert.Equal("ZESCO prepaid, shop", result.Lines[1].Description);
        Assert.Equal(new DateOnly(2026, 10, 29), result.Lines[2].Date);
        Assert.Equal(-15m, result.Lines[2].Amount);
    }

    [Fact]
    public void Reads_a_signed_amount_statement_with_semicolons()
    {
        const string csv = "Transaction Date;Details;Reference;Amount (ZMW)\n2026-10-01;Airtel Money cash out;MP123;-250.50\n01-Oct-2026;Deposit;;K1,000\n";
        var result = BankStatementParser.Parse(csv);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(-250.50m, result.Lines[0].Amount);
        Assert.Equal("MP123", result.Lines[0].Reference);
        Assert.Equal(1000m, result.Lines[1].Amount);
        Assert.Equal(new DateOnly(2026, 10, 1), result.Lines[1].Date);
    }

    [Theory]
    [InlineData("K1,234.50", 1234.50)]
    [InlineData("ZMW -20", -20)]
    [InlineData("(150.00)", -150)]
    [InlineData("300.00 CR", 300)]
    [InlineData("45.00 DR", -45)]
    [InlineData("12.00-", -12)]
    public void Reads_bank_amount_formats(string text, double expected) =>
        Assert.Equal((decimal)expected, BankStatementParser.ParseAmount(text));

    [Fact]
    public void Says_so_when_the_file_isnt_a_statement()
    {
        Assert.False(BankStatementParser.Parse("name,email\nMary,m@example.com").Ok);
        Assert.False(BankStatementParser.Parse("").Ok);
    }

    [Fact]
    public void Identical_lines_get_different_fingerprints_but_reimports_match()
    {
        var airtime = new StatementLine(Today, "Airtime", -20m);
        var first = BankStatementParser.Fingerprints([airtime, airtime]);
        var again = BankStatementParser.Fingerprints([airtime, airtime]);

        Assert.NotEqual(first[0], first[1]);
        Assert.Equal(first, again);
    }

    [Fact]
    public void Matcher_picks_the_same_amount_closest_in_date_within_a_week()
    {
        LedgerCandidate[] candidates =
        [
            new(1, Today.AddDays(-5), 500m, "", ""),
            new(2, Today.AddDays(-1), 500m, "", ""),
            new(3, Today, 499m, "", ""),
            new(4, Today.AddDays(-20), 500m, "", ""),
        ];
        Assert.Equal(2, BankMatcher.Suggest(Today, 500m, candidates)!.EntryId);
        Assert.Null(BankMatcher.Suggest(Today.AddDays(30), 500m, candidates));
    }

    // ---------- Accountant role ----------

    [Fact]
    public void Accountants_keep_the_books_but_dont_sell()
    {
        Assert.True(UserRole.Accountant.Can(Permission.ViewAccounts));
        Assert.True(UserRole.Accountant.Can(Permission.ManageAccounts));
        Assert.True(UserRole.Accountant.Can(Permission.ViewAuditLog));
        Assert.False(UserRole.Accountant.Can(Permission.Sell));
        Assert.False(UserRole.Accountant.Can(Permission.ManageStaff));
        Assert.Equal("/accounting", UserRole.Accountant.HomePage());

        Assert.True(UserRole.Manager.Can(Permission.ManageAccounts));
        Assert.False(UserRole.Supervisor.Can(Permission.ViewAccounts));
        Assert.Equal("/", UserRole.Cashier.HomePage());
    }
}
