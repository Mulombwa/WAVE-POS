using System.Globalization;
using Microsoft.Data.Sqlite;
using WavePOS.Core.Audit;
using WavePOS.Core.Models;
using WavePOS.Core.Security;

namespace WavePOS.Data;

public enum LoginStatus
{
    Success,
    InvalidCredentials,
    LockedOut,
    Disabled,
}

public record LoginResult(LoginStatus Status, User? User = null, DateTime? LockedUntil = null);

/// <summary>Staff accounts. There's no limit on the number of users.</summary>
public class UserRepository(PosDatabase db)
{
    /// <summary>Wrong attempts allowed before the account is locked for <see cref="LockoutDuration"/>.</summary>
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    private const string SelectColumns =
        "SELECT id, username, display_name, role, is_active, employee_code, phone, email, last_login FROM users";

    /// <summary>True once at least one account exists. Before that the app shows first-time setup.</summary>
    public bool AnyUsers()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM users);";
        return (long)cmd.ExecuteScalar()! == 1;
    }

    public User Create(string username, string displayName, string password, UserRole role, Actor? actor = null) =>
        Create(new User { Username = username, DisplayName = displayName, Role = role }, password, actor);

    public User Create(User user, string password, Actor? actor = null)
    {
        var username = user.Username.Trim();
        var displayName = user.DisplayName.Trim();
        if (username.Length == 0) throw new ArgumentException("Username is required.");
        if (displayName.Length == 0) throw new ArgumentException("Name is required.");
        if (PasswordHasher.Validate(password) is { } error) throw new ArgumentException(error);

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO users (username, display_name, password_hash, role, employee_code, phone, email, created_at)
            VALUES ($u, $d, $h, $r, $code, $phone, $email, $now);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$d", displayName);
        cmd.Parameters.AddWithValue("$h", PasswordHasher.Hash(password));
        cmd.Parameters.AddWithValue("$r", user.Role.ToString());
        cmd.Parameters.AddWithValue("$code", user.EmployeeCode.Trim());
        cmd.Parameters.AddWithValue("$phone", user.Phone.Trim());
        cmd.Parameters.AddWithValue("$email", user.Email.Trim());
        cmd.Parameters.AddWithValue("$now", Iso(DateTime.Now));

        long id;
        try
        {
            id = (long)cmd.ExecuteScalar()!;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // UNIQUE constraint
        {
            throw new ArgumentException($"The username \"{username}\" is already taken.");
        }

        var by = actor ?? new Actor(id, displayName); // first-time setup: the new manager creates themself
        AuditRepository.Write(conn, tx, by, AuditActions.UserCreated, "user", id.ToString(CultureInfo.InvariantCulture),
            $"Created {user.Role} account \"{username}\" for {displayName}");
        tx.Commit();
        return GetById(id)!;
    }

    /// <summary>Updates name, role, active flag and employee details. Keeps at least one active manager.</summary>
    public void Update(User user, Actor actor)
    {
        if (string.IsNullOrWhiteSpace(user.DisplayName)) throw new ArgumentException("Name is required.");

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var before = GetById(conn, tx, user.Id) ?? throw new InvalidOperationException("User not found.");

        var stillManager = user.IsActive && user.Role == UserRole.Manager;
        if (before.IsActive && before.Role == UserRole.Manager && !stillManager && CountActiveManagers(conn, tx) <= 1)
            throw new InvalidOperationException("There must be at least one active manager. Make someone else a manager first.");

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE users SET display_name = $d, role = $r, is_active = $a, employee_code = $code, phone = $phone, email = $email
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", user.Id);
        cmd.Parameters.AddWithValue("$d", user.DisplayName.Trim());
        cmd.Parameters.AddWithValue("$r", user.Role.ToString());
        cmd.Parameters.AddWithValue("$a", user.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$code", user.EmployeeCode.Trim());
        cmd.Parameters.AddWithValue("$phone", user.Phone.Trim());
        cmd.Parameters.AddWithValue("$email", user.Email.Trim());
        cmd.ExecuteNonQuery();

        var changes = new List<string>();
        if (before.DisplayName != user.DisplayName.Trim()) changes.Add($"name → {user.DisplayName.Trim()}");
        if (before.Role != user.Role) changes.Add($"role {before.Role} → {user.Role}");
        if (before.IsActive != user.IsActive) changes.Add(user.IsActive ? "re-enabled" : "disabled");
        if (before.EmployeeCode != user.EmployeeCode.Trim() || before.Phone != user.Phone.Trim() || before.Email != user.Email.Trim())
            changes.Add("employee details");
        if (changes.Count > 0)
            AuditRepository.Write(conn, tx, actor, AuditActions.UserUpdated, "user", user.Id.ToString(CultureInfo.InvariantCulture),
                $"{before.Username}: {string.Join("; ", changes)}");
        tx.Commit();
    }

    /// <summary>Sets a new PIN/password (e.g. when someone forgets theirs) and clears any lockout.</summary>
    public void ResetPassword(long userId, string newPassword, Actor actor)
    {
        if (PasswordHasher.Validate(newPassword) is { } error) throw new ArgumentException(error);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var user = GetById(conn, tx, userId) ?? throw new InvalidOperationException("User not found.");
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE users SET password_hash = $h, failed_attempts = 0, locked_until = NULL WHERE id = $id;";
        cmd.Parameters.AddWithValue("$h", PasswordHasher.Hash(newPassword));
        cmd.Parameters.AddWithValue("$id", userId);
        cmd.ExecuteNonQuery();
        AuditRepository.Write(conn, tx, actor, AuditActions.PasswordReset, "user", userId.ToString(CultureInfo.InvariantCulture),
            $"Reset PIN/password for {user.Username}");
        tx.Commit();
    }

    public User? GetById(long id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{SelectColumns} WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public List<User> GetAll()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{SelectColumns} ORDER BY is_active DESC, display_name;";
        var list = new List<User>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    /// <summary>
    /// Checks a username and password. Failed attempts are counted and the account is
    /// locked for a few minutes after too many, to stop people guessing PINs.
    /// Every attempt is written to the audit trail.
    /// </summary>
    public LoginResult Authenticate(string username, string password, DateTime? now = null)
    {
        var time = now ?? DateTime.Now;
        var attempted = username.Trim();

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using var find = conn.CreateCommand();
        find.Transaction = tx;
        find.CommandText = $"""
            SELECT id, username, display_name, role, is_active, employee_code, phone, email, last_login,
                   password_hash, failed_attempts, locked_until
            FROM users WHERE username = $u;
            """;
        find.Parameters.AddWithValue("$u", attempted);

        using var r = find.ExecuteReader();
        if (!r.Read())
        {
            r.Close();
            // Hash anyway so a missing user takes as long as a wrong password.
            PasswordHasher.Verify(password, PasswordHasher.Hash("timing-equaliser"));
            Audit(conn, tx, new Actor(null, Truncate(attempted)), AuditActions.LoginFailed, null, "Unknown username");
            tx.Commit();
            return new LoginResult(LoginStatus.InvalidCredentials);
        }

        var user = Read(r);
        var hash = r.GetString(9);
        var failed = r.GetInt32(10);
        DateTime? lockedUntil = r.IsDBNull(11) ? null : DateTime.Parse(r.GetString(11), CultureInfo.InvariantCulture);
        r.Close();
        var actor = new Actor(user.Id, user.DisplayName);

        if (lockedUntil is { } until && until > time)
        {
            Audit(conn, tx, actor, AuditActions.LoginFailed, user.Id, "Account is locked");
            tx.Commit();
            return new LoginResult(LoginStatus.LockedOut, LockedUntil: until);
        }

        if (!PasswordHasher.Verify(password, hash))
        {
            failed++;
            DateTime? lockUntil = failed >= MaxFailedAttempts ? time + LockoutDuration : null;
            Execute(conn, tx, "UPDATE users SET failed_attempts = $f, locked_until = $l WHERE id = $id;",
                ("$f", lockUntil is null ? failed : 0), ("$l", lockUntil is null ? DBNull.Value : Iso(lockUntil.Value)), ("$id", user.Id));
            if (lockUntil is null)
                Audit(conn, tx, actor, AuditActions.LoginFailed, user.Id, $"Wrong PIN/password (attempt {failed})");
            else
                Audit(conn, tx, actor, AuditActions.AccountLocked, user.Id, $"Locked for {LockoutDuration.TotalMinutes:0} minutes after {MaxFailedAttempts} wrong attempts");
            tx.Commit();
            return lockUntil is null
                ? new LoginResult(LoginStatus.InvalidCredentials)
                : new LoginResult(LoginStatus.LockedOut, LockedUntil: lockUntil);
        }

        if (!user.IsActive)
        {
            Audit(conn, tx, actor, AuditActions.LoginFailed, user.Id, "Account is disabled");
            tx.Commit();
            return new LoginResult(LoginStatus.Disabled);
        }

        Execute(conn, tx, "UPDATE users SET failed_attempts = 0, locked_until = NULL, last_login = $now WHERE id = $id;",
            ("$now", Iso(time)), ("$id", user.Id));
        Audit(conn, tx, actor, AuditActions.LoginSucceeded, user.Id, "Signed in");
        tx.Commit();
        return new LoginResult(LoginStatus.Success, user);
    }

    private static void Audit(SqliteConnection conn, SqliteTransaction tx, Actor actor, string action, long? userId, string details) =>
        AuditRepository.Write(conn, tx, actor, action, "user", userId?.ToString(CultureInfo.InvariantCulture), details);

    private static int CountActiveManagers(SqliteConnection conn, SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM users WHERE is_active = 1 AND role = 'Manager';";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static User? GetById(SqliteConnection conn, SqliteTransaction tx, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"{SelectColumns} WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    private static void Execute(SqliteConnection conn, SqliteTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private static User Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Username = r.GetString(1),
        DisplayName = r.GetString(2),
        Role = Enum.TryParse<UserRole>(r.GetString(3), out var role) ? role : UserRole.Cashier,
        IsActive = r.GetInt64(4) == 1,
        EmployeeCode = r.GetString(5),
        Phone = r.GetString(6),
        Email = r.GetString(7),
        LastLogin = r.IsDBNull(8) ? null : DateTime.Parse(r.GetString(8), CultureInfo.InvariantCulture),
    };

    private static string Truncate(string s) => s.Length <= 64 ? s : s[..64];
    private static string Iso(DateTime t) => t.ToString("o", CultureInfo.InvariantCulture);
}
