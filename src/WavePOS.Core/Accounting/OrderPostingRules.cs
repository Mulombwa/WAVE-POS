namespace WavePOS.Core.Accounting;

/// <summary>
/// Postings for purchase orders (procure-to-pay) and sales orders (order-to-cash).
/// <para>Buying: goods received → Inventory / Goods received not yet billed; supplier bill →
/// clears that, adds claimable VAT and the amount owed; payment → settles what's owed.</para>
/// <para>Selling: delivery → cost of sales out of Inventory; tax invoice → owed by the customer,
/// sales and VAT; payment → money in, settles what the customer owes.</para>
/// </summary>
public static partial class PostingRules
{
    public static JournalDraft? GoodsReceived(long poId, long receiptId, string supplier, DateOnly date, decimal value,
        Func<string, long> account)
    {
        if (Money.Round(value) == 0) return null;
        var e = New(date, $"GRN{receiptId:D6}", $"Goods received from {supplier} on PO{poId:D6}", JournalSource.Purchase, poId);
        Debit(e, account(SystemAccounts.Inventory), value, "Stock in");
        Credit(e, account(SystemAccounts.GoodsReceived), value, "Received, not yet billed");
        return e;
    }

    public static JournalDraft SupplierBill(long poId, string supplierInvoice, string supplier, DateOnly date, decimal net, decimal vat,
        Func<string, long> account)
    {
        var e = New(date, $"Bill {supplierInvoice}", $"Bill from {supplier} for PO{poId:D6}", JournalSource.Purchase, poId);
        Debit(e, account(SystemAccounts.GoodsReceived), net, "Goods billed");
        Debit(e, account(SystemAccounts.VatInput), vat, "VAT on purchases");
        Credit(e, account(SystemAccounts.Payables), net + vat, $"Owed to {supplier}");
        return e;
    }

    public static JournalDraft SupplierPayment(long poId, string supplierInvoice, string supplier, DateOnly date, decimal amount,
        long paidFromAccountId, Func<string, long> account)
    {
        var e = New(date, $"Payment of bill {supplierInvoice}", $"Paid {supplier}", JournalSource.Purchase, poId);
        Debit(e, account(SystemAccounts.Payables), amount, $"Paid {supplier}");
        Credit(e, paidFromAccountId, amount, $"Paid {supplier}");
        return e;
    }

    public static JournalDraft? Delivery(long orderId, long deliveryId, string customer, DateOnly date, decimal cost,
        Func<string, long> account)
    {
        if (Money.Round(cost) == 0) return null;
        var e = New(date, $"DN{deliveryId:D6}", $"Delivered to {customer} on SO{orderId:D6}", JournalSource.SalesOrder, orderId);
        Debit(e, account(SystemAccounts.CostOfSales), cost, "Cost of goods delivered");
        Credit(e, account(SystemAccounts.Inventory), cost, "Stock out");
        return e;
    }

    public static JournalDraft CustomerInvoice(long orderId, long invoiceId, string customer, DateOnly date, decimal total, decimal vat,
        Func<string, long> account)
    {
        var e = New(date, $"INV{invoiceId:D6}", $"Tax invoice to {customer} for SO{orderId:D6}", JournalSource.SalesOrder, orderId);
        Debit(e, account(SystemAccounts.Receivables), total, $"Owed by {customer}");
        Credit(e, account(SystemAccounts.Sales), total - vat, "Sales excl. VAT");
        Credit(e, account(SystemAccounts.VatOutput), vat, "VAT on sales");
        return e;
    }

    public static JournalDraft CustomerPayment(long orderId, long invoiceId, string customer, DateOnly date, decimal amount,
        long receivedIntoAccountId, Func<string, long> account)
    {
        var e = New(date, $"Payment for INV{invoiceId:D6}", $"Received from {customer}", JournalSource.SalesOrder, orderId);
        Debit(e, receivedIntoAccountId, amount, $"From {customer}");
        Credit(e, account(SystemAccounts.Receivables), amount, $"Paid by {customer}");
        return e;
    }
}
