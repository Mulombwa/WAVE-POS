using System.Globalization;
using Microsoft.Data.Sqlite;
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

public class UserRepository(PosDatabase db)
{
    /// <summary>Wrong attempts allowed before the account is locked for <see cref="LockoutDuration"/>.</summary>
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    private const string SelectColumns = "SELECT id, username, display_name, role, is_active FROM users";

    /// <summary>True once at least one account exists. Before that the app shows first-time setup.</summary>
    public bool AnyUsers()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM users);";
        return (long)cmd.ExecuteScalar()! == 1;
    }

    public User Create(string username, string displayName, string password, UserRole role)
    {
        username = username.Trim();
        displayName = displayName.Trim();
        if (username.Length == 0) throw new ArgumentException("Username is required.");
        if (displayName.Length == 0) throw new ArgumentException("Name is required.");
        if (PasswordHasher.Validate(password) is { } error) throw new ArgumentException(error);

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (username, display_name, password_hash, role, created_at)
            VALUES ($u, $d, $h, $r, $now);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$d", displayName);
        cmd.Parameters.AddWithValue("$h", PasswordHasher.Hash(password));
        cmd.Parameters.AddWithValue("$r", role.ToString());
        cmd.Parameters.AddWithValue("$now", Iso(DateTime.Now));

        try
        {
            var id = (long)cmd.ExecuteScalar()!;
            return new User { Id = id, Username = username, DisplayName = displayName, Role = role };
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // UNIQUE constraint
        {
            throw new ArgumentException($"The username \"{username}\" is already taken.");
        }
    }

    public List<User> GetAll()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{SelectColumns} ORDER BY display_name;";
        var list = new List<User>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    /// <summary>
    /// Checks a username and password. Failed attempts are counted and the account is
    /// locked for a few minutes after too many, to stop people guessing PINs.
    /// </summary>
    public LoginResult Authenticate(string username, string password, DateTime? now = null)
    {
        var time = now ?? DateTime.Now;

        using var conn = db.Open();
        using var find = conn.CreateCommand();
        find.CommandText = """
            SELECT id, username, display_name, role, is_active, password_hash, failed_attempts, locked_until
            FROM users WHERE username = $u;
            """;
        find.Parameters.AddWithValue("$u", username.Trim());

        using var r = find.ExecuteReader();
        if (!r.Read())
        {
            // Hash anyway so a missing user takes as long as a wrong password.
            PasswordHasher.Verify(password, PasswordHasher.Hash("timing-equaliser"));
            return new LoginResult(LoginStatus.InvalidCredentials);
        }

        var user = Read(r);
        var hash = r.GetString(5);
        var failed = r.GetInt32(6);
        DateTime? lockedUntil = r.IsDBNull(7) ? null : DateTime.Parse(r.GetString(7), CultureInfo.InvariantCulture);
        r.Close();

        if (lockedUntil is { } until && until > time) return new LoginResult(LoginStatus.LockedOut, LockedUntil: until);

        if (!PasswordHasher.Verify(password, hash))
        {
            failed++;
            DateTime? lockUntil = failed >= MaxFailedAttempts ? time + LockoutDuration : null;
            Execute(conn, "UPDATE users SET failed_attempts = $f, locked_until = $l WHERE id = $id;",
                ("$f", lockUntil is null ? failed : 0), ("$l", lockUntil is null ? DBNull.Value : Iso(lockUntil.Value)), ("$id", user.Id));
            return lockUntil is null
                ? new LoginResult(LoginStatus.InvalidCredentials)
                : new LoginResult(LoginStatus.LockedOut, LockedUntil: lockUntil);
        }

        if (!user.IsActive) return new LoginResult(LoginStatus.Disabled);

        Execute(conn, "UPDATE users SET failed_attempts = 0, locked_until = NULL, last_login = $now WHERE id = $id;",
            ("$now", Iso(time)), ("$id", user.Id));
        return new LoginResult(LoginStatus.Success, user);
    }

    private static void Execute(SqliteConnection conn, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = conn.CreateCommand();
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
    };

    private static string Iso(DateTime t) => t.ToString("o", CultureInfo.InvariantCulture);
}
