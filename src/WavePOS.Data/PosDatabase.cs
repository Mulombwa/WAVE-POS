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
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        ConnectionString = new SqliteConnectionStringBuilder { DataSource = filePath }.ToString();
    }

    public string ConnectionString { get; }

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

        if (seedSampleProducts) SeedSampleProducts(conn);
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
            insert.CommandText = """
                INSERT INTO products (sku, barcode, name, category, price_ngwee, vat_rate, stock_quantity)
                VALUES ($sku, $barcode, $name, $cat, $price, $vat, 100);
                """;
            insert.Parameters.AddWithValue("$sku", p.sku);
            insert.Parameters.AddWithValue("$barcode", p.barcode);
            insert.Parameters.AddWithValue("$name", p.name);
            insert.Parameters.AddWithValue("$cat", p.cat);
            insert.Parameters.AddWithValue("$price", Core.Money.ToNgwee(p.price));
            insert.Parameters.AddWithValue("$vat", (double)p.vat);
            insert.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
