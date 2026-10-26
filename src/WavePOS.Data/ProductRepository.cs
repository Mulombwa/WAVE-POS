using Microsoft.Data.Sqlite;
using WavePOS.Core;
using WavePOS.Core.Models;

namespace WavePOS.Data;

public class ProductRepository(PosDatabase db)
{
    private const string SelectColumns =
        "SELECT id, sku, barcode, name, category, price_ngwee, vat_rate, stock_quantity FROM products";

    public List<Product> GetAll()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{SelectColumns} WHERE is_active = 1 ORDER BY category, name;";
        return ReadAll(cmd);
    }

    /// <summary>Matches name, SKU or barcode (case-insensitive, partial).</summary>
    public List<Product> Search(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return GetAll();

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            {SelectColumns}
            WHERE is_active = 1 AND (name LIKE $q OR sku LIKE $q OR barcode LIKE $q)
            ORDER BY category, name;
            """;
        cmd.Parameters.AddWithValue("$q", $"%{text.Trim()}%");
        return ReadAll(cmd);
    }

    /// <summary>Exact barcode or SKU match — used when a scanner sends a code.</summary>
    public Product? FindByCode(string code)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{SelectColumns} WHERE is_active = 1 AND (barcode = $c OR sku = $c COLLATE NOCASE) LIMIT 1;";
        cmd.Parameters.AddWithValue("$c", code.Trim());
        return ReadAll(cmd).FirstOrDefault();
    }

    private static List<Product> ReadAll(SqliteCommand cmd)
    {
        var list = new List<Product>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Product
            {
                Id = r.GetInt64(0),
                Sku = r.GetString(1),
                Barcode = r.IsDBNull(2) ? null : r.GetString(2),
                Name = r.GetString(3),
                Category = r.GetString(4),
                Price = Money.FromNgwee(r.GetInt64(5)),
                VatRate = (decimal)r.GetDouble(6),
                StockQuantity = r.GetInt32(7),
            });
        }
        return list;
    }
}
