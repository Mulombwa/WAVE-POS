using System.Globalization;
using Microsoft.Data.Sqlite;
using WavePOS.Core;
using WavePOS.Core.Accounting;
using WavePOS.Core.Audit;

namespace WavePOS.Data;

public record JournalSummary(long Id, DateOnly Date, string Reference, string Memo, JournalSource Source, string? SourceId,
    decimal Amount, string UserName, long? ReversesId, long? ReversedById)
{
    public string Number => $"J{Id:D6}";
}

public record JournalLineView(long AccountId, string AccountCode, string AccountName, decimal Debit, decimal Credit, string Description);

public record JournalEntryView(JournalSummary Summary, List<JournalLineView> Lines, DateTime CreatedAt);

public record JournalFilter(DateOnly? From = null, DateOnly? To = null, JournalSource? Source = null, string? Search = null);

public record LedgerLine(long EntryId, DateOnly Date, string Reference, string Memo, JournalSource Source,
    decimal Debit, decimal Credit, decimal RunningBalance, string Description);

public record AccountLedger(Account Account, DateOnly From, DateOnly To, decimal OpeningBalance, List<LedgerLine> Lines)
{
    public decimal ClosingBalance => Lines.Count == 0 ? OpeningBalance : Lines[^1].RunningBalance;
}

/// <summary>
/// The general ledger: chart of accounts, journal entries and the reports built from them.
/// Sales, refunds and stock changes post here automatically, inside the same transaction.
/// </summary>
public class LedgerRepository(PosDatabase db)
{
    private const string LockDateKey = "books.lock_date";

    // ── Chart of accounts ───────────────────────────────────────────────

    public List<Account> Accounts(bool includeInactive = true)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{AccountColumns} {(includeInactive ? "" : "WHERE is_active = 1")} ORDER BY code;";
        var list = new List<Account>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadAccount(r));
        return list;
    }

    public Account? GetAccount(long id)
    {
        using var conn = db.Open();
        return GetAccount(conn, null, id);
    }

    public long CreateAccount(Account account, Actor actor)
    {
        Validate(account);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO accounts (code, name, type, is_bank, is_active, description, created_at)
            VALUES ($code, $name, $type, $bank, 1, $desc, $now);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$code", account.Code.Trim());
        cmd.Parameters.AddWithValue("$name", account.Name.Trim());
        cmd.Parameters.AddWithValue("$type", account.Type.ToString());
        cmd.Parameters.AddWithValue("$bank", account.IsBank && account.Type == AccountType.Asset ? 1 : 0);
        cmd.Parameters.AddWithValue("$desc", account.Description.Trim());
        cmd.Parameters.AddWithValue("$now", Now());
        long id;
        try { id = (long)cmd.ExecuteScalar()!; }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new ArgumentException($"Account code {account.Code.Trim()} is already used."); }

        AuditRepository.Write(conn, tx, actor, AuditActions.AccountCreated, "account", Id(id),
            $"Created account {account.Code.Trim()} {account.Name.Trim()} ({account.Type})");
        tx.Commit();
        return id;
    }

    /// <summary>
    /// Saves code, name, description, bank flag and active flag. The type can only change while
    /// nothing has been posted. Accounts the system posts to can't be switched off.
    /// </summary>
    public void UpdateAccount(Account account, Actor actor)
    {
        Validate(account);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var before = GetAccount(conn, tx, account.Id) ?? throw new InvalidOperationException("Account not found.");
        var posted = HasPostings(conn, tx, account.Id);

        if (before.Type != account.Type && posted)
            throw new InvalidOperationException("This account has postings, so its type can't change. Create a new account instead.");
        if (before.IsSystem && before.Type != account.Type)
            throw new InvalidOperationException("The till posts to this account automatically, so its type can't change.");
        if (before.IsSystem && !account.IsActive)
            throw new InvalidOperationException("The till posts to this account automatically, so it can't be switched off.");
        if (!account.IsActive && before.IsActive && Balance(conn, tx, account.Id) != 0)
            throw new InvalidOperationException("Move the balance out of this account before switching it off.");

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE accounts SET code = $code, name = $name, type = $type, is_bank = $bank, is_active = $active, description = $desc
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", account.Id);
        cmd.Parameters.AddWithValue("$code", account.Code.Trim());
        cmd.Parameters.AddWithValue("$name", account.Name.Trim());
        cmd.Parameters.AddWithValue("$type", account.Type.ToString());
        cmd.Parameters.AddWithValue("$bank", account.IsBank && account.Type == AccountType.Asset ? 1 : 0);
        cmd.Parameters.AddWithValue("$active", account.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$desc", account.Description.Trim());
        try { cmd.ExecuteNonQuery(); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new ArgumentException($"Account code {account.Code.Trim()} is already used."); }

        var changes = new List<string>();
        if (before.Code != account.Code.Trim()) changes.Add($"code {before.Code} → {account.Code.Trim()}");
        if (before.Name != account.Name.Trim()) changes.Add($"name \"{before.Name}\" → \"{account.Name.Trim()}\"");
        if (before.Type != account.Type) changes.Add($"type {before.Type} → {account.Type}");
        if (before.IsBank != account.IsBank) changes.Add(account.IsBank ? "marked as bank account" : "no longer a bank account");
        if (before.IsActive != account.IsActive) changes.Add(account.IsActive ? "switched on" : "switched off");
        if (before.Description != account.Description.Trim()) changes.Add("description changed");
        if (changes.Count > 0)
            AuditRepository.Write(conn, tx, actor, AuditActions.AccountUpdated, "account", Id(account.Id),
                $"Account {account.Code.Trim()}: {string.Join(", ", changes)}");
        tx.Commit();
    }

    private static void Validate(Account a)
    {
        if (string.IsNullOrWhiteSpace(a.Code)) throw new ArgumentException("Enter an account code, e.g. 6100.");
        if (a.Code.Trim().Length > 10) throw new ArgumentException("Account codes can be up to 10 characters.");
        if (string.IsNullOrWhiteSpace(a.Name)) throw new ArgumentException("Enter an account name.");
    }

    // ── Posting ─────────────────────────────────────────────────────────

    /// <summary>Lock date: no manual journals or bank postings on or before it (e.g. after VAT is filed).</summary>
    public DateOnly? LockDate()
    {
        using var conn = db.Open();
        return LockDate(conn, null);
    }

    public void SetLockDate(DateOnly? date, Actor actor)
    {
        if (date is { } d && d >= DateOnly.FromDateTime(DateTime.Today))
            throw new ArgumentException("The lock date must be in the past. The till keeps posting today's sales.");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var before = LockDate(conn, tx);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        cmd.Parameters.AddWithValue("$k", LockDateKey);
        cmd.Parameters.AddWithValue("$v", (object?)date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        AuditRepository.Write(conn, tx, actor, AuditActions.LockDateChanged, "books", null,
            $"Lock date {(before is { } b ? b.ToString("dd MMM yyyy") : "none")} → {(date is { } a ? a.ToString("dd MMM yyyy") : "none")}");
        tx.Commit();
    }

    /// <summary>Posts a journal typed in by an accountant. Returns the entry id.</summary>
    public long PostJournal(JournalDraft draft, Actor actor)
    {
        if (draft.Source != JournalSource.Manual) throw new ArgumentException("Only manual journals can be posted here.");
        if (string.IsNullOrWhiteSpace(draft.Memo)) throw new ArgumentException("Say what the journal is for.");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var id = Post(conn, tx, draft, actor, enforceLock: true);
        AuditRepository.Write(conn, tx, actor, AuditActions.JournalPosted, "journal", Id(id),
            $"J{id:D6} {draft.Date:dd MMM yyyy} {Money.Format(draft.TotalDebit)}: {draft.Memo.Trim()}");
        tx.Commit();
        return id;
    }

    /// <summary>
    /// Cancels an entry with an equal and opposite one. Entries from sales, refunds and stock
    /// can't be reversed here; refund the sale or adjust the stock instead.
    /// </summary>
    public long Reverse(long entryId, DateOnly date, string reason, Actor actor)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Say why the entry is being reversed.");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var entry = GetEntry(conn, tx, entryId) ?? throw new InvalidOperationException("Journal not found.");
        if (entry.Summary.Source.IsAutomatic())
            throw new InvalidOperationException(entry.Summary.Source == JournalSource.Stock
                ? "This entry came from a stock change. Adjust the stock instead."
                : "This entry came from the till. Process a refund instead.");
        if (entry.Summary.ReversedById is { } already) throw new InvalidOperationException($"Already reversed by J{already:D6}.");
        if (entry.Summary.Source == JournalSource.Reversal) throw new InvalidOperationException("This is already a reversal. Post a new journal instead.");

        var draft = new JournalDraft
        {
            Date = date,
            Reference = $"Reversal of J{entryId:D6}",
            Memo = reason.Trim(),
            Source = JournalSource.Reversal,
            SourceId = Id(entryId),
            Lines = JournalValidator.Reverse(entry.Lines.Select(l => new JournalLineDraft(l.AccountId, l.Debit, l.Credit, l.Description))),
        };
        var id = Post(conn, tx, draft, actor, enforceLock: true, reversesId: entryId);

        // A reversed bank posting puts the statement line back in the to-do list.
        if (entry.Summary.Source == JournalSource.Bank)
            BankRepository.Unmatch(conn, tx, entryId);

        AuditRepository.Write(conn, tx, actor, AuditActions.JournalReversed, "journal", Id(entryId),
            $"J{entryId:D6} reversed by J{id:D6}: {reason.Trim()}");
        tx.Commit();
        return id;
    }

    /// <summary>Checks and saves an entry inside the caller's transaction. Returns its id.</summary>
    internal static long Post(SqliteConnection conn, SqliteTransaction tx, JournalDraft draft, Actor actor,
        bool enforceLock = false, long? reversesId = null)
    {
        if (JournalValidator.Check(draft, enforceLock ? LockDate(conn, tx) : null) is { } problem)
            throw new InvalidOperationException(problem);
        foreach (var accountId in draft.Lines.Select(l => l.AccountId).Distinct())
        {
            var account = GetAccount(conn, tx, accountId) ?? throw new InvalidOperationException("One of the accounts doesn't exist.");
            if (!account.IsActive) throw new InvalidOperationException($"Account {account.Display} is switched off.");
        }

        long id;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO journal_entries (date, reference, memo, source, source_id, reverses_id, user_id, user_name, created_at)
                VALUES ($date, $ref, $memo, $source, $sid, $rev, $uid, $uname, $now);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$date", draft.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$ref", draft.Reference.Trim());
            cmd.Parameters.AddWithValue("$memo", draft.Memo.Trim());
            cmd.Parameters.AddWithValue("$source", draft.Source.ToString());
            cmd.Parameters.AddWithValue("$sid", (object?)draft.SourceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rev", (object?)reversesId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$uid", (object?)actor.UserId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$uname", actor.Name);
            cmd.Parameters.AddWithValue("$now", Now());
            id = (long)cmd.ExecuteScalar()!;
        }

        foreach (var line in draft.Lines)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO journal_lines (entry_id, account_id, debit_ngwee, credit_ngwee, description)
                VALUES ($e, $a, $d, $c, $desc);
                """;
            cmd.Parameters.AddWithValue("$e", id);
            cmd.Parameters.AddWithValue("$a", line.AccountId);
            cmd.Parameters.AddWithValue("$d", Money.ToNgwee(line.Debit));
            cmd.Parameters.AddWithValue("$c", Money.ToNgwee(line.Credit));
            cmd.Parameters.AddWithValue("$desc", line.Description.Trim());
            cmd.ExecuteNonQuery();
        }
        return id;
    }

    /// <summary>Posts an automatic entry (sale, refund, stock) if it has anything in it.</summary>
    internal static void PostAutomatic(SqliteConnection conn, SqliteTransaction tx, JournalDraft? draft, Actor actor)
    {
        if (draft is null || draft.Lines.Count == 0) return;
        Post(conn, tx, draft, actor);
    }

    /// <summary>Looks up system accounts by key (see <see cref="SystemAccounts"/>) within a transaction.</summary>
    internal static Func<string, long> SystemAccountLookup(SqliteConnection conn, SqliteTransaction? tx)
    {
        var cache = new Dictionary<string, long>();
        return key =>
        {
            if (cache.TryGetValue(key, out var id)) return id;
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id FROM accounts WHERE system_key = $k;";
            cmd.Parameters.AddWithValue("$k", key);
            return cache[key] = cmd.ExecuteScalar() as long?
                ?? throw new InvalidOperationException($"The books are missing the '{key}' account.");
        };
    }

    /// <summary>
    /// First run: creates the default chart of accounts and brings in the stock already on hand
    /// as an opening balance, so the Inventory account matches the stock value from day one.
    /// </summary>
    internal static void StartBooks(SqliteConnection conn)
    {
        using var count = conn.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM accounts;";
        if ((long)count.ExecuteScalar()! > 0) return;

        using var tx = conn.BeginTransaction();
        foreach (var a in DefaultChart.Accounts)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO accounts (code, name, type, system_key, is_bank, is_active, description, created_at)
                VALUES ($code, $name, $type, $key, $bank, 1, $desc, $now);
                """;
            cmd.Parameters.AddWithValue("$code", a.Code);
            cmd.Parameters.AddWithValue("$name", a.Name);
            cmd.Parameters.AddWithValue("$type", a.Type.ToString());
            cmd.Parameters.AddWithValue("$key", (object?)a.Key ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bank", a.IsBank ? 1 : 0);
            cmd.Parameters.AddWithValue("$desc", a.Description);
            cmd.Parameters.AddWithValue("$now", Now());
            cmd.ExecuteNonQuery();
        }

        using var value = conn.CreateCommand();
        value.Transaction = tx;
        value.CommandText = "SELECT COALESCE(SUM(MAX(stock_quantity, 0) * cost_ngwee), 0) FROM products;";
        var stockValue = Money.FromNgwee((long)value.ExecuteScalar()!);
        if (stockValue > 0)
        {
            var account = SystemAccountLookup(conn, tx);
            Post(conn, tx, new JournalDraft
            {
                Date = DateOnly.FromDateTime(DateTime.Today),
                Reference = "Opening stock",
                Memo = "Stock on hand when the books were started, at cost",
                Source = JournalSource.Opening,
                Lines =
                [
                    new(account(SystemAccounts.Inventory), stockValue, 0, "Stock on hand"),
                    new(account(SystemAccounts.OpeningBalances), 0, stockValue, "Opening stock"),
                ],
            }, Actor.System);
        }
        tx.Commit();
    }

    // ── Reading journals ────────────────────────────────────────────────

    public (List<JournalSummary> Entries, int Total) Journals(JournalFilter filter, int page = 1, int pageSize = 50)
    {
        using var conn = db.Open();
        var (where, bind) = JournalWhere(filter);

        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) FROM journal_entries e {where};";
        bind(countCmd);
        var total = Convert.ToInt32(countCmd.ExecuteScalar());

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{SummarySelect} {where} ORDER BY e.date DESC, e.id DESC LIMIT $limit OFFSET $offset;";
        bind(cmd);
        cmd.Parameters.AddWithValue("$limit", pageSize);
        cmd.Parameters.AddWithValue("$offset", (Math.Max(page, 1) - 1) * pageSize);
        var list = new List<JournalSummary>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadSummary(r));
        return (list, total);
    }

    public JournalEntryView? GetEntry(long id)
    {
        using var conn = db.Open();
        return GetEntry(conn, null, id);
    }

    /// <summary>Every posting to one account between two dates, with a running balance.</summary>
    public AccountLedger Ledger(long accountId, DateOnly from, DateOnly to)
    {
        using var conn = db.Open();
        var account = GetAccount(conn, null, accountId) ?? throw new InvalidOperationException("Account not found.");
        var sign = account.Type.IsDebitNormal() ? 1 : -1;

        using var open = conn.CreateCommand();
        open.CommandText = """
            SELECT COALESCE(SUM(l.debit_ngwee - l.credit_ngwee), 0)
            FROM journal_lines l JOIN journal_entries e ON e.id = l.entry_id
            WHERE l.account_id = $a AND e.date < $from;
            """;
        open.Parameters.AddWithValue("$a", accountId);
        open.Parameters.AddWithValue("$from", Day(from));
        var running = sign * Money.FromNgwee((long)open.ExecuteScalar()!);
        var opening = running;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT e.id, e.date, e.reference, e.memo, e.source, l.debit_ngwee, l.credit_ngwee, l.description
            FROM journal_lines l JOIN journal_entries e ON e.id = l.entry_id
            WHERE l.account_id = $a AND e.date >= $from AND e.date <= $to
            ORDER BY e.date, e.id, l.id;
            """;
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$from", Day(from));
        cmd.Parameters.AddWithValue("$to", Day(to));
        var lines = new List<LedgerLine>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var debit = Money.FromNgwee(r.GetInt64(5));
            var credit = Money.FromNgwee(r.GetInt64(6));
            running += sign * (debit - credit);
            lines.Add(new LedgerLine(r.GetInt64(0), ParseDay(r.GetString(1)), r.GetString(2), r.GetString(3),
                ParseSource(r.GetString(4)), debit, credit, running, r.GetString(7)));
        }
        return new AccountLedger(account, from, to, opening, lines);
    }

    // ── Reports ─────────────────────────────────────────────────────────

    /// <summary>Debits and credits per account between two dates (from = null: since the books started).</summary>
    public List<AccountBalance> Balances(DateOnly? from, DateOnly to)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT a.id, a.code, a.name, a.type, a.system_key, a.is_bank, a.is_active, a.description,
                   COALESCE(SUM(t.debit_ngwee), 0), COALESCE(SUM(t.credit_ngwee), 0)
            FROM accounts a
            LEFT JOIN (SELECT l.account_id, l.debit_ngwee, l.credit_ngwee
                       FROM journal_lines l JOIN journal_entries e ON e.id = l.entry_id
                       WHERE e.date <= $to {(from is null ? "" : "AND e.date >= $from")}) t ON t.account_id = a.id
            GROUP BY a.id ORDER BY a.code;
            """;
        cmd.Parameters.AddWithValue("$to", Day(to));
        if (from is { } f) cmd.Parameters.AddWithValue("$from", Day(f));
        var list = new List<AccountBalance>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new AccountBalance(ReadAccount(r), Money.FromNgwee(r.GetInt64(8)), Money.FromNgwee(r.GetInt64(9))));
        return list;
    }

    public TrialBalance TrialBalance(DateOnly asAt) => FinancialReports.TrialBalance(asAt, Balances(null, asAt));

    public ProfitAndLoss ProfitAndLoss(DateOnly from, DateOnly to) => FinancialReports.ProfitAndLoss(from, to, Balances(from, to));

    public BalanceSheet BalanceSheet(DateOnly asAt) => FinancialReports.BalanceSheet(asAt, Balances(null, asAt));

    /// <summary>
    /// Figures for the ZRA VAT return: VAT from the ledger, and till sales (less refunds) split
    /// into standard-rated (16%) and zero-rated or exempt, excluding VAT.
    /// </summary>
    public VatSummary Vat(DateOnly from, DateOnly to)
    {
        var balances = Balances(from, to);
        decimal Of(string key) => balances.FirstOrDefault(b => b.Account.SystemKey == key)?.Balance ?? 0;

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT vat_rate, SUM(gross) FROM (
                SELECT l.vat_rate, l.unit_price_ngwee * l.quantity AS gross
                FROM sale_lines l JOIN sales s ON s.id = l.sale_id
                WHERE substr(s.timestamp, 1, 10) BETWEEN $from AND $to
                UNION ALL
                SELECT rl.vat_rate, -rl.unit_price_ngwee * rl.quantity
                FROM return_lines rl JOIN returns r ON r.id = rl.return_id
                WHERE substr(r.timestamp, 1, 10) BETWEEN $from AND $to
            ) GROUP BY vat_rate;
            """;
        cmd.Parameters.AddWithValue("$from", Day(from));
        cmd.Parameters.AddWithValue("$to", Day(to));
        decimal standard = 0, zero = 0;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var rate = (decimal)r.GetDouble(0);
            var gross = Money.FromNgwee(r.GetInt64(1));
            if (rate > 0) standard += Money.Round(gross / (1 + rate));
            else zero += gross;
        }
        return new VatSummary(from, to, Of(SystemAccounts.VatOutput), Of(SystemAccounts.VatInput), standard, zero);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private const string AccountColumns = "SELECT id, code, name, type, system_key, is_bank, is_active, description FROM accounts";

    private const string SummarySelect = """
        SELECT e.id, e.date, e.reference, e.memo, e.source, e.source_id,
               (SELECT COALESCE(SUM(debit_ngwee), 0) FROM journal_lines WHERE entry_id = e.id),
               e.user_name, e.reverses_id,
               (SELECT x.id FROM journal_entries x WHERE x.reverses_id = e.id)
        FROM journal_entries e
        """;

    private static (string Where, Action<SqliteCommand> Bind) JournalWhere(JournalFilter f)
    {
        var parts = new List<string>();
        if (f.From is not null) parts.Add("e.date >= $from");
        if (f.To is not null) parts.Add("e.date <= $to");
        if (f.Source is not null) parts.Add("e.source = $source");
        if (!string.IsNullOrWhiteSpace(f.Search)) parts.Add("(e.reference LIKE $q OR e.memo LIKE $q OR ('J' || printf('%06d', e.id)) LIKE $q)");
        var where = parts.Count == 0 ? "" : "WHERE " + string.Join(" AND ", parts);
        return (where, cmd =>
        {
            if (f.From is { } from) cmd.Parameters.AddWithValue("$from", Day(from));
            if (f.To is { } to) cmd.Parameters.AddWithValue("$to", Day(to));
            if (f.Source is { } s) cmd.Parameters.AddWithValue("$source", s.ToString());
            if (!string.IsNullOrWhiteSpace(f.Search)) cmd.Parameters.AddWithValue("$q", $"%{f.Search.Trim()}%");
        });
    }

    internal static JournalEntryView? GetEntry(SqliteConnection conn, SqliteTransaction? tx, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"{SummarySelect} WHERE e.id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        JournalSummary summary;
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) return null;
            summary = ReadSummary(r);
        }

        using var created = conn.CreateCommand();
        created.Transaction = tx;
        created.CommandText = "SELECT created_at FROM journal_entries WHERE id = $id;";
        created.Parameters.AddWithValue("$id", id);
        var createdAt = DateTime.Parse((string)created.ExecuteScalar()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        using var lines = conn.CreateCommand();
        lines.Transaction = tx;
        lines.CommandText = """
            SELECT l.account_id, a.code, a.name, l.debit_ngwee, l.credit_ngwee, l.description
            FROM journal_lines l JOIN accounts a ON a.id = l.account_id
            WHERE l.entry_id = $id ORDER BY l.id;
            """;
        lines.Parameters.AddWithValue("$id", id);
        var list = new List<JournalLineView>();
        using (var lr = lines.ExecuteReader())
            while (lr.Read())
                list.Add(new JournalLineView(lr.GetInt64(0), lr.GetString(1), lr.GetString(2),
                    Money.FromNgwee(lr.GetInt64(3)), Money.FromNgwee(lr.GetInt64(4)), lr.GetString(5)));
        return new JournalEntryView(summary, list, createdAt);
    }

    internal static Account? GetAccount(SqliteConnection conn, SqliteTransaction? tx, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"{AccountColumns} WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadAccount(r) : null;
    }

    /// <summary>Debits minus credits on an account, all time.</summary>
    internal static decimal Balance(SqliteConnection conn, SqliteTransaction? tx, long accountId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(SUM(debit_ngwee - credit_ngwee), 0) FROM journal_lines WHERE account_id = $a;";
        cmd.Parameters.AddWithValue("$a", accountId);
        return Money.FromNgwee((long)cmd.ExecuteScalar()!);
    }

    private static bool HasPostings(SqliteConnection conn, SqliteTransaction tx, long accountId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM journal_lines WHERE account_id = $a);";
        cmd.Parameters.AddWithValue("$a", accountId);
        return (long)cmd.ExecuteScalar()! == 1;
    }

    private static DateOnly? LockDate(SqliteConnection conn, SqliteTransaction? tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", LockDateKey);
        return cmd.ExecuteScalar() is string s && s.Length > 0 ? ParseDay(s) : null;
    }

    private static Account ReadAccount(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Code = r.GetString(1),
        Name = r.GetString(2),
        Type = Enum.Parse<AccountType>(r.GetString(3)),
        SystemKey = r.IsDBNull(4) ? null : r.GetString(4),
        IsBank = r.GetInt64(5) == 1,
        IsActive = r.GetInt64(6) == 1,
        Description = r.GetString(7),
    };

    private static JournalSummary ReadSummary(SqliteDataReader r) => new(
        r.GetInt64(0), ParseDay(r.GetString(1)), r.GetString(2), r.GetString(3), ParseSource(r.GetString(4)),
        r.IsDBNull(5) ? null : r.GetString(5), Money.FromNgwee(r.GetInt64(6)), r.GetString(7),
        r.IsDBNull(8) ? null : r.GetInt64(8), r.IsDBNull(9) ? null : r.GetInt64(9));

    private static JournalSource ParseSource(string s) => Enum.TryParse<JournalSource>(s, out var v) ? v : JournalSource.Manual;

    internal static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static DateOnly ParseDay(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Now() => DateTime.Now.ToString("o", CultureInfo.InvariantCulture);

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
}
