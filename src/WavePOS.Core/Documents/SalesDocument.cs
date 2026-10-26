using WavePOS.Core.Models;
using WavePOS.Core.Sales;

namespace WavePOS.Core.Documents;

public enum DocumentKind
{
    Receipt,
    DraftInvoice,
}

public record DocumentLine(long? ProductId, string Description, int Quantity, decimal UnitPrice, decimal VatRate)
{
    public decimal LineTotal => UnitPrice * Quantity;
}

/// <summary>
/// Everything needed to print a receipt or invoice, independent of how it's drawn.
/// The desktop app renders it with WPF; the web and mobile apps can render the same data.
/// </summary>
public class SalesDocument
{
    public const int DraftValidDays = 14;

    public DocumentKind Kind { get; init; }
    public string Number { get; set; } = "";
    public DateTime Date { get; init; }
    public string CashierName { get; init; } = "";
    public string? CustomerName { get; init; }
    public List<DocumentLine> Lines { get; init; } = [];
    public decimal Total { get; init; }
    public decimal VatAmount { get; init; }

    /// <summary>Total before VAT.</summary>
    public decimal NetAmount => Total - VatAmount;

    // Receipts only
    public PaymentMethod? PaymentMethod { get; init; }
    public decimal AmountTendered { get; init; }
    public decimal Change { get; init; }

    // Draft invoices only
    public DateTime? ValidUntil { get; init; }

    public string Title => Kind == DocumentKind.Receipt ? "RECEIPT" : "DRAFT INVOICE";

    public static SalesDocument FromSale(Sale sale, string cashierName) => new()
    {
        Kind = DocumentKind.Receipt,
        Number = sale.Id.ToString("D6"),
        Date = sale.Timestamp,
        CashierName = cashierName,
        Lines = sale.Lines.Select(l => new DocumentLine(l.ProductId, l.ProductName, l.Quantity, l.UnitPrice, l.VatRate)).ToList(),
        Total = sale.Total,
        VatAmount = sale.VatAmount,
        PaymentMethod = sale.PaymentMethod,
        AmountTendered = sale.AmountTendered,
        Change = sale.Change,
    };

    /// <summary>A quote/proforma from the current cart. The cart is left unchanged.</summary>
    public static SalesDocument DraftFromCart(Cart cart, string cashierName, string? customerName, DateTime now)
    {
        if (cart.IsEmpty) throw new InvalidOperationException("Add items before creating a draft invoice.");

        return new SalesDocument
        {
            Kind = DocumentKind.DraftInvoice,
            Date = now,
            CashierName = cashierName,
            CustomerName = string.IsNullOrWhiteSpace(customerName) ? null : customerName.Trim(),
            Lines = cart.Lines.Select(l => new DocumentLine(l.Product.Id, l.Product.Name, l.Quantity, l.Product.Price, l.Product.VatRate)).ToList(),
            Total = cart.Total,
            VatAmount = cart.VatAmount,
            ValidUntil = now.Date.AddDays(DraftValidDays),
        };
    }
}

public static class PaymentMethodNames
{
    public static string DisplayName(this PaymentMethod method) => method switch
    {
        PaymentMethod.MobileMoney => "Mobile Money",
        _ => method.ToString(),
    };
}
