using WavePOS.Core.Accounting;
using WavePOS.Core.Orders;

namespace WavePOS.Core.Tests;

public class OrderTests
{
    private static readonly Dictionary<string, long> Ids = new();
    private static long Acct(string key) => Ids.TryGetValue(key, out var id) ? id : Ids[key] = Ids.Count + 1;
    private static decimal Net(JournalDraft e, string key) => e.Lines.Where(l => l.AccountId == Acct(key)).Sum(l => l.Debit - l.Credit);
    private static readonly DateOnly Today = new(2026, 10, 27);

    [Fact]
    public void Purchase_order_totals_add_vat_on_top_of_cost()
    {
        var po = new PurchaseOrder
        {
            Lines =
            [
                new PurchaseOrderLine { Quantity = 10, UnitCost = 40m, VatRate = 0.16m },
                new PurchaseOrderLine { Quantity = 3, UnitCost = 17.5m, VatRate = 0m },
            ],
        };
        Assert.Equal(452.50m, po.Net);
        Assert.Equal(64m, po.Vat);
        Assert.Equal(516.50m, po.Total);
    }

    [Fact]
    public void Sales_order_prices_include_vat()
    {
        var so = new SalesOrder { Lines = [new SalesOrderLine { Quantity = 2, UnitPrice = 58m, VatRate = 0.16m }] };
        Assert.Equal(116m, so.Total);
        Assert.Equal(16m, so.Vat);
        Assert.Equal(100m, so.Net);
    }

    [Fact]
    public void Status_follows_what_has_been_received_or_delivered()
    {
        List<PurchaseOrderLine> lines = [new() { Quantity = 5 }, new() { Quantity = 2 }];
        Assert.Equal(PurchaseOrderStatus.Approved, OrderRules.AfterReceipt(lines));
        lines[0].Received = 5;
        Assert.Equal(PurchaseOrderStatus.PartlyReceived, OrderRules.AfterReceipt(lines));
        lines[1].Received = 2;
        Assert.Equal(PurchaseOrderStatus.Received, OrderRules.AfterReceipt(lines));

        List<SalesOrderLine> sold = [new() { Quantity = 4, Delivered = 1 }];
        Assert.Equal(SalesOrderStatus.PartlyDelivered, OrderRules.AfterDelivery(sold));
        sold[0].Delivered = 4;
        Assert.Equal(SalesOrderStatus.Delivered, OrderRules.AfterDelivery(sold));
    }

    [Fact]
    public void Orders_over_the_limit_need_approval_and_nobody_approves_their_own()
    {
        Assert.False(OrderRules.NeedsApproval(5000m, 5000m));
        Assert.True(OrderRules.NeedsApproval(5000.01m, 5000m));
        Assert.True(OrderRules.NeedsApproval(1m, 0m));

        Assert.NotNull(OrderRules.CheckApprover(approverId: 1, creatorId: 1, activeApprovers: 2));
        Assert.Null(OrderRules.CheckApprover(approverId: 2, creatorId: 1, activeApprovers: 2));
        Assert.Null(OrderRules.CheckApprover(approverId: 1, creatorId: 1, activeApprovers: 1)); // one-manager business
    }

    [Fact]
    public void Quantities_cant_exceed_what_is_left()
    {
        var lines = new Dictionary<long, (string, int)> { [1] = ("Sugar 2kg", 4), [2] = ("Rice 2kg", 0) };
        Assert.Null(OrderRules.CheckQuantities(new Dictionary<long, int> { [1] = 4 }, lines));
        Assert.Contains("Only 4", OrderRules.CheckQuantities(new Dictionary<long, int> { [1] = 5 }, lines));
        Assert.Contains("Only 0", OrderRules.CheckQuantities(new Dictionary<long, int> { [2] = 1 }, lines));
        Assert.Contains("at least one", OrderRules.CheckQuantities(new Dictionary<long, int> { [1] = 0 }, lines));
        Assert.Contains("negative", OrderRules.CheckQuantities(new Dictionary<long, int> { [1] = -1, [2] = 0 }, lines));
        Assert.NotNull(OrderRules.CheckQuantities(new Dictionary<long, int> { [9] = 1 }, lines));
    }

    [Fact]
    public void Credit_check_counts_what_is_owed_plus_the_new_order()
    {
        Assert.Null(OrderRules.CheckCredit(new CreditTerms("", null, 30), owed: 1_000_000m, orderTotal: 5m));
        Assert.Null(OrderRules.CheckCredit(new CreditTerms("", 1000m, 30), owed: 600m, orderTotal: 400m));
        Assert.Contains("Over the credit limit", OrderRules.CheckCredit(new CreditTerms("", 1000m, 30), owed: 600m, orderTotal: 400.01m));
    }

    [Theory]
    [InlineData(10, 10, 0, 10)] // at the level: top up to 20
    [InlineData(3, 10, 0, 17)]
    [InlineData(3, 10, 5, 12)]  // some already on order
    [InlineData(3, 10, 8, 0)]   // 11 expected: above the level
    [InlineData(3, 10, 20, 0)]  // enough on order
    [InlineData(11, 10, 0, 0)]  // above the level
    [InlineData(-2, 10, 0, 20)] // oversold counts as none
    [InlineData(0, 0, 0, 0)]    // no reorder level set
    public void Reorder_tops_up_to_twice_the_reorder_level(int onHand, int level, int onOrder, int expected) =>
        Assert.Equal(expected, OrderRules.ReorderQuantity(onHand, level, onOrder));

    [Fact]
    public void Ageing_buckets_by_days_past_due()
    {
        Assert.Equal(AgeBucket.NotDue, OrderRules.Age(Today, Today));
        Assert.Equal(AgeBucket.Days1To30, OrderRules.Age(Today.AddDays(-1), Today));
        Assert.Equal(AgeBucket.Days31To60, OrderRules.Age(Today.AddDays(-45), Today));
        Assert.Equal(AgeBucket.Over90, OrderRules.Age(Today.AddDays(-91), Today));
        Assert.Equal(Today.AddDays(30), OrderRules.DueDate(Today, 30));
    }

    [Fact]
    public void Procure_to_pay_postings_balance_and_clear_goods_received()
    {
        var grn = PostingRules.GoodsReceived(1, 1, "Zambia Sugar", Today, 400m, Acct)!;
        var bill = PostingRules.SupplierBill(1, "INV-77", "Zambia Sugar", Today, 400m, 64m, Acct);
        var pay = PostingRules.SupplierPayment(1, "INV-77", "Zambia Sugar", Today, 464m, 99, Acct);

        foreach (var e in new[] { grn, bill, pay }) Assert.Null(JournalValidator.Check(e));
        Assert.Equal(0m, Net(grn, SystemAccounts.GoodsReceived) + Net(bill, SystemAccounts.GoodsReceived));
        Assert.Equal(400m, Net(grn, SystemAccounts.Inventory));
        Assert.Equal(64m, Net(bill, SystemAccounts.VatInput));
        Assert.Equal(0m, Net(bill, SystemAccounts.Payables) + Net(pay, SystemAccounts.Payables));
        Assert.Equal(-464m, pay.Lines.Single(l => l.AccountId == 99).Credit * -1);
        Assert.Null(PostingRules.GoodsReceived(1, 2, "Free samples", Today, 0m, Acct));
    }

    [Fact]
    public void Order_to_cash_postings_balance_and_clear_receivables()
    {
        var dn = PostingRules.Delivery(1, 1, "Lusaka Lodge", Today, 70m, Acct)!;
        var inv = PostingRules.CustomerInvoice(1, 1, "Lusaka Lodge", Today, 116m, 16m, Acct);
        var paid = PostingRules.CustomerPayment(1, 1, "Lusaka Lodge", Today, 116m, 98, Acct);

        foreach (var e in new[] { dn, inv, paid }) Assert.Null(JournalValidator.Check(e));
        Assert.Equal(70m, Net(dn, SystemAccounts.CostOfSales));
        Assert.Equal(-100m, Net(inv, SystemAccounts.Sales));
        Assert.Equal(-16m, Net(inv, SystemAccounts.VatOutput));
        Assert.Equal(0m, Net(inv, SystemAccounts.Receivables) + Net(paid, SystemAccounts.Receivables));
        Assert.Equal(JournalSource.SalesOrder, inv.Source);
        Assert.True(JournalSource.SalesOrder.IsAutomatic());
    }
}
