using Microsoft.Data.Sqlite;
using WavePOS.Core.Accounting;
using WavePOS.Core.Audit;
using WavePOS.Core.Inventory;
using WavePOS.Core.Models;
using WavePOS.Core.Returns;
using WavePOS.Core.Sales;

namespace WavePOS.Data.Tests;

public sealed class AccountingDataTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wavepos-books-{Guid.NewGuid():N}.db");
    private readonly PosDatabase _db;
    private readonly LedgerRepository _ledger;
    private readonly BankRepository _bank;
    private readonly Actor _ann;
    private readonly User _manager;
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    public AccountingDataTests()
    {
        _db = new PosDatabase(_path);
        _db.Initialize();
        _ledger = new LedgerRepository(_db);
        _bank = new BankRepository(_db);
        _manager = new UserRepository(_db).Create("ann", "Ann Phiri", "4321", UserRole.Admin);
        _ann = new Actor(_manager.Id, _manager.DisplayName);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private Product Product(string sku) => new ProductRepository(_db).GetAll().Single(p => p.Sku == sku);
    private Account Sys(string key) => _ledger.Accounts().Single(a => a.SystemKey == key);
    private decimal BalanceOf(string key) => _ledger.Balances(null, Today).Single(b => b.Account.SystemKey == key).Balance;

    private long Sell(PaymentMethod method, params (string Sku, int Qty)[] items)
    {
        var cart = new Cart();
        foreach (var (sku, qty) in items) cart.Add(Product(sku), qty);
        var sale = cart.Checkout(method, method == PaymentMethod.Cash ? 10_000m : cart.Total);
        sale.CashierId = _manager.Id;
        return new SaleRepository(_db).Save(sale, _ann);
    }

    /// <summary>The Inventory account must always equal the stock on hand at cost.</summary>
    private void AssertBooksMatchStock()
    {
        Assert.Equal(new InventoryRepository(_db).Summary().StockValue, BalanceOf(SystemAccounts.Inventory));
        Assert.True(_ledger.TrialBalance(Today).Balances);
        Assert.True(_ledger.BalanceSheet(Today).Balances);
    }

    [Fact]
    public void New_books_have_the_default_chart_and_opening_stock()
    {
        var accounts = _ledger.Accounts();
        Assert.Equal(DefaultChart.Accounts.Count, accounts.Count);
        Assert.Equal(2, accounts.Count(a => a.IsBank));

        var opening = Assert.Single(_ledger.Journals(new JournalFilter(Source: JournalSource.Opening)).Entries);
        Assert.Equal(new InventoryRepository(_db).Summary().StockValue, opening.Amount);
        AssertBooksMatchStock();

        // Opening the database again doesn't start the books twice.
        _db.Initialize();
        Assert.Single(_ledger.Journals(new JournalFilter(Source: JournalSource.Opening)).Entries);
    }

    [Fact]
    public void A_sale_posts_cash_sales_vat_and_cost_of_sales()
    {
        // Coca-Cola K11.60 incl. 16% VAT, cost K8.12; bread K25 zero-rated, cost K17.50.
        var cashBefore = BalanceOf(SystemAccounts.Cash);
        var saleId = Sell(PaymentMethod.Cash, ("DRK-001", 10), ("BRD-001", 2));

        Assert.Equal(166m, BalanceOf(SystemAccounts.Cash) - cashBefore);
        Assert.Equal(16m, BalanceOf(SystemAccounts.VatOutput));
        Assert.Equal(150m, BalanceOf(SystemAccounts.Sales));
        Assert.Equal(116.20m, BalanceOf(SystemAccounts.CostOfSales));
        var entry = Assert.Single(_ledger.Journals(new JournalFilter(Source: JournalSource.Sale)).Entries);
        Assert.Equal(saleId.ToString(), entry.SourceId);
        AssertBooksMatchStock();

        var pl = _ledger.ProfitAndLoss(Today, Today);
        Assert.Equal(150m - 116.20m, pl.GrossProfit);
    }

    [Fact]
    public void A_refund_reverses_the_sale_and_restocks_at_the_cost_it_sold_at()
    {
        var saleId = Sell(PaymentMethod.MobileMoney, ("DRK-001", 5));
        // The cost goes up after the sale; the refund still uses the cost it was sold at.
        var coke = Product("DRK-001");
        coke.Cost = 9m;
        new ProductRepository(_db).Update(coke, _ann);

        var line = new ReturnRepository(_db).GetReturnable(saleId)[0];
        new ReturnRepository(_db).Process(new ReturnRequest
        {
            SaleId = saleId,
            Lines = [new ReturnLineRequest(line.SaleLineId, 2, Restock: true)],
            Reason = ReturnReason.Faulty,
            RefundMethod = PaymentMethod.MobileMoney,
        }, _ann);

        Assert.Equal(34.80m, BalanceOf(SystemAccounts.MobileMoney));      // 58.00 in, 23.20 out
        Assert.Equal(-20m, BalanceOf(SystemAccounts.SalesReturns));       // shown as a negative income line
        Assert.Equal(4.80m, BalanceOf(SystemAccounts.VatOutput));
        Assert.Equal(3 * 8.12m, BalanceOf(SystemAccounts.CostOfSales));
        Assert.Single(_ledger.Journals(new JournalFilter(Source: JournalSource.Return)).Entries);
        AssertBooksMatchStock();
    }

    [Fact]
    public void Stock_deliveries_losses_counts_and_cost_changes_keep_inventory_in_step()
    {
        var inventory = new InventoryRepository(_db);
        var sugar = Product("GRO-003"); // cost K43.40, 100 on hand

        inventory.Receive(sugar.Id, 50, 46m, "Delivery from Zambia Sugar", _ann);
        Assert.Equal(2300m, BalanceOf(SystemAccounts.Payables));

        inventory.Adjust(sugar.Id, -3, StockReason.Damaged, "Torn bags", _ann);
        inventory.Count(sugar.Id, 140, "Month-end count", _ann);
        var rice = Product("GRO-004");
        rice.Cost = 38m; // cost down: stock worth less
        new ProductRepository(_db).Update(rice, _ann);
        new ProductRepository(_db).Create(new NewProductRequest("Polo Shirt", "Clothing", "POLO", 250m, 120m, 0.16m, 3, 4,
            ProductOption.Parse("Size", "S,M")), _ann);
        Sell(PaymentMethod.Card, ("GRO-003", 4), ("GRO-004", 1));

        Assert.True(BalanceOf(SystemAccounts.StockLosses) > 0);
        AssertBooksMatchStock();
    }

    [Fact]
    public void Manual_journals_post_reverse_and_are_audited()
    {
        var bank = Sys(SystemAccounts.Bank);
        var capital = Sys(SystemAccounts.OwnerCapital);
        var id = _ledger.PostJournal(new JournalDraft
        {
            Date = Today,
            Memo = "Owner put money into the business",
            Lines = [new(bank.Id, 5000m, 0), new(capital.Id, 0, 5000m)],
        }, _ann);

        Assert.Equal(5000m, BalanceOf(SystemAccounts.Bank));
        var reversal = _ledger.Reverse(id, Today, "Posted to the wrong account", _ann);
        Assert.Equal(0m, BalanceOf(SystemAccounts.Bank));
        Assert.Equal(reversal, _ledger.GetEntry(id)!.Summary.ReversedById);

        var ex = Assert.Throws<InvalidOperationException>(() => _ledger.Reverse(id, Today, "Again", _ann));
        Assert.Contains("Already reversed", ex.Message);

        var audit = new AuditRepository(_db);
        Assert.Equal(1, audit.Query(new AuditFilter(Action: AuditActions.JournalPosted)).Total);
        Assert.Equal(1, audit.Query(new AuditFilter(Action: AuditActions.JournalReversed)).Total);
        Assert.True(audit.Verify().IsIntact);
    }

    [Fact]
    public void Unbalanced_or_locked_journals_are_refused()
    {
        var cash = Sys(SystemAccounts.Cash).Id;
        var rent = _ledger.Accounts().Single(a => a.Code == "6000").Id;

        Assert.Throws<InvalidOperationException>(() => _ledger.PostJournal(new JournalDraft
        {
            Date = Today, Memo = "Rent", Lines = [new(rent, 100m, 0), new(cash, 0, 90m)],
        }, _ann));

        _ledger.SetLockDate(Today.AddDays(-1), _ann);
        var ex = Assert.Throws<InvalidOperationException>(() => _ledger.PostJournal(new JournalDraft
        {
            Date = Today.AddDays(-1), Memo = "Rent", Lines = [new(rent, 100m, 0), new(cash, 0, 100m)],
        }, _ann));
        Assert.Contains("locked", ex.Message);
        Assert.Throws<ArgumentException>(() => _ledger.SetLockDate(Today, _ann));
    }

    [Fact]
    public void Till_entries_cant_be_reversed_and_journals_cant_be_edited_in_the_database()
    {
        Sell(PaymentMethod.Cash, ("DRK-003", 1));
        var sale = _ledger.Journals(new JournalFilter(Source: JournalSource.Sale)).Entries[0];
        Assert.Throws<InvalidOperationException>(() => _ledger.Reverse(sale.Id, Today, "Mistake", _ann));

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE journal_lines SET debit_ngwee = 1 WHERE entry_id = $id;";
        cmd.Parameters.AddWithValue("$id", sale.Id);
        Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
        cmd.CommandText = "DELETE FROM journal_entries WHERE id = $id;";
        Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
    }

    [Fact]
    public void System_accounts_cant_be_switched_off_and_codes_are_unique()
    {
        var cash = Sys(SystemAccounts.Cash);
        cash.IsActive = false;
        Assert.Throws<InvalidOperationException>(() => _ledger.UpdateAccount(cash, _ann));

        var id = _ledger.CreateAccount(new Account { Code = "6100", Name = "Security guards", Type = AccountType.Expense }, _ann);
        Assert.Equal("Security guards", _ledger.GetAccount(id)!.Name);
        Assert.Throws<ArgumentException>(() =>
            _ledger.CreateAccount(new Account { Code = "6100", Name = "Duplicate", Type = AccountType.Expense }, _ann));
    }

    private const string Statement = """
        Date,Description,Reference,Debit,Credit,Balance
        {0},Cash deposit,DEP1,,500.00,500.00
        {0},Monthly service fee,,25.00,,475.00
        {0},ZESCO prepaid,Z99,116.00,,359.00
        {0},Unknown transfer,,10.00,,349.00
        {0},Reversal unknown transfer,,,10.00,359.00
        """;

    [Fact]
    public void Bank_statements_import_once_and_reconcile()
    {
        var bank = Sys(SystemAccounts.Bank);
        var csv = string.Format(Statement, Today.ToString("dd/MM/yyyy"));

        var first = _bank.Import(bank.Id, "zanaco-oct.csv", csv, _ann);
        Assert.Equal(5, first.Imported);
        var again = _bank.Import(bank.Id, "zanaco-oct.csv", csv, _ann);
        Assert.Equal(0, again.Imported);
        Assert.Equal(5, again.Duplicates);

        // Cash banked: the books already have it as a journal, so the line is suggested as a match.
        _ledger.PostJournal(new JournalDraft
        {
            Date = Today,
            Memo = "Takings banked",
            Lines = [new(bank.Id, 500m, 0), new(Sys(SystemAccounts.Cash).Id, 0, 500m)],
        }, _ann);
        var lines = _bank.Lines(bank.Id, BankLineStatus.Unmatched);
        var deposit = lines.Single(l => l.Amount == 500m);
        Assert.NotNull(deposit.Suggestion);
        _bank.Match(deposit.Id, deposit.Suggestion!.EntryId, _ann);

        // Fees and electricity aren't in the books yet: give them an account.
        _bank.Categorise(lines.Single(l => l.Amount == -25m).Id, Sys(SystemAccounts.BankCharges).Id, false, "", _ann);
        var zesco = lines.Single(l => l.Amount == -116m);
        var electricity = _ledger.Accounts().Single(a => a.Code == "6020");
        var zescoEntry = _bank.Categorise(zesco.Id, electricity.Id, includesVat: true, "", _ann);
        Assert.Equal(16m, BalanceOf(SystemAccounts.VatInput));

        _bank.Exclude(lines.Single(l => l.Amount == -10m).Id, "Bank error, reversed the same day", _ann);
        _bank.Exclude(lines.Single(l => l.Amount == 10m).Id, "Reversal of bank error", _ann);

        var summary = _bank.Summary(bank.Id);
        Assert.Equal(0, summary.UnmatchedCount);
        Assert.Equal(359m, summary.StatementBalance);
        Assert.Equal(359m, summary.LedgerBalance);
        Assert.Equal(0, summary.UnreconciledLedgerCount);

        // Reversing a bank posting puts the line back on the to-do list.
        _ledger.Reverse(zescoEntry, Today, "Wrong account", _ann);
        Assert.Equal(BankLineStatus.Unmatched, _bank.Lines(bank.Id).Single(l => l.Id == zesco.Id).Status);
        Assert.Equal(0m, BalanceOf(SystemAccounts.VatInput));
        Assert.True(_ledger.TrialBalance(Today).Balances);
    }

    [Fact]
    public void Only_bank_accounts_take_statements()
    {
        var csv = string.Format(Statement, Today.ToString("dd/MM/yyyy"));
        Assert.Throws<InvalidOperationException>(() => _bank.Import(Sys(SystemAccounts.Cash).Id, "x.csv", csv, _ann));
        Assert.Throws<ArgumentException>(() => _bank.Import(Sys(SystemAccounts.Bank).Id, "x.csv", "hello", _ann));
    }

    [Fact]
    public void Vat_summary_splits_standard_and_zero_rated_sales()
    {
        Sell(PaymentMethod.Cash, ("DRK-001", 10), ("BRD-001", 2)); // K116 at 16%, K50 at 0%
        var vat = _ledger.Vat(Today, Today);

        Assert.Equal(16m, vat.OutputVat);
        Assert.Equal(100m, vat.StandardRatedSales);
        Assert.Equal(50m, vat.ZeroRatedSales);
        Assert.Equal(16m, vat.NetPayable);
    }

    [Fact]
    public void Account_ledger_runs_a_balance()
    {
        Sell(PaymentMethod.Cash, ("DRK-003", 2));
        Sell(PaymentMethod.Cash, ("DRK-003", 3));
        var ledger = _ledger.Ledger(Sys(SystemAccounts.Cash).Id, Today, Today);

        Assert.Equal(0m, ledger.OpeningBalance);
        Assert.Equal([12m, 30m], ledger.Lines.Select(l => l.RunningBalance));
        Assert.Equal(30m, ledger.ClosingBalance);
    }
}
