using System.Globalization;
using Microsoft.Data.Sqlite;
using WavePOS.Core;
using WavePOS.Core.Accounting;
using WavePOS.Core.Audit;

namespace WavePOS.Data;

public enum BankLineStatus
{
    /// <summary>Still to do: not yet explained in the books.</summary>
    Unmatched,
    /// <summary>Explained by a journal entry (matched to one, or given an account).</summary>
    Matched,
    /// <summary>Left out on purpose, e.g. a line the bank later reversed.</summary>
    Excluded,
}

public record BankLineView(long Id, long AccountId, DateOnly Date, string Description, string Reference, decimal Amount,
    decimal? Balance, BankLineStatus Status, long? JournalEntryId, string Note, LedgerCandidate? Suggestion);

public record BankImportResult(int Imported, int Duplicates, int Skipped, DateOnly? From, DateOnly? To);

public record ReconciliationSummary(Account Account, decimal LedgerBalance, decimal? StatementBalance, DateOnly? StatementDate,
    int UnmatchedCount, decimal UnmatchedTotal, int UnreconciledLedgerCount, decimal UnreconciledLedgerTotal);

/// <summary>
/// Bank and mobile-money statements: import CSV exports, then reconcile each line against the books.
/// Live bank feeds can plug in later by feeding the same <see cref="StatementLine"/> records into <see cref="ImportLines"/>.
/// </summary>
public class BankRepository(PosDatabase db)
{
    /// <summary>Active accounts marked as bank accounts or wallets.</summary>
    public List<Account> BankAccounts() => new LedgerRepository(db).Accounts(includeInactive: false).Where(a => a.IsBank).ToList();

    /// <summary>Reads a CSV statement and adds its lines. Lines already imported are skipped, so re-importing is safe.</summary>
    public BankImportResult Import(long accountId, string fileName, string csvText, Actor actor)
    {
        var parsed = BankStatementParser.Parse(csvText);
        if (!parsed.Ok) throw new ArgumentException(parsed.Error);
        var result = ImportLines(accountId, fileName, parsed.Lines, actor);
        return result with { Skipped = parsed.SkippedRows };
    }

    public BankImportResult ImportLines(long accountId, string source, IReadOnlyList<StatementLine> lines, Actor actor)
    {
        if (lines.Count == 0) throw new ArgumentException("There are no statement lines to import.");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var account = BankAccount(conn, tx, accountId);

        long importId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO bank_imports (account_id, file_name, imported_at, user_name, line_count, duplicate_count)
                VALUES ($a, $f, $now, $u, 0, 0);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$f", Path.GetFileName(source.Trim()).Length == 0 ? "Pasted statement" : Path.GetFileName(source.Trim()));
            cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$u", actor.Name);
            importId = (long)cmd.ExecuteScalar()!;
        }

        var fingerprints = BankStatementParser.Fingerprints(lines);
        var imported = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO bank_lines (account_id, import_id, date, description, reference, amount_ngwee, balance_ngwee, fingerprint)
                VALUES ($a, $imp, $date, $desc, $ref, $amt, $bal, $fp);
                """;
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$imp", importId);
            cmd.Parameters.AddWithValue("$date", LedgerRepository.Day(l.Date));
            cmd.Parameters.AddWithValue("$desc", l.Description.Trim());
            cmd.Parameters.AddWithValue("$ref", l.Reference.Trim());
            cmd.Parameters.AddWithValue("$amt", Money.ToNgwee(l.Amount));
            cmd.Parameters.AddWithValue("$bal", l.Balance is { } b ? Money.ToNgwee(b) : DBNull.Value);
            cmd.Parameters.AddWithValue("$fp", fingerprints[i]);
            imported += cmd.ExecuteNonQuery();
        }
        var duplicates = lines.Count - imported;

        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = "UPDATE bank_imports SET line_count = $n, duplicate_count = $d WHERE id = $id;";
            upd.Parameters.AddWithValue("$n", imported);
            upd.Parameters.AddWithValue("$d", duplicates);
            upd.Parameters.AddWithValue("$id", importId);
            upd.ExecuteNonQuery();
        }

        var from = lines.Min(l => l.Date);
        var to = lines.Max(l => l.Date);
        AuditRepository.Write(conn, tx, actor, AuditActions.BankImported, "account", accountId.ToString(CultureInfo.InvariantCulture),
            $"{account.Display}: imported {imported} statement line(s) for {from:dd MMM yyyy}–{to:dd MMM yyyy}" +
            (duplicates > 0 ? $", {duplicates} already imported" : ""));
        tx.Commit();
        return new BankImportResult(imported, duplicates, 0, from, to);
    }

    /// <summary>Statement lines, newest first, with a suggested match for each line still to do.</summary>
    public List<BankLineView> Lines(long accountId, BankLineStatus? status = null, int limit = 500)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, account_id, date, description, reference, amount_ngwee, balance_ngwee, status, journal_entry_id, note
            FROM bank_lines WHERE account_id = $a {(status is null ? "" : "AND status = $s")}
            ORDER BY date DESC, id DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$a", accountId);
        if (status is { } s) cmd.Parameters.AddWithValue("$s", s.ToString());
        cmd.Parameters.AddWithValue("$limit", limit);
        var raw = new List<BankLineView>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                raw.Add(new BankLineView(r.GetInt64(0), r.GetInt64(1), LedgerRepository.ParseDay(r.GetString(2)), r.GetString(3),
                    r.GetString(4), Money.FromNgwee(r.GetInt64(5)), r.IsDBNull(6) ? null : Money.FromNgwee(r.GetInt64(6)),
                    Enum.Parse<BankLineStatus>(r.GetString(7)), r.IsDBNull(8) ? null : r.GetInt64(8), r.GetString(9), null));

        // Suggest each ledger entry for at most one line, oldest lines first.
        var candidates = Candidates(conn, null, accountId);
        var suggestions = new Dictionary<long, LedgerCandidate>();
        foreach (var line in raw.Where(l => l.Status == BankLineStatus.Unmatched).OrderBy(l => l.Date).ThenBy(l => l.Id))
        {
            if (BankMatcher.Suggest(line.Date, line.Amount, candidates) is not { } match) continue;
            suggestions[line.Id] = match;
            candidates.Remove(match);
        }
        return raw.Select(l => suggestions.TryGetValue(l.Id, out var m) ? l with { Suggestion = m } : l).ToList();
    }

    /// <summary>Entries on the account that no statement line explains yet (e.g. cash banked but not yet cleared).</summary>
    public List<LedgerCandidate> UnreconciledEntries(long accountId)
    {
        using var conn = db.Open();
        return Candidates(conn, null, accountId);
    }

    /// <summary>Links a statement line to a journal entry already in the books with the same amount.</summary>
    public void Match(long lineId, long entryId, Actor actor)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var line = Line(conn, tx, lineId, BankLineStatus.Unmatched);
        var candidate = Candidates(conn, tx, line.AccountId).FirstOrDefault(c => c.EntryId == entryId)
            ?? throw new InvalidOperationException("That journal isn't on this account, or it's already matched to another statement line.");
        if (candidate.Amount != line.Amount)
            throw new InvalidOperationException($"The amounts differ: statement {Money.Format(line.Amount)}, books {Money.Format(candidate.Amount)}.");

        SetMatched(conn, tx, lineId, entryId, "");
        AuditRepository.Write(conn, tx, actor, AuditActions.BankMatched, "bank_line", Id(lineId),
            $"Statement line {line.Date:dd MMM yyyy} {Money.Format(line.Amount)} \"{line.Description}\" matched to J{entryId:D6}");
        tx.Commit();
    }

    /// <summary>
    /// Records a statement line the books don't have yet (bank charges, rent, a supplier payment)
    /// by posting it to the chosen account. With VAT included, 16/116 goes to VAT.
    /// </summary>
    public long Categorise(long lineId, long accountId, bool includesVat, string memo, Actor actor)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var line = Line(conn, tx, lineId, BankLineStatus.Unmatched);
        if (accountId == line.AccountId) throw new ArgumentException("Choose a different account from the bank account itself.");

        var description = string.IsNullOrWhiteSpace(memo) ? line.Description : memo.Trim();
        var draft = PostingRules.BankLine(lineId, line.Date, description, line.Amount, line.AccountId, accountId, includesVat,
            LedgerRepository.SystemAccountLookup(conn, tx));
        if (line.Reference.Length > 0) draft.Reference = line.Reference;
        var entryId = LedgerRepository.Post(conn, tx, draft, actor, enforceLock: true);

        SetMatched(conn, tx, lineId, entryId, "");
        var account = LedgerRepository.GetAccount(conn, tx, accountId)!;
        AuditRepository.Write(conn, tx, actor, AuditActions.BankCategorised, "bank_line", Id(lineId),
            $"Statement line {line.Date:dd MMM yyyy} {Money.Format(line.Amount)} \"{line.Description}\" posted to {account.Display} as J{entryId:D6}" +
            (includesVat ? " (incl. VAT)" : ""));
        tx.Commit();
        return entryId;
    }

    /// <summary>Leaves a line out of the reconciliation, with a reason.</summary>
    public void Exclude(long lineId, string reason, Actor actor)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Say why this line is being left out.");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var line = Line(conn, tx, lineId, BankLineStatus.Unmatched);
        SetStatus(conn, tx, lineId, BankLineStatus.Excluded, reason.Trim());
        AuditRepository.Write(conn, tx, actor, AuditActions.BankExcluded, "bank_line", Id(lineId),
            $"Statement line {line.Date:dd MMM yyyy} {Money.Format(line.Amount)} \"{line.Description}\" excluded: {reason.Trim()}");
        tx.Commit();
    }

    /// <summary>
    /// Puts a line back on the to-do list: undoes an exclusion or a match. A line that was given an
    /// account has its own journal, so reverse that journal instead.
    /// </summary>
    public void Reopen(long lineId, Actor actor)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var line = Line(conn, tx, lineId, null);
        if (line.Status == BankLineStatus.Unmatched) return;
        if (line.JournalEntryId is { } entryId && LedgerRepository.GetEntry(conn, tx, entryId)?.Summary.Source == JournalSource.Bank)
            throw new InvalidOperationException($"This line was posted as J{entryId:D6}. Reverse that journal to undo it.");

        SetStatus(conn, tx, lineId, BankLineStatus.Unmatched, "");
        AuditRepository.Write(conn, tx, actor, AuditActions.BankMatched, "bank_line", Id(lineId),
            $"Statement line {line.Date:dd MMM yyyy} {Money.Format(line.Amount)} \"{line.Description}\" reopened (was {line.Status}" +
            (line.JournalEntryId is { } e ? $", J{e:D6}" : "") + ")");
        tx.Commit();
    }

    public ReconciliationSummary Summary(long accountId)
    {
        using var conn = db.Open();
        var account = LedgerRepository.GetAccount(conn, null, accountId) ?? throw new InvalidOperationException("Account not found.");
        var ledger = LedgerRepository.Balance(conn, null, accountId);

        decimal? statementBalance = null;
        DateOnly? statementDate = null;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT balance_ngwee, date FROM bank_lines
                WHERE account_id = $a AND balance_ngwee IS NOT NULL ORDER BY date DESC, id DESC LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$a", accountId);
            using var r = cmd.ExecuteReader();
            if (r.Read()) { statementBalance = Money.FromNgwee(r.GetInt64(0)); statementDate = LedgerRepository.ParseDay(r.GetString(1)); }
        }

        int unmatched;
        decimal unmatchedTotal;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(amount_ngwee), 0) FROM bank_lines WHERE account_id = $a AND status = 'Unmatched';";
            cmd.Parameters.AddWithValue("$a", accountId);
            using var r = cmd.ExecuteReader();
            r.Read();
            unmatched = r.GetInt32(0);
            unmatchedTotal = Money.FromNgwee(r.GetInt64(1));
        }

        var open = Candidates(conn, null, accountId);
        return new ReconciliationSummary(account, ledger, statementBalance, statementDate, unmatched, unmatchedTotal,
            open.Count, open.Sum(c => c.Amount));
    }

    /// <summary>Statement lines still to do, across all bank accounts (for the overview).</summary>
    public int UnmatchedCount()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM bank_lines WHERE status = 'Unmatched';";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Called when a bank journal is reversed: its statement line goes back on the to-do list.</summary>
    internal static void Unmatch(SqliteConnection conn, SqliteTransaction tx, long entryId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE bank_lines SET status = 'Unmatched', journal_entry_id = NULL, note = '' WHERE journal_entry_id = $e;";
        cmd.Parameters.AddWithValue("$e", entryId);
        cmd.ExecuteNonQuery();
    }

    // Ledger entries on the account that aren't reversed and aren't yet explained by a statement line.
    private static List<LedgerCandidate> Candidates(SqliteConnection conn, SqliteTransaction? tx, long accountId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT e.id, e.date, e.reference, e.memo, SUM(l.debit_ngwee - l.credit_ngwee)
            FROM journal_lines l JOIN journal_entries e ON e.id = l.entry_id
            WHERE l.account_id = $a
              AND e.reverses_id IS NULL
              AND NOT EXISTS (SELECT 1 FROM journal_entries x WHERE x.reverses_id = e.id)
              AND NOT EXISTS (SELECT 1 FROM bank_lines b WHERE b.account_id = $a AND b.journal_entry_id = e.id)
            GROUP BY e.id
            HAVING SUM(l.debit_ngwee - l.credit_ngwee) <> 0
            ORDER BY e.date, e.id;
            """;
        cmd.Parameters.AddWithValue("$a", accountId);
        var list = new List<LedgerCandidate>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new LedgerCandidate(r.GetInt64(0), LedgerRepository.ParseDay(r.GetString(1)),
                Money.FromNgwee(r.GetInt64(4)), r.GetString(2), r.GetString(3)));
        return list;
    }

    private static Account BankAccount(SqliteConnection conn, SqliteTransaction tx, long accountId)
    {
        var account = LedgerRepository.GetAccount(conn, tx, accountId) ?? throw new InvalidOperationException("Account not found.");
        if (!account.IsBank || !account.IsActive) throw new InvalidOperationException($"{account.Display} isn't an active bank account.");
        return account;
    }

    private static BankLineView Line(SqliteConnection conn, SqliteTransaction tx, long lineId, BankLineStatus? mustBe)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT id, account_id, date, description, reference, amount_ngwee, balance_ngwee, status, journal_entry_id, note
            FROM bank_lines WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", lineId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException("Statement line not found.");
        var line = new BankLineView(r.GetInt64(0), r.GetInt64(1), LedgerRepository.ParseDay(r.GetString(2)), r.GetString(3), r.GetString(4),
            Money.FromNgwee(r.GetInt64(5)), r.IsDBNull(6) ? null : Money.FromNgwee(r.GetInt64(6)),
            Enum.Parse<BankLineStatus>(r.GetString(7)), r.IsDBNull(8) ? null : r.GetInt64(8), r.GetString(9), null);
        if (mustBe is { } s && line.Status != s)
            throw new InvalidOperationException(line.Status == BankLineStatus.Matched ? "This line is already reconciled." : "This line was excluded.");
        return line;
    }

    private static void SetMatched(SqliteConnection conn, SqliteTransaction tx, long lineId, long entryId, string note)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE bank_lines SET status = 'Matched', journal_entry_id = $e, note = $n WHERE id = $id;";
        cmd.Parameters.AddWithValue("$e", entryId);
        cmd.Parameters.AddWithValue("$n", note);
        cmd.Parameters.AddWithValue("$id", lineId);
        cmd.ExecuteNonQuery();
    }

    private static void SetStatus(SqliteConnection conn, SqliteTransaction tx, long lineId, BankLineStatus status, string note)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE bank_lines SET status = $s, journal_entry_id = NULL, note = $n WHERE id = $id;";
        cmd.Parameters.AddWithValue("$s", status.ToString());
        cmd.Parameters.AddWithValue("$n", note);
        cmd.Parameters.AddWithValue("$id", lineId);
        cmd.ExecuteNonQuery();
    }

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
}
