using System.Globalization;
using Microsoft.Data.Sqlite;
using WavePOS.Core;
using WavePOS.Core.Audit;
using WavePOS.Core.HR;

namespace WavePOS.Data;

public record HrSummary(int Headcount, int OnLeaveToday, int PendingLeave, int ContractsEndingSoon, int WithoutContract);

public record MonthAttendance(decimal OvertimeHours, int AbsentDays);

/// <summary>
/// Employees, contracts, leave and attendance. Personal and pay details are only shown to HR and
/// payroll staff, and every opening of an employee's record is written to the audit trail.
/// </summary>
public class HrRepository(PosDatabase db)
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    // ── Employees ───────────────────────────────────────────────────────

    public List<Employee> Employees(bool includeLeft = false, string? search = null)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        var where = new List<string> { "1 = 1" };
        if (!includeLeft) where.Add("status = 'Active'");
        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Add("(first_name || ' ' || last_name LIKE $q OR number LIKE $q OR job_title LIKE $q OR department LIKE $q)");
            cmd.Parameters.AddWithValue("$q", $"%{search.Trim()}%");
        }
        cmd.CommandText = $"{EmployeeSelect} WHERE {string.Join(" AND ", where)} ORDER BY last_name, first_name;";
        return ReadEmployees(cmd);
    }

    public Employee? GetEmployee(long id)
    {
        using var conn = db.Open();
        return GetEmployee(conn, null, id);
    }

    /// <summary>Records that someone opened an employee's full record (personal data).</summary>
    public void LogViewed(long id, Actor actor) =>
        new AuditRepository(db).Write(actor, AuditActions.EmployeeViewed, "employee", Str(id), $"Viewed E{id:D4}");

    /// <summary>Adds (Id = 0) or updates an employee. Returns the id.</summary>
    public long SaveEmployee(Employee e, Actor actor)
    {
        if (string.IsNullOrWhiteSpace(e.FirstName) || string.IsNullOrWhiteSpace(e.LastName)) throw new ArgumentException("Enter the employee's first and last names.");
        var tpin = e.Tpin.Trim();
        if (tpin.Length > 0 && (tpin.Length != 10 || !tpin.All(char.IsAsciiDigit))) throw new ArgumentException("A ZRA TPIN is 10 digits.");
        if (e.Status == EmployeeStatus.Left && e.LeftDate is null) throw new ArgumentException("Enter the date they left.");
        if (e.LeftDate is { } left && left < e.HireDate) throw new ArgumentException("The leaving date can't be before the hire date.");

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        Bind(cmd, e);
        long id;
        if (e.Id == 0)
        {
            cmd.CommandText = $"""
                INSERT INTO employees ({Columns}, created_at) VALUES ({Params}, $now);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
            id = (long)cmd.ExecuteScalar()!;
            PurchasingRepository.Exec(conn, tx, "UPDATE employees SET number = $n WHERE id = $id;", ("$n", $"E{id:D4}"), ("$id", id));
            AuditRepository.Write(conn, tx, actor, AuditActions.EmployeeCreated, "employee", Str(id),
                $"Added employee E{id:D4} ({e.JobTitle.Trim()}), hired {e.HireDate:dd MMM yyyy}");
        }
        else
        {
            id = e.Id;
            var before = GetEmployee(conn, tx, id) ?? throw new InvalidOperationException("Employee not found.");
            var sets = string.Join(", ", Columns.Split(", ").Zip(Params.Split(", "), (c, p) => $"{c} = {p}"));
            cmd.CommandText = $"UPDATE employees SET {sets} WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
            var changed = string.Join(", ", Changes(before, e));
            AuditRepository.Write(conn, tx, actor, AuditActions.EmployeeUpdated, "employee", Str(id),
                $"{before.Number}: changed {(changed.Length > 0 ? changed : "nothing")}");
        }
        tx.Commit();
        return id;
    }

    // Field names only, not the personal values, so the audit trail doesn't copy personal data.
    private static IEnumerable<string> Changes(Employee a, Employee b)
    {
        if (a.Name != b.Name.Trim()) yield return "name";
        if (a.JobTitle != b.JobTitle.Trim()) yield return "job title";
        if (a.Department != b.Department.Trim()) yield return "department";
        if (a.Phone != b.Phone.Trim() || a.Email != b.Email.Trim() || a.Address != b.Address.Trim()) yield return "contact details";
        if (a.Nrc != b.Nrc.Trim() || a.Tpin != b.Tpin.Trim() || a.NapsaNumber != b.NapsaNumber.Trim() || a.NhimaNumber != b.NhimaNumber.Trim()) yield return "statutory numbers";
        if (a.PayMethod != b.PayMethod || a.BankName != b.BankName.Trim() || a.BankAccount != b.BankAccount.Trim() || a.BankBranch != b.BankBranch.Trim() || a.MobileMoneyNumber != b.MobileMoneyNumber.Trim())
            yield return "payment details";
        if (a.Status != b.Status) yield return b.Status == EmployeeStatus.Left ? $"LEFT on {b.LeftDate:dd MMM yyyy}" : "re-activated";
        if (a.HireDate != b.HireDate) yield return "hire date";
    }

    // ── Contracts ───────────────────────────────────────────────────────

    public List<Contract> Contracts(long employeeId)
    {
        using var conn = db.Open();
        return Contracts(conn, null, employeeId);
    }

    /// <summary>The contract in force on a date, if any.</summary>
    public Contract? ContractOn(long employeeId, DateOnly date) => Contracts(employeeId).FirstOrDefault(c => c.CoversDay(date));

    /// <summary>
    /// Adds a contract (a new hire, a pay rise, a renewal). An open-ended earlier contract is ended
    /// the day before the new one starts. Returns the new contract's id.
    /// </summary>
    public long AddContract(Contract c, Actor actor)
    {
        if (c.BasicPay <= 0) throw new ArgumentException("Enter the basic pay.");
        if (c.HousingAllowance < 0 || c.TransportAllowance < 0 || c.OtherAllowance < 0) throw new ArgumentException("Allowances can't be negative.");
        if (c.HoursPerWeek is <= 0 or > 72) throw new ArgumentException("Enter normal hours a week (up to 72).");
        if (c.LeaveDaysPerYear < 24) throw new ArgumentException("The Employment Code Act gives at least 24 days of annual leave a year (2 a month).");
        if (c.EndDate is { } end && end < c.StartDate) throw new ArgumentException("The contract can't end before it starts.");
        if (c.Type == ContractType.FixedTerm && c.EndDate is null) throw new ArgumentException("A fixed-term contract needs an end date.");

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var emp = GetEmployee(conn, tx, c.EmployeeId) ?? throw new InvalidOperationException("Employee not found.");
        if (c.StartDate < emp.HireDate) throw new ArgumentException("The contract can't start before the hire date.");
        var existing = Contracts(conn, tx, c.EmployeeId);
        if (existing.Any(x => x.StartDate >= c.StartDate)) throw new InvalidOperationException("A later contract already exists. New contracts must start after the last one.");
        foreach (var prev in existing.Where(x => x.EndDate is null || x.EndDate >= c.StartDate))
            PurchasingRepository.Exec(conn, tx, "UPDATE employment_contracts SET end_date = $e WHERE id = $id;",
                ("$e", LedgerRepository.Day(c.StartDate.AddDays(-1))), ("$id", prev.Id));

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO employment_contracts (employee_id, type, start_date, end_date, basic_ngwee, housing_ngwee, transport_ngwee,
                                              other_allowance_ngwee, hours_per_week, leave_days_per_year, notes, created_by, created_at)
            VALUES ($e, $t, $s, $end, $b, $h, $tr, $o, $hrs, $leave, $notes, $by, $now);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$e", c.EmployeeId);
        cmd.Parameters.AddWithValue("$t", c.Type.ToString());
        cmd.Parameters.AddWithValue("$s", LedgerRepository.Day(c.StartDate));
        cmd.Parameters.AddWithValue("$end", c.EndDate is { } d ? LedgerRepository.Day(d) : DBNull.Value);
        cmd.Parameters.AddWithValue("$b", Money.ToNgwee(c.BasicPay));
        cmd.Parameters.AddWithValue("$h", Money.ToNgwee(c.HousingAllowance));
        cmd.Parameters.AddWithValue("$tr", Money.ToNgwee(c.TransportAllowance));
        cmd.Parameters.AddWithValue("$o", Money.ToNgwee(c.OtherAllowance));
        cmd.Parameters.AddWithValue("$hrs", (double)c.HoursPerWeek);
        cmd.Parameters.AddWithValue("$leave", (double)c.LeaveDaysPerYear);
        cmd.Parameters.AddWithValue("$notes", c.Notes.Trim());
        cmd.Parameters.AddWithValue("$by", actor.Name);
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
        var id = (long)cmd.ExecuteScalar()!;
        AuditRepository.Write(conn, tx, actor, AuditActions.ContractAdded, "employee", Str(c.EmployeeId),
            $"{emp.Number}: {c.Type.Label()} contract from {c.StartDate:dd MMM yyyy}{(c.EndDate is { } e2 ? $" to {e2:dd MMM yyyy}" : "")}, " +
            $"basic {Money.Format(c.BasicPay)} + allowances {Money.Format(c.Allowances)} a month");
        tx.Commit();
        return id;
    }

    // ── Leave ───────────────────────────────────────────────────────────

    public List<LeaveRequest> LeaveRequests(LeaveStatus? status = null, long? employeeId = null)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        var where = new List<string> { "1 = 1" };
        if (status is { } s) { where.Add("r.status = $s"); cmd.Parameters.AddWithValue("$s", s.ToString()); }
        if (employeeId is { } e) { where.Add("r.employee_id = $e"); cmd.Parameters.AddWithValue("$e", e); }
        cmd.CommandText = $"""
            SELECT r.id, r.employee_id, e.first_name || ' ' || e.last_name, r.type, r.start_date, r.end_date, r.days, r.reason, r.status,
                   r.requested_by, r.decided_by, r.decision_note
            FROM leave_requests r JOIN employees e ON e.id = r.employee_id
            WHERE {string.Join(" AND ", where)} ORDER BY r.start_date DESC, r.id DESC;
            """;
        var list = new List<LeaveRequest>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new LeaveRequest(r.GetInt64(0), r.GetInt64(1), r.GetString(2), Enum.Parse<LeaveType>(r.GetString(3)),
                LedgerRepository.ParseDay(r.GetString(4)), LedgerRepository.ParseDay(r.GetString(5)), (decimal)r.GetDouble(6), r.GetString(7),
                Enum.Parse<LeaveStatus>(r.GetString(8)), r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetString(11)));
        return list;
    }

    /// <summary>Annual leave available on a date: earned since hire less approved annual leave.</summary>
    public decimal AnnualLeaveBalance(long employeeId, DateOnly asAt)
    {
        using var conn = db.Open();
        return AnnualLeaveBalance(conn, null, employeeId, asAt, excludingRequest: null);
    }

    public long RequestLeave(long employeeId, LeaveType type, DateOnly start, DateOnly end, string reason, Actor actor)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var emp = GetEmployee(conn, tx, employeeId) ?? throw new InvalidOperationException("Employee not found.");
        if (emp.Status != EmployeeStatus.Active) throw new InvalidOperationException($"{emp.Name} has left.");
        // Annual leave can be taken up to what will have been earned by the start of the leave.
        var balance = AnnualLeaveBalance(conn, tx, employeeId, start, excludingRequest: null) - PendingAnnual(conn, tx, employeeId);
        if (HrRules.CheckLeave(type, start, end, balance) is { } problem) throw new InvalidOperationException(problem);
        if (Overlaps(conn, tx, employeeId, start, end, excludingRequest: null))
            throw new InvalidOperationException($"{emp.Name} already has leave booked on some of those days.");

        var days = HrRules.WorkingDays(start, end);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO leave_requests (employee_id, type, start_date, end_date, days, reason, status, requested_by, created_at)
            VALUES ($e, $t, $s, $end, $d, $r, 'Pending', $by, $now);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$e", employeeId);
        cmd.Parameters.AddWithValue("$t", type.ToString());
        cmd.Parameters.AddWithValue("$s", LedgerRepository.Day(start));
        cmd.Parameters.AddWithValue("$end", LedgerRepository.Day(end));
        cmd.Parameters.AddWithValue("$d", days);
        cmd.Parameters.AddWithValue("$r", reason.Trim());
        cmd.Parameters.AddWithValue("$by", actor.Name);
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
        var id = (long)cmd.ExecuteScalar()!;
        AuditRepository.Write(conn, tx, actor, AuditActions.LeaveRequested, "employee", Str(employeeId),
            $"{emp.Number}: {type.Label()} leave requested {start:dd MMM}–{end:dd MMM yyyy} ({days} day(s))");
        tx.Commit();
        return id;
    }

    /// <summary>Approves or rejects a pending request, or cancels a pending or approved one.</summary>
    public void DecideLeave(long requestId, LeaveStatus decision, string note, Actor actor)
    {
        if (decision == LeaveStatus.Pending) throw new ArgumentException("Choose approve, reject or cancel.");
        if (decision == LeaveStatus.Rejected && string.IsNullOrWhiteSpace(note)) throw new ArgumentException("Say why the leave is turned down.");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var req = LeaveRequestIn(conn, tx, requestId) ?? throw new InvalidOperationException("Leave request not found.");
        var allowed = decision == LeaveStatus.Cancelled
            ? req.Status is LeaveStatus.Pending or LeaveStatus.Approved
            : req.Status == LeaveStatus.Pending;
        if (!allowed) throw new InvalidOperationException($"This request is already {req.Status.ToString().ToLowerInvariant()}.");
        if (decision == LeaveStatus.Approved && req.Type == LeaveType.Annual)
        {
            var balance = AnnualLeaveBalance(conn, tx, req.EmployeeId, req.Start, excludingRequest: requestId);
            if (req.Days > balance) throw new InvalidOperationException($"Only {balance:0.#} day(s) of annual leave available now.");
        }
        PurchasingRepository.Exec(conn, tx, "UPDATE leave_requests SET status = $s, decided_by = $by, decided_at = $at, decision_note = $n WHERE id = $id;",
            ("$s", decision.ToString()), ("$by", actor.Name), ("$at", DateTime.Now.ToString("o", CultureInfo.InvariantCulture)),
            ("$n", note.Trim()), ("$id", requestId));
        AuditRepository.Write(conn, tx, actor, AuditActions.LeaveDecided, "employee", Str(req.EmployeeId),
            $"{req.EmployeeName}: {req.Type.Label()} leave {req.Start:dd MMM}–{req.End:dd MMM yyyy} {decision.ToString().ToLowerInvariant()}" +
            (note.Trim().Length > 0 ? $": {note.Trim()}" : ""));
        tx.Commit();
    }

    /// <summary>Approved leave covering a day (for the attendance register and the dashboard).</summary>
    public List<LeaveRequest> OnLeave(DateOnly date) =>
        LeaveRequests(LeaveStatus.Approved).Where(r => r.Start <= date && r.End >= date).ToList();

    // ── Attendance ──────────────────────────────────────────────────────

    public List<AttendanceRecord> Attendance(DateOnly date)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT employee_id, date, status, hours_worked, overtime_hours, note FROM attendance WHERE date = $d;";
        cmd.Parameters.AddWithValue("$d", LedgerRepository.Day(date));
        return ReadAttendance(cmd);
    }

    public List<AttendanceRecord> AttendanceFor(long employeeId, DateOnly from, DateOnly to)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT employee_id, date, status, hours_worked, overtime_hours, note FROM attendance WHERE employee_id = $e AND date BETWEEN $f AND $t ORDER BY date;";
        cmd.Parameters.AddWithValue("$e", employeeId);
        cmd.Parameters.AddWithValue("$f", LedgerRepository.Day(from));
        cmd.Parameters.AddWithValue("$t", LedgerRepository.Day(to));
        return ReadAttendance(cmd);
    }

    /// <summary>Saves the register for one day (adds or replaces each employee's line).</summary>
    public void SaveAttendance(DateOnly date, IReadOnlyList<AttendanceRecord> records, Actor actor)
    {
        if (date > Today) throw new ArgumentException("Attendance can't be recorded for future days.");
        foreach (var r in records)
        {
            if (r.HoursWorked is < 0 or > 24 || r.OvertimeHours is < 0 or > 24) throw new ArgumentException("Hours must be between 0 and 24.");
            if (r.Status is AttendanceStatus.Absent or AttendanceStatus.DayOff or AttendanceStatus.OnLeave && r.OvertimeHours > 0)
                throw new ArgumentException("Overtime can only be recorded for someone who was at work.");
        }
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var r in records)
            PurchasingRepository.Exec(conn, tx, """
                INSERT INTO attendance (employee_id, date, status, hours_worked, overtime_hours, note, recorded_by, recorded_at)
                VALUES ($e, $d, $s, $h, $o, $n, $by, $at)
                ON CONFLICT(employee_id, date) DO UPDATE SET status = excluded.status, hours_worked = excluded.hours_worked,
                    overtime_hours = excluded.overtime_hours, note = excluded.note, recorded_by = excluded.recorded_by, recorded_at = excluded.recorded_at;
                """, ("$e", r.EmployeeId), ("$d", LedgerRepository.Day(date)), ("$s", r.Status.ToString()), ("$h", (double)r.HoursWorked),
                ("$o", (double)r.OvertimeHours), ("$n", r.Note.Trim()), ("$by", actor.Name), ("$at", DateTime.Now.ToString("o", CultureInfo.InvariantCulture)));
        AuditRepository.Write(conn, tx, actor, AuditActions.AttendanceRecorded, "attendance", LedgerRepository.Day(date),
            $"Attendance for {date:dd MMM yyyy}: {records.Count(r => r.Status is AttendanceStatus.Present or AttendanceStatus.Late)} at work, " +
            $"{records.Count(r => r.Status == AttendanceStatus.Absent)} absent, {records.Sum(r => r.OvertimeHours):0.#} overtime hour(s)");
        tx.Commit();
    }

    /// <summary>Overtime hours and unauthorised absences (working days, not on approved leave) in a month.</summary>
    public MonthAttendance MonthTotals(long employeeId, int year, int month)
    {
        var (first, last) = HrRules.Month(year, month);
        var records = AttendanceFor(employeeId, first, last);
        var leave = LeaveRequests(LeaveStatus.Approved, employeeId).Where(r => r.Start <= last && r.End >= first).ToList();
        var absent = records.Count(r => r.Status == AttendanceStatus.Absent && HrRules.IsWorkingDay(r.Date)
                                        && !leave.Any(l => l.Start <= r.Date && l.End >= r.Date));
        return new MonthAttendance(records.Sum(r => r.OvertimeHours), absent);
    }

    /// <summary>Approved unpaid leave days (working days) falling in a month.</summary>
    public int UnpaidLeaveDays(long employeeId, int year, int month)
    {
        var (first, last) = HrRules.Month(year, month);
        return LeaveRequests(LeaveStatus.Approved, employeeId)
            .Where(r => r.Type == LeaveType.Unpaid && r.Start <= last && r.End >= first)
            .Sum(r => HrRules.WorkingDays(r.Start > first ? r.Start : first, r.End < last ? r.End : last));
    }

    public HrSummary Summary()
    {
        var active = Employees();
        var today = Today;
        var ending = 0;
        var noContract = 0;
        foreach (var e in active)
        {
            var c = ContractOn(e.Id, today);
            if (c is null) noContract++;
            else if (c.EndDate is { } end && end <= today.AddDays(30)) ending++;
        }
        return new HrSummary(active.Count, OnLeave(today).Count, LeaveRequests(LeaveStatus.Pending).Count, ending, noContract);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private const string Columns =
        "first_name, last_name, job_title, department, phone, email, address, date_of_birth, hire_date, left_date, status, nrc, tpin, " +
        "napsa_number, nhima_number, pay_method, bank_name, bank_branch, bank_account, mobile_money_number, notes";
    private const string Params =
        "$fn, $ln, $job, $dept, $phone, $email, $addr, $dob, $hire, $left, $status, $nrc, $tpin, $napsa, $nhima, $pm, $bank, $branch, $acct, $momo, $notes";

    private const string EmployeeSelect = $"SELECT id, number, {Columns} FROM employees";

    private static void Bind(SqliteCommand cmd, Employee e)
    {
        cmd.Parameters.AddWithValue("$fn", e.FirstName.Trim());
        cmd.Parameters.AddWithValue("$ln", e.LastName.Trim());
        cmd.Parameters.AddWithValue("$job", e.JobTitle.Trim());
        cmd.Parameters.AddWithValue("$dept", e.Department.Trim());
        cmd.Parameters.AddWithValue("$phone", e.Phone.Trim());
        cmd.Parameters.AddWithValue("$email", e.Email.Trim());
        cmd.Parameters.AddWithValue("$addr", e.Address.Trim());
        cmd.Parameters.AddWithValue("$dob", e.DateOfBirth is { } dob ? LedgerRepository.Day(dob) : DBNull.Value);
        cmd.Parameters.AddWithValue("$hire", LedgerRepository.Day(e.HireDate));
        cmd.Parameters.AddWithValue("$left", e.LeftDate is { } l ? LedgerRepository.Day(l) : DBNull.Value);
        cmd.Parameters.AddWithValue("$status", e.Status.ToString());
        cmd.Parameters.AddWithValue("$nrc", e.Nrc.Trim());
        cmd.Parameters.AddWithValue("$tpin", e.Tpin.Trim());
        cmd.Parameters.AddWithValue("$napsa", e.NapsaNumber.Trim());
        cmd.Parameters.AddWithValue("$nhima", e.NhimaNumber.Trim());
        cmd.Parameters.AddWithValue("$pm", e.PayMethod.ToString());
        cmd.Parameters.AddWithValue("$bank", e.BankName.Trim());
        cmd.Parameters.AddWithValue("$branch", e.BankBranch.Trim());
        cmd.Parameters.AddWithValue("$acct", e.BankAccount.Trim());
        cmd.Parameters.AddWithValue("$momo", e.MobileMoneyNumber.Trim());
        cmd.Parameters.AddWithValue("$notes", e.Notes.Trim());
    }

    private static List<Employee> ReadEmployees(SqliteCommand cmd)
    {
        var list = new List<Employee>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Employee
            {
                Id = r.GetInt64(0), Number = r.IsDBNull(1) ? "" : r.GetString(1), FirstName = r.GetString(2), LastName = r.GetString(3),
                JobTitle = r.GetString(4), Department = r.GetString(5), Phone = r.GetString(6), Email = r.GetString(7), Address = r.GetString(8),
                DateOfBirth = r.IsDBNull(9) ? null : LedgerRepository.ParseDay(r.GetString(9)), HireDate = LedgerRepository.ParseDay(r.GetString(10)),
                LeftDate = r.IsDBNull(11) ? null : LedgerRepository.ParseDay(r.GetString(11)), Status = Enum.Parse<EmployeeStatus>(r.GetString(12)),
                Nrc = r.GetString(13), Tpin = r.GetString(14), NapsaNumber = r.GetString(15), NhimaNumber = r.GetString(16),
                PayMethod = Enum.Parse<PayMethod>(r.GetString(17)), BankName = r.GetString(18), BankBranch = r.GetString(19),
                BankAccount = r.GetString(20), MobileMoneyNumber = r.GetString(21), Notes = r.GetString(22),
            });
        return list;
    }

    internal static Employee? GetEmployee(SqliteConnection conn, SqliteTransaction? tx, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"{EmployeeSelect} WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return ReadEmployees(cmd).FirstOrDefault();
    }

    internal static List<Contract> Contracts(SqliteConnection conn, SqliteTransaction? tx, long employeeId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT id, employee_id, type, start_date, end_date, basic_ngwee, housing_ngwee, transport_ngwee, other_allowance_ngwee,
                   hours_per_week, leave_days_per_year, notes
            FROM employment_contracts WHERE employee_id = $e ORDER BY start_date;
            """;
        cmd.Parameters.AddWithValue("$e", employeeId);
        var list = new List<Contract>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Contract
            {
                Id = r.GetInt64(0), EmployeeId = r.GetInt64(1), Type = Enum.Parse<ContractType>(r.GetString(2)),
                StartDate = LedgerRepository.ParseDay(r.GetString(3)), EndDate = r.IsDBNull(4) ? null : LedgerRepository.ParseDay(r.GetString(4)),
                BasicPay = Money.FromNgwee(r.GetInt64(5)), HousingAllowance = Money.FromNgwee(r.GetInt64(6)),
                TransportAllowance = Money.FromNgwee(r.GetInt64(7)), OtherAllowance = Money.FromNgwee(r.GetInt64(8)),
                HoursPerWeek = (decimal)r.GetDouble(9), LeaveDaysPerYear = (decimal)r.GetDouble(10), Notes = r.GetString(11),
            });
        return list;
    }

    internal static decimal AnnualLeaveBalance(SqliteConnection conn, SqliteTransaction? tx, long employeeId, DateOnly asAt, long? excludingRequest)
    {
        var emp = GetEmployee(conn, tx, employeeId) ?? throw new InvalidOperationException("Employee not found.");
        var contract = Contracts(conn, tx, employeeId).LastOrDefault(c => c.StartDate <= asAt);
        var perYear = contract?.LeaveDaysPerYear ?? 24m;
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(SUM(days), 0) FROM leave_requests WHERE employee_id = $e AND type = 'Annual' AND status = 'Approved' AND id <> $x;";
        cmd.Parameters.AddWithValue("$e", employeeId);
        cmd.Parameters.AddWithValue("$x", excludingRequest ?? 0);
        var taken = Convert.ToDecimal(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        return HrRules.AnnualLeaveBalance(emp.HireDate, asAt, perYear, taken);
    }

    private static decimal PendingAnnual(SqliteConnection conn, SqliteTransaction tx, long employeeId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(SUM(days), 0) FROM leave_requests WHERE employee_id = $e AND type = 'Annual' AND status = 'Pending';";
        cmd.Parameters.AddWithValue("$e", employeeId);
        return Convert.ToDecimal(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static bool Overlaps(SqliteConnection conn, SqliteTransaction tx, long employeeId, DateOnly start, DateOnly end, long? excludingRequest)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT EXISTS (SELECT 1 FROM leave_requests WHERE employee_id = $e AND status IN ('Pending', 'Approved')
                           AND start_date <= $end AND end_date >= $start AND id <> $x);
            """;
        cmd.Parameters.AddWithValue("$e", employeeId);
        cmd.Parameters.AddWithValue("$start", LedgerRepository.Day(start));
        cmd.Parameters.AddWithValue("$end", LedgerRepository.Day(end));
        cmd.Parameters.AddWithValue("$x", excludingRequest ?? 0);
        return (long)cmd.ExecuteScalar()! == 1;
    }

    private static LeaveRequest? LeaveRequestIn(SqliteConnection conn, SqliteTransaction tx, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT r.id, r.employee_id, e.first_name || ' ' || e.last_name, r.type, r.start_date, r.end_date, r.days, r.reason, r.status,
                   r.requested_by, r.decided_by, r.decision_note
            FROM leave_requests r JOIN employees e ON e.id = r.employee_id WHERE r.id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new LeaveRequest(r.GetInt64(0), r.GetInt64(1), r.GetString(2), Enum.Parse<LeaveType>(r.GetString(3)),
                LedgerRepository.ParseDay(r.GetString(4)), LedgerRepository.ParseDay(r.GetString(5)), (decimal)r.GetDouble(6), r.GetString(7),
                Enum.Parse<LeaveStatus>(r.GetString(8)), r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetString(11))
            : null;
    }

    private static List<AttendanceRecord> ReadAttendance(SqliteCommand cmd)
    {
        var list = new List<AttendanceRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new AttendanceRecord(r.GetInt64(0), LedgerRepository.ParseDay(r.GetString(1)), Enum.Parse<AttendanceStatus>(r.GetString(2)),
                (decimal)r.GetDouble(3), (decimal)r.GetDouble(4), r.GetString(5)));
        return list;
    }

    private static string Str(long id) => id.ToString(CultureInfo.InvariantCulture);
}
