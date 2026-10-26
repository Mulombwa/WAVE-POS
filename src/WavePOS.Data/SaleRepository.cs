using System.Globalization;
using WavePOS.Core;
using WavePOS.Core.Documents;
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
                INSERT INTO sales (timestamp, cashier_id, total_ngwee, vat_ngwee, payment_method, tendered_ngwee, change_ngwee)
                VALUES ($ts, $cashier, $total, $vat, $method, $tendered, $change);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$ts", sale.Timestamp.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$cashier", (object?)sale.CashierId ?? DBNull.Value);
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

    /// <summary>Loads a saved sale as a receipt (for reprinting). Null if not found.</summary>
    public SalesDocument? GetReceipt(long saleId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.timestamp, s.total_ngwee, s.vat_ngwee, s.payment_method, s.tendered_ngwee, s.change_ngwee,
                   COALESCE(u.display_name, '')
            FROM sales s LEFT JOIN users u ON u.id = s.cashier_id
            WHERE s.id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", saleId);

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var timestamp = DateTime.Parse(r.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var total = Money.FromNgwee(r.GetInt64(1));
        var vat = Money.FromNgwee(r.GetInt64(2));
        var method = Enum.Parse<PaymentMethod>(r.GetString(3));
        var tendered = Money.FromNgwee(r.GetInt64(4));
        var change = Money.FromNgwee(r.GetInt64(5));
        var cashier = r.GetString(6);
        r.Close();

        using var lines = conn.CreateCommand();
        lines.CommandText = """
            SELECT product_id, product_name, quantity, unit_price_ngwee, vat_rate
            FROM sale_lines WHERE sale_id = $id ORDER BY id;
            """;
        lines.Parameters.AddWithValue("$id", saleId);
        var list = new List<DocumentLine>();
        using (var lr = lines.ExecuteReader())
            while (lr.Read())
                list.Add(new DocumentLine(lr.GetInt64(0), lr.GetString(1), lr.GetInt32(2),
                    Money.FromNgwee(lr.GetInt64(3)), (decimal)lr.GetDouble(4)));

        return new SalesDocument
        {
            Kind = DocumentKind.Receipt,
            Number = saleId.ToString("D6"),
            Date = timestamp,
            CashierName = cashier,
            Lines = list,
            Total = total,
            VatAmount = vat,
            PaymentMethod = method,
            AmountTendered = tendered,
            Change = change,
        };
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
