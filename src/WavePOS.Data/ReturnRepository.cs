using System.Globalization;
using Microsoft.Data.Sqlite;
using WavePOS.Core;
using WavePOS.Core.Accounting;
using WavePOS.Core.Audit;
using WavePOS.Core.Documents;
using WavePOS.Core.Inventory;
using WavePOS.Core.Models;
using WavePOS.Core.Returns;

namespace WavePOS.Data;

public record ReturnSummary(long Id, long SaleId, DateTime Date, decimal Total, PaymentMethod Method, ReturnReason Reason, string UserName);

/// <summary>Returns, refunds and exchanges against a previous sale.</summary>
public class ReturnRepository(PosDatabase db)
{
    /// <summary>The sale's lines with how many of each can still be returned. Empty if the sale doesn't exist.</summary>
    public List<ReturnableLine> GetReturnable(long saleId)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction(deferred: true);
        return GetReturnable(conn, tx, saleId);
    }

    /// <summary>
    /// Processes a refund: checks quantities, saves it, puts restockable items back in stock
    /// and writes the audit trail — all or nothing. Returns the refund id.
    /// </summary>
    public long Process(ReturnRequest request, Actor actor)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        var saleLines = GetReturnable(conn, tx, request.SaleId);
        if (saleLines.Count == 0) throw new InvalidOperationException($"Sale #{request.SaleId:D6} wasn't found.");
        var lines = ReturnCalculator.Validate(request, saleLines);
        var total = ReturnCalculator.Total(lines);
        var vat = ReturnCalculator.Vat(lines);

        long returnId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO returns (sale_id, timestamp, user_id, reason, note, refund_method, total_ngwee, vat_ngwee)
                VALUES ($sale, $ts, $uid, $reason, $note, $method, $total, $vat);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$sale", request.SaleId);
            cmd.Parameters.AddWithValue("$ts", DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$uid", (object?)actor.UserId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$reason", request.Reason.ToString());
            cmd.Parameters.AddWithValue("$note", request.Note.Trim());
            cmd.Parameters.AddWithValue("$method", request.RefundMethod.ToString());
            cmd.Parameters.AddWithValue("$total", Money.ToNgwee(total));
            cmd.Parameters.AddWithValue("$vat", Money.ToNgwee(vat));
            returnId = (long)cmd.ExecuteScalar()!;
        }

        var restockedCost = 0m;
        var rounding = new List<(Product Product, decimal Difference)>();
        foreach (var l in lines)
        {
            if (l.Restock)
            {
                // Goods come back at the cost they were sold at, averaged into the current cost.
                var soldAt = SoldAtCost(conn, tx, l.Line.SaleLineId);
                restockedCost += soldAt * l.Quantity;
                var product = ProductRepository.GetById(conn, tx, l.Line.ProductId)!;
                rounding.Add((product, InventoryRepository.AverageIn(conn, tx, product, l.Quantity, soldAt)));
            }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO return_lines (return_id, sale_line_id, product_id, product_name, unit_price_ngwee, quantity, vat_rate, restocked)
                VALUES ($r, $sl, $p, $name, $price, $qty, $vat, $restock);
                """;
            cmd.Parameters.AddWithValue("$r", returnId);
            cmd.Parameters.AddWithValue("$sl", l.Line.SaleLineId);
            cmd.Parameters.AddWithValue("$p", l.Line.ProductId);
            cmd.Parameters.AddWithValue("$name", l.Line.ProductName);
            cmd.Parameters.AddWithValue("$price", Money.ToNgwee(l.Line.UnitPrice));
            cmd.Parameters.AddWithValue("$qty", l.Quantity);
            cmd.Parameters.AddWithValue("$vat", (double)l.Line.VatRate);
            cmd.Parameters.AddWithValue("$restock", l.Restock ? 1 : 0);
            cmd.ExecuteNonQuery();

            if (l.Restock)
                InventoryRepository.ApplyMovement(conn, tx, l.Line.ProductId, l.Quantity, StockReason.Return,
                    request.Reason.ToString(), $"Refund R{returnId:D6} (sale #{request.SaleId:D6})", actor);
        }

        LedgerRepository.PostAutomatic(conn, tx, PostingRules.Refund(returnId, request.SaleId, DateOnly.FromDateTime(DateTime.Today),
            request.RefundMethod, total, vat, restockedCost, LedgerRepository.SystemAccountLookup(conn, tx)), actor);
        foreach (var (product, difference) in rounding)
            InventoryRepository.PostRounding(conn, tx, product, difference, actor);

        AuditRepository.Write(conn, tx, actor, AuditActions.ReturnProcessed, "return", returnId.ToString(CultureInfo.InvariantCulture),
            $"Refund R{returnId:D6} against sale #{request.SaleId:D6}: {lines.Sum(l => l.Quantity)} item(s), " +
            $"{Money.Format(total)} by {request.RefundMethod.DisplayName()}, reason {request.Reason}" +
            (lines.Any(l => !l.Restock) ? $", {lines.Where(l => !l.Restock).Sum(l => l.Quantity)} not restocked" : ""));

        tx.Commit();
        return returnId;
    }

    /// <summary>The refund slip to print and give the customer.</summary>
    public SalesDocument? GetRefundDocument(long returnId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.sale_id, r.timestamp, r.reason, r.refund_method, r.total_ngwee, r.vat_ngwee,
                   COALESCE(u.display_name, ''), CASE WHEN c.is_anonymised = 0 THEN c.name END
            FROM returns r
            LEFT JOIN users u ON u.id = r.user_id
            LEFT JOIN sales s ON s.id = r.sale_id
            LEFT JOIN customers c ON c.id = s.customer_id
            WHERE r.id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", returnId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var saleId = r.GetInt64(0);
        var date = DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var reason = r.GetString(2);
        var method = Enum.Parse<PaymentMethod>(r.GetString(3));
        var total = Money.FromNgwee(r.GetInt64(4));
        var vat = Money.FromNgwee(r.GetInt64(5));
        var user = r.GetString(6);
        var customer = r.IsDBNull(7) ? null : r.GetString(7);
        r.Close();

        using var lines = conn.CreateCommand();
        lines.CommandText = "SELECT product_id, product_name, quantity, unit_price_ngwee, vat_rate FROM return_lines WHERE return_id = $id ORDER BY id;";
        lines.Parameters.AddWithValue("$id", returnId);
        var list = new List<DocumentLine>();
        using (var lr = lines.ExecuteReader())
            while (lr.Read())
                list.Add(new DocumentLine(lr.GetInt64(0), lr.GetString(1), lr.GetInt32(2), Money.FromNgwee(lr.GetInt64(3)), (decimal)lr.GetDouble(4)));

        return new SalesDocument
        {
            Kind = DocumentKind.Refund,
            Number = $"R{returnId:D6}",
            Date = date,
            CashierName = user,
            CustomerName = customer,
            Lines = list,
            Total = total,
            VatAmount = vat,
            PaymentMethod = method,
            AmountTendered = total,
            OriginalReceiptNumber = saleId.ToString("D6"),
            ReturnReason = Enum.TryParse<ReturnReason>(reason, out var parsed) ? parsed.Label() : reason,
        };
    }

    public List<ReturnSummary> Recent(int limit = 50)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.sale_id, r.timestamp, r.total_ngwee, r.refund_method, r.reason, COALESCE(u.display_name, '')
            FROM returns r LEFT JOIN users u ON u.id = r.user_id ORDER BY r.id DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<ReturnSummary>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ReturnSummary(r.GetInt64(0), r.GetInt64(1),
                DateTime.Parse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Money.FromNgwee(r.GetInt64(3)), Enum.Parse<PaymentMethod>(r.GetString(4)),
                Enum.TryParse<ReturnReason>(r.GetString(5), out var reason) ? reason : ReturnReason.Other, r.GetString(6)));
        return list;
    }

    /// <summary>The unit cost recorded when the item was sold (older sales fall back to today's cost).</summary>
    private static decimal SoldAtCost(SqliteConnection conn, SqliteTransaction tx, long saleLineId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT CASE WHEN l.unit_cost_ngwee > 0 THEN l.unit_cost_ngwee ELSE COALESCE(p.cost_ngwee, 0) END
            FROM sale_lines l LEFT JOIN products p ON p.id = l.product_id WHERE l.id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", saleLineId);
        return Money.FromNgwee(cmd.ExecuteScalar() as long? ?? 0);
    }

    private static List<ReturnableLine> GetReturnable(SqliteConnection conn, SqliteTransaction tx, long saleId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT l.id, l.product_id, l.product_name, l.unit_price_ngwee, l.vat_rate, l.quantity,
                   COALESCE((SELECT SUM(rl.quantity) FROM return_lines rl WHERE rl.sale_line_id = l.id), 0)
            FROM sale_lines l WHERE l.sale_id = $s ORDER BY l.id;
            """;
        cmd.Parameters.AddWithValue("$s", saleId);
        var list = new List<ReturnableLine>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ReturnableLine(r.GetInt64(0), r.GetInt64(1), r.GetString(2), Money.FromNgwee(r.GetInt64(3)),
                (decimal)r.GetDouble(4), r.GetInt32(5), r.GetInt32(6)));
        return list;
    }
}
