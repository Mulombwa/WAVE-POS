using WavePOS.Core.Models;
using WavePOS.Core.Sales;

namespace WavePOS.Core.Tests;

public class CartTests
{
    private static readonly Product Bread = new() { Id = 1, Name = "Bread", Price = 25.00m, VatRate = 0m };
    private static readonly Product Coke = new() { Id = 2, Name = "Coke 500ml", Price = 11.60m, VatRate = 0.16m };

    [Fact]
    public void Adding_same_product_twice_increases_quantity()
    {
        var cart = new Cart();
        cart.Add(Coke);
        cart.Add(Coke, 2);

        Assert.Single(cart.Lines);
        Assert.Equal(3, cart.ItemCount);
        Assert.Equal(34.80m, cart.Total);
    }

    [Fact]
    public void Vat_is_extracted_from_inclusive_prices()
    {
        var cart = new Cart();
        cart.Add(Coke, 10); // K116.00 incl. 16% VAT -> VAT K16.00
        cart.Add(Bread);    // exempt

        Assert.Equal(141.00m, cart.Total);
        Assert.Equal(16.00m, cart.VatAmount);
    }

    [Fact]
    public void Setting_quantity_to_zero_removes_line()
    {
        var cart = new Cart();
        cart.Add(Bread);
        cart.SetQuantity(Bread.Id, 0);

        Assert.True(cart.IsEmpty);
    }

    [Fact]
    public void Cash_checkout_calculates_change()
    {
        var cart = new Cart();
        cart.Add(Bread);
        cart.Add(Coke);

        var sale = cart.Checkout(PaymentMethod.Cash, 50m);

        Assert.Equal(36.60m, sale.Total);
        Assert.Equal(13.40m, sale.Change);
        Assert.Equal(2, sale.Lines.Count);
    }

    [Fact]
    public void Cash_checkout_rejects_short_payment()
    {
        var cart = new Cart();
        cart.Add(Bread);

        Assert.Throws<InvalidOperationException>(() => cart.Checkout(PaymentMethod.Cash, 20m));
    }

    [Fact]
    public void Card_checkout_takes_exact_total()
    {
        var cart = new Cart();
        cart.Add(Bread);

        var sale = cart.Checkout(PaymentMethod.Card, 0m);

        Assert.Equal(25.00m, sale.AmountTendered);
        Assert.Equal(0m, sale.Change);
    }

    [Fact]
    public void Empty_cart_cannot_checkout()
    {
        Assert.Throws<InvalidOperationException>(() => new Cart().Checkout(PaymentMethod.Cash, 100m));
    }

    [Fact]
    public void Money_formats_kwacha()
    {
        Assert.Equal("K1,234.50", Money.Format(1234.5m));
    }
}
