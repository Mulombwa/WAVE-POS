namespace WavePOS.Core.Models;

/// <summary>
/// A sellable item (one SKU). Products with sizes/colours are a family of these variants,
/// sharing a <see cref="FamilyId"/>; each variant has its own SKU, barcode, price and stock.
/// Prices are VAT-inclusive (shelf price).
/// </summary>
public class Product
{
    public long Id { get; set; }
    public string Sku { get; set; } = "";
    public string? Barcode { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "General";
    public decimal Price { get; set; }

    /// <summary>What the shop pays per unit (for stock value and margins).</summary>
    public decimal Cost { get; set; }

    /// <summary>VAT rate as a fraction, e.g. 0.16 for 16%. 0 for exempt items.</summary>
    public decimal VatRate { get; set; } = 0.16m;

    public int StockQuantity { get; set; }

    /// <summary>Warn when stock falls to this level or below. 0 = no warning.</summary>
    public int ReorderLevel { get; set; }

    public bool IsActive { get; set; } = true;

    // Variants
    public long? FamilyId { get; set; }
    public string? Option1 { get; set; }
    public string? Option2 { get; set; }

    /// <summary>File name of the product's picture in the business's data folder, or null.</summary>
    public string? ImageFile { get; set; }

    public bool IsLowStock => ReorderLevel > 0 && StockQuantity <= ReorderLevel;
    public decimal StockValue => Cost * Math.Max(StockQuantity, 0);
}
