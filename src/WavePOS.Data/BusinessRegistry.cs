using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using WavePOS.Core.Audit;
using WavePOS.Core.Models;
using WavePOS.Core.Security;

namespace WavePOS.Data;

/// <summary>A business using WAVE POS on this server.</summary>
public record Business(long Id, string Code, string Name, BusinessSize Size, ModuleSet Modules, string DatabasePath,
    string Phone, string Email, string Town, DateTime CreatedAt, bool IsActive);

/// <summary>What a business fills in on the sign-up page.</summary>
public record BusinessSignUp
{
    public string BusinessName { get; init; } = "";
    public BusinessSize Size { get; init; } = BusinessSize.Small;
    public string Town { get; init; } = "";
    public string Phone { get; init; } = "";
    public string Email { get; init; } = "";
    public string Tpin { get; init; } = "";
    public string OwnerName { get; init; } = "";
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public bool SampleProducts { get; init; }
}

/// <summary>
/// The businesses on this server. Each one has its own database in its own folder
/// (businesses/{id}/wavepos.db), so one business's data, logo and pictures can never be read
/// through another's account. The registry itself only holds names, codes and switched-on modules.
/// </summary>
public class BusinessRegistry
{
    private readonly string _folder;
    private readonly string _connectionString;
    private readonly ConcurrentDictionary<long, PosDatabase> _databases = new();
    private readonly object _signUpLock = new();
    private List<Business>? _cache;

    private static readonly HashSet<string> ReservedCodes =
        ["admin", "wave", "wavepos", "login", "logout", "welcome", "start", "api", "support", "help", "system", "test"];

    public BusinessRegistry(string folder)
    {
        _folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(_folder);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_folder, "platform.db") }.ToString();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS businesses (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                code        TEXT    NOT NULL UNIQUE COLLATE NOCASE,
                name        TEXT    NOT NULL,
                size        TEXT    NOT NULL,
                modules     TEXT    NOT NULL,
                db_path     TEXT    NOT NULL,
                phone       TEXT    NOT NULL DEFAULT '',
                email       TEXT    NOT NULL DEFAULT '',
                town        TEXT    NOT NULL DEFAULT '',
                created_at  TEXT    NOT NULL,
                is_active   INTEGER NOT NULL DEFAULT 1
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public string Folder => _folder;

    public List<Business> All()
    {
        if (_cache is { } cached) return cached;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, code, name, size, modules, db_path, phone, email, town, created_at, is_active FROM businesses ORDER BY id;";
        var list = new List<Business>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Business(r.GetInt64(0), r.GetString(1), r.GetString(2),
                Enum.TryParse<BusinessSize>(r.GetString(3), out var size) ? size : BusinessSize.Small,
                ModuleSet.Parse(r.GetString(4)), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8),
                DateTime.Parse(r.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), r.GetInt64(10) == 1));
        return _cache = list;
    }

    public bool Any() => All().Count > 0;

    public Business? Get(long id) => All().FirstOrDefault(b => b.Id == id);

    /// <summary>Finds a business by the code staff type when signing in (not case-sensitive).</summary>
    public Business? Find(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : All().FirstOrDefault(b => string.Equals(b.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The business's own database, created and upgraded the first time it's used.</summary>
    public PosDatabase Database(Business business) => _databases.GetOrAdd(business.Id, _ =>
    {
        var db = new PosDatabase(business.DatabasePath);
        db.Initialize(seedSampleProducts: false);
        return db;
    });

    /// <summary>
    /// Signs up a new business: its own database, its details, the modules for its size, and the owner's
    /// super admin account. Returns the business and the owner.
    /// </summary>
    public (Business Business, User Owner) Register(BusinessSignUp s)
    {
        var name = s.BusinessName.Trim();
        if (name.Length < 2) throw new ArgumentException("Enter the business name.");
        if (name.Length > 80) throw new ArgumentException("The business name can be up to 80 characters.");
        if (string.IsNullOrWhiteSpace(s.OwnerName)) throw new ArgumentException("Enter your full name.");
        if (string.IsNullOrWhiteSpace(s.Phone) && string.IsNullOrWhiteSpace(s.Email)) throw new ArgumentException("Enter a phone number or email so we can reach you.");
        var tpin = s.Tpin.Trim();
        if (tpin.Length > 0 && (tpin.Length != 10 || !tpin.All(char.IsAsciiDigit))) throw new ArgumentException("A ZRA TPIN is 10 digits (or leave it blank).");
        if (PasswordHasher.Validate(s.Password) is { } weak) throw new ArgumentException(weak);
        if (string.IsNullOrWhiteSpace(s.Username)) throw new ArgumentException("Choose a username.");

        lock (_signUpLock) // one sign-up at a time, so two businesses can't get the same code
        {
            var code = UniqueCode(name);
            var modules = ModuleSet.ForSize(s.Size);
            long id;
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO businesses (code, name, size, modules, db_path, phone, email, town, created_at)
                    VALUES ($code, $name, $size, $mods, '', $phone, $email, $town, $now);
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("$code", code);
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$size", s.Size.ToString());
                cmd.Parameters.AddWithValue("$mods", modules.ToString());
                cmd.Parameters.AddWithValue("$phone", s.Phone.Trim());
                cmd.Parameters.AddWithValue("$email", s.Email.Trim());
                cmd.Parameters.AddWithValue("$town", s.Town.Trim());
                cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
                id = (long)cmd.ExecuteScalar()!;
            }

            var path = Path.Combine(_folder, "businesses", id.ToString(CultureInfo.InvariantCulture), "wavepos.db");
            try
            {
                var db = new PosDatabase(path);
                db.Initialize(seedSampleProducts: s.SampleProducts);
                var settings = new SettingsRepository(db);
                var profile = settings.GetBusinessProfile();
                profile.Name = name;
                profile.Phone = s.Phone.Trim();
                profile.Email = s.Email.Trim();
                profile.Address = s.Town.Trim();
                profile.Tpin = tpin;
                profile.ReceiptFooter = "Thank you for shopping with us!";
                settings.SaveBusinessProfile(profile);
                var owner = new UserRepository(db).Create(s.Username, s.OwnerName, s.Password, UserRole.SuperAdmin);
                new AuditRepository(db).Write(new Actor(owner.Id, owner.DisplayName), AuditActions.BusinessCreated, "business", code,
                    $"Business \"{name}\" signed up as {s.Size.Label()} with {string.Join(", ", modules.On.Select(m => m.Label()))}");

                Exec("UPDATE businesses SET db_path = $p WHERE id = $id;", ("$p", path), ("$id", id));
                _cache = null;
                _databases[id] = db;
                return (Get(id)!, owner);
            }
            catch
            {
                // Don't leave a half-made business behind.
                Exec("DELETE FROM businesses WHERE id = $id;", ("$id", id));
                _cache = null;
                SqliteConnection.ClearAllPools();
                try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch (IOException) { }
                throw;
            }
        }
    }

    /// <summary>
    /// Upgrade from a single-business install: the existing database becomes the first business,
    /// with every module on so nothing that was in use disappears.
    /// </summary>
    public Business? AdoptExisting(string databasePath)
    {
        if (Any() || !File.Exists(databasePath)) return null;
        var db = new PosDatabase(databasePath);
        db.Initialize(seedSampleProducts: false);
        if (!new UserRepository(db).AnyUsers()) return null;
        var name = new SettingsRepository(db).GetBusinessProfile().Name;
        lock (_signUpLock)
        {
            Exec("""
                INSERT INTO businesses (code, name, size, modules, db_path, created_at)
                VALUES ($code, $name, 'Large', $mods, $path, $now);
                """, ("$code", UniqueCode(name)), ("$name", name), ("$mods", ModuleSet.All.ToString()),
                ("$path", Path.GetFullPath(databasePath)), ("$now", DateTime.Now.ToString("o", CultureInfo.InvariantCulture)));
            _cache = null;
        }
        var adopted = All()[0];
        _databases[adopted.Id] = db;
        return adopted;
    }

    /// <summary>Switches modules on or off (super admin only). The till and stock always stay on.</summary>
    public Business SetModules(long businessId, BusinessSize size, ModuleSet modules, Actor actor)
    {
        var before = Get(businessId) ?? throw new InvalidOperationException("Business not found.");
        foreach (var m in modules.On)
            foreach (var needed in ModuleSet.Needs(m))
                if (!modules.Has(needed)) throw new ArgumentException($"{m.Label()} needs {needed.Label()} switched on too.");
        Exec("UPDATE businesses SET size = $s, modules = $m WHERE id = $id;", ("$s", size.ToString()), ("$m", modules.ToString()), ("$id", businessId));
        _cache = null;
        static string List(IEnumerable<Module> ms) => string.Join(", ", ms.Select(m => m.Label())) is { Length: > 0 } s ? s : "nothing";
        var added = List(modules.On.Where(m => !before.Modules.Has(m)));
        var removed = List(before.Modules.On.Where(m => !modules.Has(m)));
        new AuditRepository(Database(before)).Write(actor, AuditActions.ModulesChanged, "business", before.Code,
            $"Modules changed ({size.Label()}): switched on {added}; switched off {removed}");
        return Get(businessId)!;
    }

    /// <summary>A short code from the business name, e.g. "Mwila's Salon" → "mwilas-salon", made unique.</summary>
    public static string CodeFrom(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-' && (char.IsWhiteSpace(c) || c is '-' or '_' or '&' or '.')) sb.Append('-');
        }
        var code = sb.ToString().Trim('-');
        if (code.Length > 24) code = code[..24].Trim('-');
        return code.Length < 3 ? $"shop-{code}".Trim('-') : code;
    }

    private string UniqueCode(string name)
    {
        var baseCode = CodeFrom(name);
        if (ReservedCodes.Contains(baseCode)) baseCode += "-shop";
        var code = baseCode;
        for (var n = 2; All().Any(b => string.Equals(b.Code, code, StringComparison.OrdinalIgnoreCase)) || Taken(code); n++)
            code = $"{baseCode}-{n}";
        return code;
    }

    private bool Taken(string code)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM businesses WHERE code = $c);";
        cmd.Parameters.AddWithValue("$c", code);
        return (long)cmd.ExecuteScalar()! == 1;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void Exec(string sql, params (string Name, object Value)[] args)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }
}
