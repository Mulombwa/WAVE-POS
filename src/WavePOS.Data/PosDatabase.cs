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
        AddColumnIfMissing(conn, "products", "image_file", "TEXT");

        CreateRetailTables(conn);
        CreateAccountingTables(conn);
        CreateOrderTables(conn);
        CreateHrTables(conn);
        UpgradeRoles(conn);
        AddColumnIfMissing(conn, "customers", "tpin", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(conn, "customers", "credit_limit_ngwee", "INTEGER");
        AddColumnIfMissing(conn, "customers", "payment_terms_days", "INTEGER NOT NULL DEFAULT 0");

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

    /// <summary>
    /// Supply chain: suppliers, purchase orders, goods received, supplier bills and payments;
    /// sales orders (quotes), deliveries, tax invoices and customer payments.
    /// </summary>
    private static void CreateOrderTables(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS suppliers (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                code                TEXT    UNIQUE,
                name                TEXT    NOT NULL,
                tpin                TEXT    NOT NULL DEFAULT '',
                contact_name        TEXT    NOT NULL DEFAULT '',
                phone               TEXT    NOT NULL DEFAULT '',
                email               TEXT    NOT NULL DEFAULT '',
                address             TEXT    NOT NULL DEFAULT '',
                payment_terms_days  INTEGER NOT NULL DEFAULT 30,
                is_active           INTEGER NOT NULL DEFAULT 1,
                notes               TEXT    NOT NULL DEFAULT '',
                created_at          TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS purchase_orders (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                supplier_id     INTEGER NOT NULL REFERENCES suppliers(id),
                status          TEXT    NOT NULL,
                order_date      TEXT    NOT NULL,
                expected_date   TEXT,
                notes           TEXT    NOT NULL DEFAULT '',
                created_by      INTEGER REFERENCES users(id),
                created_by_name TEXT    NOT NULL,
                approved_by     INTEGER REFERENCES users(id),
                approved_by_name TEXT,
                approved_at     TEXT,
                created_at      TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_po_status ON purchase_orders(status);

            CREATE TABLE IF NOT EXISTS purchase_order_lines (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                po_id            INTEGER NOT NULL REFERENCES purchase_orders(id),
                product_id       INTEGER NOT NULL REFERENCES products(id),
                description      TEXT    NOT NULL,
                quantity         INTEGER NOT NULL CHECK (quantity > 0),
                unit_cost_ngwee  INTEGER NOT NULL CHECK (unit_cost_ngwee >= 0),
                vat_rate         REAL    NOT NULL,
                received         INTEGER NOT NULL DEFAULT 0,
                billed           INTEGER NOT NULL DEFAULT 0,
                CHECK (received >= 0 AND received <= quantity AND billed >= 0 AND billed <= received)
            );

            CREATE TABLE IF NOT EXISTS goods_receipts (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                po_id        INTEGER NOT NULL REFERENCES purchase_orders(id),
                received_on  TEXT    NOT NULL,
                note         TEXT    NOT NULL DEFAULT '',
                user_name    TEXT    NOT NULL,
                created_at   TEXT    NOT NULL
            );
            CREATE TABLE IF NOT EXISTS goods_receipt_lines (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                receipt_id  INTEGER NOT NULL REFERENCES goods_receipts(id),
                po_line_id  INTEGER NOT NULL REFERENCES purchase_order_lines(id),
                quantity    INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS supplier_bills (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                supplier_id       INTEGER NOT NULL REFERENCES suppliers(id),
                po_id             INTEGER NOT NULL REFERENCES purchase_orders(id),
                supplier_invoice  TEXT    NOT NULL,
                bill_date         TEXT    NOT NULL,
                due_date          TEXT    NOT NULL,
                net_ngwee         INTEGER NOT NULL,
                vat_ngwee         INTEGER NOT NULL,
                paid_ngwee        INTEGER NOT NULL DEFAULT 0,
                journal_entry_id  INTEGER REFERENCES journal_entries(id),
                user_name         TEXT    NOT NULL,
                created_at        TEXT    NOT NULL,
                UNIQUE (supplier_id, supplier_invoice)
            );
            CREATE TABLE IF NOT EXISTS supplier_bill_lines (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                bill_id          INTEGER NOT NULL REFERENCES supplier_bills(id),
                po_line_id       INTEGER NOT NULL REFERENCES purchase_order_lines(id),
                quantity         INTEGER NOT NULL,
                unit_cost_ngwee  INTEGER NOT NULL,
                vat_rate         REAL    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS supplier_payments (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                bill_id           INTEGER NOT NULL REFERENCES supplier_bills(id),
                paid_on           TEXT    NOT NULL,
                amount_ngwee      INTEGER NOT NULL CHECK (amount_ngwee > 0),
                account_id        INTEGER NOT NULL REFERENCES accounts(id),
                reference         TEXT    NOT NULL DEFAULT '',
                journal_entry_id  INTEGER REFERENCES journal_entries(id),
                user_name         TEXT    NOT NULL,
                created_at        TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sales_orders (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                customer_id      INTEGER NOT NULL REFERENCES customers(id),
                status           TEXT    NOT NULL,
                order_date       TEXT    NOT NULL,
                valid_until      TEXT,
                notes            TEXT    NOT NULL DEFAULT '',
                created_by       INTEGER REFERENCES users(id),
                created_by_name  TEXT    NOT NULL,
                credit_override  TEXT,
                created_at       TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_so_status ON sales_orders(status);

            CREATE TABLE IF NOT EXISTS sales_order_lines (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                order_id          INTEGER NOT NULL REFERENCES sales_orders(id),
                product_id        INTEGER NOT NULL REFERENCES products(id),
                description       TEXT    NOT NULL,
                quantity          INTEGER NOT NULL CHECK (quantity > 0),
                unit_price_ngwee  INTEGER NOT NULL CHECK (unit_price_ngwee >= 0),
                vat_rate          REAL    NOT NULL,
                delivered         INTEGER NOT NULL DEFAULT 0,
                invoiced          INTEGER NOT NULL DEFAULT 0,
                CHECK (delivered >= 0 AND delivered <= quantity AND invoiced >= 0 AND invoiced <= delivered)
            );

            CREATE TABLE IF NOT EXISTS deliveries (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                order_id      INTEGER NOT NULL REFERENCES sales_orders(id),
                delivered_on  TEXT    NOT NULL,
                note          TEXT    NOT NULL DEFAULT '',
                user_name     TEXT    NOT NULL,
                created_at    TEXT    NOT NULL
            );
            CREATE TABLE IF NOT EXISTS delivery_lines (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                delivery_id      INTEGER NOT NULL REFERENCES deliveries(id),
                order_line_id    INTEGER NOT NULL REFERENCES sales_order_lines(id),
                quantity         INTEGER NOT NULL,
                unit_cost_ngwee  INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS customer_invoices (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                customer_id       INTEGER NOT NULL REFERENCES customers(id),
                order_id          INTEGER NOT NULL REFERENCES sales_orders(id),
                invoice_date      TEXT    NOT NULL,
                due_date          TEXT    NOT NULL,
                total_ngwee       INTEGER NOT NULL,
                vat_ngwee         INTEGER NOT NULL,
                paid_ngwee        INTEGER NOT NULL DEFAULT 0,
                journal_entry_id  INTEGER REFERENCES journal_entries(id),
                user_name         TEXT    NOT NULL,
                created_at        TEXT    NOT NULL
            );
            CREATE TABLE IF NOT EXISTS customer_invoice_lines (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                invoice_id        INTEGER NOT NULL REFERENCES customer_invoices(id),
                order_line_id     INTEGER NOT NULL REFERENCES sales_order_lines(id),
                description       TEXT    NOT NULL,
                quantity          INTEGER NOT NULL,
                unit_price_ngwee  INTEGER NOT NULL,
                vat_rate          REAL    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS customer_payments (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                invoice_id        INTEGER NOT NULL REFERENCES customer_invoices(id),
                paid_on           TEXT    NOT NULL,
                amount_ngwee      INTEGER NOT NULL CHECK (amount_ngwee > 0),
                account_id        INTEGER NOT NULL REFERENCES accounts(id),
                reference         TEXT    NOT NULL DEFAULT '',
                journal_entry_id  INTEGER REFERENCES journal_entries(id),
                user_name         TEXT    NOT NULL,
                created_at        TEXT    NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>HR and payroll: employees, contracts, leave, attendance, statutory rates, payroll runs and payslips.</summary>
    private static void CreateHrTables(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS employees (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                number              TEXT    UNIQUE,
                first_name          TEXT    NOT NULL,
                last_name           TEXT    NOT NULL,
                job_title           TEXT    NOT NULL DEFAULT '',
                department          TEXT    NOT NULL DEFAULT '',
                phone               TEXT    NOT NULL DEFAULT '',
                email               TEXT    NOT NULL DEFAULT '',
                address             TEXT    NOT NULL DEFAULT '',
                date_of_birth       TEXT,
                hire_date           TEXT    NOT NULL,
                left_date           TEXT,
                status              TEXT    NOT NULL DEFAULT 'Active',
                nrc                 TEXT    NOT NULL DEFAULT '',
                tpin                TEXT    NOT NULL DEFAULT '',
                napsa_number        TEXT    NOT NULL DEFAULT '',
                nhima_number        TEXT    NOT NULL DEFAULT '',
                pay_method          TEXT    NOT NULL DEFAULT 'Bank',
                bank_name           TEXT    NOT NULL DEFAULT '',
                bank_branch         TEXT    NOT NULL DEFAULT '',
                bank_account        TEXT    NOT NULL DEFAULT '',
                mobile_money_number TEXT    NOT NULL DEFAULT '',
                notes               TEXT    NOT NULL DEFAULT '',
                created_at          TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS employment_contracts (
                id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                employee_id           INTEGER NOT NULL REFERENCES employees(id),
                type                  TEXT    NOT NULL,
                start_date            TEXT    NOT NULL,
                end_date              TEXT,
                basic_ngwee           INTEGER NOT NULL CHECK (basic_ngwee >= 0),
                housing_ngwee         INTEGER NOT NULL DEFAULT 0,
                transport_ngwee       INTEGER NOT NULL DEFAULT 0,
                other_allowance_ngwee INTEGER NOT NULL DEFAULT 0,
                hours_per_week        REAL    NOT NULL,
                leave_days_per_year   REAL    NOT NULL,
                notes                 TEXT    NOT NULL DEFAULT '',
                created_by            TEXT    NOT NULL,
                created_at            TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_contracts_employee ON employment_contracts(employee_id, start_date);

            CREATE TABLE IF NOT EXISTS leave_requests (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                employee_id    INTEGER NOT NULL REFERENCES employees(id),
                type           TEXT    NOT NULL,
                start_date     TEXT    NOT NULL,
                end_date       TEXT    NOT NULL,
                days           REAL    NOT NULL,
                reason         TEXT    NOT NULL DEFAULT '',
                status         TEXT    NOT NULL DEFAULT 'Pending',
                requested_by   TEXT    NOT NULL,
                decided_by     TEXT,
                decided_at     TEXT,
                decision_note  TEXT    NOT NULL DEFAULT '',
                created_at     TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_leave_employee ON leave_requests(employee_id, start_date);

            CREATE TABLE IF NOT EXISTS attendance (
                employee_id     INTEGER NOT NULL REFERENCES employees(id),
                date            TEXT    NOT NULL,
                status          TEXT    NOT NULL,
                hours_worked    REAL    NOT NULL DEFAULT 0,
                overtime_hours  REAL    NOT NULL DEFAULT 0,
                note            TEXT    NOT NULL DEFAULT '',
                recorded_by     TEXT    NOT NULL,
                recorded_at     TEXT    NOT NULL,
                PRIMARY KEY (employee_id, date)
            );

            CREATE TABLE IF NOT EXISTS payroll_rates (
                effective_from  TEXT PRIMARY KEY,
                rates_json      TEXT NOT NULL,
                set_by          TEXT NOT NULL,
                created_at      TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS payroll_runs (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                period              TEXT    NOT NULL UNIQUE,
                status              TEXT    NOT NULL DEFAULT 'Draft',
                rates_from          TEXT    NOT NULL,
                prepared_by_id      INTEGER,
                prepared_by         TEXT    NOT NULL,
                approved_by         TEXT,
                approved_at         TEXT,
                journal_entry_id    INTEGER REFERENCES journal_entries(id),
                paid_on             TEXT,
                payment_entry_id    INTEGER REFERENCES journal_entries(id),
                created_at          TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS payslips (
                id                     INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id                 INTEGER NOT NULL REFERENCES payroll_runs(id),
                employee_id            INTEGER NOT NULL REFERENCES employees(id),
                employee_number        TEXT    NOT NULL,
                employee_name          TEXT    NOT NULL,
                job_title              TEXT    NOT NULL,
                department             TEXT    NOT NULL,
                nrc                    TEXT    NOT NULL,
                tpin                   TEXT    NOT NULL,
                napsa_number           TEXT    NOT NULL,
                pay_method             TEXT    NOT NULL,
                pay_account            TEXT    NOT NULL,
                basic_ngwee            INTEGER NOT NULL,
                allowances_ngwee       INTEGER NOT NULL,
                overtime_hours         REAL    NOT NULL,
                overtime_ngwee         INTEGER NOT NULL,
                bonus_ngwee            INTEGER NOT NULL DEFAULT 0,
                unpaid_days            REAL    NOT NULL,
                unpaid_ngwee           INTEGER NOT NULL,
                other_deductions_ngwee INTEGER NOT NULL DEFAULT 0,
                other_deductions_note  TEXT    NOT NULL DEFAULT '',
                gross_ngwee            INTEGER NOT NULL,
                taxable_ngwee          INTEGER NOT NULL,
                paye_ngwee             INTEGER NOT NULL,
                napsa_employee_ngwee   INTEGER NOT NULL,
                napsa_employer_ngwee   INTEGER NOT NULL,
                nhima_employee_ngwee   INTEGER NOT NULL,
                nhima_employer_ngwee   INTEGER NOT NULL,
                sdl_ngwee              INTEGER NOT NULL,
                net_ngwee              INTEGER NOT NULL,
                annual_leave_balance   REAL    NOT NULL DEFAULT 0,
                UNIQUE (run_id, employee_id)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Roles were expanded: Manager became Admin and Supervisor became Operations manager. Every business
    /// needs a super admin (the owner), so the longest-standing active admin becomes one if there isn't one.
    /// </summary>
    private static void UpgradeRoles(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE users SET role = 'Admin' WHERE role = 'Manager';
            UPDATE users SET role = 'OperationsManager' WHERE role = 'Supervisor';
            UPDATE users SET role = 'SuperAdmin'
            WHERE NOT EXISTS (SELECT 1 FROM users WHERE role = 'SuperAdmin' AND is_active = 1)
              AND id = (SELECT MIN(id) FROM users WHERE role = 'Admin' AND is_active = 1);
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
