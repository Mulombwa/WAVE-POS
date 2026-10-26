namespace WavePOS.Core.Models;

/// <summary>
/// An item that can be sold. Prices are VAT-inclusive (shelf price).
/// </summary>
public class Product
{
    public long Id { get; set; }
    public string Sku { get; set; } = "";
    public string? Barcode { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "General";
    public decimal Price { get; set; }

    /// <summary>VAT rate as a fraction, e.g. 0.16 for 16%. 0 for exempt items.</summary>
    public decimal VatRate { get; set; } = 0.16m;

    public int StockQuantity { get; set; }
}
