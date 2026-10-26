using System.Globalization;
using Microsoft.Data.Sqlite;
using WavePOS.Core.Audit;

namespace WavePOS.Data;

public record AuditFilter(string? Action = null, string? EntityType = null, string? EntityId = null,
    long? UserId = null, DateTime? FromUtc = null, DateTime? ToUtc = null, string? Search = null);

public record AuditVerification(bool IsIntact, int EntriesChecked, long? FirstBrokenId, string Message);

/// <summary>
/// The audit trail: who did what and when. Entries can only be added (database triggers block
/// edits and deletes) and each is hash-chained to the previous one, so tampering is detectable.
/// </summary>
public class AuditRepository(PosDatabase db)
{
    public void Write(Actor actor, string action, string entityType, string? entityId, string details)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction(); // immediate: one writer at a time keeps the chain linear
        Write(conn, tx, actor, action, entityType, entityId, details);
        tx.Commit();
    }

    /// <summary>Adds an entry inside another operation's transaction, so both are saved or neither.</summary>
    internal static void Write(SqliteConnection conn, SqliteTransaction tx, Actor actor,
        string action, string entityType, string? entityId, string details)
    {
        long lastId = 0;
        var previousHash = AuditEntry.GenesisHash;
        using (var last = conn.CreateCommand())
        {
            last.Transaction = tx;
            last.CommandText = "SELECT id, hash FROM audit_log ORDER BY id DESC LIMIT 1;";
            using var r = last.ExecuteReader();
            if (r.Read())
            {
                lastId = r.GetInt64(0);
                previousHash = r.GetString(1);
            }
        }

        var entry = new AuditEntry
        {
            Id = lastId + 1,
            TimestampUtc = DateTime.UtcNow,
            UserId = actor.UserId,
            UserName = actor.Name,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Details = details,
            PreviousHash = previousHash,
        };
        entry.Hash = entry.ComputeHash();

        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO audit_log (id, timestamp_utc, user_id, user_name, action, entity_type, entity_id, details, previous_hash, hash)
            VALUES ($id, $ts, $uid, $uname, $action, $etype, $eid, $details, $prev, $hash);
            """;
        insert.Parameters.AddWithValue("$id", entry.Id);
        insert.Parameters.AddWithValue("$ts", entry.TimestampUtc.ToString("o", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$uid", (object?)entry.UserId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$uname", entry.UserName);
        insert.Parameters.AddWithValue("$action", entry.Action);
        insert.Parameters.AddWithValue("$etype", entry.EntityType);
        insert.Parameters.AddWithValue("$eid", (object?)entry.EntityId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$details", entry.Details);
        insert.Parameters.AddWithValue("$prev", entry.PreviousHash);
        insert.Parameters.AddWithValue("$hash", entry.Hash);
        insert.ExecuteNonQuery();
    }

    /// <summary>Newest first, one page at a time.</summary>
    public (List<AuditEntry> Entries, int Total) Query(AuditFilter filter, int page = 1, int pageSize = 50)
    {
        using var conn = db.Open();
        var where = new List<string>();
        void Bind(SqliteCommand c)
        {
            // "auth." matches a whole category; "auth.login" matches exactly.
            if (filter.Action is { Length: > 0 })
                c.Parameters.AddWithValue("$action", filter.Action.EndsWith('.') ? filter.Action + "%" : filter.Action);
            if (filter.EntityType is { Length: > 0 }) c.Parameters.AddWithValue("$etype", filter.EntityType);
            if (filter.EntityId is { Length: > 0 }) c.Parameters.AddWithValue("$eid", filter.EntityId);
            if (filter.UserId is { } uid) c.Parameters.AddWithValue("$uid", uid);
            if (filter.FromUtc is { } from) c.Parameters.AddWithValue("$from", from.ToString("o", CultureInfo.InvariantCulture));
            if (filter.ToUtc is { } to) c.Parameters.AddWithValue("$to", to.ToString("o", CultureInfo.InvariantCulture));
            if (filter.Search is { Length: > 0 }) c.Parameters.AddWithValue("$q", $"%{filter.Search.Trim()}%");
        }
        if (filter.Action is { Length: > 0 }) where.Add("action LIKE $action");
        if (filter.EntityType is { Length: > 0 }) where.Add("entity_type = $etype");
        if (filter.EntityId is { Length: > 0 }) where.Add("entity_id = $eid");
        if (filter.UserId is not null) where.Add("user_id = $uid");
        if (filter.FromUtc is not null) where.Add("timestamp_utc >= $from");
        if (filter.ToUtc is not null) where.Add("timestamp_utc < $to");
        if (filter.Search is { Length: > 0 }) where.Add("(details LIKE $q OR user_name LIKE $q OR entity_id LIKE $q)");
        var whereSql = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);

        int total;
        using (var count = conn.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(*) FROM audit_log {whereSql};";
            Bind(count);
            total = Convert.ToInt32(count.ExecuteScalar());
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, timestamp_utc, user_id, user_name, action, entity_type, entity_id, details, previous_hash, hash
            FROM audit_log {whereSql} ORDER BY id DESC LIMIT $limit OFFSET $offset;
            """;
        Bind(cmd);
        cmd.Parameters.AddWithValue("$limit", pageSize);
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, page - 1) * pageSize);
        var list = new List<AuditEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Read(r));
        return (list, total);
    }

    /// <summary>Re-checks every entry's hash and link to the previous entry.</summary>
    public AuditVerification Verify()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, timestamp_utc, user_id, user_name, action, entity_type, entity_id, details, previous_hash, hash
            FROM audit_log ORDER BY id;
            """;
        using var r = cmd.ExecuteReader();
        var expectedPrevious = AuditEntry.GenesisHash;
        long expectedId = 1;
        var count = 0;
        while (r.Read())
        {
            var e = Read(r);
            count++;
            if (e.Id != expectedId)
                return new(false, count, e.Id, $"Entry {expectedId} is missing.");
            if (e.PreviousHash != expectedPrevious)
                return new(false, count, e.Id, $"Entry {e.Id} doesn't link to the entry before it.");
            if (e.ComputeHash() != e.Hash)
                return new(false, count, e.Id, $"Entry {e.Id} has been altered.");
            expectedPrevious = e.Hash;
            expectedId++;
        }
        return new(true, count, null, count == 0 ? "No entries yet." : $"All {count} entries are intact.");
    }

    private static AuditEntry Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        TimestampUtc = DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        UserId = r.IsDBNull(2) ? null : r.GetInt64(2),
        UserName = r.GetString(3),
        Action = r.GetString(4),
        EntityType = r.GetString(5),
        EntityId = r.IsDBNull(6) ? null : r.GetString(6),
        Details = r.GetString(7),
        PreviousHash = r.GetString(8),
        Hash = r.GetString(9),
    };
}
