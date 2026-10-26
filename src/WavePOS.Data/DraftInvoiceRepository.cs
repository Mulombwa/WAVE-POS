using System.Globalization;
using WavePOS.Core;
using WavePOS.Core.Documents;

namespace WavePOS.Data;

public class DraftInvoiceRepository(PosDatabase db)
{
    /// <summary>Saves a draft invoice, gives it a number like DRF-00001, and sets <see cref="SalesDocument.Number"/>.</summary>
    public string Save(SalesDocument draft, long? cashierId)
    {
        if (draft.Kind != DocumentKind.DraftInvoice) throw new ArgumentException("Only draft invoices can be saved here.");

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        long id;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO draft_invoices (created_at, valid_until, cashier_id, customer_name, total_ngwee, vat_ngwee)
                VALUES ($created, $valid, $cashier, $customer, $total, $vat);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$created", draft.Date.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$valid", (object?)draft.ValidUntil?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cashier", (object?)cashierId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$customer", (object?)draft.CustomerName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$total", Money.ToNgwee(draft.Total));
            cmd.Parameters.AddWithValue("$vat", Money.ToNgwee(draft.VatAmount));
            id = (long)cmd.ExecuteScalar()!;
        }

        var number = $"DRF-{id:D5}";
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE draft_invoices SET number = $n WHERE id = $id;";
            cmd.Parameters.AddWithValue("$n", number);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        foreach (var line in draft.Lines)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO draft_invoice_lines (draft_id, product_id, description, unit_price_ngwee, quantity, vat_rate)
                VALUES ($d, $p, $desc, $price, $qty, $vat);
                """;
            cmd.Parameters.AddWithValue("$d", id);
            cmd.Parameters.AddWithValue("$p", (object?)line.ProductId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$desc", line.Description);
            cmd.Parameters.AddWithValue("$price", Money.ToNgwee(line.UnitPrice));
            cmd.Parameters.AddWithValue("$qty", line.Quantity);
            cmd.Parameters.AddWithValue("$vat", (double)line.VatRate);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        draft.Number = number;
        return number;
    }

    public int Count()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM draft_invoices;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
