using System.Globalization;
using Microsoft.Data.Sqlite;
using WavePOS.Core;
using WavePOS.Core.Accounting;
using WavePOS.Core.Audit;
using WavePOS.Core.Inventory;

namespace WavePOS.Data;

public record InventorySummary(int ProductCount, int TotalUnits, decimal StockValue, decimal RetailValue, int LowStockCount, int OutOfStockCount);

/// <summary>
/// Stock changes. Every change goes through <see cref="ApplyMovement"/>, which updates the
/// stock level and writes a line in the stock ledger in the same transaction.
/// </summary>
public class InventoryRepository(PosDatabase db)
{
    /// <summary>Goods delivered. Updates the cost to a weighted average when a unit cost is given.</summary>
    public int Receive(long productId, int quantity, decimal? unitCost, string note, Actor actor, string? reference = null)
    {
        if (quantity <= 0) throw new ArgumentException("Enter how many units arrived.");
        if (unitCost < 0) throw new ArgumentException("Cost can't be negative.");

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var product = ProductRepository.GetById(conn, tx, productId) ?? throw new InvalidOperationException("Product not found.");

        var cost = unitCost ?? product.Cost;
        var rounding = AverageIn(conn, tx, product, quantity, cost);
        var balance = ApplyMovement(conn, tx, productId, quantity, StockReason.Received, note, reference, actor);
        PostStock(conn, tx, product, StockReason.Received, quantity, cost, actor);
        PostRounding(conn, tx, product, rounding, actor);
        AuditRepository.Write(conn, tx, actor, AuditActions.StockReceived, "product", productId.ToString(CultureInfo.InvariantCulture),
            $"{product.Name}: received {quantity}{(unitCost is { } c2 ? $" at {Money.Format(c2)}" : "")} → {balance} on hand. {note}".Trim());
        tx.Commit();
        return balance;
    }

    /// <summary>Manual change, e.g. -2 Damaged. Needs a reason other than Sale/Return/Received.</summary>
    public int Adjust(long productId, int change, StockReason reason, string note, Actor actor)
    {
        if (change == 0) throw new ArgumentException("Enter a change other than zero.");
        if (reason is StockReason.Sale or StockReason.Return or StockReason.Received)
            throw new ArgumentException("Use the till, returns or Receive stock for that.");
        if (string.IsNullOrWhiteSpace(note) && reason == StockReason.Adjustment)
            throw new ArgumentException("Say why the stock is being adjusted.");

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var product = ProductRepository.GetById(conn, tx, productId) ?? throw new InvalidOperationException("Product not found.");
        var balance = ApplyMovement(conn, tx, productId, change, reason, note, null, actor);
        PostStock(conn, tx, product, reason, change, product.Cost, actor);
        AuditRepository.Write(conn, tx, actor, AuditActions.StockAdjusted, "product", productId.ToString(CultureInfo.InvariantCulture),
            $"{product.Name}: {change:+0;-0} ({reason}) → {balance} on hand. {note}".Trim());
        tx.Commit();
        return balance;
    }

    /// <summary>Stock take: sets the level to what was counted and records the difference.</summary>
    public int Count(long productId, int counted, string note, Actor actor)
    {
        if (counted < 0) throw new ArgumentException("The count can't be negative.");

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var product = ProductRepository.GetById(conn, tx, productId) ?? throw new InvalidOperationException("Product not found.");
        var change = counted - product.StockQuantity;
        if (change != 0)
        {
            ApplyMovement(conn, tx, productId, change, StockReason.StockCount, note, null, actor);
            PostStock(conn, tx, product, StockReason.StockCount, change, product.Cost, actor);
        }
        AuditRepository.Write(conn, tx, actor, AuditActions.StockAdjusted, "product", productId.ToString(CultureInfo.InvariantCulture),
            $"{product.Name}: stock count {counted} (was {product.StockQuantity}, difference {change:+0;-0;0}). {note}".Trim());
        tx.Commit();
        return counted;
    }

    public List<StockMovement> History(long productId, int limit = 200)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT m.id, m.product_id, p.name, m.change, m.balance_after, m.reason, m.note, m.reference, m.user_name, m.timestamp
            FROM stock_movements m JOIN products p ON p.id = m.product_id
            WHERE m.product_id = $p ORDER BY m.id DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$p", productId);
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<StockMovement>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new StockMovement(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4),
                Enum.TryParse<StockReason>(r.GetString(5), out var reason) ? reason : StockReason.Adjustment,
                r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8),
                DateTime.Parse(r.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        return list;
    }

    public InventorySummary Summary()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(MAX(stock_quantity, 0)), 0),
                   COALESCE(SUM(MAX(stock_quantity, 0) * cost_ngwee), 0),
                   COALESCE(SUM(MAX(stock_quantity, 0) * price_ngwee), 0),
                   COALESCE(SUM(CASE WHEN reorder_level > 0 AND stock_quantity <= reorder_level THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN stock_quantity <= 0 THEN 1 ELSE 0 END), 0)
            FROM products WHERE is_active = 1;
            """;
        using var r = cmd.ExecuteReader();
        r.Read();
        return new InventorySummary(r.GetInt32(0), r.GetInt32(1), Money.FromNgwee(r.GetInt64(2)),
            Money.FromNgwee(r.GetInt64(3)), r.GetInt32(4), r.GetInt32(5));
    }

    /// <summary>
    /// Stock coming in at a unit cost: moves the product's cost to the weighted average of what
    /// was on hand and what came in. Call before <see cref="ApplyMovement"/>. Returns the small
    /// difference (from rounding the average to the ngwee) between the new stock value and the old
    /// value plus what came in, to post with <see cref="PostRounding"/>.
    /// </summary>
    internal static decimal AverageIn(SqliteConnection conn, SqliteTransaction tx, Core.Models.Product product, int quantity, decimal unitCost)
    {
        var onHand = Math.Max(product.StockQuantity, 0);
        var average = Money.Round(onHand + quantity <= 0 ? unitCost : (product.Cost * onHand + unitCost * quantity) / (onHand + quantity));
        if (average != product.Cost)
        {
            using var upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = "UPDATE products SET cost_ngwee = $c WHERE id = $id;";
            upd.Parameters.AddWithValue("$c", Money.ToNgwee(average));
            upd.Parameters.AddWithValue("$id", product.Id);
            upd.ExecuteNonQuery();
        }
        var before = onHand * product.Cost;
        var after = Math.Max(product.StockQuantity + quantity, 0) * average;
        return Money.Round(after - before - quantity * unitCost);
    }

    internal static void PostRounding(SqliteConnection conn, SqliteTransaction tx, Core.Models.Product product, decimal difference, Actor actor) =>
        LedgerRepository.PostAutomatic(conn, tx, PostingRules.Revaluation(product.Id, product.Name, DateOnly.FromDateTime(DateTime.Today),
            difference, "rounding on average cost", LedgerRepository.SystemAccountLookup(conn, tx)), actor);

    /// <summary>Posts a stock change to the books at cost (Inventory against supplier, opening balances or stock losses).</summary>
    internal static void PostStock(SqliteConnection conn, SqliteTransaction tx, Core.Models.Product product, StockReason reason,
        int change, decimal unitCost, Actor actor) =>
        LedgerRepository.PostAutomatic(conn, tx, PostingRules.StockMovement(product.Id, product.Name, DateOnly.FromDateTime(DateTime.Today),
            reason, change, unitCost, LedgerRepository.SystemAccountLookup(conn, tx)), actor);

    /// <summary>Changes stock and records it in the ledger, inside the caller's transaction. Returns the new level.</summary>
    internal static int ApplyMovement(SqliteConnection conn, SqliteTransaction tx, long productId, int change,
        StockReason reason, string note, string? reference, Actor actor)
    {
        using var upd = conn.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = """
            UPDATE products SET stock_quantity = stock_quantity + $c WHERE id = $id;
            SELECT stock_quantity FROM products WHERE id = $id;
            """;
        upd.Parameters.AddWithValue("$c", change);
        upd.Parameters.AddWithValue("$id", productId);
        var balance = Convert.ToInt32(upd.ExecuteScalar() ?? throw new InvalidOperationException("Product not found."));

        using var mv = conn.CreateCommand();
        mv.Transaction = tx;
        mv.CommandText = """
            INSERT INTO stock_movements (product_id, change, balance_after, reason, note, reference, user_id, user_name, timestamp)
            VALUES ($p, $c, $b, $r, $n, $ref, $uid, $uname, $ts);
            """;
        mv.Parameters.AddWithValue("$p", productId);
        mv.Parameters.AddWithValue("$c", change);
        mv.Parameters.AddWithValue("$b", balance);
        mv.Parameters.AddWithValue("$r", reason.ToString());
        mv.Parameters.AddWithValue("$n", note.Trim());
        mv.Parameters.AddWithValue("$ref", (object?)reference ?? DBNull.Value);
        mv.Parameters.AddWithValue("$uid", (object?)actor.UserId ?? DBNull.Value);
        mv.Parameters.AddWithValue("$uname", actor.Name);
        mv.Parameters.AddWithValue("$ts", DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
        mv.ExecuteNonQuery();
        return balance;
    }
}
