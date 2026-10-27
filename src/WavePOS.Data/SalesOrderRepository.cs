using System.Globalization;
using Microsoft.Data.Sqlite;
using WavePOS.Core;
using WavePOS.Core.Accounting;
using WavePOS.Core.Audit;
using WavePOS.Core.Inventory;
using WavePOS.Core.Orders;
using WavePOS.Core.Security;

namespace WavePOS.Data;

public record SoLineInput(long ProductId, int Quantity, decimal UnitPrice);

public record CustomerInvoice(long Id, long CustomerId, string CustomerName, long OrderId, DateOnly Date, DateOnly DueDate,
    decimal Total, decimal Vat, decimal Paid, long? JournalEntryId)
{
    public string Number => $"INV{Id:D6}";
    public decimal Outstanding => Total - Paid;
    public AgeBucket Age(DateOnly today) => OrderRules.Age(DueDate, today);
}

public record DeliveryView(long Id, long OrderId, DateOnly Date, string Note, string UserName, List<(string Description, int Quantity)> Lines)
{
    public string Number => $"DN{Id:D6}";
}

public record ProductAvailability(int OnHand, int Reserved)
{
    public int Available => OnHand - Reserved;
}

public record SalesSummary(int OpenQuotes, int ToDeliver, int ToInvoice, decimal Owed, decimal Overdue);

/// <summary>Thrown when an order would take a customer over their credit limit.</summary>
public class CreditLimitException(string message) : InvalidOperationException(message);

/// <summary>
/// Order-to-cash: quotes, sales orders (with a credit check), deliveries from stock, tax invoices
/// for what was delivered, and customer payments. Every step is audited and posts to the books.
/// </summary>
public class SalesOrderRepository(PosDatabase db)
{
    // ── Credit terms ────────────────────────────────────────────────────

    public CreditTerms GetCreditTerms(long customerId)
    {
        using var conn = db.Open();
        return GetCreditTerms(conn, null, customerId);
    }

    /// <summary>Sets a customer's TPIN, credit limit (null = no limit) and payment terms.</summary>
    public void SetCreditTerms(long customerId, CreditTerms terms, Actor actor)
    {
        if (terms.CreditLimit < 0) throw new ArgumentException("The credit limit can't be negative.");
        if (terms.PaymentTermsDays is < 0 or > 365) throw new ArgumentException("Payment terms must be between 0 and 365 days.");
        var tpin = terms.Tpin.Trim();
        if (tpin.Length > 0 && (tpin.Length != 10 || !tpin.All(char.IsAsciiDigit))) throw new ArgumentException("A ZRA TPIN is 10 digits.");

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var before = GetCreditTerms(conn, tx, customerId);
        PurchasingRepository.Exec(conn, tx, "UPDATE customers SET tpin = $t, credit_limit_ngwee = $l, payment_terms_days = $d WHERE id = $id;",
            ("$t", tpin), ("$l", terms.CreditLimit is { } l ? Money.ToNgwee(l) : (object)DBNull.Value), ("$d", terms.PaymentTermsDays), ("$id", customerId));
        AuditRepository.Write(conn, tx, actor, AuditActions.CreditTermsChanged, "customer", Str(customerId),
            $"C{customerId:D5} credit terms: limit {Limit(before.CreditLimit)} → {Limit(terms.CreditLimit)}, " +
            $"terms {before.PaymentTermsDays} → {terms.PaymentTermsDays} days{(before.Tpin != tpin ? ", TPIN changed" : "")}");
        tx.Commit();
    }

    /// <summary>What the customer owes plus confirmed orders not yet invoiced: what counts against the credit limit.</summary>
    public decimal Exposure(long customerId, long? excludingOrderId = null)
    {
        using var conn = db.Open();
        return Exposure(conn, null, customerId, excludingOrderId);
    }

    // ── Quotes and orders ───────────────────────────────────────────────

    public long CreateQuote(long customerId, DateOnly? validUntil, string notes, IReadOnlyList<SoLineInput> lines, Actor actor)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var customer = CustomerName(conn, tx, customerId);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO sales_orders (customer_id, status, order_date, valid_until, notes, created_by, created_by_name, created_at)
            VALUES ($c, 'Quote', $date, $valid, $notes, $uid, $uname, $now);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$c", customerId);
        cmd.Parameters.AddWithValue("$date", LedgerRepository.Day(Today));
        cmd.Parameters.AddWithValue("$valid", validUntil is { } v ? LedgerRepository.Day(v) : DBNull.Value);
        cmd.Parameters.AddWithValue("$notes", notes.Trim());
        cmd.Parameters.AddWithValue("$uid", (object?)actor.UserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uname", actor.Name);
        cmd.Parameters.AddWithValue("$now", Now());
        var id = (long)cmd.ExecuteScalar()!;
        var total = WriteLines(conn, tx, id, lines);
        AuditRepository.Write(conn, tx, actor, AuditActions.QuoteCreated, "sales_order", Str(id),
            $"Quote SO{id:D6} for {customer}: {lines.Count} line(s), {Money.Format(total)}");
        tx.Commit();
        return id;
    }

    public void UpdateQuote(long orderId, long customerId, DateOnly? validUntil, string notes, IReadOnlyList<SoLineInput> lines, Actor actor)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var order = GetOrder(conn, tx, orderId) ?? throw new InvalidOperationException("Order not found.");
        if (!order.CanEdit) throw new InvalidOperationException("Only quotes can be changed. This order has been confirmed.");
        var customer = CustomerName(conn, tx, customerId);
        PurchasingRepository.Exec(conn, tx, "UPDATE sales_orders SET customer_id = $c, valid_until = $v, notes = $n WHERE id = $id;",
            ("$c", customerId), ("$v", validUntil is { } v ? LedgerRepository.Day(v) : (object)DBNull.Value), ("$n", notes.Trim()), ("$id", orderId));
        PurchasingRepository.Exec(conn, tx, "DELETE FROM sales_order_lines WHERE order_id = $id;", ("$id", orderId));
        var total = WriteLines(conn, tx, orderId, lines);
        AuditRepository.Write(conn, tx, actor, AuditActions.QuoteChanged, "sales_order", Str(orderId),
            $"Quote SO{orderId:D6} changed: {customer}, {lines.Count} line(s), {Money.Format(total)}");
        tx.Commit();
    }

    /// <summary>
    /// The customer accepts: the quote becomes an order and its stock is reserved. If it would take
    /// them over their credit limit, it needs someone allowed to manage credit and a reason
    /// (<paramref name="overrideReason"/>); otherwise <see cref="CreditLimitException"/> is thrown.
    /// </summary>
    public void Confirm(long orderId, Actor actor, string? overrideReason = null)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var order = GetOrder(conn, tx, orderId) ?? throw new InvalidOperationException("Order not found.");
        if (order.Status != SalesOrderStatus.Quote) throw new InvalidOperationException("This order is already confirmed.");
        if (order.Lines.Count == 0) throw new InvalidOperationException("Add at least one item first.");

        var terms = GetCreditTerms(conn, tx, order.CustomerId);
        var exposure = Exposure(conn, tx, order.CustomerId, orderId);
        string? overrideNote = null;
        if (OrderRules.CheckCredit(terms, exposure, order.Total) is { } problem)
        {
            if (string.IsNullOrWhiteSpace(overrideReason)) throw new CreditLimitException(problem);
            if (!PurchasingRepository.ActiveUsersWho(conn, tx, Permission.ManageCredit).Contains(actor.UserId ?? 0))
                throw new CreditLimitException(problem + " Ask a manager to approve it.");
            overrideNote = $"{actor.Name}: {overrideReason.Trim()}";
            AuditRepository.Write(conn, tx, actor, AuditActions.CreditOverride, "sales_order", Str(orderId),
                $"SO{orderId:D6} confirmed over the credit limit for {order.CustomerName}. {problem} Reason: {overrideReason.Trim()}");
        }

        PurchasingRepository.Exec(conn, tx, "UPDATE sales_orders SET status = 'Confirmed', order_date = $d, credit_override = $o WHERE id = $id;",
            ("$d", LedgerRepository.Day(Today)), ("$o", (object?)overrideNote ?? DBNull.Value), ("$id", orderId));
        AuditRepository.Write(conn, tx, actor, AuditActions.OrderConfirmed, "sales_order", Str(orderId),
            $"SO{orderId:D6} confirmed for {order.CustomerName}: {Money.Format(order.Total)}");
        tx.Commit();
    }

    /// <summary>Cancels a quote or an order that nothing has been delivered on.</summary>
    public void Cancel(long orderId, string reason, Actor actor)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Say why the order is being cancelled.");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var order = GetOrder(conn, tx, orderId) ?? throw new InvalidOperationException("Order not found.");
        if (order.Lines.Any(l => l.Delivered > 0)) throw new InvalidOperationException("Goods have been delivered on this order, so it can't be cancelled.");
        if (order.Status == SalesOrderStatus.Cancelled) return;
        PurchasingRepository.Exec(conn, tx, "UPDATE sales_orders SET status = 'Cancelled' WHERE id = $id;", ("$id", orderId));
        AuditRepository.Write(conn, tx, actor, AuditActions.OrderCancelled, "sales_order", Str(orderId), $"SO{orderId:D6} cancelled: {reason.Trim()}");
        tx.Commit();
    }

    /// <summary>
    /// Goods leave the warehouse: takes them out of stock (it must be on hand) and posts cost of
    /// sales. Part deliveries are fine. Returns the delivery note id.
    /// </summary>
    public long Deliver(long orderId, IReadOnlyDictionary<long, int> quantities, string note, Actor actor)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var order = GetOrder(conn, tx, orderId) ?? throw new InvalidOperationException("Order not found.");
        if (!order.CanDeliver) throw new InvalidOperationException(order.Status == SalesOrderStatus.Quote
            ? "Confirm the order before delivering." : "This order is closed.");
        if (OrderRules.CheckQuantities(quantities, order.Lines.ToDictionary(l => l.Id, l => (l.Description, l.ToDeliver))) is { } problem)
            throw new InvalidOperationException(problem);
        foreach (var group in order.Lines.Where(l => quantities.GetValueOrDefault(l.Id) > 0).GroupBy(l => l.ProductId))
        {
            var wanted = group.Sum(l => quantities[l.Id]);
            var product = ProductRepository.GetById(conn, tx, group.Key)!;
            if (product.StockQuantity < wanted)
                throw new InvalidOperationException($"Only {Math.Max(product.StockQuantity, 0)} × {product.Name} in stock. Receive more stock first.");
        }

        long deliveryId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO deliveries (order_id, delivered_on, note, user_name, created_at) VALUES ($o, $d, $n, $u, $now);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$o", orderId);
            cmd.Parameters.AddWithValue("$d", LedgerRepository.Day(Today));
            cmd.Parameters.AddWithValue("$n", note.Trim());
            cmd.Parameters.AddWithValue("$u", actor.Name);
            cmd.Parameters.AddWithValue("$now", Now());
            deliveryId = (long)cmd.ExecuteScalar()!;
        }

        decimal cost = 0;
        var items = 0;
        foreach (var line in order.Lines)
        {
            var qty = quantities.GetValueOrDefault(line.Id);
            if (qty == 0) continue;
            var unitCost = ProductRepository.GetById(conn, tx, line.ProductId)!.Cost;
            InventoryRepository.ApplyMovement(conn, tx, line.ProductId, -qty, StockReason.Sale, note, $"DN{deliveryId:D6} (SO{orderId:D6})", actor);
            PurchasingRepository.Exec(conn, tx, "UPDATE sales_order_lines SET delivered = delivered + $q WHERE id = $id;", ("$q", qty), ("$id", line.Id));
            PurchasingRepository.Exec(conn, tx, "INSERT INTO delivery_lines (delivery_id, order_line_id, quantity, unit_cost_ngwee) VALUES ($d, $l, $q, $c);",
                ("$d", deliveryId), ("$l", line.Id), ("$q", qty), ("$c", Money.ToNgwee(unitCost)));
            line.Delivered += qty;
            cost += qty * unitCost;
            items += qty;
        }

        PurchasingRepository.Exec(conn, tx, "UPDATE sales_orders SET status = $s WHERE id = $id;",
            ("$s", OrderRules.AfterDelivery(order.Lines).ToString()), ("$id", orderId));
        LedgerRepository.PostAutomatic(conn, tx,
            PostingRules.Delivery(orderId, deliveryId, order.CustomerName, Today, cost, LedgerRepository.SystemAccountLookup(conn, tx)), actor);
        AuditRepository.Write(conn, tx, actor, AuditActions.OrderDelivered, "sales_order", Str(orderId),
            $"DN{deliveryId:D6}: delivered {items} item(s) to {order.CustomerName} on SO{orderId:D6}");
        tx.Commit();
        return deliveryId;
    }

    /// <summary>Raises a tax invoice for everything delivered but not yet invoiced. Returns the invoice id.</summary>
    public long Invoice(long orderId, DateOnly date, Actor actor)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var order = GetOrder(conn, tx, orderId) ?? throw new InvalidOperationException("Order not found.");
        var lines = order.Lines.Where(l => l.ToInvoice > 0).ToList();
        if (lines.Count == 0) throw new InvalidOperationException("Nothing has been delivered that isn't invoiced yet. Deliver the goods first.");
        var terms = GetCreditTerms(conn, tx, order.CustomerId);

        var total = lines.Sum(l => Money.Round(l.ToInvoice * l.UnitPrice));
        var vat = lines.Sum(l => PostingRules.VatIncluded(Money.Round(l.ToInvoice * l.UnitPrice), l.VatRate));

        long invoiceId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO customer_invoices (customer_id, order_id, invoice_date, due_date, total_ngwee, vat_ngwee, user_name, created_at)
                VALUES ($c, $o, $d, $due, $t, $v, $u, $now);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$c", order.CustomerId);
            cmd.Parameters.AddWithValue("$o", orderId);
            cmd.Parameters.AddWithValue("$d", LedgerRepository.Day(date));
            cmd.Parameters.AddWithValue("$due", LedgerRepository.Day(OrderRules.DueDate(date, terms.PaymentTermsDays)));
            cmd.Parameters.AddWithValue("$t", Money.ToNgwee(total));
            cmd.Parameters.AddWithValue("$v", Money.ToNgwee(vat));
            cmd.Parameters.AddWithValue("$u", actor.Name);
            cmd.Parameters.AddWithValue("$now", Now());
            invoiceId = (long)cmd.ExecuteScalar()!;
        }

        var entryId = LedgerRepository.Post(conn, tx, PostingRules.CustomerInvoice(orderId, invoiceId, order.CustomerName, date, total, vat,
            LedgerRepository.SystemAccountLookup(conn, tx)), actor, enforceLock: true);
        PurchasingRepository.Exec(conn, tx, "UPDATE customer_invoices SET journal_entry_id = $je WHERE id = $id;", ("$je", entryId), ("$id", invoiceId));

        foreach (var l in lines)
        {
            PurchasingRepository.Exec(conn, tx, """
                INSERT INTO customer_invoice_lines (invoice_id, order_line_id, description, quantity, unit_price_ngwee, vat_rate)
                VALUES ($i, $l, $d, $q, $p, $v);
                """, ("$i", invoiceId), ("$l", l.Id), ("$d", l.Description), ("$q", l.ToInvoice), ("$p", Money.ToNgwee(l.UnitPrice)), ("$v", (double)l.VatRate));
            PurchasingRepository.Exec(conn, tx, "UPDATE sales_order_lines SET invoiced = delivered WHERE id = $id;", ("$id", l.Id));
        }

        AuditRepository.Write(conn, tx, actor, AuditActions.InvoiceRaised, "sales_order", Str(orderId),
            $"INV{invoiceId:D6} to {order.CustomerName} for SO{orderId:D6}: {Money.Format(total)} incl. {Money.Format(vat)} VAT");
        tx.Commit();
        return invoiceId;
    }

    /// <summary>Records money received against an invoice into cash or a bank / mobile-money account.</summary>
    public long ReceivePayment(long invoiceId, decimal amount, DateOnly date, long intoAccountId, string reference, Actor actor)
    {
        amount = Money.Round(amount);
        if (amount <= 0) throw new ArgumentException("Enter the amount received.");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var invoice = Invoices(conn, tx, "i.id = $id", ("$id", invoiceId)).FirstOrDefault() ?? throw new InvalidOperationException("Invoice not found.");
        if (amount > invoice.Outstanding) throw new InvalidOperationException($"Only {Money.Format(invoice.Outstanding)} is still owed on this invoice.");
        var into = PurchasingRepository.MoneyAccount(conn, tx, intoAccountId);

        var entryId = LedgerRepository.Post(conn, tx, PostingRules.CustomerPayment(invoice.OrderId, invoiceId, invoice.CustomerName, date, amount,
            into.Id, LedgerRepository.SystemAccountLookup(conn, tx)), actor, enforceLock: true);
        long id;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO customer_payments (invoice_id, paid_on, amount_ngwee, account_id, reference, journal_entry_id, user_name, created_at)
                VALUES ($i, $d, $a, $acc, $ref, $je, $u, $now);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$i", invoiceId);
            cmd.Parameters.AddWithValue("$d", LedgerRepository.Day(date));
            cmd.Parameters.AddWithValue("$a", Money.ToNgwee(amount));
            cmd.Parameters.AddWithValue("$acc", into.Id);
            cmd.Parameters.AddWithValue("$ref", reference.Trim());
            cmd.Parameters.AddWithValue("$je", entryId);
            cmd.Parameters.AddWithValue("$u", actor.Name);
            cmd.Parameters.AddWithValue("$now", Now());
            id = (long)cmd.ExecuteScalar()!;
        }
        PurchasingRepository.Exec(conn, tx, "UPDATE customer_invoices SET paid_ngwee = paid_ngwee + $a WHERE id = $id;",
            ("$a", Money.ToNgwee(amount)), ("$id", invoiceId));
        AuditRepository.Write(conn, tx, actor, AuditActions.PaymentReceived, "sales_order", Str(invoice.OrderId),
            $"Received {Money.Format(amount)} from {invoice.CustomerName} for INV{invoiceId:D6} into {into.Display}" +
            (amount < invoice.Outstanding ? $" ({Money.Format(invoice.Outstanding - amount)} still owed)" : " (paid in full)"));
        tx.Commit();
        return id;
    }

    // ── Reading ─────────────────────────────────────────────────────────

    public SalesOrder? GetOrder(long id)
    {
        using var conn = db.Open();
        return GetOrder(conn, null, id);
    }

    public List<SalesOrder> Orders(SalesOrderStatus? status = null, long? customerId = null, bool openOnly = false)
    {
        using var conn = db.Open();
        var where = new List<string> { "1 = 1" };
        var args = new List<(string, object)>();
        if (status is { } s) { where.Add("o.status = $st"); args.Add(("$st", s.ToString())); }
        if (customerId is { } c) { where.Add("o.customer_id = $c"); args.Add(("$c", c)); }
        if (openOnly) where.Add("o.status <> 'Cancelled'");
        return ReadOrders(conn, null, string.Join(" AND ", where), [.. args]);
    }

    public List<CustomerInvoice> Invoices(bool outstandingOnly = false, long? customerId = null, long? orderId = null)
    {
        using var conn = db.Open();
        var where = new List<string> { "1 = 1" };
        var args = new List<(string, object)>();
        if (outstandingOnly) where.Add("i.paid_ngwee < i.total_ngwee");
        if (customerId is { } c) { where.Add("i.customer_id = $c"); args.Add(("$c", c)); }
        if (orderId is { } o) { where.Add("i.order_id = $o"); args.Add(("$o", o)); }
        return Invoices(conn, null, string.Join(" AND ", where), [.. args]);
    }

    /// <summary>One invoice with its lines, for printing.</summary>
    public (CustomerInvoice Invoice, List<SalesOrderLine> Lines)? GetInvoice(long invoiceId)
    {
        using var conn = db.Open();
        var inv = Invoices(conn, null, "i.id = $id", ("$id", invoiceId)).FirstOrDefault();
        if (inv is null) return null;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT description, quantity, unit_price_ngwee, vat_rate FROM customer_invoice_lines WHERE invoice_id = $id ORDER BY id;";
        cmd.Parameters.AddWithValue("$id", invoiceId);
        var lines = new List<SalesOrderLine>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            lines.Add(new SalesOrderLine { Description = r.GetString(0), Quantity = r.GetInt32(1), UnitPrice = Money.FromNgwee(r.GetInt64(2)), VatRate = (decimal)r.GetDouble(3) });
        return (inv, lines);
    }

    public List<DeliveryView> Deliveries(long orderId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT d.id, d.delivered_on, d.note, d.user_name, l.description, dl.quantity
            FROM deliveries d
            JOIN delivery_lines dl ON dl.delivery_id = d.id
            JOIN sales_order_lines l ON l.id = dl.order_line_id
            WHERE d.order_id = $o ORDER BY d.id, dl.id;
            """;
        cmd.Parameters.AddWithValue("$o", orderId);
        var list = new List<DeliveryView>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.GetInt64(0);
            if (list.Count == 0 || list[^1].Id != id)
                list.Add(new DeliveryView(id, orderId, LedgerRepository.ParseDay(r.GetString(1)), r.GetString(2), r.GetString(3), []));
            list[^1].Lines.Add((r.GetString(4), r.GetInt32(5)));
        }
        return list;
    }

    /// <summary>One delivery note, for printing.</summary>
    public DeliveryView? GetDelivery(long deliveryId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT order_id FROM deliveries WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", deliveryId);
        return cmd.ExecuteScalar() is long orderId ? Deliveries(orderId).FirstOrDefault(d => d.Id == deliveryId) : null;
    }

    public List<PaymentView> Payments(long orderId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT p.id, p.invoice_id, p.paid_on, p.amount_ngwee, a.code || ' · ' || a.name, p.reference, p.user_name, p.journal_entry_id
            FROM customer_payments p JOIN customer_invoices i ON i.id = p.invoice_id JOIN accounts a ON a.id = p.account_id
            WHERE i.order_id = $o ORDER BY p.id;
            """;
        cmd.Parameters.AddWithValue("$o", orderId);
        var list = new List<PaymentView>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new PaymentView(r.GetInt64(0), r.GetInt64(1), LedgerRepository.ParseDay(r.GetString(2)), Money.FromNgwee(r.GetInt64(3)),
                r.GetString(4), r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetInt64(7)));
        return list;
    }

    /// <summary>Stock on hand and units reserved by confirmed orders not yet delivered (other than the given order).</summary>
    public Dictionary<long, ProductAvailability> Availability(IEnumerable<long> productIds, long? excludingOrderId = null)
    {
        var ids = productIds.Distinct().ToList();
        var map = new Dictionary<long, ProductAvailability>();
        if (ids.Count == 0) return map;
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT p.id, p.stock_quantity,
                   COALESCE((SELECT SUM(l.quantity - l.delivered) FROM sales_order_lines l JOIN sales_orders o ON o.id = l.order_id
                             WHERE l.product_id = p.id AND o.status IN ('Confirmed', 'PartlyDelivered') AND o.id <> $ex), 0)
            FROM products p WHERE p.id IN ({string.Join(',', ids)});
            """;
        cmd.Parameters.AddWithValue("$ex", excludingOrderId ?? 0);
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetInt64(0)] = new ProductAvailability(r.GetInt32(1), r.GetInt32(2));
        return map;
    }

    public SalesSummary Summary()
    {
        var today = Today;
        var orders = Orders(openOnly: true);
        var invoices = Invoices(outstandingOnly: true);
        return new SalesSummary(
            orders.Count(o => o.Status == SalesOrderStatus.Quote),
            orders.Count(o => o.CanDeliver),
            orders.Count(o => o.HasUninvoiced),
            invoices.Sum(i => i.Outstanding),
            invoices.Where(i => i.DueDate < today).Sum(i => i.Outstanding));
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    private static CreditTerms GetCreditTerms(SqliteConnection conn, SqliteTransaction? tx, long customerId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT tpin, credit_limit_ngwee, payment_terms_days FROM customers WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", customerId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException("Customer not found.");
        return new CreditTerms(r.GetString(0), r.IsDBNull(1) ? null : Money.FromNgwee(r.GetInt64(1)), r.GetInt32(2));
    }

    private static decimal Exposure(SqliteConnection conn, SqliteTransaction? tx, long customerId, long? excludingOrderId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT COALESCE((SELECT SUM(total_ngwee - paid_ngwee) FROM customer_invoices WHERE customer_id = $c), 0)
                 + COALESCE((SELECT SUM((l.quantity - l.invoiced) * l.unit_price_ngwee)
                             FROM sales_order_lines l JOIN sales_orders o ON o.id = l.order_id
                             WHERE o.customer_id = $c AND o.status IN ('Confirmed', 'PartlyDelivered', 'Delivered') AND o.id <> $ex), 0);
            """;
        cmd.Parameters.AddWithValue("$c", customerId);
        cmd.Parameters.AddWithValue("$ex", excludingOrderId ?? 0);
        return Money.FromNgwee((long)cmd.ExecuteScalar()!);
    }

    private static string CustomerName(SqliteConnection conn, SqliteTransaction tx, long customerId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT name, is_anonymised FROM customers WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", customerId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException("Choose a customer.");
        if (r.GetInt64(1) == 1) throw new InvalidOperationException("This customer has been anonymised.");
        return r.GetString(0);
    }

    private static decimal WriteLines(SqliteConnection conn, SqliteTransaction tx, long orderId, IReadOnlyList<SoLineInput> lines)
    {
        if (lines.Count == 0) throw new ArgumentException("Add at least one item.");
        decimal total = 0;
        foreach (var l in lines)
        {
            if (l.Quantity <= 0) throw new ArgumentException("Quantities must be at least 1.");
            if (l.UnitPrice < 0) throw new ArgumentException("Prices can't be negative.");
            var p = ProductRepository.GetById(conn, tx, l.ProductId) ?? throw new ArgumentException("One of the products doesn't exist.");
            PurchasingRepository.Exec(conn, tx, """
                INSERT INTO sales_order_lines (order_id, product_id, description, quantity, unit_price_ngwee, vat_rate)
                VALUES ($o, $p, $d, $q, $pr, $v);
                """, ("$o", orderId), ("$p", p.Id), ("$d", p.Name), ("$q", l.Quantity), ("$pr", Money.ToNgwee(l.UnitPrice)), ("$v", (double)p.VatRate));
            total += Money.Round(l.Quantity * Money.Round(l.UnitPrice));
        }
        return total;
    }

    internal static SalesOrder? GetOrder(SqliteConnection conn, SqliteTransaction? tx, long id) =>
        ReadOrders(conn, tx, "o.id = $id", ("$id", id)).FirstOrDefault();

    private static List<SalesOrder> ReadOrders(SqliteConnection conn, SqliteTransaction? tx, string where, params (string, object)[] args)
    {
        var orders = new List<SalesOrder>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                SELECT o.id, o.customer_id, c.name, o.status, o.order_date, o.valid_until, o.notes, o.created_by_name, o.credit_override
                FROM sales_orders o JOIN customers c ON c.id = o.customer_id
                WHERE {where} ORDER BY o.id DESC;
                """;
            foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                orders.Add(new SalesOrder
                {
                    Id = r.GetInt64(0), CustomerId = r.GetInt64(1), CustomerName = r.GetString(2),
                    Status = Enum.Parse<SalesOrderStatus>(r.GetString(3)), OrderDate = LedgerRepository.ParseDay(r.GetString(4)),
                    ValidUntil = r.IsDBNull(5) ? null : LedgerRepository.ParseDay(r.GetString(5)), Notes = r.GetString(6),
                    CreatedByName = r.GetString(7), CreditOverride = r.IsDBNull(8) ? null : r.GetString(8),
                });
        }
        if (orders.Count == 0) return orders;

        var byId = orders.ToDictionary(o => o.Id);
        using var lines = conn.CreateCommand();
        lines.Transaction = tx;
        lines.CommandText = $"""
            SELECT id, order_id, product_id, description, quantity, unit_price_ngwee, vat_rate, delivered, invoiced
            FROM sales_order_lines WHERE order_id IN ({string.Join(',', byId.Keys)}) ORDER BY id;
            """;
        using var lr = lines.ExecuteReader();
        while (lr.Read())
            byId[lr.GetInt64(1)].Lines.Add(new SalesOrderLine
            {
                Id = lr.GetInt64(0), ProductId = lr.GetInt64(2), Description = lr.GetString(3), Quantity = lr.GetInt32(4),
                UnitPrice = Money.FromNgwee(lr.GetInt64(5)), VatRate = (decimal)lr.GetDouble(6), Delivered = lr.GetInt32(7), Invoiced = lr.GetInt32(8),
            });
        return orders;
    }

    private static List<CustomerInvoice> Invoices(SqliteConnection conn, SqliteTransaction? tx, string where, params (string, object)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT i.id, i.customer_id, c.name, i.order_id, i.invoice_date, i.due_date, i.total_ngwee, i.vat_ngwee, i.paid_ngwee, i.journal_entry_id
            FROM customer_invoices i JOIN customers c ON c.id = i.customer_id
            WHERE {where} ORDER BY i.due_date, i.id;
            """;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        var list = new List<CustomerInvoice>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new CustomerInvoice(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3),
                LedgerRepository.ParseDay(r.GetString(4)), LedgerRepository.ParseDay(r.GetString(5)),
                Money.FromNgwee(r.GetInt64(6)), Money.FromNgwee(r.GetInt64(7)), Money.FromNgwee(r.GetInt64(8)),
                r.IsDBNull(9) ? null : r.GetInt64(9)));
        return list;
    }

    private static string Limit(decimal? limit) => limit is { } l ? Money.Format(l) : "none";
    private static string Now() => DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
    private static string Str(long id) => id.ToString(CultureInfo.InvariantCulture);
}
