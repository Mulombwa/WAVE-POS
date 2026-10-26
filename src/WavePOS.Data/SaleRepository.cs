using System.Globalization;
using WavePOS.Core;
using WavePOS.Core.Audit;
using WavePOS.Core.Documents;
using WavePOS.Core.Inventory;
using WavePOS.Core.Models;

namespace WavePOS.Data;

public class SaleRepository(PosDatabase db)
{
    /// <summary>
    /// Saves the sale and its lines, takes the items out of stock (via the stock ledger) and
    /// records it in the audit trail — all in one transaction. Returns the new sale id.
    /// </summary>
    public long Save(Sale sale, Actor? actor = null)
    {
        actor ??= new Actor(sale.CashierId, "Till");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO sales (timestamp, cashier_id, customer_id, total_ngwee, vat_ngwee, payment_method, tendered_ngwee, change_ngwee)
                VALUES ($ts, $cashier, $customer, $total, $vat, $method, $tendered, $change);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$ts", sale.Timestamp.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$cashier", (object?)sale.CashierId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$customer", (object?)sale.CustomerId ?? DBNull.Value);
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
                """;
            lineCmd.Parameters.AddWithValue("$sale", sale.Id);
            lineCmd.Parameters.AddWithValue("$product", line.ProductId);
            lineCmd.Parameters.AddWithValue("$name", line.ProductName);
            lineCmd.Parameters.AddWithValue("$price", Money.ToNgwee(line.UnitPrice));
            lineCmd.Parameters.AddWithValue("$qty", line.Quantity);
            lineCmd.Parameters.AddWithValue("$vat", (double)line.VatRate);
            lineCmd.ExecuteNonQuery();

            InventoryRepository.ApplyMovement(conn, tx, line.ProductId, -line.Quantity, StockReason.Sale, "",
                $"Sale #{sale.Id:D6}", actor);
        }

        AuditRepository.Write(conn, tx, actor, AuditActions.SaleCompleted, "sale", sale.Id.ToString(CultureInfo.InvariantCulture),
            $"Sale #{sale.Id:D6}: {sale.Lines.Sum(l => l.Quantity)} item(s), {Money.Format(sale.Total)} by {sale.PaymentMethod.DisplayName()}"
            + (sale.CustomerId is { } c ? $", customer #{c}" : ""));

        tx.Commit();
        return sale.Id;
    }

    /// <summary>A customer's purchases, newest first.</summary>
    public List<(long Id, DateTime Date, decimal Total, PaymentMethod Method, int Items)> ForCustomer(long customerId, int limit = 100)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.timestamp, s.total_ngwee, s.payment_method, COALESCE(SUM(l.quantity), 0)
            FROM sales s LEFT JOIN sale_lines l ON l.sale_id = s.id
            WHERE s.customer_id = $c GROUP BY s.id ORDER BY s.id DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$c", customerId);
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<(long, DateTime, decimal, PaymentMethod, int)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetInt64(0), DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Money.FromNgwee(r.GetInt64(2)), Enum.Parse<PaymentMethod>(r.GetString(3)), r.GetInt32(4)));
        return list;
    }

    /// <summary>Loads a saved sale as a receipt (for reprinting). Null if not found.</summary>
    public SalesDocument? GetReceipt(long saleId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.timestamp, s.total_ngwee, s.vat_ngwee, s.payment_method, s.tendered_ngwee, s.change_ngwee,
                   COALESCE(u.display_name, ''),
                   CASE WHEN c.is_anonymised = 0 THEN c.name END
            FROM sales s
            LEFT JOIN users u ON u.id = s.cashier_id
            LEFT JOIN customers c ON c.id = s.customer_id
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
        var customer = r.IsDBNull(7) ? null : r.GetString(7);
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
            CustomerName = customer,
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
