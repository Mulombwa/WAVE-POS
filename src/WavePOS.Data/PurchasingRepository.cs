using System.Globalization;
using Microsoft.Data.Sqlite;
using WavePOS.Core;
using WavePOS.Core.Accounting;
using WavePOS.Core.Audit;
using WavePOS.Core.Inventory;
using WavePOS.Core.Models;
using WavePOS.Core.Orders;
using WavePOS.Core.Security;

namespace WavePOS.Data;

public record PoLineInput(long ProductId, int Quantity, decimal UnitCost);

public record SupplierBill(long Id, long SupplierId, string SupplierName, long PoId, string SupplierInvoice,
    DateOnly BillDate, DateOnly DueDate, decimal Net, decimal Vat, decimal Paid, long? JournalEntryId)
{
    public decimal Total => Net + Vat;
    public decimal Outstanding => Total - Paid;
    public AgeBucket Age(DateOnly today) => OrderRules.Age(DueDate, today);
}

public record GoodsReceiptView(long Id, long PoId, DateOnly Date, string Note, string UserName, List<(string Description, int Quantity)> Lines)
{
    public string Number => $"GRN{Id:D6}";
}

public record PaymentView(long Id, long DocumentId, DateOnly Date, decimal Amount, string AccountName, string Reference, string UserName, long? JournalEntryId);

public record ReorderSuggestion(Product Product, int OnOrder, int Suggested);

public record PurchasingSummary(int AwaitingApproval, int AwaitingDelivery, int ToBill, decimal Owed, decimal Overdue);

/// <summary>
/// Procure-to-pay: suppliers, purchase orders (with approval above a limit), goods received,
/// supplier bills (only for what was received, at the ordered price) and payments. Every step
/// is audited and posts to the books in the same transaction.
/// </summary>
public class PurchasingRepository(PosDatabase db)
{
    private const string ApprovalLimitKey = "purchasing.approval_limit";
    public const decimal DefaultApprovalLimit = 5000m;

    // ── Suppliers ───────────────────────────────────────────────────────

    public List<Supplier> Suppliers(bool includeInactive = false, string? search = null)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (!includeInactive) where.Add("s.is_active = 1");
        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Add("(s.name LIKE $q OR s.code LIKE $q OR s.tpin LIKE $q OR s.phone LIKE $q)");
            cmd.Parameters.AddWithValue("$q", $"%{search.Trim()}%");
        }
        cmd.CommandText = $"{SupplierSelect} {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")} ORDER BY s.name;";
        return ReadSuppliers(cmd);
    }

    public Supplier? GetSupplier(long id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{SupplierSelect} WHERE s.id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return ReadSuppliers(cmd).FirstOrDefault();
    }

    /// <summary>Adds (Id = 0) or updates a supplier. Returns its id.</summary>
    public long SaveSupplier(Supplier s, Actor actor)
    {
        if (string.IsNullOrWhiteSpace(s.Name)) throw new ArgumentException("Enter the supplier's name.");
        if (s.PaymentTermsDays is < 0 or > 365) throw new ArgumentException("Payment terms must be between 0 and 365 days.");
        if (s.Tpin.Trim().Length > 0 && (s.Tpin.Trim().Length != 10 || !s.Tpin.Trim().All(char.IsAsciiDigit)))
            throw new ArgumentException("A ZRA TPIN is 10 digits.");

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$name", s.Name.Trim());
        cmd.Parameters.AddWithValue("$tpin", s.Tpin.Trim());
        cmd.Parameters.AddWithValue("$contact", s.ContactName.Trim());
        cmd.Parameters.AddWithValue("$phone", s.Phone.Trim());
        cmd.Parameters.AddWithValue("$email", s.Email.Trim());
        cmd.Parameters.AddWithValue("$address", s.Address.Trim());
        cmd.Parameters.AddWithValue("$terms", s.PaymentTermsDays);
        cmd.Parameters.AddWithValue("$active", s.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$notes", s.Notes.Trim());

        long id;
        if (s.Id == 0)
        {
            cmd.CommandText = """
                INSERT INTO suppliers (name, tpin, contact_name, phone, email, address, payment_terms_days, is_active, notes, created_at)
                VALUES ($name, $tpin, $contact, $phone, $email, $address, $terms, 1, $notes, $now);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$now", Now());
            id = (long)cmd.ExecuteScalar()!;
            Exec(conn, tx, "UPDATE suppliers SET code = $c WHERE id = $id;", ("$c", $"S{id:D5}"), ("$id", id));
            AuditRepository.Write(conn, tx, actor, AuditActions.SupplierCreated, "supplier", Str(id), $"Added supplier S{id:D5} {s.Name.Trim()}");
        }
        else
        {
            id = s.Id;
            cmd.CommandText = """
                UPDATE suppliers SET name = $name, tpin = $tpin, contact_name = $contact, phone = $phone, email = $email,
                       address = $address, payment_terms_days = $terms, is_active = $active, notes = $notes
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            if (cmd.ExecuteNonQuery() == 0) throw new InvalidOperationException("Supplier not found.");
            AuditRepository.Write(conn, tx, actor, AuditActions.SupplierUpdated, "supplier", Str(id),
                $"Updated supplier S{id:D5} {s.Name.Trim()}{(s.IsActive ? "" : " (switched off)")}");
        }
        tx.Commit();
        return id;
    }

    // ── Purchase orders ─────────────────────────────────────────────────

    public decimal ApprovalLimit()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", ApprovalLimitKey);
        return cmd.ExecuteScalar() is string s && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v)
            ? v : DefaultApprovalLimit;
    }

    public void SetApprovalLimit(decimal limit, Actor actor)
    {
        if (limit < 0) throw new ArgumentException("The approval limit can't be negative.");
        var before = ApprovalLimit();
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        Exec(conn, tx, "INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
            ("$k", ApprovalLimitKey), ("$v", Money.Round(limit).ToString(CultureInfo.InvariantCulture)));
        AuditRepository.Write(conn, tx, actor, AuditActions.ApprovalLimitChanged, "settings", "purchasing",
            $"Purchase approval limit {Money.Format(before)} → {Money.Format(limit)}");
        tx.Commit();
    }

    public long CreateOrder(long supplierId, DateOnly? expected, string notes, IReadOnlyList<PoLineInput> lines, Actor actor)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var supplier = ActiveSupplier(conn, tx, supplierId);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO purchase_orders (supplier_id, status, order_date, expected_date, notes, created_by, created_by_name, created_at)
            VALUES ($s, 'Draft', $date, $exp, $notes, $uid, $uname, $now);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$s", supplierId);
        cmd.Parameters.AddWithValue("$date", LedgerRepository.Day(Today));
        cmd.Parameters.AddWithValue("$exp", expected is { } e ? LedgerRepository.Day(e) : DBNull.Value);
        cmd.Parameters.AddWithValue("$notes", notes.Trim());
        cmd.Parameters.AddWithValue("$uid", (object?)actor.UserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uname", actor.Name);
        cmd.Parameters.AddWithValue("$now", Now());
        var id = (long)cmd.ExecuteScalar()!;
        var total = WriteLines(conn, tx, id, lines);
        AuditRepository.Write(conn, tx, actor, AuditActions.PurchaseOrderCreated, "purchase_order", Str(id),
            $"PO{id:D6} drafted for {supplier}: {lines.Count} line(s), {Money.Format(total)} incl. VAT");
        tx.Commit();
        return id;
    }

    /// <summary>Changes a draft (supplier, expected date, notes and lines).</summary>
    public void UpdateDraft(long poId, long supplierId, DateOnly? expected, string notes, IReadOnlyList<PoLineInput> lines, Actor actor)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var po = GetOrder(conn, tx, poId) ?? throw new InvalidOperationException("Purchase order not found.");
        if (!po.CanEdit) throw new InvalidOperationException("Only draft orders can be changed.");
        var supplier = ActiveSupplier(conn, tx, supplierId);
        Exec(conn, tx, "UPDATE purchase_orders SET supplier_id = $s, expected_date = $exp, notes = $n WHERE id = $id;",
            ("$s", supplierId), ("$exp", expected is { } e ? LedgerRepository.Day(e) : (object)DBNull.Value), ("$n", notes.Trim()), ("$id", poId));
        Exec(conn, tx, "DELETE FROM purchase_order_lines WHERE po_id = $id;", ("$id", poId));
        var total = WriteLines(conn, tx, poId, lines);
        AuditRepository.Write(conn, tx, actor, AuditActions.PurchaseOrderChanged, "purchase_order", Str(poId),
            $"PO{poId:D6} changed: {supplier}, {lines.Count} line(s), {Money.Format(total)} incl. VAT");
        tx.Commit();
    }

    /// <summary>
    /// Sends a draft for ordering. Within the approval limit it's approved straight away;
    /// above it, it waits for a manager. Returns the new status.
    /// </summary>
    public PurchaseOrderStatus Submit(long poId, Actor actor)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var po = GetOrder(conn, tx, poId) ?? throw new InvalidOperationException("Purchase order not found.");
        if (po.Status != PurchaseOrderStatus.Draft) throw new InvalidOperationException("This order has already been submitted.");
        if (po.Lines.Count == 0) throw new InvalidOperationException("Add at least one item first.");

        var limit = ApprovalLimitIn(conn, tx);
        PurchaseOrderStatus status;
        if (OrderRules.NeedsApproval(po.Total, limit))
        {
            status = PurchaseOrderStatus.AwaitingApproval;
            SetStatus(conn, tx, poId, status);
            AuditRepository.Write(conn, tx, actor, AuditActions.PurchaseOrderSubmitted, "purchase_order", Str(poId),
                $"PO{poId:D6} ({Money.Format(po.Total)}) sent for approval: over the {Money.Format(limit)} limit");
        }
        else
        {
            status = PurchaseOrderStatus.Approved;
            MarkApproved(conn, tx, poId, actor);
            AuditRepository.Write(conn, tx, actor, AuditActions.PurchaseOrderApproved, "purchase_order", Str(poId),
                $"PO{poId:D6} ({Money.Format(po.Total)}) ordered: within the {Money.Format(limit)} approval limit");
        }
        tx.Commit();
        return status;
    }

    /// <summary>A manager approves an order over the limit. Nobody approves their own order if someone else can.</summary>
    public void Approve(long poId, Actor actor)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var po = GetOrder(conn, tx, poId) ?? throw new InvalidOperationException("Purchase order not found.");
        if (po.Status != PurchaseOrderStatus.AwaitingApproval) throw new InvalidOperationException("This order isn't waiting for approval.");
        if (actor.UserId is not { } approverId) throw new InvalidOperationException("Sign in to approve orders.");
        var approvers = ActiveUsersWho(conn, tx, Permission.ApprovePurchases);
        if (!approvers.Contains(approverId)) throw new InvalidOperationException("You aren't allowed to approve purchase orders.");
        if (OrderRules.CheckApprover(approverId, po.CreatedById, approvers.Count) is { } problem) throw new InvalidOperationException(problem);

        MarkApproved(conn, tx, poId, actor);
        var self = approverId == po.CreatedById;
        AuditRepository.Write(conn, tx, actor, AuditActions.PurchaseOrderApproved, "purchase_order", Str(poId),
            $"PO{poId:D6} ({Money.Format(po.Total)}) approved" + (self ? " by its creator (the only person who can approve)" : $", raised by {po.CreatedByName}"));
        tx.Commit();
    }

    /// <summary>Sends an order back to draft with a reason.</summary>
    public void Reject(long poId, string reason, Actor actor)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Say why the order is being sent back.");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var po = GetOrder(conn, tx, poId) ?? throw new InvalidOperationException("Purchase order not found.");
        if (po.Status != PurchaseOrderStatus.AwaitingApproval) throw new InvalidOperationException("This order isn't waiting for approval.");
        if (!ActiveUsersWho(conn, tx, Permission.ApprovePurchases).Contains(actor.UserId ?? 0))
            throw new InvalidOperationException("You aren't allowed to approve purchase orders.");
        SetStatus(conn, tx, poId, PurchaseOrderStatus.Draft);
        AuditRepository.Write(conn, tx, actor, AuditActions.PurchaseOrderRejected, "purchase_order", Str(poId),
            $"PO{poId:D6} sent back to draft: {reason.Trim()}");
        tx.Commit();
    }

    /// <summary>Cancels an order that nothing has been received against.</summary>
    public void Cancel(long poId, string reason, Actor actor)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Say why the order is being cancelled.");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var po = GetOrder(conn, tx, poId) ?? throw new InvalidOperationException("Purchase order not found.");
        if (po.Lines.Any(l => l.Received > 0)) throw new InvalidOperationException("Goods have been received on this order, so it can't be cancelled.");
        if (po.Status == PurchaseOrderStatus.Cancelled) return;
        SetStatus(conn, tx, poId, PurchaseOrderStatus.Cancelled);
        AuditRepository.Write(conn, tx, actor, AuditActions.PurchaseOrderCancelled, "purchase_order", Str(poId),
            $"PO{poId:D6} cancelled: {reason.Trim()}");
        tx.Commit();
    }

    /// <summary>
    /// Goods arrived: adds them to stock at the ordered cost (averaged into the product's cost) and
    /// posts Inventory against Goods received not yet billed. Part deliveries are fine. Returns the GRN id.
    /// </summary>
    public long Receive(long poId, IReadOnlyDictionary<long, int> quantities, string note, Actor actor)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var po = GetOrder(conn, tx, poId) ?? throw new InvalidOperationException("Purchase order not found.");
        if (!po.CanReceive) throw new InvalidOperationException(po.Status is PurchaseOrderStatus.Draft or PurchaseOrderStatus.AwaitingApproval
            ? "This order hasn't been approved yet." : "This order is closed.");
        if (OrderRules.CheckQuantities(quantities, po.Lines.ToDictionary(l => l.Id, l => (l.Description, l.Outstanding))) is { } problem)
            throw new InvalidOperationException(problem);

        long receiptId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO goods_receipts (po_id, received_on, note, user_name, created_at) VALUES ($po, $d, $n, $u, $now);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$po", poId);
            cmd.Parameters.AddWithValue("$d", LedgerRepository.Day(Today));
            cmd.Parameters.AddWithValue("$n", note.Trim());
            cmd.Parameters.AddWithValue("$u", actor.Name);
            cmd.Parameters.AddWithValue("$now", Now());
            receiptId = (long)cmd.ExecuteScalar()!;
        }

        decimal value = 0;
        var items = 0;
        foreach (var line in po.Lines)
        {
            var qty = quantities.GetValueOrDefault(line.Id);
            if (qty == 0) continue;
            var product = ProductRepository.GetById(conn, tx, line.ProductId)!;
            var rounding = InventoryRepository.AverageIn(conn, tx, product, qty, line.UnitCost);
            InventoryRepository.ApplyMovement(conn, tx, line.ProductId, qty, StockReason.Received, note, $"GRN{receiptId:D6} (PO{poId:D6})", actor);
            InventoryRepository.PostRounding(conn, tx, product, rounding, actor);
            Exec(conn, tx, "UPDATE purchase_order_lines SET received = received + $q WHERE id = $id;", ("$q", qty), ("$id", line.Id));
            Exec(conn, tx, "INSERT INTO goods_receipt_lines (receipt_id, po_line_id, quantity) VALUES ($r, $l, $q);",
                ("$r", receiptId), ("$l", line.Id), ("$q", qty));
            line.Received += qty;
            value += Money.Round(qty * line.UnitCost);
            items += qty;
        }

        SetStatus(conn, tx, poId, OrderRules.AfterReceipt(po.Lines));
        LedgerRepository.PostAutomatic(conn, tx,
            PostingRules.GoodsReceived(poId, receiptId, po.SupplierName, Today, value, LedgerRepository.SystemAccountLookup(conn, tx)), actor);
        AuditRepository.Write(conn, tx, actor, AuditActions.GoodsReceived, "purchase_order", Str(poId),
            $"GRN{receiptId:D6}: received {items} item(s) worth {Money.Format(value)} on PO{poId:D6}");
        tx.Commit();
        return receiptId;
    }

    /// <summary>
    /// Enters the supplier's bill for everything received but not yet billed on the order, at the
    /// ordered price (three-way match: order, receipt, bill). Returns the bill id.
    /// </summary>
    public long EnterBill(long poId, string supplierInvoice, DateOnly billDate, Actor actor)
    {
        if (string.IsNullOrWhiteSpace(supplierInvoice)) throw new ArgumentException("Enter the supplier's invoice number.");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var po = GetOrder(conn, tx, poId) ?? throw new InvalidOperationException("Purchase order not found.");
        var toBill = po.Lines.Where(l => l.ToBill > 0).ToList();
        if (toBill.Count == 0) throw new InvalidOperationException("Nothing has been received that isn't billed yet. Receive the goods first.");
        var supplier = GetSupplierIn(conn, tx, po.SupplierId)!;

        var net = toBill.Sum(l => Money.Round(l.ToBill * l.UnitCost));
        var vat = toBill.Sum(l => Money.Round(Money.Round(l.ToBill * l.UnitCost) * l.VatRate));
        var draft = PostingRules.SupplierBill(poId, supplierInvoice.Trim(), supplier.Name, billDate, net, vat, LedgerRepository.SystemAccountLookup(conn, tx));
        var entryId = LedgerRepository.Post(conn, tx, draft, actor, enforceLock: true);

        long billId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO supplier_bills (supplier_id, po_id, supplier_invoice, bill_date, due_date, net_ngwee, vat_ngwee, journal_entry_id, user_name, created_at)
                VALUES ($s, $po, $inv, $date, $due, $net, $vat, $je, $u, $now);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$s", po.SupplierId);
            cmd.Parameters.AddWithValue("$po", poId);
            cmd.Parameters.AddWithValue("$inv", supplierInvoice.Trim());
            cmd.Parameters.AddWithValue("$date", LedgerRepository.Day(billDate));
            cmd.Parameters.AddWithValue("$due", LedgerRepository.Day(OrderRules.DueDate(billDate, supplier.PaymentTermsDays)));
            cmd.Parameters.AddWithValue("$net", Money.ToNgwee(net));
            cmd.Parameters.AddWithValue("$vat", Money.ToNgwee(vat));
            cmd.Parameters.AddWithValue("$je", entryId);
            cmd.Parameters.AddWithValue("$u", actor.Name);
            cmd.Parameters.AddWithValue("$now", Now());
            try { billId = (long)cmd.ExecuteScalar()!; }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                throw new InvalidOperationException($"Bill {supplierInvoice.Trim()} from {supplier.Name} has already been entered.");
            }
        }

        foreach (var l in toBill)
        {
            Exec(conn, tx, "INSERT INTO supplier_bill_lines (bill_id, po_line_id, quantity, unit_cost_ngwee, vat_rate) VALUES ($b, $l, $q, $c, $v);",
                ("$b", billId), ("$l", l.Id), ("$q", l.ToBill), ("$c", Money.ToNgwee(l.UnitCost)), ("$v", (double)l.VatRate));
            Exec(conn, tx, "UPDATE purchase_order_lines SET billed = received WHERE id = $id;", ("$id", l.Id));
        }

        AuditRepository.Write(conn, tx, actor, AuditActions.SupplierBillEntered, "purchase_order", Str(poId),
            $"Bill {supplierInvoice.Trim()} from {supplier.Name} entered for PO{poId:D6}: {Money.Format(net + vat)} incl. {Money.Format(vat)} VAT");
        tx.Commit();
        return billId;
    }

    /// <summary>Pays all or part of a bill from cash or a bank / mobile-money account. Returns the payment id.</summary>
    public long PayBill(long billId, decimal amount, DateOnly date, long fromAccountId, string reference, Actor actor)
    {
        amount = Money.Round(amount);
        if (amount <= 0) throw new ArgumentException("Enter the amount paid.");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var bill = Bills(conn, tx, "b.id = $id", ("$id", billId)).FirstOrDefault() ?? throw new InvalidOperationException("Bill not found.");
        if (amount > bill.Outstanding) throw new InvalidOperationException($"Only {Money.Format(bill.Outstanding)} is still owed on this bill.");
        var from = MoneyAccount(conn, tx, fromAccountId);

        var entryId = LedgerRepository.Post(conn, tx, PostingRules.SupplierPayment(bill.PoId, bill.SupplierInvoice, bill.SupplierName, date, amount,
            from.Id, LedgerRepository.SystemAccountLookup(conn, tx)), actor, enforceLock: true);
        long id;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO supplier_payments (bill_id, paid_on, amount_ngwee, account_id, reference, journal_entry_id, user_name, created_at)
                VALUES ($b, $d, $a, $acc, $ref, $je, $u, $now);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$b", billId);
            cmd.Parameters.AddWithValue("$d", LedgerRepository.Day(date));
            cmd.Parameters.AddWithValue("$a", Money.ToNgwee(amount));
            cmd.Parameters.AddWithValue("$acc", from.Id);
            cmd.Parameters.AddWithValue("$ref", reference.Trim());
            cmd.Parameters.AddWithValue("$je", entryId);
            cmd.Parameters.AddWithValue("$u", actor.Name);
            cmd.Parameters.AddWithValue("$now", Now());
            id = (long)cmd.ExecuteScalar()!;
        }
        Exec(conn, tx, "UPDATE supplier_bills SET paid_ngwee = paid_ngwee + $a WHERE id = $id;", ("$a", Money.ToNgwee(amount)), ("$id", billId));
        AuditRepository.Write(conn, tx, actor, AuditActions.SupplierPaid, "purchase_order", Str(bill.PoId),
            $"Paid {bill.SupplierName} {Money.Format(amount)} for bill {bill.SupplierInvoice} from {from.Display}" +
            (amount < bill.Outstanding ? $" ({Money.Format(bill.Outstanding - amount)} still owed)" : " (paid in full)"));
        tx.Commit();
        return id;
    }

    // ── Reading ─────────────────────────────────────────────────────────

    public PurchaseOrder? GetOrder(long id)
    {
        using var conn = db.Open();
        return GetOrder(conn, null, id);
    }

    public List<PurchaseOrder> Orders(PurchaseOrderStatus? status = null, long? supplierId = null, bool openOnly = false)
    {
        using var conn = db.Open();
        var where = new List<string>();
        var args = new List<(string, object)>();
        if (status is { } s) { where.Add("po.status = $st"); args.Add(("$st", s.ToString())); }
        if (supplierId is { } sid) { where.Add("po.supplier_id = $sid"); args.Add(("$sid", sid)); }
        if (openOnly) where.Add("po.status NOT IN ('Received', 'Cancelled')");
        return ReadOrders(conn, null, where.Count == 0 ? "1 = 1" : string.Join(" AND ", where), [.. args]);
    }

    public List<SupplierBill> Bills(bool outstandingOnly = false, long? supplierId = null, long? poId = null)
    {
        using var conn = db.Open();
        var where = new List<string> { "1 = 1" };
        var args = new List<(string, object)>();
        if (outstandingOnly) where.Add("b.paid_ngwee < b.net_ngwee + b.vat_ngwee");
        if (supplierId is { } s) { where.Add("b.supplier_id = $s"); args.Add(("$s", s)); }
        if (poId is { } p) { where.Add("b.po_id = $p"); args.Add(("$p", p)); }
        return Bills(conn, null, string.Join(" AND ", where), [.. args]);
    }

    public List<GoodsReceiptView> Receipts(long poId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.received_on, r.note, r.user_name, l.description, gl.quantity
            FROM goods_receipts r
            JOIN goods_receipt_lines gl ON gl.receipt_id = r.id
            JOIN purchase_order_lines l ON l.id = gl.po_line_id
            WHERE r.po_id = $po ORDER BY r.id, gl.id;
            """;
        cmd.Parameters.AddWithValue("$po", poId);
        var list = new List<GoodsReceiptView>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.GetInt64(0);
            if (list.Count == 0 || list[^1].Id != id)
                list.Add(new GoodsReceiptView(id, poId, LedgerRepository.ParseDay(r.GetString(1)), r.GetString(2), r.GetString(3), []));
            list[^1].Lines.Add((r.GetString(4), r.GetInt32(5)));
        }
        return list;
    }

    public List<PaymentView> Payments(long poId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT p.id, p.bill_id, p.paid_on, p.amount_ngwee, a.code || ' · ' || a.name, p.reference, p.user_name, p.journal_entry_id
            FROM supplier_payments p JOIN supplier_bills b ON b.id = p.bill_id JOIN accounts a ON a.id = p.account_id
            WHERE b.po_id = $po ORDER BY p.id;
            """;
        cmd.Parameters.AddWithValue("$po", poId);
        var list = new List<PaymentView>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new PaymentView(r.GetInt64(0), r.GetInt64(1), LedgerRepository.ParseDay(r.GetString(2)), Money.FromNgwee(r.GetInt64(3)),
                r.GetString(4), r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetInt64(7)));
        return list;
    }

    /// <summary>Units on approved orders not yet received, per product.</summary>
    public Dictionary<long, int> OnOrder()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT l.product_id, SUM(l.quantity - l.received)
            FROM purchase_order_lines l JOIN purchase_orders po ON po.id = l.po_id
            WHERE po.status IN ('Approved', 'PartlyReceived', 'AwaitingApproval')
            GROUP BY l.product_id;
            """;
        var map = new Dictionary<long, int>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetInt64(0)] = r.GetInt32(1);
        return map;
    }

    /// <summary>Active products at or below their reorder level, counting what's already on order.</summary>
    public List<ReorderSuggestion> ReorderSuggestions()
    {
        var onOrder = OnOrder();
        return new ProductRepository(db).GetAll()
            .Select(p => new ReorderSuggestion(p, onOrder.GetValueOrDefault(p.Id),
                OrderRules.ReorderQuantity(p.StockQuantity, p.ReorderLevel, onOrder.GetValueOrDefault(p.Id))))
            .Where(s => s.Suggested > 0)
            .OrderBy(s => s.Product.Category).ThenBy(s => s.Product.Name)
            .ToList();
    }

    public PurchasingSummary Summary()
    {
        var today = Today;
        var orders = Orders(openOnly: true);
        var bills = Bills(outstandingOnly: true);
        return new PurchasingSummary(
            orders.Count(o => o.Status == PurchaseOrderStatus.AwaitingApproval),
            orders.Count(o => o.CanReceive),
            Orders().Count(o => o.HasUnbilled),
            bills.Sum(b => b.Outstanding),
            bills.Where(b => b.DueDate < today).Sum(b => b.Outstanding));
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    private const string SupplierSelect = """
        SELECT s.id, s.code, s.name, s.tpin, s.contact_name, s.phone, s.email, s.address, s.payment_terms_days, s.is_active, s.notes,
               COALESCE((SELECT SUM(b.net_ngwee + b.vat_ngwee - b.paid_ngwee) FROM supplier_bills b WHERE b.supplier_id = s.id), 0)
        FROM suppliers s
        """;

    private static List<Supplier> ReadSuppliers(SqliteCommand cmd)
    {
        var list = new List<Supplier>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Supplier
            {
                Id = r.GetInt64(0), Code = r.IsDBNull(1) ? "" : r.GetString(1), Name = r.GetString(2), Tpin = r.GetString(3),
                ContactName = r.GetString(4), Phone = r.GetString(5), Email = r.GetString(6), Address = r.GetString(7),
                PaymentTermsDays = r.GetInt32(8), IsActive = r.GetInt64(9) == 1, Notes = r.GetString(10),
                Balance = Money.FromNgwee(r.GetInt64(11)),
            });
        return list;
    }

    private static Supplier? GetSupplierIn(SqliteConnection conn, SqliteTransaction? tx, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"{SupplierSelect} WHERE s.id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return ReadSuppliers(cmd).FirstOrDefault();
    }

    private static string ActiveSupplier(SqliteConnection conn, SqliteTransaction tx, long id)
    {
        var s = GetSupplierIn(conn, tx, id) ?? throw new InvalidOperationException("Choose a supplier.");
        if (!s.IsActive) throw new InvalidOperationException($"{s.Name} is switched off.");
        return s.Name;
    }

    private static decimal WriteLines(SqliteConnection conn, SqliteTransaction tx, long poId, IReadOnlyList<PoLineInput> lines)
    {
        if (lines.Count == 0) throw new ArgumentException("Add at least one item.");
        decimal total = 0;
        foreach (var l in lines)
        {
            if (l.Quantity <= 0) throw new ArgumentException("Quantities must be at least 1.");
            if (l.UnitCost < 0) throw new ArgumentException("Costs can't be negative.");
            var p = ProductRepository.GetById(conn, tx, l.ProductId) ?? throw new ArgumentException("One of the products doesn't exist.");
            Exec(conn, tx, """
                INSERT INTO purchase_order_lines (po_id, product_id, description, quantity, unit_cost_ngwee, vat_rate)
                VALUES ($po, $p, $d, $q, $c, $v);
                """, ("$po", poId), ("$p", p.Id), ("$d", p.Name), ("$q", l.Quantity), ("$c", Money.ToNgwee(l.UnitCost)), ("$v", (double)p.VatRate));
            var net = Money.Round(l.Quantity * Money.Round(l.UnitCost));
            total += net + Money.Round(net * p.VatRate);
        }
        return total;
    }

    internal static PurchaseOrder? GetOrder(SqliteConnection conn, SqliteTransaction? tx, long id) =>
        ReadOrders(conn, tx, "po.id = $id", ("$id", id)).FirstOrDefault();

    private static List<PurchaseOrder> ReadOrders(SqliteConnection conn, SqliteTransaction? tx, string where, params (string, object)[] args)
    {
        var orders = new List<PurchaseOrder>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                SELECT po.id, po.supplier_id, s.name, po.status, po.order_date, po.expected_date, po.notes, po.created_by, po.created_by_name,
                       po.approved_by_name, po.approved_at
                FROM purchase_orders po JOIN suppliers s ON s.id = po.supplier_id
                WHERE {where} ORDER BY po.id DESC;
                """;
            foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                orders.Add(new PurchaseOrder
                {
                    Id = r.GetInt64(0), SupplierId = r.GetInt64(1), SupplierName = r.GetString(2),
                    Status = Enum.Parse<PurchaseOrderStatus>(r.GetString(3)), OrderDate = LedgerRepository.ParseDay(r.GetString(4)),
                    ExpectedDate = r.IsDBNull(5) ? null : LedgerRepository.ParseDay(r.GetString(5)), Notes = r.GetString(6),
                    CreatedById = r.IsDBNull(7) ? null : r.GetInt64(7), CreatedByName = r.GetString(8),
                    ApprovedByName = r.IsDBNull(9) ? null : r.GetString(9),
                    ApprovedAt = r.IsDBNull(10) ? null : DateTime.Parse(r.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                });
        }
        if (orders.Count == 0) return orders;

        var byId = orders.ToDictionary(o => o.Id);
        using var lines = conn.CreateCommand();
        lines.Transaction = tx;
        lines.CommandText = $"""
            SELECT id, po_id, product_id, description, quantity, unit_cost_ngwee, vat_rate, received, billed
            FROM purchase_order_lines WHERE po_id IN ({string.Join(',', byId.Keys)}) ORDER BY id;
            """;
        using var lr = lines.ExecuteReader();
        while (lr.Read())
            byId[lr.GetInt64(1)].Lines.Add(new PurchaseOrderLine
            {
                Id = lr.GetInt64(0), ProductId = lr.GetInt64(2), Description = lr.GetString(3), Quantity = lr.GetInt32(4),
                UnitCost = Money.FromNgwee(lr.GetInt64(5)), VatRate = (decimal)lr.GetDouble(6), Received = lr.GetInt32(7), Billed = lr.GetInt32(8),
            });
        return orders;
    }

    private static List<SupplierBill> Bills(SqliteConnection conn, SqliteTransaction? tx, string where, params (string, object)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT b.id, b.supplier_id, s.name, b.po_id, b.supplier_invoice, b.bill_date, b.due_date, b.net_ngwee, b.vat_ngwee, b.paid_ngwee, b.journal_entry_id
            FROM supplier_bills b JOIN suppliers s ON s.id = b.supplier_id
            WHERE {where} ORDER BY b.due_date, b.id;
            """;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        var list = new List<SupplierBill>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new SupplierBill(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3), r.GetString(4),
                LedgerRepository.ParseDay(r.GetString(5)), LedgerRepository.ParseDay(r.GetString(6)),
                Money.FromNgwee(r.GetInt64(7)), Money.FromNgwee(r.GetInt64(8)), Money.FromNgwee(r.GetInt64(9)),
                r.IsDBNull(10) ? null : r.GetInt64(10)));
        return list;
    }

    private static decimal ApprovalLimitIn(SqliteConnection conn, SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", ApprovalLimitKey);
        return cmd.ExecuteScalar() is string s && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v)
            ? v : DefaultApprovalLimit;
    }

    /// <summary>Ids of active users whose role has the permission.</summary>
    internal static HashSet<long> ActiveUsersWho(SqliteConnection conn, SqliteTransaction? tx, Permission permission)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id, role FROM users WHERE is_active = 1;";
        var ids = new HashSet<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (Enum.TryParse<UserRole>(r.GetString(1), out var role) && role.Can(permission)) ids.Add(r.GetInt64(0));
        return ids;
    }

    internal static Account MoneyAccount(SqliteConnection conn, SqliteTransaction tx, long accountId)
    {
        var a = LedgerRepository.GetAccount(conn, tx, accountId) ?? throw new InvalidOperationException("Choose an account.");
        if (!a.IsActive || !(a.IsBank || a.SystemKey == SystemAccounts.Cash))
            throw new InvalidOperationException("Choose cash or a bank / mobile-money account.");
        return a;
    }

    private static void MarkApproved(SqliteConnection conn, SqliteTransaction tx, long poId, Actor actor) =>
        Exec(conn, tx, "UPDATE purchase_orders SET status = 'Approved', approved_by = $u, approved_by_name = $n, approved_at = $at WHERE id = $id;",
            ("$u", (object?)actor.UserId ?? DBNull.Value), ("$n", actor.Name), ("$at", Now()), ("$id", poId));

    private static void SetStatus(SqliteConnection conn, SqliteTransaction tx, long poId, PurchaseOrderStatus status) =>
        Exec(conn, tx, "UPDATE purchase_orders SET status = $s WHERE id = $id;", ("$s", status.ToString()), ("$id", poId));

    internal static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }

    private static string Now() => DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
    private static string Str(long id) => id.ToString(CultureInfo.InvariantCulture);
}
