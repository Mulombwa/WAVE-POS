using System.Globalization;
using Microsoft.Data.Sqlite;
using WavePOS.Core;
using WavePOS.Core.Accounting;
using WavePOS.Core.Audit;
using WavePOS.Core.HR;
using WavePOS.Core.Security;

namespace WavePOS.Data;

public enum PayrollStatus { Draft, Approved, Paid }

public class Payslip
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public string Period { get; set; } = "";
    public long EmployeeId { get; set; }
    public string EmployeeNumber { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public string JobTitle { get; set; } = "";
    public string Department { get; set; } = "";
    public string Nrc { get; set; } = "";
    public string Tpin { get; set; } = "";
    public string NapsaNumber { get; set; } = "";
    public PayMethod PayMethod { get; set; }
    public string PayAccount { get; set; } = "";
    public decimal Basic { get; set; }
    public decimal Allowances { get; set; }
    public decimal OvertimeHours { get; set; }
    public decimal OvertimePay { get; set; }
    public decimal Bonus { get; set; }
    public decimal UnpaidDays { get; set; }
    public decimal UnpaidDeduction { get; set; }
    public string OtherDeductionsNote { get; set; } = "";
    public decimal AnnualLeaveBalance { get; set; }
    public PayResult Result { get; set; } = new();
}

public class PayrollRun
{
    public long Id { get; set; }
    public string Period { get; set; } = "";
    public PayrollStatus Status { get; set; }
    public DateOnly RatesFrom { get; set; }
    public long? PreparedById { get; set; }
    public string PreparedBy { get; set; } = "";
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public long? JournalEntryId { get; set; }
    public DateOnly? PaidOn { get; set; }
    public long? PaymentEntryId { get; set; }
    public List<Payslip> Payslips { get; set; } = [];

    public int Year => int.Parse(Period[..4], CultureInfo.InvariantCulture);
    public int Month => int.Parse(Period[5..], CultureInfo.InvariantCulture);
    public string Title => new DateOnly(Year, Month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);
    public DateOnly PeriodEnd => HrRules.Month(Year, Month).Last;

    /// <summary>PAYE, NAPSA, NHIMA and the skills levy are due by the 10th of the following month.</summary>
    public DateOnly StatutoryDueDate => new DateOnly(Year, Month, 10).AddMonths(1);

    public decimal Gross => Payslips.Sum(p => p.Result.Gross);
    public decimal Net => Payslips.Sum(p => p.Result.Net);
    public decimal Paye => Payslips.Sum(p => p.Result.Paye);
    public decimal Napsa => Payslips.Sum(p => p.Result.NapsaEmployee + p.Result.NapsaEmployer);
    public decimal Nhima => Payslips.Sum(p => p.Result.NhimaEmployee + p.Result.NhimaEmployer);
    public decimal SkillsLevy => Payslips.Sum(p => p.Result.SkillsLevy);
    public decimal EmployerCost => Payslips.Sum(p => p.Result.EmployerCost);
}

/// <summary>Someone left out of a payroll run, and why.</summary>
public record PayrollSkip(string EmployeeName, string Reason);

/// <summary>
/// Monthly payroll: worked out from each employee's contract, overtime and unpaid days; adjusted
/// (bonus, advance recovery) while in draft; approved by someone other than the preparer (posting
/// wages and statutory contributions to the books); then paid (posting net pay out of the bank).
/// </summary>
public class PayrollRepository(PosDatabase db)
{
    // ── Statutory rates ─────────────────────────────────────────────────

    /// <summary>Built-in tables plus any the business has added (theirs win on the same date), oldest first.</summary>
    public List<PayrollRates> Rates()
    {
        using var conn = db.Open();
        return Rates(conn, null);
    }

    public PayrollRates RatesFor(DateOnly date) => PayrollRates.For(date, Rates());

    public void SaveRates(PayrollRates rates, Actor actor)
    {
        if (rates.Check() is { } problem) throw new ArgumentException(problem);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        PurchasingRepository.Exec(conn, tx, """
            INSERT INTO payroll_rates (effective_from, rates_json, set_by, created_at) VALUES ($d, $j, $by, $now)
            ON CONFLICT(effective_from) DO UPDATE SET rates_json = excluded.rates_json, set_by = excluded.set_by, created_at = excluded.created_at;
            """, ("$d", LedgerRepository.Day(rates.EffectiveFrom)), ("$j", (rates with { Source = $"Set by {actor.Name}" }).ToJson()),
            ("$by", actor.Name), ("$now", DateTime.Now.ToString("o", CultureInfo.InvariantCulture)));
        AuditRepository.Write(conn, tx, actor, AuditActions.PayrollRatesChanged, "payroll", LedgerRepository.Day(rates.EffectiveFrom),
            $"Statutory rates from {rates.EffectiveFrom:dd MMM yyyy}: PAYE bands {string.Join(" / ", rates.PayeBands.Select(b => $"{b.Rate:P0} to {(b.UpTo is { } u ? Money.Format(u) : "above")}"))}, " +
            $"NAPSA {rates.NapsaRate:P1} up to {Money.Format(rates.NapsaEarningsCeiling)}, NHIMA {rates.NhimaRate:P1}, SDL {rates.SkillsLevyRate:P1}");
        tx.Commit();
    }

    // ── Runs ────────────────────────────────────────────────────────────

    public List<PayrollRun> Runs()
    {
        using var conn = db.Open();
        return ReadRuns(conn, null, "1 = 1");
    }

    public PayrollRun? GetRun(long id)
    {
        using var conn = db.Open();
        return ReadRuns(conn, null, "r.id = $id", ("$id", id)).FirstOrDefault();
    }

    /// <summary>
    /// Prepares a month's payroll for everyone employed with a contract during it. Returns the run id
    /// and anyone left out (e.g. no contract).
    /// </summary>
    public (long RunId, List<PayrollSkip> Skipped) CreateRun(int year, int month, Actor actor)
    {
        var period = $"{year:D4}-{month:D2}";
        var (_, last) = HrRules.Month(year, month);
        if (new DateOnly(year, month, 1) > DateOnly.FromDateTime(DateTime.Today).AddMonths(1))
            throw new ArgumentException("Payroll can only be prepared up to next month.");
        var rates = RatesFor(last);
        var (slips, skipped) = BuildSlips(year, month, rates, keep: []);
        if (slips.Count == 0) throw new InvalidOperationException("Nobody to pay this month. Add employees with contracts first.");

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        long runId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO payroll_runs (period, status, rates_from, prepared_by_id, prepared_by, created_at)
                VALUES ($p, 'Draft', $r, $uid, $by, $now);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$p", period);
            cmd.Parameters.AddWithValue("$r", LedgerRepository.Day(rates.EffectiveFrom));
            cmd.Parameters.AddWithValue("$uid", (object?)actor.UserId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$by", actor.Name);
            cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
            try { runId = (long)cmd.ExecuteScalar()!; }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new InvalidOperationException($"Payroll for {period} already exists."); }
        }
        foreach (var s in slips) InsertSlip(conn, tx, runId, s);
        AuditRepository.Write(conn, tx, actor, AuditActions.PayrollPrepared, "payroll", Str(runId),
            $"Payroll {period} prepared: {slips.Count} employee(s), gross {Money.Format(slips.Sum(s => s.Result.Gross))}, net {Money.Format(slips.Sum(s => s.Result.Net))}" +
            (skipped.Count > 0 ? $"; left out: {string.Join(", ", skipped.Select(s => s.EmployeeName))}" : ""));
        tx.Commit();
        return (runId, skipped);
    }

    /// <summary>Works a draft out again (e.g. after attendance or contracts changed), keeping bonuses and deductions entered.</summary>
    public List<PayrollSkip> Recalculate(long runId, Actor actor)
    {
        var run = GetRun(runId) ?? throw new InvalidOperationException("Payroll not found.");
        if (run.Status != PayrollStatus.Draft) throw new InvalidOperationException("Only draft payrolls can be recalculated.");
        var keep = run.Payslips.ToDictionary(p => p.EmployeeId, p => (p.Bonus, p.Result.OtherDeductions, p.OtherDeductionsNote));
        var rates = RatesFor(run.PeriodEnd);
        var (slips, skipped) = BuildSlips(run.Year, run.Month, rates, keep);

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        PurchasingRepository.Exec(conn, tx, "DELETE FROM payslips WHERE run_id = $r;", ("$r", runId));
        PurchasingRepository.Exec(conn, tx, "UPDATE payroll_runs SET rates_from = $d WHERE id = $r;", ("$d", LedgerRepository.Day(rates.EffectiveFrom)), ("$r", runId));
        foreach (var s in slips) InsertSlip(conn, tx, runId, s);
        AuditRepository.Write(conn, tx, actor, AuditActions.PayrollAdjusted, "payroll", Str(runId),
            $"Payroll {run.Period} recalculated: {slips.Count} employee(s), net {Money.Format(slips.Sum(s => s.Result.Net))}");
        tx.Commit();
        return skipped;
    }

    /// <summary>Sets a payslip's bonus and after-tax deductions (e.g. salary advance recovery) on a draft payroll.</summary>
    public void Adjust(long payslipId, decimal bonus, decimal otherDeductions, string note, Actor actor)
    {
        if (otherDeductions > 0 && string.IsNullOrWhiteSpace(note)) throw new ArgumentException("Say what the deduction is for.");
        using var conn = db.Open();
        var slip = ReadSlips(conn, null, "p.id = $id", ("$id", payslipId)).FirstOrDefault() ?? throw new InvalidOperationException("Payslip not found.");
        var run = ReadRuns(conn, null, "r.id = $id", ("$id", slip.RunId)).Single();
        if (run.Status != PayrollStatus.Draft) throw new InvalidOperationException("This payroll is approved and can't be changed.");
        var rates = PayrollRates.For(run.PeriodEnd, Rates(conn, null));
        var result = PayrollCalculator.Calculate(new PayInput
        {
            Basic = slip.Basic, Allowances = slip.Allowances, OvertimePay = slip.OvertimePay, Bonus = Money.Round(bonus),
            UnpaidDeduction = slip.UnpaidDeduction, OtherDeductions = Money.Round(otherDeductions),
        }, rates);
        slip.Bonus = Money.Round(bonus);
        slip.OtherDeductionsNote = otherDeductions > 0 ? note.Trim() : "";
        slip.Result = result;

        using var tx = conn.BeginTransaction();
        PurchasingRepository.Exec(conn, tx, "DELETE FROM payslips WHERE id = $id;", ("$id", payslipId));
        InsertSlip(conn, tx, slip.RunId, slip);
        AuditRepository.Write(conn, tx, actor, AuditActions.PayrollAdjusted, "payroll", Str(slip.RunId),
            $"Payroll {run.Period}, {slip.EmployeeNumber}: bonus {Money.Format(slip.Bonus)}, other deductions {Money.Format(result.OtherDeductions)}" +
            (slip.OtherDeductionsNote.Length > 0 ? $" ({slip.OtherDeductionsNote})" : "") + $" → net {Money.Format(result.Net)}");
        tx.Commit();
    }

    /// <summary>
    /// Approves a draft payroll and posts it to the books (dated the last day of the month). Whoever
    /// prepared it can't approve it, unless they're the only person who can.
    /// </summary>
    public void Approve(long runId, Actor actor)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var run = ReadRuns(conn, tx, "r.id = $id", ("$id", runId)).FirstOrDefault() ?? throw new InvalidOperationException("Payroll not found.");
        if (run.Status != PayrollStatus.Draft) throw new InvalidOperationException("This payroll is already approved.");
        if (actor.UserId is not { } approverId) throw new InvalidOperationException("Sign in to approve payroll.");
        var approvers = PurchasingRepository.ActiveUsersWho(conn, tx, Permission.ApprovePayroll);
        if (!approvers.Contains(approverId)) throw new InvalidOperationException("You aren't allowed to approve payroll.");
        if (approverId == run.PreparedById && approvers.Count > 1)
            throw new InvalidOperationException("You prepared this payroll, so another manager has to approve it.");

        var entryId = LedgerRepository.Post(conn, tx, PostingRules.Payroll(runId, run.Period, run.PeriodEnd, run.Payslips.Select(p => p.Result).ToList(),
            LedgerRepository.SystemAccountLookup(conn, tx)), actor, enforceLock: true);
        PurchasingRepository.Exec(conn, tx, "UPDATE payroll_runs SET status = 'Approved', approved_by = $by, approved_at = $at, journal_entry_id = $je WHERE id = $id;",
            ("$by", actor.Name), ("$at", DateTime.Now.ToString("o", CultureInfo.InvariantCulture)), ("$je", entryId), ("$id", runId));
        AuditRepository.Write(conn, tx, actor, AuditActions.PayrollApproved, "payroll", Str(runId),
            $"Payroll {run.Period} approved as J{entryId:D6}: gross {Money.Format(run.Gross)}, PAYE {Money.Format(run.Paye)}, net {Money.Format(run.Net)}" +
            (approverId == run.PreparedById ? " (approved by its preparer, the only person who can approve)" : $", prepared by {run.PreparedBy}"));
        tx.Commit();
    }

    /// <summary>Records net pay paid out from cash, bank or mobile money.</summary>
    public void Pay(long runId, DateOnly date, long fromAccountId, Actor actor)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var run = ReadRuns(conn, tx, "r.id = $id", ("$id", runId)).FirstOrDefault() ?? throw new InvalidOperationException("Payroll not found.");
        if (run.Status != PayrollStatus.Approved) throw new InvalidOperationException(run.Status == PayrollStatus.Draft ? "Approve the payroll first." : "This payroll is already paid.");
        var from = PurchasingRepository.MoneyAccount(conn, tx, fromAccountId);
        var entryId = LedgerRepository.Post(conn, tx, PostingRules.PayWages(runId, run.Period, date, run.Net, from.Id,
            LedgerRepository.SystemAccountLookup(conn, tx)), actor, enforceLock: true);
        PurchasingRepository.Exec(conn, tx, "UPDATE payroll_runs SET status = 'Paid', paid_on = $d, payment_entry_id = $je WHERE id = $id;",
            ("$d", LedgerRepository.Day(date)), ("$je", entryId), ("$id", runId));
        AuditRepository.Write(conn, tx, actor, AuditActions.PayrollPaid, "payroll", Str(runId),
            $"Payroll {run.Period}: net pay {Money.Format(run.Net)} paid from {from.Display} on {date:dd MMM yyyy}");
        tx.Commit();
    }

    public void DeleteDraft(long runId, Actor actor)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var run = ReadRuns(conn, tx, "r.id = $id", ("$id", runId)).FirstOrDefault() ?? throw new InvalidOperationException("Payroll not found.");
        if (run.Status != PayrollStatus.Draft) throw new InvalidOperationException("Approved payrolls are in the books and can't be deleted.");
        PurchasingRepository.Exec(conn, tx, "DELETE FROM payslips WHERE run_id = $id;", ("$id", runId));
        PurchasingRepository.Exec(conn, tx, "DELETE FROM payroll_runs WHERE id = $id;", ("$id", runId));
        AuditRepository.Write(conn, tx, actor, AuditActions.PayrollDeleted, "payroll", Str(runId), $"Draft payroll {run.Period} deleted");
        tx.Commit();
    }

    public Payslip? GetPayslip(long id)
    {
        using var conn = db.Open();
        return ReadSlips(conn, null, "p.id = $id", ("$id", id)).FirstOrDefault();
    }

    public List<Payslip> PayslipsFor(long employeeId)
    {
        using var conn = db.Open();
        return ReadSlips(conn, null, "p.employee_id = $e", ("$e", employeeId)).OrderByDescending(p => p.Period).ToList();
    }

    // ── Working out payslips ────────────────────────────────────────────

    private (List<Payslip> Slips, List<PayrollSkip> Skipped) BuildSlips(int year, int month, PayrollRates rates,
        Dictionary<long, (decimal Bonus, decimal Other, string Note)> keep)
    {
        var hr = new HrRepository(db);
        var (first, last) = HrRules.Month(year, month);
        var slips = new List<Payslip>();
        var skipped = new List<PayrollSkip>();
        foreach (var e in hr.Employees(includeLeft: true))
        {
            if (e.HireDate > last || e.LeftDate < first) continue; // not employed this month
            var contract = hr.Contracts(e.Id).LastOrDefault(c => c.StartDate <= last && (c.EndDate is null || c.EndDate >= first));
            if (contract is null) { skipped.Add(new(e.Name, "no contract covering this month")); continue; }

            // Pay only for the days employed under the contract this month.
            var effective = new Contract
            {
                StartDate = contract.StartDate, BasicPay = contract.BasicPay,
                EndDate = e.LeftDate is { } left && (contract.EndDate is null || left < contract.EndDate) ? left : contract.EndDate,
            };
            var basic = HrRules.ProRatedBasic(effective, year, month);
            var share = contract.BasicPay == 0 ? 0 : basic / contract.BasicPay;
            var allowances = Money.Round(contract.Allowances * share);

            var attendance = hr.MonthTotals(e.Id, year, month);
            var unpaidDays = hr.UnpaidLeaveDays(e.Id, year, month) + attendance.AbsentDays;
            var unpaid = Math.Min(Money.Round(HrRules.DailyRate(contract.BasicPay, year, month) * unpaidDays), basic);
            var overtime = HrRules.OvertimePay(contract.BasicPay, contract.HoursPerWeek, attendance.OvertimeHours);
            var (bonus, other, note) = keep.GetValueOrDefault(e.Id, (0m, 0m, ""));

            PayResult result;
            try
            {
                result = PayrollCalculator.Calculate(new PayInput
                {
                    Basic = basic, Allowances = allowances, OvertimePay = overtime, Bonus = bonus, UnpaidDeduction = unpaid, OtherDeductions = other,
                }, rates);
            }
            catch (ArgumentException ex) { skipped.Add(new(e.Name, ex.Message)); continue; }

            slips.Add(new Payslip
            {
                EmployeeId = e.Id, EmployeeNumber = e.Number, EmployeeName = e.Name, JobTitle = e.JobTitle, Department = e.Department,
                Nrc = e.Nrc, Tpin = e.Tpin, NapsaNumber = e.NapsaNumber, PayMethod = e.PayMethod,
                PayAccount = e.PayMethod switch
                {
                    PayMethod.Bank => $"{e.BankName} {e.BankBranch} {e.BankAccount}".Trim(),
                    PayMethod.MobileMoney => e.MobileMoneyNumber,
                    _ => "Cash",
                },
                Basic = basic, Allowances = allowances, OvertimeHours = attendance.OvertimeHours, OvertimePay = overtime, Bonus = bonus,
                UnpaidDays = unpaidDays, UnpaidDeduction = unpaid, OtherDeductionsNote = note,
                AnnualLeaveBalance = hr.AnnualLeaveBalance(e.Id, last), Result = result,
            });
        }
        return (slips, skipped);
    }

    // ── Storage ─────────────────────────────────────────────────────────

    private static List<PayrollRates> Rates(SqliteConnection conn, SqliteTransaction? tx)
    {
        var tables = PayrollRates.BuiltIn.ToDictionary(r => r.EffectiveFrom);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT rates_json FROM payroll_rates;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var t = PayrollRates.FromJson(r.GetString(0));
            tables[t.EffectiveFrom] = t;
        }
        return tables.Values.OrderBy(t => t.EffectiveFrom).ToList();
    }

    private static void InsertSlip(SqliteConnection conn, SqliteTransaction tx, long runId, Payslip s)
    {
        static object N(decimal v) => Money.ToNgwee(v);
        var r = s.Result;
        PurchasingRepository.Exec(conn, tx, """
            INSERT INTO payslips (run_id, employee_id, employee_number, employee_name, job_title, department, nrc, tpin, napsa_number,
                pay_method, pay_account, basic_ngwee, allowances_ngwee, overtime_hours, overtime_ngwee, bonus_ngwee, unpaid_days, unpaid_ngwee,
                other_deductions_ngwee, other_deductions_note, gross_ngwee, taxable_ngwee, paye_ngwee, napsa_employee_ngwee, napsa_employer_ngwee,
                nhima_employee_ngwee, nhima_employer_ngwee, sdl_ngwee, net_ngwee, annual_leave_balance)
            VALUES ($run, $e, $num, $name, $job, $dept, $nrc, $tpin, $napsa, $pm, $acct, $basic, $allow, $oth, $ot, $bonus, $ud, $up,
                $other, $note, $gross, $tax, $paye, $nee, $ner, $hee, $her, $sdl, $net, $leave);
            """,
            ("$run", runId), ("$e", s.EmployeeId), ("$num", s.EmployeeNumber), ("$name", s.EmployeeName), ("$job", s.JobTitle), ("$dept", s.Department),
            ("$nrc", s.Nrc), ("$tpin", s.Tpin), ("$napsa", s.NapsaNumber), ("$pm", s.PayMethod.ToString()), ("$acct", s.PayAccount),
            ("$basic", N(s.Basic)), ("$allow", N(s.Allowances)), ("$oth", (double)s.OvertimeHours), ("$ot", N(s.OvertimePay)), ("$bonus", N(s.Bonus)),
            ("$ud", (double)s.UnpaidDays), ("$up", N(s.UnpaidDeduction)), ("$other", N(r.OtherDeductions)), ("$note", s.OtherDeductionsNote),
            ("$gross", N(r.Gross)), ("$tax", N(r.Taxable)), ("$paye", N(r.Paye)), ("$nee", N(r.NapsaEmployee)), ("$ner", N(r.NapsaEmployer)),
            ("$hee", N(r.NhimaEmployee)), ("$her", N(r.NhimaEmployer)), ("$sdl", N(r.SkillsLevy)), ("$net", N(r.Net)), ("$leave", (double)s.AnnualLeaveBalance));
    }

    private static List<PayrollRun> ReadRuns(SqliteConnection conn, SqliteTransaction? tx, string where, params (string, object)[] args)
    {
        var runs = new List<PayrollRun>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                SELECT r.id, r.period, r.status, r.rates_from, r.prepared_by_id, r.prepared_by, r.approved_by, r.approved_at,
                       r.journal_entry_id, r.paid_on, r.payment_entry_id
                FROM payroll_runs r WHERE {where} ORDER BY r.period DESC;
                """;
            foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                runs.Add(new PayrollRun
                {
                    Id = r.GetInt64(0), Period = r.GetString(1), Status = Enum.Parse<PayrollStatus>(r.GetString(2)),
                    RatesFrom = LedgerRepository.ParseDay(r.GetString(3)), PreparedById = r.IsDBNull(4) ? null : r.GetInt64(4),
                    PreparedBy = r.GetString(5), ApprovedBy = r.IsDBNull(6) ? null : r.GetString(6),
                    ApprovedAt = r.IsDBNull(7) ? null : DateTime.Parse(r.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    JournalEntryId = r.IsDBNull(8) ? null : r.GetInt64(8), PaidOn = r.IsDBNull(9) ? null : LedgerRepository.ParseDay(r.GetString(9)),
                    PaymentEntryId = r.IsDBNull(10) ? null : r.GetInt64(10),
                });
        }
        foreach (var run in runs) run.Payslips = ReadSlips(conn, tx, "p.run_id = $r", ("$r", run.Id));
        return runs;
    }

    private static List<Payslip> ReadSlips(SqliteConnection conn, SqliteTransaction? tx, string where, params (string, object)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT p.id, p.run_id, r.period, p.employee_id, p.employee_number, p.employee_name, p.job_title, p.department, p.nrc, p.tpin, p.napsa_number,
                   p.pay_method, p.pay_account, p.basic_ngwee, p.allowances_ngwee, p.overtime_hours, p.overtime_ngwee, p.bonus_ngwee, p.unpaid_days,
                   p.unpaid_ngwee, p.other_deductions_ngwee, p.other_deductions_note, p.gross_ngwee, p.taxable_ngwee, p.paye_ngwee,
                   p.napsa_employee_ngwee, p.napsa_employer_ngwee, p.nhima_employee_ngwee, p.nhima_employer_ngwee, p.sdl_ngwee, p.annual_leave_balance
            FROM payslips p JOIN payroll_runs r ON r.id = p.run_id
            WHERE {where} ORDER BY p.employee_name;
            """;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        var list = new List<Payslip>();
        using var r = cmd.ExecuteReader();
        decimal K(int i) => Money.FromNgwee(r.GetInt64(i));
        while (r.Read())
            list.Add(new Payslip
            {
                Id = r.GetInt64(0), RunId = r.GetInt64(1), Period = r.GetString(2), EmployeeId = r.GetInt64(3), EmployeeNumber = r.GetString(4),
                EmployeeName = r.GetString(5), JobTitle = r.GetString(6), Department = r.GetString(7), Nrc = r.GetString(8), Tpin = r.GetString(9),
                NapsaNumber = r.GetString(10), PayMethod = Enum.Parse<PayMethod>(r.GetString(11)), PayAccount = r.GetString(12),
                Basic = K(13), Allowances = K(14), OvertimeHours = (decimal)r.GetDouble(15), OvertimePay = K(16), Bonus = K(17),
                UnpaidDays = (decimal)r.GetDouble(18), UnpaidDeduction = K(19), OtherDeductionsNote = r.GetString(21),
                AnnualLeaveBalance = (decimal)r.GetDouble(30),
                Result = new PayResult
                {
                    OtherDeductions = K(20), Gross = K(22), Taxable = K(23), Paye = K(24), NapsaEmployee = K(25), NapsaEmployer = K(26),
                    NhimaEmployee = K(27), NhimaEmployer = K(28), SkillsLevy = K(29),
                },
            });
        return list;
    }

    private static string Str(long id) => id.ToString(CultureInfo.InvariantCulture);
}
