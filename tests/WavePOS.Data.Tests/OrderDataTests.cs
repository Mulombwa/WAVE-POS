using Microsoft.Data.Sqlite;
using WavePOS.Core.Accounting;
using WavePOS.Core.Audit;
using WavePOS.Core.Models;
using WavePOS.Core.Orders;

namespace WavePOS.Data.Tests;

public sealed class OrderDataTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wavepos-orders-{Guid.NewGuid():N}.db");
    private readonly PosDatabase _db;
    private readonly PurchasingRepository _buy;
    private readonly SalesOrderRepository _sell;
    private readonly LedgerRepository _ledger;
    private readonly Actor _ann, _ben, _sam;
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    public OrderDataTests()
    {
        _db = new PosDatabase(_path);
        _db.Initialize();
        _buy = new PurchasingRepository(_db);
        _sell = new SalesOrderRepository(_db);
        _ledger = new LedgerRepository(_db);
        var users = new UserRepository(_db);
        _ann = ActorFor(users.Create("ann", "Ann Phiri", "4321", UserRole.Manager));
        _ben = ActorFor(users.Create("ben", "Ben Zulu", "4321", UserRole.Manager));
        _sam = ActorFor(users.Create("sam", "Sam Mwale", "4321", UserRole.Supervisor));
    }

    private static Actor ActorFor(User u) => new(u.Id, u.DisplayName);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private Product Product(string sku) => new ProductRepository(_db).GetAll().Single(p => p.Sku == sku);
    private decimal Balance(string key) => _ledger.Balances(null, Today).Single(b => b.Account.SystemKey == key).Balance;
    private long AccountId(string key) => _ledger.Accounts().Single(a => a.SystemKey == key).Id;

    private void AssertBooksBalanceAndMatchStock()
    {
        Assert.True(_ledger.TrialBalance(Today).Balances);
        Assert.True(_ledger.BalanceSheet(Today).Balances);
        Assert.Equal(new InventoryRepository(_db).Summary().StockValue, Balance(SystemAccounts.Inventory));
    }

    private long Supplier(int terms = 30) =>
        _buy.SaveSupplier(new Supplier { Name = "Zambia Sugar", Tpin = "1000123456", PaymentTermsDays = terms }, _sam);

    [Fact]
    public void Procure_to_pay_end_to_end()
    {
        var sugar = Product("GRO-003"); // cost K43.40, 100 in stock
        var supplier = Supplier();
        var po = _buy.CreateOrder(supplier, Today.AddDays(3), "", [new PoLineInput(sugar.Id, 20, 46m)], _sam);
        Assert.Equal(PurchaseOrderStatus.Approved, _buy.Submit(po, _sam)); // K1,067.20 is within the K5,000 limit
        var line = _buy.GetOrder(po)!.Lines[0];

        _buy.Receive(po, new Dictionary<long, int> { [line.Id] = 15 }, "First load", _sam);
        Assert.Equal(PurchaseOrderStatus.PartlyReceived, _buy.GetOrder(po)!.Status);
        Assert.Equal(115, Product("GRO-003").StockQuantity);
        Assert.Equal(690m, Balance(SystemAccounts.GoodsReceived));
        Assert.Contains(sugar.Id, _buy.OnOrder().Keys);

        var bill1 = _buy.EnterBill(po, "ZS-1001", Today, _ann);
        Assert.Equal(0m, Balance(SystemAccounts.GoodsReceived));
        Assert.Equal(800.40m, Balance(SystemAccounts.Payables)); // 690 + 16% VAT
        Assert.Equal(110.40m, Balance(SystemAccounts.VatInput));
        Assert.Throws<InvalidOperationException>(() => _buy.EnterBill(po, "ZS-1002", Today, _ann)); // nothing new received

        _buy.Receive(po, new Dictionary<long, int> { [line.Id] = 5 }, "", _sam);
        Assert.Equal(PurchaseOrderStatus.Received, _buy.GetOrder(po)!.Status);
        Assert.Throws<InvalidOperationException>(() => _buy.Receive(po, new Dictionary<long, int> { [line.Id] = 1 }, "", _sam));
        _buy.EnterBill(po, "ZS-1002", Today, _ann);

        var bank = AccountId(SystemAccounts.Bank);
        _buy.PayBill(bill1, 500m, Today, bank, "EFT 1", _ann);
        var bill = _buy.Bills(poId: po).Single(b => b.Id == bill1);
        Assert.Equal(300.40m, bill.Outstanding);
        Assert.Equal(Today.AddDays(30), bill.DueDate);
        Assert.Contains("still owed", Assert.Throws<InvalidOperationException>(() => _buy.PayBill(bill1, 300.41m, Today, bank, "", _ann)).Message);
        Assert.Contains("bank", Assert.Throws<InvalidOperationException>(() =>
            _buy.PayBill(bill1, 1m, Today, AccountId(SystemAccounts.Sales), "", _ann)).Message); // can't pay from a sales account
        _buy.PayBill(bill1, 300.40m, Today, bank, "EFT 2", _ann);

        Assert.Equal(266.80m, Balance(SystemAccounts.Payables)); // second bill still owed
        Assert.Equal(-800.40m, Balance(SystemAccounts.Bank));
        Assert.Equal(266.80m, _buy.GetSupplier(supplier)!.Balance);
        Assert.Equal(2, _buy.Payments(po).Count);
        Assert.Equal(2, _buy.Receipts(po).Count);
        AssertBooksBalanceAndMatchStock();
    }

    [Fact]
    public void Big_orders_wait_for_another_manager()
    {
        var supplier = Supplier();
        var rice = Product("GRO-004");
        var po = _buy.CreateOrder(supplier, null, "", [new PoLineInput(rice.Id, 200, 40m)], _ann); // K9,280 incl. VAT
        Assert.Equal(PurchaseOrderStatus.AwaitingApproval, _buy.Submit(po, _ann));

        var line = _buy.GetOrder(po)!.Lines[0];
        Assert.Throws<InvalidOperationException>(() => _buy.Receive(po, new Dictionary<long, int> { [line.Id] = 1 }, "", _sam));
        Assert.Contains("aren't allowed", Assert.Throws<InvalidOperationException>(() => _buy.Approve(po, _sam)).Message);
        Assert.Contains("another manager", Assert.Throws<InvalidOperationException>(() => _buy.Approve(po, _ann)).Message);

        _buy.Approve(po, _ben);
        var approved = _buy.GetOrder(po)!;
        Assert.Equal(PurchaseOrderStatus.Approved, approved.Status);
        Assert.Equal("Ben Zulu", approved.ApprovedByName);
        Assert.Equal(1, new AuditRepository(_db).Query(new AuditFilter(Action: AuditActions.PurchaseOrderApproved)).Total);
    }

    [Fact]
    public void The_only_manager_can_approve_their_own_order_and_it_is_flagged()
    {
        new UserRepository(_db).Update(new User { Id = _ben.UserId!.Value, Username = "ben", DisplayName = "Ben Zulu", Role = UserRole.Manager, IsActive = false }, _ann);
        _buy.SetApprovalLimit(0m, _ann);
        var po = _buy.CreateOrder(Supplier(), null, "", [new PoLineInput(Product("GRO-004").Id, 1, 40m)], _ann);
        Assert.Equal(PurchaseOrderStatus.AwaitingApproval, _buy.Submit(po, _ann));
        _buy.Approve(po, _ann);
        var entry = new AuditRepository(_db).Query(new AuditFilter(Action: AuditActions.PurchaseOrderApproved)).Entries.Single();
        Assert.Contains("only person who can approve", entry.Details);
    }

    [Fact]
    public void Rejected_orders_go_back_to_draft_and_orders_with_receipts_cant_be_cancelled()
    {
        _buy.SetApprovalLimit(100m, _ann);
        var po = _buy.CreateOrder(Supplier(), null, "", [new PoLineInput(Product("GRO-004").Id, 10, 40m)], _sam);
        _buy.Submit(po, _sam);
        _buy.Reject(po, "Buy from the cheaper supplier", _ann);
        Assert.Equal(PurchaseOrderStatus.Draft, _buy.GetOrder(po)!.Status);

        _buy.UpdateDraft(po, _buy.GetOrder(po)!.SupplierId, null, "", [new PoLineInput(Product("GRO-004").Id, 2, 40m)], _sam);
        _buy.Submit(po, _sam);
        Assert.Equal(PurchaseOrderStatus.Approved, _buy.GetOrder(po)!.Status);
        var line = _buy.GetOrder(po)!.Lines[0];
        _buy.Receive(po, new Dictionary<long, int> { [line.Id] = 1 }, "", _sam);
        Assert.Throws<InvalidOperationException>(() => _buy.Cancel(po, "Changed mind", _ann));
    }

    [Fact]
    public void Reorder_suggestions_count_stock_already_on_order()
    {
        var bread = Product("BRD-001");
        new InventoryRepository(_db).Count(bread.Id, 4, "Count", _ann); // reorder level 10
        var suggestion = _buy.ReorderSuggestions().Single(s => s.Product.Id == bread.Id);
        Assert.Equal(16, suggestion.Suggested);

        var po = _buy.CreateOrder(Supplier(), null, "", [new PoLineInput(bread.Id, 5, 17.5m)], _sam);
        _buy.Submit(po, _sam);
        Assert.Equal(11, _buy.ReorderSuggestions().Single(s => s.Product.Id == bread.Id).Suggested); // 4 + 5 on order → top up to 20

        var more = _buy.CreateOrder(Supplier(), null, "", [new PoLineInput(bread.Id, 5, 17.5m)], _sam);
        _buy.Submit(more, _sam);
        Assert.DoesNotContain(_buy.ReorderSuggestions(), s => s.Product.Id == bread.Id); // 14 expected, above the level
    }

    private long Customer(decimal? limit = null, int terms = 14)
    {
        var c = new CustomerRepository(_db).Create(new Customer { Name = "Lusaka Lodge", Phone = "0977000000" }, _ann);
        _sell.SetCreditTerms(c.Id, new CreditTerms("2000123456", limit, terms), _ann);
        return c.Id;
    }

    [Fact]
    public void Order_to_cash_end_to_end()
    {
        var coke = Product("DRK-001"); // K11.60 incl. VAT, cost K8.12
        var bread = Product("BRD-001"); // K25.00 zero-rated, cost K17.50
        var customer = Customer();
        var order = _sell.CreateQuote(customer, Today.AddDays(14), "", [new SoLineInput(coke.Id, 24, 11m), new SoLineInput(bread.Id, 10, 25m)], _sam);
        Assert.Equal(SalesOrderStatus.Quote, _sell.GetOrder(order)!.Status);
        _sell.Confirm(order, _sam);
        Assert.Equal(24, _sell.Availability([coke.Id])[coke.Id].Reserved);

        var lines = _sell.GetOrder(order)!.Lines;
        _sell.Deliver(order, new Dictionary<long, int> { [lines[0].Id] = 24, [lines[1].Id] = 4 }, "Van 1", _sam);
        Assert.Equal(SalesOrderStatus.PartlyDelivered, _sell.GetOrder(order)!.Status);
        Assert.Equal(76, Product("DRK-001").StockQuantity);
        Assert.Equal(24 * 8.12m + 4 * 17.50m, Balance(SystemAccounts.CostOfSales));

        var invoice = _sell.Invoice(order, Today, _ann);
        var inv = _sell.Invoices(orderId: order).Single();
        Assert.Equal(364m, inv.Total);          // 24 × 11 + 4 × 25
        Assert.Equal(36.41m, inv.Vat);          // 264 × 16/116
        Assert.Equal(Today.AddDays(14), inv.DueDate);
        Assert.Equal(364m, Balance(SystemAccounts.Receivables));
        Assert.Equal(36.41m, Balance(SystemAccounts.VatOutput));
        Assert.Throws<InvalidOperationException>(() => _sell.Invoice(order, Today, _ann)); // nothing new delivered

        _sell.ReceivePayment(invoice, 364m, Today, AccountId(SystemAccounts.MobileMoney), "MoMo 55", _ann);
        Assert.Equal(0m, Balance(SystemAccounts.Receivables));
        Assert.Equal(364m, Balance(SystemAccounts.MobileMoney));
        Assert.Throws<InvalidOperationException>(() => _sell.ReceivePayment(invoice, 1m, Today, AccountId(SystemAccounts.Cash), "", _ann));

        var vat = _ledger.Vat(Today, Today);
        Assert.Equal(227.59m, vat.StandardRatedSales); // 264 − 36.41
        Assert.Equal(100m, vat.ZeroRatedSales);
        AssertBooksBalanceAndMatchStock();
    }

    [Fact]
    public void Credit_limit_needs_a_manager_override_with_a_reason()
    {
        var customer = Customer(limit: 100m);
        var order = _sell.CreateQuote(customer, null, "", [new SoLineInput(Product("DRK-002").Id, 2, 69m)], _sam); // K138

        Assert.Throws<CreditLimitException>(() => _sell.Confirm(order, _sam));
        Assert.Contains("Ask a manager", Assert.Throws<CreditLimitException>(() => _sell.Confirm(order, _sam, "Good customer")).Message);
        _sell.Confirm(order, _ann, "Paying cash on delivery");

        Assert.Equal(SalesOrderStatus.Confirmed, _sell.GetOrder(order)!.Status);
        Assert.Contains("Paying cash on delivery", _sell.GetOrder(order)!.CreditOverride);
        Assert.Equal(1, new AuditRepository(_db).Query(new AuditFilter(Action: AuditActions.CreditOverride)).Total);
        Assert.Equal(138m, _sell.Exposure(customer));
    }

    [Fact]
    public void Cant_deliver_more_than_is_in_stock_or_change_a_confirmed_order()
    {
        var water = Product("DRK-003");
        var order = _sell.CreateQuote(Customer(), null, "", [new SoLineInput(water.Id, 150, 6m)], _sam);
        _sell.Confirm(order, _sam);
        var line = _sell.GetOrder(order)!.Lines[0];

        Assert.Contains("Only 100", Assert.Throws<InvalidOperationException>(() =>
            _sell.Deliver(order, new Dictionary<long, int> { [line.Id] = 150 }, "", _sam)).Message);
        Assert.Throws<InvalidOperationException>(() =>
            _sell.UpdateQuote(order, _sell.GetOrder(order)!.CustomerId, null, "", [new SoLineInput(water.Id, 1, 6m)], _sam));
        _sell.Deliver(order, new Dictionary<long, int> { [line.Id] = 100 }, "", _sam);
        Assert.Throws<InvalidOperationException>(() => _sell.Cancel(order, "No longer needed", _ann));
        AssertBooksBalanceAndMatchStock();
    }

    [Fact]
    public void Older_databases_get_the_new_goods_received_account()
    {
        // The business already used code 2010 for something else, with postings: a new account is added beside it.
        var old = _ledger.Accounts().Single(a => a.SystemKey == SystemAccounts.GoodsReceived);
        _ledger.PostJournal(new JournalDraft
        {
            Date = Today, Memo = "Accrual",
            Lines = [new(AccountId(SystemAccounts.Cash), 10m, 0), new(old.Id, 0, 10m)],
        }, _ann);
        using (var conn = _db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE accounts SET system_key = NULL, name = 'Supplier accruals' WHERE system_key = 'goods_received';";
            cmd.ExecuteNonQuery();
        }
        _db.Initialize();
        var added = _ledger.Accounts().Single(a => a.SystemKey == SystemAccounts.GoodsReceived);
        Assert.Equal("2010-1", added.Code); // 2010 was taken by an account in use
        Assert.Equal("Supplier accruals", _ledger.GetAccount(old.Id)!.Name);
    }
}
