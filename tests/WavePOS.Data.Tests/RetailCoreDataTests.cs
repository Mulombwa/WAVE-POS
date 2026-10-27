using Microsoft.Data.Sqlite;
using WavePOS.Core.Audit;
using WavePOS.Core.Inventory;
using WavePOS.Core.Models;
using WavePOS.Core.Returns;
using WavePOS.Core.Sales;

namespace WavePOS.Data.Tests;

public sealed class RetailCoreDataTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wavepos-test-{Guid.NewGuid():N}.db");
    private readonly PosDatabase _db;
    private readonly Actor _mary;
    private readonly User _manager;

    public RetailCoreDataTests()
    {
        _db = new PosDatabase(_path);
        _db.Initialize();
        _manager = new UserRepository(_db).Create("mary", "Mary Banda", "4321", UserRole.Admin);
        _mary = new Actor(_manager.Id, _manager.DisplayName);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private long Sell(Product product, int qty, long? customerId = null)
    {
        var cart = new Cart();
        cart.Add(product, qty);
        var sale = cart.Checkout(PaymentMethod.Cash, 100_000m);
        sale.CashierId = _manager.Id;
        sale.CustomerId = customerId;
        return new SaleRepository(_db).Save(sale, _mary);
    }

    // ---------- Inventory ----------

    [Fact]
    public void Creating_a_product_with_sizes_and_colours_makes_one_variant_each()
    {
        var created = new ProductRepository(_db).Create(new NewProductRequest(
            "Polo Shirt", "Clothing", "POLO", 250m, 120m, 0.16m, ReorderLevel: 3, OpeningStock: 5,
            ProductOption.Parse("Size", "S,M,L"), ProductOption.Parse("Colour", "Red,Blue")), _mary);

        Assert.Equal(6, created.Count);
        Assert.All(created, p => Assert.Equal(5, p.StockQuantity));
        Assert.Single(created.Select(p => p.FamilyId).Distinct());
        Assert.Contains(created, p => p.Sku == "POLO-M-BLUE" && p.Option1 == "M" && p.Option2 == "Blue");
        Assert.Equal(6, new ProductRepository(_db).GetFamily(created[0].FamilyId!.Value).Count);
        Assert.Single(new InventoryRepository(_db).History(created[0].Id));
    }

    [Fact]
    public void Receiving_stock_averages_the_cost()
    {
        var inventory = new InventoryRepository(_db);
        var p = new ProductRepository(_db).Create(new NewProductRequest("Torch", "Hardware", "", 100m, 40m, 0.16m, 2, 10), _mary)[0];

        var balance = inventory.Receive(p.Id, 10, 60m, "Delivery from supplier", _mary, "INV-55");

        Assert.Equal(20, balance);
        Assert.Equal(50m, new ProductRepository(_db).GetById(p.Id)!.Cost); // (10×40 + 10×60) / 20
        Assert.Equal(StockReason.Received, inventory.History(p.Id)[0].Reason);
    }

    [Fact]
    public void Adjustments_and_counts_go_through_the_ledger()
    {
        var inventory = new InventoryRepository(_db);
        var bread = new ProductRepository(_db).GetAll().First(p => p.Name.StartsWith("White Bread"));

        Assert.Equal(98, inventory.Adjust(bread.Id, -2, StockReason.Damaged, "Dropped", _mary));
        Assert.Equal(90, inventory.Count(bread.Id, 90, "Weekly count", _mary));
        Assert.Throws<ArgumentException>(() => inventory.Adjust(bread.Id, 0, StockReason.Damaged, "", _mary));
        Assert.Throws<ArgumentException>(() => inventory.Adjust(bread.Id, 5, StockReason.Sale, "", _mary));

        var history = inventory.History(bread.Id);
        Assert.Equal(new[] { StockReason.StockCount, StockReason.Damaged, StockReason.InitialStock }, history.Select(h => h.Reason));
        Assert.Equal(-8, history[0].Change);
    }

    [Fact]
    public void Sales_reduce_stock_through_the_ledger_and_low_stock_shows_up()
    {
        var bread = new ProductRepository(_db).GetAll().First(p => p.Name.StartsWith("White Bread"));
        Sell(bread, 92);

        var after = new ProductRepository(_db).GetById(bread.Id)!;
        Assert.Equal(8, after.StockQuantity);
        Assert.True(after.IsLowStock);
        Assert.Equal(StockReason.Sale, new InventoryRepository(_db).History(bread.Id)[0].Reason);
        Assert.Contains(new ProductRepository(_db).ListInventory(lowStockOnly: true), p => p.Id == bread.Id);
        Assert.Equal(1, new InventoryRepository(_db).Summary().LowStockCount);
    }

    [Fact]
    public void Price_change_is_audited_with_old_and_new_value()
    {
        var products = new ProductRepository(_db);
        var p = products.GetAll()[0];
        p.Price += 5m;
        products.Update(p, _mary);

        var (entries, _) = new AuditRepository(_db).Query(new AuditFilter(Action: AuditActions.ProductUpdated));
        Assert.Contains("price", Assert.Single(entries).Details);
    }

    // ---------- Customers ----------

    [Fact]
    public void Customer_is_linked_to_sales_and_receipts()
    {
        var customers = new CustomerRepository(_db);
        var c = customers.Create(new Customer { Name = "Chanda Mwila", Phone = "0977000001", MarketingConsent = true }, _mary);
        var bread = new ProductRepository(_db).GetAll()[0];
        var saleId = Sell(bread, 2, c.Id);

        Assert.Equal("C00001", c.Code);
        Assert.NotNull(c.ConsentDate);
        var profile = customers.Get(c.Id)!;
        Assert.Equal(1, profile.Visits);
        Assert.Equal(bread.Price * 2, profile.TotalSpent);
        Assert.Equal("Chanda Mwila", new SaleRepository(_db).GetReceipt(saleId)!.CustomerName);
        Assert.Single(customers.Search("0977"));
    }

    [Fact]
    public void Anonymising_removes_personal_data_but_keeps_sales()
    {
        var customers = new CustomerRepository(_db);
        var c = customers.Create(new Customer { Name = "Chanda Mwila", Phone = "0977000001", Email = "c@x.zm" }, _mary);
        var saleId = Sell(new ProductRepository(_db).GetAll()[0], 1, c.Id);

        var export = customers.ExportData(c.Id, _mary);
        Assert.Contains("Chanda Mwila", export);
        Assert.Contains(saleId.ToString("D6"), export);

        customers.Anonymise(c.Id, _mary);

        var after = customers.Get(c.Id)!;
        Assert.True(after.IsAnonymised);
        Assert.Equal("", after.Phone);
        Assert.Empty(customers.Search("Chanda"));
        Assert.Null(new SaleRepository(_db).GetReceipt(saleId)!.CustomerName);
        Assert.Equal(1, after.Visits); // sale kept
        Assert.Throws<InvalidOperationException>(() => customers.Update(after, _mary));

        var actions = new AuditRepository(_db).Query(new AuditFilter(EntityType: "customer")).Entries.Select(e => e.Action).ToList();
        Assert.Contains(AuditActions.CustomerExported, actions);
        Assert.Contains(AuditActions.CustomerAnonymised, actions);
    }

    // ---------- Returns ----------

    [Fact]
    public void Partial_return_restocks_and_cannot_exceed_what_was_sold()
    {
        var products = new ProductRepository(_db);
        var coke = products.GetAll().First(p => p.Name.StartsWith("Coca"));
        var saleId = Sell(coke, 3);
        var returns = new ReturnRepository(_db);
        var line = returns.GetReturnable(saleId).Single();

        var refundId = returns.Process(new ReturnRequest
        {
            SaleId = saleId, Lines = [new(line.SaleLineId, 2, Restock: true)],
            Reason = ReturnReason.Faulty, RefundMethod = PaymentMethod.Cash,
        }, _mary);

        Assert.Equal(99, products.GetById(coke.Id)!.StockQuantity); // 100 - 3 + 2
        Assert.Equal(1, returns.GetReturnable(saleId).Single().QuantityRemaining);

        var slip = returns.GetRefundDocument(refundId)!;
        Assert.Equal("REFUND", slip.Title);
        Assert.Equal(coke.Price * 2, slip.Total);
        Assert.Equal(saleId.ToString("D6"), slip.OriginalReceiptNumber);

        Assert.Throws<InvalidOperationException>(() => returns.Process(new ReturnRequest
        {
            SaleId = saleId, Lines = [new(line.SaleLineId, 2, true)], RefundMethod = PaymentMethod.Cash,
        }, _mary));
    }

    [Fact]
    public void Damaged_returns_are_not_put_back_in_stock()
    {
        var products = new ProductRepository(_db);
        var bread = products.GetAll()[0];
        var saleId = Sell(bread, 1);
        var line = new ReturnRepository(_db).GetReturnable(saleId).Single();

        new ReturnRepository(_db).Process(new ReturnRequest
        {
            SaleId = saleId, Lines = [new(line.SaleLineId, 1, Restock: false)],
            Reason = ReturnReason.Faulty, RefundMethod = PaymentMethod.Cash,
        }, _mary);

        Assert.Equal(99, products.GetById(bread.Id)!.StockQuantity);
    }

    // ---------- Staff ----------

    [Fact]
    public void Last_active_super_admin_cannot_be_demoted_or_disabled()
    {
        var users = new UserRepository(_db);
        var me = users.GetById(_manager.Id)!;
        me.Role = UserRole.SuperAdmin;
        users.Update(me, _mary);
        me.Role = UserRole.Cashier;
        Assert.Throws<InvalidOperationException>(() => users.Update(me, _mary));

        var second = users.Create(new User { Username = "john", DisplayName = "John Phiri", Role = UserRole.SuperAdmin }, "1111", _mary);
        users.Update(me, _mary); // fine now there's another super admin
        Assert.Equal(UserRole.Cashier, users.GetById(_manager.Id)!.Role);
        Assert.Equal(UserRole.SuperAdmin, second.Role);
    }

    [Fact]
    public void Only_a_super_admin_manages_admins()
    {
        var users = new UserRepository(_db);
        Assert.Throws<InvalidOperationException>(() =>
            users.Create(new User { Username = "ada", DisplayName = "Ada", Role = UserRole.Admin }, "1111", _mary, actorRole: UserRole.Admin));
        var clerk = users.Create(new User { Username = "wes", DisplayName = "Wes", Role = UserRole.Warehousing }, "1111", _mary, actorRole: UserRole.Admin);
        clerk.Role = UserRole.Admin;
        Assert.Throws<InvalidOperationException>(() => users.Update(clerk, _mary, UserRole.Admin));     // can't promote to admin
        Assert.Throws<InvalidOperationException>(() => users.ResetPassword(_manager.Id, "9999", _mary, UserRole.Admin)); // can't touch an admin
        users.Update(clerk, _mary, UserRole.SuperAdmin);
        Assert.Equal(UserRole.Admin, users.GetById(clerk.Id)!.Role);
    }

    [Fact]
    public void Old_role_names_are_upgraded_and_the_first_admin_becomes_super_admin()
    {
        using (var conn = _db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE users SET role = 'Manager'; INSERT INTO users (username, display_name, password_hash, role, created_at) VALUES ('sue', 'Sue', 'x', 'Supervisor', '2026-01-01');";
            cmd.ExecuteNonQuery();
        }
        _db.Initialize();
        var all = new UserRepository(_db).GetAll();
        Assert.Equal(UserRole.SuperAdmin, all.Single(u => u.Username == "mary").Role);
        Assert.Equal(UserRole.OperationsManager, all.Single(u => u.Username == "sue").Role);
    }

    [Fact]
    public void Reset_password_clears_lockout_and_logins_are_audited()
    {
        var users = new UserRepository(_db);
        for (var i = 0; i < UserRepository.MaxFailedAttempts; i++) users.Authenticate("mary", "0000");
        Assert.Equal(LoginStatus.LockedOut, users.Authenticate("mary", "4321").Status);

        users.ResetPassword(_manager.Id, "9876", _mary);
        Assert.Equal(LoginStatus.Success, users.Authenticate("mary", "9876").Status);

        var audit = new AuditRepository(_db);
        Assert.True(audit.Query(new AuditFilter(Action: AuditActions.LoginFailed)).Total >= 4);
        Assert.Equal(1, audit.Query(new AuditFilter(Action: AuditActions.AccountLocked)).Total);
        Assert.Equal(1, audit.Query(new AuditFilter(Action: AuditActions.PasswordReset)).Total);
        Assert.Equal(1, audit.Query(new AuditFilter(Action: AuditActions.LoginSucceeded)).Total);
    }

    // ---------- Audit trail ----------

    [Fact]
    public void Audit_log_cannot_be_edited_or_deleted()
    {
        Sell(new ProductRepository(_db).GetAll()[0], 1);
        using var conn = _db.Open();

        var update = conn.CreateCommand();
        update.CommandText = "UPDATE audit_log SET details = 'nothing happened';";
        Assert.Throws<SqliteException>(() => update.ExecuteNonQuery());

        var delete = conn.CreateCommand();
        delete.CommandText = "DELETE FROM audit_log;";
        Assert.Throws<SqliteException>(() => delete.ExecuteNonQuery());

        Assert.True(new AuditRepository(_db).Verify().IsIntact);
    }

    [Fact]
    public void Tampering_is_detected_even_if_the_protection_is_removed()
    {
        Sell(new ProductRepository(_db).GetAll()[0], 1);
        Sell(new ProductRepository(_db).GetAll()[1], 1);
        var audit = new AuditRepository(_db);
        Assert.True(audit.Verify().IsIntact);

        using (var conn = _db.Open())
        {
            var hack = conn.CreateCommand();
            hack.CommandText = """
                DROP TRIGGER audit_log_no_update;
                UPDATE audit_log SET details = 'Sale #000001: 1 item(s), K0.01 by Cash' WHERE action = 'sale.completed';
                """;
            hack.ExecuteNonQuery();
        }

        var result = audit.Verify();
        Assert.False(result.IsIntact);
        Assert.Contains("altered", result.Message);
    }
}
