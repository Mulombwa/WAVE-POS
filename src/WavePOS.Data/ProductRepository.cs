using System.Globalization;
using Microsoft.Data.Sqlite;
using WavePOS.Core;
using WavePOS.Core.Audit;
using WavePOS.Core.Inventory;
using WavePOS.Core.Models;

namespace WavePOS.Data;

public record NewProductRequest(
    string Name, string Category, string SkuPrefix, decimal Price, decimal Cost, decimal VatRate,
    int ReorderLevel, int OpeningStock, ProductOption? Option1 = null, ProductOption? Option2 = null, string? Barcode = null);

public class ProductRepository(PosDatabase db)
{
    private const string SelectColumns = """
        SELECT id, sku, barcode, name, category, price_ngwee, vat_rate, stock_quantity,
               cost_ngwee, reorder_level, is_active, family_id, option1, option2
        FROM products
        """;

    /// <summary>Active products for the till.</summary>
    public List<Product> GetAll()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{SelectColumns} WHERE is_active = 1 ORDER BY category, name;";
        return ReadAll(cmd);
    }

    /// <summary>Matches name, SKU or barcode (case-insensitive, partial). Active products only.</summary>
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

    public Product? GetById(long id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{SelectColumns} WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return ReadAll(cmd).FirstOrDefault();
    }

    /// <summary>Other variants in the same family (sizes/colours of one product).</summary>
    public List<Product> GetFamily(long familyId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{SelectColumns} WHERE family_id = $f ORDER BY id;";
        cmd.Parameters.AddWithValue("$f", familyId);
        return ReadAll(cmd);
    }

    /// <summary>Inventory list for managers, including inactive products if asked.</summary>
    public List<Product> ListInventory(string? search = null, string? category = null, bool lowStockOnly = false, bool includeInactive = false)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (!includeInactive) where.Add("is_active = 1");
        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Add("(name LIKE $q OR sku LIKE $q OR barcode LIKE $q)");
            cmd.Parameters.AddWithValue("$q", $"%{search.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(category))
        {
            where.Add("category = $cat");
            cmd.Parameters.AddWithValue("$cat", category);
        }
        if (lowStockOnly) where.Add("reorder_level > 0 AND stock_quantity <= reorder_level");
        cmd.CommandText = $"{SelectColumns} {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")} ORDER BY category, name;";
        return ReadAll(cmd);
    }

    public List<string> Categories()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT category FROM products ORDER BY category;";
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>
    /// Adds a product. With options (e.g. Size × Colour) it creates one variant per combination,
    /// each with its own SKU and opening stock. Returns the new products.
    /// </summary>
    public List<Product> Create(NewProductRequest request, Actor actor)
    {
        if (request.Price < 0 || request.Cost < 0) throw new ArgumentException("Price and cost can't be negative.");
        if (request.VatRate is < 0 or > 1) throw new ArgumentException("VAT rate must be between 0% and 100%.");
        if (request.OpeningStock < 0) throw new ArgumentException("Opening stock can't be negative.");

        var specs = VariantBuilder.Build(request.Name, request.SkuPrefix, request.Option1, request.Option2);
        var category = string.IsNullOrWhiteSpace(request.Category) ? "General" : request.Category.Trim();
        var now = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        long? familyId = null;
        if (specs.Count > 1)
        {
            using var fam = conn.CreateCommand();
            fam.Transaction = tx;
            fam.CommandText = """
                INSERT INTO product_families (name, option1_name, option2_name, created_at) VALUES ($n, $o1, $o2, $now);
                SELECT last_insert_rowid();
                """;
            fam.Parameters.AddWithValue("$n", request.Name.Trim());
            fam.Parameters.AddWithValue("$o1", (object?)request.Option1?.Name ?? DBNull.Value);
            fam.Parameters.AddWithValue("$o2", (object?)request.Option2?.Name ?? DBNull.Value);
            fam.Parameters.AddWithValue("$now", now);
            familyId = (long)fam.ExecuteScalar()!;
        }

        var ids = new List<long>();
        foreach (var spec in specs)
        {
            var sku = UniqueSku(conn, tx, spec.Sku);
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO products (sku, barcode, name, category, price_ngwee, cost_ngwee, vat_rate, stock_quantity,
                                      reorder_level, family_id, option1, option2)
                VALUES ($sku, $barcode, $name, $cat, $price, $cost, $vat, 0, $reorder, $fam, $o1, $o2);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$sku", sku);
            // A barcode can only belong to one item, so it's only used for single (non-variant) products.
            insert.Parameters.AddWithValue("$barcode", specs.Count == 1 && !string.IsNullOrWhiteSpace(request.Barcode) ? request.Barcode.Trim() : DBNull.Value);
            insert.Parameters.AddWithValue("$name", spec.Name);
            insert.Parameters.AddWithValue("$cat", category);
            insert.Parameters.AddWithValue("$price", Money.ToNgwee(request.Price));
            insert.Parameters.AddWithValue("$cost", Money.ToNgwee(request.Cost));
            insert.Parameters.AddWithValue("$vat", (double)request.VatRate);
            insert.Parameters.AddWithValue("$reorder", Math.Max(0, request.ReorderLevel));
            insert.Parameters.AddWithValue("$fam", (object?)familyId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$o1", (object?)spec.Option1 ?? DBNull.Value);
            insert.Parameters.AddWithValue("$o2", (object?)spec.Option2 ?? DBNull.Value);
            long id;
            try
            {
                id = (long)insert.ExecuteScalar()!;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                throw new ArgumentException("That barcode is already used by another product.");
            }
            ids.Add(id);

            if (request.OpeningStock > 0)
            {
                InventoryRepository.ApplyMovement(conn, tx, id, request.OpeningStock, StockReason.InitialStock, "Opening stock", null, actor);
                InventoryRepository.PostStock(conn, tx, GetById(conn, tx, id)!, StockReason.InitialStock, request.OpeningStock, request.Cost, actor);
            }
        }

        AuditRepository.Write(conn, tx, actor, AuditActions.ProductCreated, "product", ids[0].ToString(CultureInfo.InvariantCulture),
            specs.Count == 1
                ? $"Created {specs[0].Name} ({Money.Format(request.Price)}, opening stock {request.OpeningStock})"
                : $"Created {request.Name.Trim()} with {specs.Count} variants ({Money.Format(request.Price)} each, opening stock {request.OpeningStock} each)");

        tx.Commit();
        return ids.Select(id => GetById(id)!).ToList();
    }

    /// <summary>Saves name, category, price, cost, VAT, barcode, reorder level and active flag (not stock — that goes through the stock ledger).</summary>
    public void Update(Product product, Actor actor)
    {
        if (string.IsNullOrWhiteSpace(product.Name)) throw new ArgumentException("Product name is required.");
        if (product.Price < 0 || product.Cost < 0) throw new ArgumentException("Price and cost can't be negative.");

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        var before = GetById(conn, tx, product.Id) ?? throw new InvalidOperationException("Product not found.");

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE products SET name = $name, category = $cat, price_ngwee = $price, cost_ngwee = $cost, vat_rate = $vat,
                   barcode = $barcode, reorder_level = $reorder, is_active = $active
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", product.Id);
        cmd.Parameters.AddWithValue("$name", product.Name.Trim());
        cmd.Parameters.AddWithValue("$cat", string.IsNullOrWhiteSpace(product.Category) ? "General" : product.Category.Trim());
        cmd.Parameters.AddWithValue("$price", Money.ToNgwee(product.Price));
        cmd.Parameters.AddWithValue("$cost", Money.ToNgwee(product.Cost));
        cmd.Parameters.AddWithValue("$vat", (double)product.VatRate);
        cmd.Parameters.AddWithValue("$barcode", string.IsNullOrWhiteSpace(product.Barcode) ? DBNull.Value : product.Barcode.Trim());
        cmd.Parameters.AddWithValue("$reorder", Math.Max(0, product.ReorderLevel));
        cmd.Parameters.AddWithValue("$active", product.IsActive ? 1 : 0);
        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new ArgumentException("That barcode is already used by another product.");
        }

        // Keep the Inventory account equal to the stock value at cost.
        if (before.Cost != product.Cost)
            LedgerRepository.PostAutomatic(conn, tx, Core.Accounting.PostingRules.Revaluation(product.Id, product.Name.Trim(),
                DateOnly.FromDateTime(DateTime.Today), Math.Max(before.StockQuantity, 0) * (product.Cost - before.Cost),
                $"cost {Money.Format(before.Cost)} → {Money.Format(product.Cost)} on {before.StockQuantity} in stock",
                LedgerRepository.SystemAccountLookup(conn, tx)), actor);

        var changes = new List<string>();
        if (before.Name != product.Name.Trim()) changes.Add($"name \"{before.Name}\" → \"{product.Name.Trim()}\"");
        if (before.Price != product.Price) changes.Add($"price {Money.Format(before.Price)} → {Money.Format(product.Price)}");
        if (before.Cost != product.Cost) changes.Add($"cost {Money.Format(before.Cost)} → {Money.Format(product.Cost)}");
        if (before.VatRate != product.VatRate) changes.Add($"VAT {before.VatRate:P0} → {product.VatRate:P0}");
        if (before.Category != product.Category) changes.Add($"category {before.Category} → {product.Category}");
        if ((before.Barcode ?? "") != (product.Barcode ?? "")) changes.Add($"barcode {before.Barcode} → {product.Barcode}");
        if (before.ReorderLevel != product.ReorderLevel) changes.Add($"reorder level {before.ReorderLevel} → {product.ReorderLevel}");
        if (before.IsActive != product.IsActive) changes.Add(product.IsActive ? "re-activated" : "deactivated");
        if (changes.Count > 0)
            AuditRepository.Write(conn, tx, actor, AuditActions.ProductUpdated, "product", product.Id.ToString(CultureInfo.InvariantCulture),
                $"{product.Name.Trim()}: {string.Join("; ", changes)}");

        tx.Commit();
    }

    private static string UniqueSku(SqliteConnection conn, SqliteTransaction tx, string sku)
    {
        var candidate = sku;
        for (var n = 2; ; n++)
        {
            using var check = conn.CreateCommand();
            check.Transaction = tx;
            check.CommandText = "SELECT COUNT(*) FROM products WHERE sku = $s COLLATE NOCASE;";
            check.Parameters.AddWithValue("$s", candidate);
            if ((long)check.ExecuteScalar()! == 0) return candidate;
            candidate = $"{sku}-{n}";
        }
    }

    internal static Product? GetById(SqliteConnection conn, SqliteTransaction tx, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"{SelectColumns} WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
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
                Cost = Money.FromNgwee(r.GetInt64(8)),
                ReorderLevel = r.GetInt32(9),
                IsActive = r.GetInt64(10) == 1,
                FamilyId = r.IsDBNull(11) ? null : r.GetInt64(11),
                Option1 = r.IsDBNull(12) ? null : r.GetString(12),
                Option2 = r.IsDBNull(13) ? null : r.GetString(13),
            });
        }
        return list;
    }
}
