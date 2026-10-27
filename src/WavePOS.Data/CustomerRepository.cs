using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WavePOS.Core;
using WavePOS.Core.Audit;
using WavePOS.Core.Models;

namespace WavePOS.Data;

/// <summary>
/// Customers (CRM). Changes, exports and anonymising are recorded in the audit trail
/// to support Zambia Data Protection Act requests.
/// </summary>
public class CustomerRepository(PosDatabase db)
{
    private const string SelectColumns = """
        SELECT c.id, c.code, c.name, c.phone, c.email, c.address, c.notes, c.marketing_consent, c.consent_date,
               c.created_at, c.is_anonymised,
               (SELECT COUNT(*) FROM sales s WHERE s.customer_id = c.id),
               (SELECT COALESCE(SUM(total_ngwee), 0) FROM sales s WHERE s.customer_id = c.id),
               (SELECT MAX(timestamp) FROM sales s WHERE s.customer_id = c.id)
        FROM customers c
        """;

    public Customer Create(Customer customer, Actor actor)
    {
        Validate(customer);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO customers (name, phone, email, address, notes, marketing_consent, consent_date, created_at)
            VALUES ($name, $phone, $email, $address, $notes, $consent, $cdate, $now);
            SELECT last_insert_rowid();
            """;
        Bind(cmd, customer);
        cmd.Parameters.AddWithValue("$cdate", customer.MarketingConsent ? Iso(DateTime.Now) : DBNull.Value);
        cmd.Parameters.AddWithValue("$now", Iso(DateTime.Now));
        var id = (long)cmd.ExecuteScalar()!;

        using (var code = conn.CreateCommand())
        {
            code.Transaction = tx;
            code.CommandText = "UPDATE customers SET code = $code WHERE id = $id;";
            code.Parameters.AddWithValue("$code", $"C{id:D5}");
            code.Parameters.AddWithValue("$id", id);
            code.ExecuteNonQuery();
        }

        AuditRepository.Write(conn, tx, actor, AuditActions.CustomerCreated, "customer", id.ToString(CultureInfo.InvariantCulture),
            $"Added customer C{id:D5}{(customer.MarketingConsent ? " (marketing consent given)" : "")}");
        tx.Commit();
        return Get(id)!;
    }

    public void Update(Customer customer, Actor actor)
    {
        Validate(customer);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var before = Get(conn, tx, customer.Id) ?? throw new InvalidOperationException("Customer not found.");
        if (before.IsAnonymised) throw new InvalidOperationException("This customer has been anonymised and can't be edited.");

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE customers SET name = $name, phone = $phone, email = $email, address = $address, notes = $notes,
                   marketing_consent = $consent, consent_date = $cdate
            WHERE id = $id;
            """;
        Bind(cmd, customer);
        cmd.Parameters.AddWithValue("$id", customer.Id);
        // Keep the original consent date unless consent is newly given; clear it when withdrawn.
        object consentDate = customer.MarketingConsent
            ? (before.MarketingConsent && before.ConsentDate is { } d ? Iso(d) : Iso(DateTime.Now))
            : DBNull.Value;
        cmd.Parameters.AddWithValue("$cdate", consentDate);
        cmd.ExecuteNonQuery();

        // Record which fields changed, not the personal values themselves.
        var changed = new List<string>();
        if (before.Name != customer.Name.Trim()) changed.Add("name");
        if (before.Phone != customer.Phone.Trim()) changed.Add("phone");
        if (before.Email != customer.Email.Trim()) changed.Add("email");
        if (before.Address != customer.Address.Trim()) changed.Add("address");
        if (before.Notes != customer.Notes.Trim()) changed.Add("notes");
        if (before.MarketingConsent != customer.MarketingConsent)
            changed.Add(customer.MarketingConsent ? "marketing consent GIVEN" : "marketing consent WITHDRAWN");
        if (changed.Count > 0)
            AuditRepository.Write(conn, tx, actor, AuditActions.CustomerUpdated, "customer", customer.Id.ToString(CultureInfo.InvariantCulture),
                $"{before.Code}: changed {string.Join(", ", changed)}");
        tx.Commit();
    }

    public Customer? Get(long id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{SelectColumns} WHERE c.id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return ReadAll(cmd).FirstOrDefault();
    }

    /// <summary>Find by name, phone, email or code. Anonymised customers are left out.</summary>
    public List<Customer> Search(string? text, int limit = 50)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        if (string.IsNullOrWhiteSpace(text))
        {
            cmd.CommandText = $"{SelectColumns} WHERE c.is_anonymised = 0 ORDER BY c.name LIMIT $limit;";
        }
        else
        {
            cmd.CommandText = $"""
                {SelectColumns}
                WHERE c.is_anonymised = 0 AND (c.name LIKE $q OR c.phone LIKE $q OR c.email LIKE $q OR c.code LIKE $q)
                ORDER BY c.name LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$q", $"%{text.Trim()}%");
        }
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadAll(cmd);
    }

    /// <summary>Records that someone opened a customer's profile (access to personal data).</summary>
    public void LogViewed(long id, Actor actor) =>
        new AuditRepository(db).Write(actor, AuditActions.CustomerViewed, "customer", id.ToString(CultureInfo.InvariantCulture), $"Viewed C{id:D5}");

    /// <summary>All personal data held about a customer, as JSON (a "subject access request").</summary>
    public string ExportData(long id, Actor actor)
    {
        var c = Get(id) ?? throw new InvalidOperationException("Customer not found.");
        var purchases = new SaleRepository(db).ForCustomer(id, 10_000);
        var export = new
        {
            exportedAt = DateTime.Now,
            customer = new
            {
                c.Code, c.Name, c.Phone, c.Email, c.Address, c.Notes,
                c.MarketingConsent, c.ConsentDate, c.CreatedAt, c.IsAnonymised,
            },
            purchases = purchases.Select(p => new { receipt = p.Id.ToString("D6"), date = p.Date, total = p.Total, method = p.Method.ToString(), items = p.Items }),
        };
        new AuditRepository(db).Write(actor, AuditActions.CustomerExported, "customer", id.ToString(CultureInfo.InvariantCulture),
            $"Exported personal data for {c.Code}");
        return JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// "Right to be forgotten": removes the customer's personal details. Their past sales stay
    /// (needed for tax records) but are no longer linked to anything identifying.
    /// </summary>
    public void Anonymise(long id, Actor actor)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var c = Get(conn, tx, id) ?? throw new InvalidOperationException("Customer not found.");
        if (c.IsAnonymised) return;
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE customers SET name = 'Anonymised customer', phone = '', email = '', address = '', notes = '',
                   marketing_consent = 0, consent_date = NULL, is_anonymised = 1
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        AuditRepository.Write(conn, tx, actor, AuditActions.CustomerAnonymised, "customer", id.ToString(CultureInfo.InvariantCulture),
            $"Anonymised {c.Code} at the customer's request");
        tx.Commit();
    }

    private static void Validate(Customer c)
    {
        if (string.IsNullOrWhiteSpace(c.Name)) throw new ArgumentException("Enter the customer's name.");
        if (!string.IsNullOrWhiteSpace(c.Email) && !c.Email.Contains('@')) throw new ArgumentException("That email address doesn't look right.");
    }

    private static void Bind(SqliteCommand cmd, Customer c)
    {
        cmd.Parameters.AddWithValue("$name", c.Name.Trim());
        cmd.Parameters.AddWithValue("$phone", c.Phone.Trim());
        cmd.Parameters.AddWithValue("$email", c.Email.Trim());
        cmd.Parameters.AddWithValue("$address", c.Address.Trim());
        cmd.Parameters.AddWithValue("$notes", c.Notes.Trim());
        cmd.Parameters.AddWithValue("$consent", c.MarketingConsent ? 1 : 0);
    }

    private static Customer? Get(SqliteConnection conn, SqliteTransaction tx, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"{SelectColumns} WHERE c.id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return ReadAll(cmd).FirstOrDefault();
    }

    private static List<Customer> ReadAll(SqliteCommand cmd)
    {
        var list = new List<Customer>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Customer
            {
                Id = r.GetInt64(0),
                Code = r.IsDBNull(1) ? "" : r.GetString(1),
                Name = r.GetString(2),
                Phone = r.GetString(3),
                Email = r.GetString(4),
                Address = r.GetString(5),
                Notes = r.GetString(6),
                MarketingConsent = r.GetInt64(7) == 1,
                ConsentDate = r.IsDBNull(8) ? null : Parse(r.GetString(8)),
                CreatedAt = Parse(r.GetString(9)),
                IsAnonymised = r.GetInt64(10) == 1,
                Visits = r.GetInt32(11),
                TotalSpent = Money.FromNgwee(r.GetInt64(12)),
                LastVisit = r.IsDBNull(13) ? null : Parse(r.GetString(13)),
            });
        }
        return list;
    }

    private static string Iso(DateTime t) => t.ToString("o", CultureInfo.InvariantCulture);
    private static DateTime Parse(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
