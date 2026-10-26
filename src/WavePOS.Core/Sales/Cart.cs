using WavePOS.Core.Models;

namespace WavePOS.Core.Sales;

public class CartLine
{
    public CartLine(Product product, int quantity)
    {
        Product = product;
        Quantity = quantity;
    }

    public Product Product { get; }
    public int Quantity { get; internal set; }
    public decimal LineTotal => Product.Price * Quantity;
}

/// <summary>
/// The current sale being built at the till. Shared by desktop, mobile and web apps.
/// </summary>
public class Cart
{
    private readonly List<CartLine> _lines = [];

    public IReadOnlyList<CartLine> Lines => _lines;
    public bool IsEmpty => _lines.Count == 0;
    public int ItemCount => _lines.Sum(l => l.Quantity);
    public decimal Total => _lines.Sum(l => l.LineTotal);

    /// <summary>VAT included in the total (prices are VAT-inclusive).</summary>
    public decimal VatAmount => Money.Round(_lines.Sum(l =>
        l.LineTotal - l.LineTotal / (1 + l.Product.VatRate)));

    /// <summary>Adds a product, or increases its quantity if it's already in the cart.</summary>
    public void Add(Product product, int quantity = 1)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));

        var existing = _lines.FirstOrDefault(l => l.Product.Id == product.Id);
        if (existing is null)
            _lines.Add(new CartLine(product, quantity));
        else
            existing.Quantity += quantity;
    }

    /// <summary>Sets a line's quantity. Zero or less removes the line.</summary>
    public void SetQuantity(long productId, int quantity)
    {
        var line = _lines.FirstOrDefault(l => l.Product.Id == productId);
        if (line is null) return;

        if (quantity <= 0)
            _lines.Remove(line);
        else
            line.Quantity = quantity;
    }

    public void Remove(long productId) => SetQuantity(productId, 0);

    public void Clear() => _lines.Clear();

    /// <summary>
    /// Turns the cart into a completed sale. For cash, the amount tendered must cover the total.
    /// Card and mobile money are taken as the exact total.
    /// </summary>
    public Sale Checkout(PaymentMethod method, decimal amountTendered, DateTime? now = null)
    {
        if (IsEmpty) throw new InvalidOperationException("The cart is empty.");

        var total = Total;
        var tendered = method == PaymentMethod.Cash ? amountTendered : total;
        if (tendered < total)
            throw new InvalidOperationException(
                $"Amount tendered ({Money.Format(tendered)}) is less than the total ({Money.Format(total)}).");

        return new Sale
        {
            Timestamp = now ?? DateTime.Now,
            Lines = _lines.Select(l => new SaleLine(
                l.Product.Id, l.Product.Name, l.Product.Price, l.Quantity, l.Product.VatRate)).ToList(),
            Total = total,
            VatAmount = VatAmount,
            PaymentMethod = method,
            AmountTendered = tendered,
            Change = tendered - total,
        };
    }
}
