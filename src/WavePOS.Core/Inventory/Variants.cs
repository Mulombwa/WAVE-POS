using System.Text;

namespace WavePOS.Core.Inventory;

/// <summary>A product option such as Size (S, M, L) or Colour (Red, Blue).</summary>
public record ProductOption(string Name, IReadOnlyList<string> Values)
{
    public static ProductOption Parse(string name, string commaSeparatedValues) => new(
        name.Trim(),
        commaSeparatedValues.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList());
}

/// <summary>One sellable variant (its own SKU, barcode, price and stock).</summary>
public record VariantSpec(string Name, string Sku, string? Option1, string? Option2);

public static class VariantBuilder
{
    public const int MaxVariants = 200;

    /// <summary>
    /// Builds every combination of up to two options, e.g. T-Shirt × (S, M) × (Red, Blue)
    /// gives 4 variants named "T-Shirt — S / Red" with SKUs like TSHIRT-S-RED.
    /// With no options, returns the single plain product.
    /// </summary>
    public static List<VariantSpec> Build(string productName, string skuPrefix, ProductOption? option1, ProductOption? option2)
    {
        productName = productName.Trim();
        if (productName.Length == 0) throw new ArgumentException("Product name is required.");
        var prefix = SkuPart(string.IsNullOrWhiteSpace(skuPrefix) ? productName : skuPrefix);

        // No option = a single "null" value, so the loops below still run once.
        List<string?> values1 = option1 is { Values.Count: > 0 } ? [.. option1.Values] : [null];
        List<string?> values2 = option2 is { Values.Count: > 0 } ? [.. option2.Values] : [null];
        if (values1.Count * values2.Count > MaxVariants)
            throw new ArgumentException($"That makes more than {MaxVariants} variants. Use fewer option values.");

        var result = new List<VariantSpec>();
        foreach (var v1 in values1)
        foreach (var v2 in values2)
        {
            var parts = new[] { v1, v2 }.Where(v => v is not null).ToList();
            var name = parts.Count == 0 ? productName : $"{productName} — {string.Join(" / ", parts)}";
            var sku = parts.Count == 0 ? prefix : $"{prefix}-{string.Join("-", parts.Select(p => SkuPart(p!)))}";
            result.Add(new VariantSpec(name, sku, v1, v2));
        }
        return result;
    }

    /// <summary>Upper-case letters and digits only, max 12 characters: "Navy Blue" → "NAVYBLUE".</summary>
    public static string SkuPart(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.ToUpperInvariant())
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
        var s = sb.ToString();
        return s.Length == 0 ? "ITEM" : s[..Math.Min(12, s.Length)];
    }
}

/// <summary>Why stock changed. Every change is recorded in the stock ledger with one of these.</summary>
public enum StockReason
{
    Sale,
    Return,
    Received,
    Adjustment,
    StockCount,
    Damaged,
    Theft,
    Expired,
    InitialStock,
}

public record StockMovement(
    long Id, long ProductId, string ProductName, int Change, int BalanceAfter,
    StockReason Reason, string Note, string? Reference, string UserName, DateTime Timestamp);
