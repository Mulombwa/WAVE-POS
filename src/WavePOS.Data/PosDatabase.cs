using Microsoft.Data.Sqlite;

namespace WavePOS.Data;

/// <summary>
/// Local SQLite database so the till keeps working offline.
/// Money is stored as whole ngwee (INTEGER) to avoid rounding errors.
/// </summary>
public class PosDatabase
{
    public PosDatabase(string filePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        Directory.CreateDirectory(dir);
        DataDirectory = dir;
        ConnectionString = new SqliteConnectionStringBuilder { DataSource = filePath }.ToString();
    }

    public string ConnectionString { get; }

    /// <summary>Folder holding the database; also used for the business's logo file.</summary>
    public string DataDirectory { get; }

    /// <summary>Default location: %LOCALAPPDATA%\WavePOS\wavepos.db</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WavePOS", "wavepos.db");

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    /// <summary>Creates tables if missing and adds sample products to an empty database.</summary>
    public void Initialize(bool seedSampleProducts = true)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS products (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                sku            TEXT    NOT NULL UNIQUE,
                barcode        TEXT    UNIQUE,
                name           TEXT    NOT NULL,
                category       TEXT    NOT NULL DEFAULT 'General',
                price_ngwee    INTEGER NOT NULL,
                vat_rate       REAL    NOT NULL DEFAULT 0.16,
                stock_quantity INTEGER NOT NULL DEFAULT 0,
                is_active      INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS sales (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp        TEXT    NOT NULL,
                total_ngwee      INTEGER NOT NULL,
                vat_ngwee        INTEGER NOT NULL,
                payment_method   TEXT    NOT NULL,
                tendered_ngwee   INTEGER NOT NULL,
                change_ngwee     INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS users (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                username         TEXT    NOT NULL UNIQUE COLLATE NOCASE,
                display_name     TEXT    NOT NULL,
                password_hash    TEXT    NOT NULL,
                role             TEXT    NOT NULL DEFAULT 'Cashier',
                is_active        INTEGER NOT NULL DEFAULT 1,
                failed_attempts  INTEGER NOT NULL DEFAULT 0,
                locked_until     TEXT,
                last_login       TEXT,
                created_at       TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS settings (
                key    TEXT PRIMARY KEY,
                value  TEXT
            );

            CREATE TABLE IF NOT EXISTS draft_invoices (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                number         TEXT    UNIQUE,
                created_at     TEXT    NOT NULL,
                valid_until    TEXT,
                cashier_id     INTEGER REFERENCES users(id),
                customer_name  TEXT,
                total_ngwee    INTEGER NOT NULL,
                vat_ngwee      INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS draft_invoice_lines (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                draft_id          INTEGER NOT NULL REFERENCES draft_invoices(id),
                product_id        INTEGER REFERENCES products(id),
                description       TEXT    NOT NULL,
                unit_price_ngwee  INTEGER NOT NULL,
                quantity          INTEGER NOT NULL,
                vat_rate          REAL    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sale_lines (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                sale_id           INTEGER NOT NULL REFERENCES sales(id),
                product_id        INTEGER NOT NULL REFERENCES products(id),
                product_name      TEXT    NOT NULL,
                unit_price_ngwee  INTEGER NOT NULL,
                quantity          INTEGER NOT NULL,
                vat_rate          REAL    NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        // Upgrades for databases created by earlier versions.
        AddColumnIfMissing(conn, "sales", "cashier_id", "INTEGER REFERENCES users(id)");
        AddColumnIfMissing(conn, "sales", "customer_id", "INTEGER REFERENCES customers(id)");
        AddColumnIfMissing(conn, "products", "cost_ngwee", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "products", "reorder_level", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "products", "family_id", "INTEGER REFERENCES product_families(id)");
        AddColumnIfMissing(conn, "products", "option1", "TEXT");
        AddColumnIfMissing(conn, "products", "option2", "TEXT");
        AddColumnIfMissing(conn, "users", "employee_code", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(conn, "users", "phone", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(conn, "users", "email", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(conn, "sale_lines", "unit_cost_ngwee", "INTEGER NOT NULL DEFAULT 0");

        CreateRetailTables(conn);
        CreateAccountingTables(conn);

        if (seedSampleProducts) SeedSampleProducts(conn);

        // Start the books: chart of accounts plus the stock already on hand as an opening balance.
        LedgerRepository.StartBooks(conn);
    }

    /// <summary>
    /// Double-entry books. Journal entries and lines can't be changed or deleted (database
    /// triggers): mistakes are corrected with a reversing entry, as accountants expect.
    /// </summary>
    private static void CreateAccountingTables(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS accounts (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                code         TEXT    NOT NULL UNIQUE,
                name         TEXT    NOT NULL,
                type         TEXT    NOT NULL,
                system_key   TEXT    UNIQUE,
                is_bank      INTEGER NOT NULL DEFAULT 0,
                is_active    INTEGER NOT NULL DEFAULT 1,
                description  TEXT    NOT NULL DEFAULT '',
                created_at   TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS journal_entries (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                date         TEXT    NOT NULL,
                reference    TEXT    NOT NULL DEFAULT '',
                memo         TEXT    NOT NULL DEFAULT '',
                source       TEXT    NOT NULL,
                source_id    TEXT,
                reverses_id  INTEGER REFERENCES journal_entries(id),
                user_id      INTEGER,
                user_name    TEXT    NOT NULL,
                created_at   TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_journal_date ON journal_entries(date);
            CREATE INDEX IF NOT EXISTS ix_journal_source ON journal_entries(source, source_id);
            -- An entry can only be reversed once.
            CREATE UNIQUE INDEX IF NOT EXISTS ux_journal_reverses ON journal_entries(reverses_id) WHERE reverses_id IS NOT NULL;

            CREATE TABLE IF NOT EXISTS journal_lines (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                entry_id      INTEGER NOT NULL REFERENCES journal_entries(id),
                account_id    INTEGER NOT NULL REFERENCES accounts(id),
                debit_ngwee   INTEGER NOT NULL DEFAULT 0,
                credit_ngwee  INTEGER NOT NULL DEFAULT 0,
                description   TEXT    NOT NULL DEFAULT '',
                CHECK (debit_ngwee >= 0 AND credit_ngwee >= 0 AND (debit_ngwee = 0) <> (credit_ngwee = 0))
            );
            CREATE INDEX IF NOT EXISTS ix_journal_lines_account ON journal_lines(account_id);
            CREATE INDEX IF NOT EXISTS ix_journal_lines_entry ON journal_lines(entry_id);

            CREATE TRIGGER IF NOT EXISTS journal_entries_no_update BEFORE UPDATE ON journal_entries
            BEGIN SELECT RAISE(ABORT, 'Posted journals cannot be changed. Post a reversal instead.'); END;
            CREATE TRIGGER IF NOT EXISTS journal_entries_no_delete BEFORE DELETE ON journal_entries
            BEGIN SELECT RAISE(ABORT, 'Posted journals cannot be deleted. Post a reversal instead.'); END;
            CREATE TRIGGER IF NOT EXISTS journal_lines_no_update BEFORE UPDATE ON journal_lines
            BEGIN SELECT RAISE(ABORT, 'Posted journals cannot be changed. Post a reversal instead.'); END;
            CREATE TRIGGER IF NOT EXISTS journal_lines_no_delete BEFORE DELETE ON journal_lines
            BEGIN SELECT RAISE(ABORT, 'Posted journals cannot be deleted. Post a reversal instead.'); END;

            -- Imported bank and mobile-money statements, for reconciliation.
            CREATE TABLE IF NOT EXISTS bank_imports (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                account_id       INTEGER NOT NULL REFERENCES accounts(id),
                file_name        TEXT    NOT NULL,
                imported_at      TEXT    NOT NULL,
                user_name        TEXT    NOT NULL,
                line_count       INTEGER NOT NULL,
                duplicate_count  INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS bank_lines (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                account_id        INTEGER NOT NULL REFERENCES accounts(id),
                import_id         INTEGER REFERENCES bank_imports(id),
                date              TEXT    NOT NULL,
                description       TEXT    NOT NULL,
                reference         TEXT    NOT NULL DEFAULT '',
                amount_ngwee      INTEGER NOT NULL,
                balance_ngwee     INTEGER,
                fingerprint       TEXT    NOT NULL,
                status            TEXT    NOT NULL DEFAULT 'Unmatched',
                journal_entry_id  INTEGER REFERENCES journal_entries(id),
                note              TEXT    NOT NULL DEFAULT '',
                UNIQUE (account_id, fingerprint)
            );
            CREATE INDEX IF NOT EXISTS ix_bank_lines_status ON bank_lines(account_id, status);
            -- A ledger entry can only explain one statement line per account.
            CREATE UNIQUE INDEX IF NOT EXISTS ux_bank_lines_entry ON bank_lines(account_id, journal_entry_id) WHERE journal_entry_id IS NOT NULL;
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Phase 1 retail tables: customers, variants, stock ledger, returns and the audit trail.</summary>
    private static void CreateRetailTables(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS customers (
                id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                code               TEXT    UNIQUE,
                name               TEXT    NOT NULL,
                phone              TEXT    NOT NULL DEFAULT '',
                email              TEXT    NOT NULL DEFAULT '',
                address            TEXT    NOT NULL DEFAULT '',
                notes              TEXT    NOT NULL DEFAULT '',
                marketing_consent  INTEGER NOT NULL DEFAULT 0,
                consent_date       TEXT,
                created_at         TEXT    NOT NULL,
                is_anonymised      INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_customers_phone ON customers(phone);

            CREATE TABLE IF NOT EXISTS product_families (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                name          TEXT NOT NULL,
                option1_name  TEXT,
                option2_name  TEXT,
                created_at    TEXT NOT NULL
            );

            -- Every stock change, so stock levels can always be explained.
            CREATE TABLE IF NOT EXISTS stock_movements (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                product_id     INTEGER NOT NULL REFERENCES products(id),
                change         INTEGER NOT NULL,
                balance_after  INTEGER NOT NULL,
                reason         TEXT    NOT NULL,
                note           TEXT    NOT NULL DEFAULT '',
                reference      TEXT,
                user_id        INTEGER REFERENCES users(id),
                user_name      TEXT    NOT NULL DEFAULT '',
                timestamp      TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_stock_movements_product ON stock_movements(product_id, id);

            CREATE TABLE IF NOT EXISTS returns (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                sale_id         INTEGER NOT NULL REFERENCES sales(id),
                timestamp       TEXT    NOT NULL,
                user_id         INTEGER REFERENCES users(id),
                reason          TEXT    NOT NULL,
                note            TEXT    NOT NULL DEFAULT '',
                refund_method   TEXT    NOT NULL,
                total_ngwee     INTEGER NOT NULL,
                vat_ngwee       INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS return_lines (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                return_id         INTEGER NOT NULL REFERENCES returns(id),
                sale_line_id      INTEGER NOT NULL REFERENCES sale_lines(id),
                product_id        INTEGER NOT NULL REFERENCES products(id),
                product_name      TEXT    NOT NULL,
                unit_price_ngwee  INTEGER NOT NULL,
                quantity          INTEGER NOT NULL,
                vat_rate          REAL    NOT NULL,
                restocked         INTEGER NOT NULL
            );

            -- Append-only audit trail. Each row carries a hash of the previous row (see AuditEntry).
            CREATE TABLE IF NOT EXISTS audit_log (
                id             INTEGER PRIMARY KEY,
                timestamp_utc  TEXT    NOT NULL,
                user_id        INTEGER,
                user_name      TEXT    NOT NULL,
                action         TEXT    NOT NULL,
                entity_type    TEXT    NOT NULL,
                entity_id      TEXT,
                details        TEXT    NOT NULL,
                previous_hash  TEXT    NOT NULL,
                hash           TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_audit_action ON audit_log(action);
            CREATE INDEX IF NOT EXISTS ix_audit_entity ON audit_log(entity_type, entity_id);

            CREATE TRIGGER IF NOT EXISTS audit_log_no_update BEFORE UPDATE ON audit_log
            BEGIN SELECT RAISE(ABORT, 'The audit log cannot be changed.'); END;

            CREATE TRIGGER IF NOT EXISTS audit_log_no_delete BEFORE DELETE ON audit_log
            BEGIN SELECT RAISE(ABORT, 'The audit log cannot be deleted from.'); END;
            """;
        cmd.ExecuteNonQuery();
    }

    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string definition)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $col;";
        check.Parameters.AddWithValue("$col", column);
        if ((long)check.ExecuteScalar()! > 0) return;

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    private static void SeedSampleProducts(SqliteConnection conn)
    {
        using var count = conn.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM products;";
        if ((long)count.ExecuteScalar()! > 0) return;

        (string sku, string barcode, string name, string cat, decimal price, decimal vat)[] items =
        [
            ("BRD-001", "6009801000011", "White Bread 700g",      "Bakery",    25.00m, 0m),
            ("BRD-002", "6009801000028", "Brown Bread 700g",      "Bakery",    27.00m, 0m),
            ("DRK-001", "5449000000996", "Coca-Cola 500ml",       "Drinks",    11.60m, 0.16m),
            ("DRK-002", "6001240100011", "Mazoe Orange 2L",       "Drinks",    69.00m, 0.16m),
            ("DRK-003", "6009880000015", "Mineral Water 500ml",   "Drinks",     6.00m, 0.16m),
            ("GRO-001", "6009612000011", "Mealie Meal 10kg",      "Groceries", 185.00m, 0m),
            ("GRO-002", "6009612000028", "Cooking Oil 2L",        "Groceries", 98.00m, 0.16m),
            ("GRO-003", "6009612000035", "Sugar 2kg",             "Groceries", 62.00m, 0.16m),
            ("GRO-004", "6009612000042", "Rice 2kg",              "Groceries", 58.00m, 0.16m),
            ("DAI-001", "6009705000011", "Fresh Milk 1L",         "Dairy",     28.00m, 0m),
            ("DAI-002", "6009705000028", "Eggs (Tray of 30)",     "Dairy",     95.00m, 0m),
            ("HOU-001", "6001087000011", "Bath Soap 150g",        "Household", 15.50m, 0.16m),
            ("HOU-002", "6001087000028", "Washing Powder 1kg",    "Household", 52.00m, 0.16m),
            ("AIR-001", "AIRTIME-K10",   "Airtime K10",           "Airtime",   10.00m, 0m),
        ];

        using var tx = conn.BeginTransaction();
        foreach (var p in items)
        {
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            // Sample cost = 70% of the selling price; reorder at 10 units. Opening stock goes in the ledger.
            insert.CommandText = """
                INSERT INTO products (sku, barcode, name, category, price_ngwee, cost_ngwee, vat_rate, stock_quantity, reorder_level)
                VALUES ($sku, $barcode, $name, $cat, $price, $cost, $vat, 100, 10);
                INSERT INTO stock_movements (product_id, change, balance_after, reason, note, user_name, timestamp)
                VALUES (last_insert_rowid(), 100, 100, 'InitialStock', 'Sample data', 'System', $now);
                """;
            insert.Parameters.AddWithValue("$sku", p.sku);
            insert.Parameters.AddWithValue("$barcode", p.barcode);
            insert.Parameters.AddWithValue("$name", p.name);
            insert.Parameters.AddWithValue("$cat", p.cat);
            insert.Parameters.AddWithValue("$price", Core.Money.ToNgwee(p.price));
            insert.Parameters.AddWithValue("$cost", Core.Money.ToNgwee(p.price * 0.7m));
            insert.Parameters.AddWithValue("$vat", (double)p.vat);
            insert.Parameters.AddWithValue("$now", DateTime.Now.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
