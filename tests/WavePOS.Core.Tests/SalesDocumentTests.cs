using WavePOS.Core.Documents;
using WavePOS.Core.Models;
using WavePOS.Core.Sales;

namespace WavePOS.Core.Tests;

public class SalesDocumentTests
{
    private static readonly Product Coke = new() { Id = 2, Name = "Coke 500ml", Price = 11.60m, VatRate = 0.16m };
    private static readonly Product Bread = new() { Id = 1, Name = "Bread", Price = 25.00m, VatRate = 0m };

    [Fact]
    public void Draft_invoice_copies_cart_and_leaves_it_unchanged()
    {
        var cart = new Cart();
        cart.Add(Coke, 10);
        cart.Add(Bread);
        var now = new DateTime(2026, 10, 27, 14, 30, 0);

        var draft = SalesDocument.DraftFromCart(cart, "Mary", "  ABC Ltd ", now);

        Assert.Equal(DocumentKind.DraftInvoice, draft.Kind);
        Assert.Equal("DRAFT INVOICE", draft.Title);
        Assert.Equal("ABC Ltd", draft.CustomerName);
        Assert.Equal(141.00m, draft.Total);
        Assert.Equal(16.00m, draft.VatAmount);
        Assert.Equal(125.00m, draft.NetAmount);
        Assert.Equal(new DateTime(2026, 11, 10), draft.ValidUntil);
        Assert.Equal(2, draft.Lines.Count);
        Assert.Equal(11, cart.ItemCount); // cart still there
    }

    [Fact]
    public void Blank_customer_name_becomes_null()
    {
        var cart = new Cart();
        cart.Add(Bread);
        Assert.Null(SalesDocument.DraftFromCart(cart, "Mary", "   ", DateTime.Now).CustomerName);
    }

    [Fact]
    public void Draft_from_empty_cart_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => SalesDocument.DraftFromCart(new Cart(), "Mary", null, DateTime.Now));
    }

    [Fact]
    public void Receipt_from_sale_includes_payment()
    {
        var cart = new Cart();
        cart.Add(Bread);
        var sale = cart.Checkout(PaymentMethod.MobileMoney, 0m);
        sale.Id = 42;

        var receipt = SalesDocument.FromSale(sale, "John");

        Assert.Equal("RECEIPT", receipt.Title);
        Assert.Equal("000042", receipt.Number);
        Assert.Equal(PaymentMethod.MobileMoney, receipt.PaymentMethod);
        Assert.Equal("Mobile Money", receipt.PaymentMethod!.Value.DisplayName());
        Assert.Equal(25.00m, receipt.Total);
    }
}
