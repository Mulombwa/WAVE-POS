using WavePOS.Core.Accounting;

namespace WavePOS.Core.Orders;

/// <summary>A business the shop buys from.</summary>
public class Supplier
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Tpin { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string Address { get; set; } = "";

    /// <summary>Days after the bill date that payment is due (0 = on receipt).</summary>
    public int PaymentTermsDays { get; set; } = 30;
    public bool IsActive { get; set; } = true;
    public string Notes { get; set; } = "";

    /// <summary>Unpaid bills, filled in when listing.</summary>
    public decimal Balance { get; set; }
}

public enum PurchaseOrderStatus
{
    Draft,
    /// <summary>Over the approval limit: waiting for a manager.</summary>
    AwaitingApproval,
    /// <summary>Approved and sent to the supplier; nothing received yet.</summary>
    Approved,
    PartlyReceived,
    Received,
    Cancelled,
}

public enum SalesOrderStatus
{
    /// <summary>A quotation: no stock reserved, can still change.</summary>
    Quote,
    /// <summary>Accepted by the customer: stock is reserved for it.</summary>
    Confirmed,
    PartlyDelivered,
    Delivered,
    Cancelled,
}

public static class OrderStatusNames
{
    public static string Label(this PurchaseOrderStatus s) => s switch
    {
        PurchaseOrderStatus.AwaitingApproval => "Awaiting approval",
        PurchaseOrderStatus.Approved => "Ordered",
        PurchaseOrderStatus.PartlyReceived => "Partly received",
        _ => s.ToString(),
    };

    public static string Label(this SalesOrderStatus s) => s switch
    {
        SalesOrderStatus.PartlyDelivered => "Partly delivered",
        _ => s.ToString(),
    };
}

/// <summary>A purchase order line. Costs exclude VAT (VAT is added on top and claimed back).</summary>
public class PurchaseOrderLine
{
    public long Id { get; set; }
    public long ProductId { get; set; }
    public string Description { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal VatRate { get; set; }
    public int Received { get; set; }
    public int Billed { get; set; }

    public decimal Net => Money.Round(UnitCost * Quantity);
    public decimal Vat => Money.Round(Net * VatRate);
    public int Outstanding => Quantity - Received;
    public int ToBill => Received - Billed;
}

public class PurchaseOrder
{
    public long Id { get; set; }
    public string Number => $"PO{Id:D6}";
    public long SupplierId { get; set; }
    public string SupplierName { get; set; } = "";
    public PurchaseOrderStatus Status { get; set; }
    public DateOnly OrderDate { get; set; }
    public DateOnly? ExpectedDate { get; set; }
    public string Notes { get; set; } = "";
    public long? CreatedById { get; set; }
    public string CreatedByName { get; set; } = "";
    public string? ApprovedByName { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public List<PurchaseOrderLine> Lines { get; set; } = [];

    public decimal Net => Lines.Sum(l => l.Net);
    public decimal Vat => Lines.Sum(l => l.Vat);
    public decimal Total => Net + Vat;
    public bool CanEdit => Status == PurchaseOrderStatus.Draft;
    public bool CanReceive => Status is PurchaseOrderStatus.Approved or PurchaseOrderStatus.PartlyReceived;
    public bool HasUnbilled => Lines.Any(l => l.ToBill > 0);
}

/// <summary>A sales order line. Prices include VAT, the same as shelf prices at the till.</summary>
public class SalesOrderLine
{
    public long Id { get; set; }
    public long ProductId { get; set; }
    public string Description { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal VatRate { get; set; }
    public int Delivered { get; set; }
    public int Invoiced { get; set; }

    public decimal Gross => Money.Round(UnitPrice * Quantity);
    public decimal Vat => PostingRules.VatIncluded(Gross, VatRate);
    public int ToDeliver => Quantity - Delivered;
    public int ToInvoice => Delivered - Invoiced;
}

public class SalesOrder
{
    public long Id { get; set; }
    public string Number => $"SO{Id:D6}";
    public long CustomerId { get; set; }
    public string CustomerName { get; set; } = "";
    public SalesOrderStatus Status { get; set; }
    public DateOnly OrderDate { get; set; }
    public DateOnly? ValidUntil { get; set; }
    public string Notes { get; set; } = "";
    public string CreatedByName { get; set; } = "";
    public string? CreditOverride { get; set; }
    public List<SalesOrderLine> Lines { get; set; } = [];

    public decimal Total => Lines.Sum(l => l.Gross);
    public decimal Vat => Lines.Sum(l => l.Vat);
    public decimal Net => Total - Vat;
    public bool CanEdit => Status == SalesOrderStatus.Quote;
    public bool CanDeliver => Status is SalesOrderStatus.Confirmed or SalesOrderStatus.PartlyDelivered;
    public bool HasUninvoiced => Lines.Any(l => l.ToInvoice > 0);
}

/// <summary>A customer's credit account: TPIN for tax invoices, credit limit and payment terms.</summary>
public record CreditTerms(string Tpin, decimal? CreditLimit, int PaymentTermsDays);

public enum AgeBucket { NotDue, Days1To30, Days31To60, Days61To90, Over90 }

/// <summary>Rules for ordering, receiving, delivering and credit control.</summary>
public static class OrderRules
{
    public static PurchaseOrderStatus AfterReceipt(IEnumerable<PurchaseOrderLine> lines)
    {
        var list = lines.ToList();
        if (list.All(l => l.Received >= l.Quantity)) return PurchaseOrderStatus.Received;
        return list.Any(l => l.Received > 0) ? PurchaseOrderStatus.PartlyReceived : PurchaseOrderStatus.Approved;
    }

    public static SalesOrderStatus AfterDelivery(IEnumerable<SalesOrderLine> lines)
    {
        var list = lines.ToList();
        if (list.All(l => l.Delivered >= l.Quantity)) return SalesOrderStatus.Delivered;
        return list.Any(l => l.Delivered > 0) ? SalesOrderStatus.PartlyDelivered : SalesOrderStatus.Confirmed;
    }

    /// <summary>Orders above the limit need a manager's approval. A limit of 0 means every order does.</summary>
    public static bool NeedsApproval(decimal total, decimal approvalLimit) => total > approvalLimit;

    /// <summary>
    /// Separation of duties: nobody approves their own order, unless they are the only person
    /// who can approve (a one-manager business), in which case it's allowed and flagged in the audit trail.
    /// </summary>
    public static string? CheckApprover(long approverId, long? creatorId, int activeApprovers) =>
        approverId == creatorId && activeApprovers > 1
            ? "You created this order, so another manager has to approve it."
            : null;

    /// <summary>Checks quantities against what's left on each line. Returns the problem or null.</summary>
    public static string? CheckQuantities(IReadOnlyDictionary<long, int> requested, IReadOnlyDictionary<long, (string Name, int Remaining)> lines)
    {
        if (requested.Values.All(q => q == 0)) return "Enter a quantity on at least one line.";
        foreach (var (lineId, qty) in requested)
        {
            if (!lines.TryGetValue(lineId, out var line)) return "That line isn't on this order.";
            if (qty < 0) return "Quantities can't be negative.";
            if (qty > line.Remaining) return $"Only {line.Remaining} × {line.Name} still to go.";
        }
        return null;
    }

    /// <summary>
    /// Credit check before confirming an order: what the customer already owes plus this order
    /// must stay within their limit. No limit set = no check. Returns the problem or null.
    /// </summary>
    public static string? CheckCredit(CreditTerms terms, decimal owed, decimal orderTotal) =>
        terms.CreditLimit is { } limit && owed + orderTotal > limit
            ? $"Over the credit limit: owes {Money.Format(owed)}, this order {Money.Format(orderTotal)}, limit {Money.Format(limit)}."
            : null;

    /// <summary>
    /// How many to order: when stock (plus what's already on order) is at or below the reorder
    /// level, top it up to twice the reorder level. 0 = no need.
    /// </summary>
    public static int ReorderQuantity(int onHand, int reorderLevel, int onOrder)
    {
        if (reorderLevel <= 0) return 0;
        var expected = Math.Max(onHand, 0) + onOrder;
        return expected > reorderLevel ? 0 : reorderLevel * 2 - expected;
    }

    public static DateOnly DueDate(DateOnly date, int termsDays) => date.AddDays(Math.Max(termsDays, 0));

    public static AgeBucket Age(DateOnly due, DateOnly today)
    {
        var days = today.DayNumber - due.DayNumber;
        return days switch
        {
            <= 0 => AgeBucket.NotDue,
            <= 30 => AgeBucket.Days1To30,
            <= 60 => AgeBucket.Days31To60,
            <= 90 => AgeBucket.Days61To90,
            _ => AgeBucket.Over90,
        };
    }

    public static string Label(this AgeBucket b) => b switch
    {
        AgeBucket.NotDue => "Not yet due",
        AgeBucket.Days1To30 => "1–30 days overdue",
        AgeBucket.Days31To60 => "31–60 days",
        AgeBucket.Days61To90 => "61–90 days",
        _ => "Over 90 days",
    };
}
