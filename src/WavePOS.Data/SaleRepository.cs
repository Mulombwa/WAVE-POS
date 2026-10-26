using System.Globalization;
using WavePOS.Core;
using WavePOS.Core.Models;

namespace WavePOS.Data;

public class SaleRepository(PosDatabase db)
{
    /// <summary>Saves the sale and its lines, and reduces stock, in one transaction. Returns the new sale id.</summary>
    public long Save(Sale sale)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO sales (timestamp, total_ngwee, vat_ngwee, payment_method, tendered_ngwee, change_ngwee)
                VALUES ($ts, $total, $vat, $method, $tendered, $change);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$ts", sale.Timestamp.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$total", Money.ToNgwee(sale.Total));
            cmd.Parameters.AddWithValue("$vat", Money.ToNgwee(sale.VatAmount));
            cmd.Parameters.AddWithValue("$method", sale.PaymentMethod.ToString());
            cmd.Parameters.AddWithValue("$tendered", Money.ToNgwee(sale.AmountTendered));
            cmd.Parameters.AddWithValue("$change", Money.ToNgwee(sale.Change));
            sale.Id = (long)cmd.ExecuteScalar()!;
        }

        foreach (var line in sale.Lines)
        {
            using var lineCmd = conn.CreateCommand();
            lineCmd.Transaction = tx;
            lineCmd.CommandText = """
                INSERT INTO sale_lines (sale_id, product_id, product_name, unit_price_ngwee, quantity, vat_rate)
                VALUES ($sale, $product, $name, $price, $qty, $vat);
                UPDATE products SET stock_quantity = stock_quantity - $qty WHERE id = $product;
                """;
            lineCmd.Parameters.AddWithValue("$sale", sale.Id);
            lineCmd.Parameters.AddWithValue("$product", line.ProductId);
            lineCmd.Parameters.AddWithValue("$name", line.ProductName);
            lineCmd.Parameters.AddWithValue("$price", Money.ToNgwee(line.UnitPrice));
            lineCmd.Parameters.AddWithValue("$qty", line.Quantity);
            lineCmd.Parameters.AddWithValue("$vat", (double)line.VatRate);
            lineCmd.ExecuteNonQuery();
        }

        tx.Commit();
        return sale.Id;
    }

    /// <summary>Total takings for a given day (for the end-of-day screen later).</summary>
    public (int Count, decimal Total) GetDayTotals(DateTime day)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(total_ngwee), 0) FROM sales WHERE substr(timestamp, 1, 10) = $day;";
        cmd.Parameters.AddWithValue("$day", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt32(0), Money.FromNgwee(r.GetInt64(1)));
    }
}
