using Microsoft.Data.Sqlite;
using WavePOS.Core.Accounting;
using WavePOS.Core.Audit;
using WavePOS.Core.HR;
using WavePOS.Core.Models;

namespace WavePOS.Data.Tests;

public sealed class HrPayrollDataTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wavepos-hr-{Guid.NewGuid():N}.db");
    private readonly PosDatabase _db;
    private readonly HrRepository _hr;
    private readonly PayrollRepository _payroll;
    private readonly LedgerRepository _ledger;
    private readonly Actor _ann, _ben, _sam;

    public HrPayrollDataTests()
    {
        _db = new PosDatabase(_path);
        _db.Initialize();
        _hr = new HrRepository(_db);
        _payroll = new PayrollRepository(_db);
        _ledger = new LedgerRepository(_db);
        var users = new UserRepository(_db);
        _ann = ActorFor(users.Create("ann", "Ann Phiri", "4321", UserRole.Manager));
        _ben = ActorFor(users.Create("ben", "Ben Zulu", "4321", UserRole.Manager));
        _sam = ActorFor(users.Create("sam", "Sam Mwale", "4321", UserRole.Supervisor));
    }

    private static Actor ActorFor(User u) => new(u.Id, u.DisplayName);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private decimal Balance(string key) =>
        _ledger.Balances(null, new DateOnly(2026, 12, 31)).Single(b => b.Account.SystemKey == key).Balance;

    private long Hire(string first, DateOnly hired, decimal basic, decimal allowances = 0)
    {
        var id = _hr.SaveEmployee(new Employee
        {
            FirstName = first, LastName = "Banda", JobTitle = "Shop assistant", Department = "Shop floor", HireDate = hired,
            Nrc = "123456/10/1", Tpin = "1000000001", NapsaNumber = "NP123", BankName = "Zanaco", BankAccount = "0123456789",
        }, _ann);
        if (basic > 0)
            _hr.AddContract(new Contract { EmployeeId = id, StartDate = hired, BasicPay = basic, HousingAllowance = allowances, HoursPerWeek = 45 }, _ann);
        return id;
    }

    [Fact]
    public void Monthly_payroll_end_to_end()
    {
        var grace = Hire("Grace", new(2026, 1, 1), 6000m, 2000m);
        var john = Hire("John", new(2026, 9, 16), 3000m);   // joined mid-September
        Hire("Peter", new(2026, 1, 1), 0m);                 // no contract yet

        // 4 hours overtime, 1 day absent, 2 days unpaid leave in September (22 working days).
        _hr.SaveAttendance(new(2026, 9, 8), [new(grace, new(2026, 9, 8), AttendanceStatus.Present, 8, 4, "Stock take")], _sam);
        _hr.SaveAttendance(new(2026, 9, 9), [new(grace, new(2026, 9, 9), AttendanceStatus.Absent, 0, 0, "")], _sam);
        var unpaid = _hr.RequestLeave(grace, LeaveType.Unpaid, new(2026, 9, 14), new(2026, 9, 15), "Family matter", _sam);
        _hr.DecideLeave(unpaid, LeaveStatus.Approved, "", _ann);

        var (runId, skipped) = _payroll.CreateRun(2026, 9, _ann);
        Assert.Equal("Peter Banda", Assert.Single(skipped).EmployeeName);

        var run = _payroll.GetRun(runId)!;
        var g = run.Payslips.Single(p => p.EmployeeId == grace);
        Assert.Equal(3, g.UnpaidDays);
        Assert.Equal(818.18m, g.UnpaidDeduction);  // 3 × 6,000 / 22
        Assert.Equal(184.62m, g.OvertimePay);
        Assert.Equal(7366.44m, g.Result.Gross);
        Assert.Equal(479.93m, g.Result.Paye);
        Assert.Equal(368.32m, g.Result.NapsaEmployee);
        Assert.Equal(51.82m, g.Result.NhimaEmployee);
        Assert.Equal(6466.37m, g.Result.Net);
        Assert.Equal(16m, g.AnnualLeaveBalance);

        var j = run.Payslips.Single(p => p.EmployeeId == john);
        Assert.Equal(1500m, j.Basic);              // half of September
        Assert.Equal(0m, j.Result.Paye);
        Assert.Equal(1410m, j.Result.Net);

        // A salary advance recovered from Grace's pay.
        _payroll.Adjust(g.Id, 0m, 500m, "Salary advance", _ann);
        Assert.Equal(5966.37m, _payroll.GetRun(runId)!.Payslips.Single(p => p.EmployeeId == grace).Result.Net);

        // Ann prepared it, so Ben approves it.
        Assert.Contains("another manager", Assert.Throws<InvalidOperationException>(() => _payroll.Approve(runId, _ann)).Message);
        Assert.Contains("aren't allowed", Assert.Throws<InvalidOperationException>(() => _payroll.Approve(runId, _sam)).Message);
        _payroll.Approve(runId, _ben);
        Assert.Throws<InvalidOperationException>(() => _payroll.Adjust(g.Id, 100m, 0m, "", _ann));

        Assert.Equal(7366.44m + 1500m, Balance(SystemAccounts.Salaries));
        Assert.Equal(479.93m, Balance(SystemAccounts.PayePayable));
        Assert.Equal((368.32m + 75m) * 2, Balance(SystemAccounts.NapsaPayable));
        Assert.Equal((51.82m + 15m) * 2, Balance(SystemAccounts.NhimaPayable));
        Assert.Equal(36.83m + 7.50m, Balance(SystemAccounts.SkillsLevyPayable));
        Assert.Equal(-500m, Balance(SystemAccounts.StaffAdvances));
        Assert.Equal(5966.37m + 1410m, Balance(SystemAccounts.WagesPayable));

        var bank = _ledger.Accounts().Single(a => a.SystemKey == SystemAccounts.Bank).Id;
        _payroll.Pay(runId, new(2026, 9, 30), bank, _ann);
        Assert.Equal(0m, Balance(SystemAccounts.WagesPayable));
        Assert.Equal(PayrollStatus.Paid, _payroll.GetRun(runId)!.Status);
        Assert.Equal(new DateOnly(2026, 10, 10), run.StatutoryDueDate);
        Assert.True(_ledger.TrialBalance(new(2026, 12, 31)).Balances);
        Assert.True(new AuditRepository(_db).Verify().IsIntact);

        Assert.Throws<InvalidOperationException>(() => _payroll.CreateRun(2026, 9, _ann)); // one payroll per month
        Assert.Single(_payroll.PayslipsFor(grace));
    }

    [Fact]
    public void Drafts_can_be_recalculated_or_deleted_keeping_adjustments()
    {
        var grace = Hire("Grace", new(2026, 1, 1), 6000m);
        var (runId, _) = _payroll.CreateRun(2026, 9, _ann);
        var slip = _payroll.GetRun(runId)!.Payslips.Single();
        _payroll.Adjust(slip.Id, 1000m, 0m, "", _ann);

        _hr.SaveAttendance(new(2026, 9, 10), [new(grace, new(2026, 9, 10), AttendanceStatus.Present, 8, 2, "")], _sam);
        _payroll.Recalculate(runId, _ann);
        var after = _payroll.GetRun(runId)!.Payslips.Single();
        Assert.Equal(1000m, after.Bonus);
        Assert.Equal(2m, after.OvertimeHours);

        _payroll.DeleteDraft(runId, _ann);
        Assert.Empty(_payroll.Runs());
    }

    [Fact]
    public void Only_the_manager_left_can_approve_their_own_payroll()
    {
        new UserRepository(_db).Update(new User { Id = _ben.UserId!.Value, Username = "ben", DisplayName = "Ben Zulu", Role = UserRole.Manager, IsActive = false }, _ann);
        Hire("Grace", new(2026, 1, 1), 6000m);
        var (runId, _) = _payroll.CreateRun(2026, 9, _ann);
        _payroll.Approve(runId, _ann);
        Assert.Contains("only person who can approve",
            new AuditRepository(_db).Query(new AuditFilter(Action: AuditActions.PayrollApproved)).Entries.Single().Details);
    }

    [Fact]
    public void Leave_balance_overlaps_and_decisions()
    {
        var grace = Hire("Grace", new(2026, 1, 1), 6000m);
        Assert.Equal(16m, _hr.AnnualLeaveBalance(grace, new(2026, 9, 30)));

        Assert.Contains("Only", Assert.Throws<InvalidOperationException>(() =>
            _hr.RequestLeave(grace, LeaveType.Annual, new(2026, 10, 1), new(2026, 10, 30), "", _sam)).Message);
        var holiday = _hr.RequestLeave(grace, LeaveType.Annual, new(2026, 10, 5), new(2026, 10, 9), "Holiday", _sam); // 5 days
        Assert.Contains("already has leave", Assert.Throws<InvalidOperationException>(() =>
            _hr.RequestLeave(grace, LeaveType.Sick, new(2026, 10, 9), new(2026, 10, 9), "", _sam)).Message);

        Assert.Throws<ArgumentException>(() => _hr.DecideLeave(holiday, LeaveStatus.Rejected, "", _ann)); // needs a reason
        _hr.DecideLeave(holiday, LeaveStatus.Approved, "", _ann);
        Assert.Equal(13m, _hr.AnnualLeaveBalance(grace, new(2026, 10, 31))); // 9 months × 2 = 18 earned, less 5
        Assert.Single(_hr.OnLeave(new(2026, 10, 7)));
        Assert.Throws<InvalidOperationException>(() => _hr.DecideLeave(holiday, LeaveStatus.Approved, "", _ann));
        _hr.DecideLeave(holiday, LeaveStatus.Cancelled, "Plans changed", _ann);
        Assert.Empty(_hr.OnLeave(new(2026, 10, 7)));
    }

    [Fact]
    public void A_new_contract_ends_the_previous_one()
    {
        var grace = Hire("Grace", new(2026, 1, 1), 6000m);
        _hr.AddContract(new Contract { EmployeeId = grace, StartDate = new(2026, 7, 1), BasicPay = 6500m, HoursPerWeek = 45 }, _ann);

        var contracts = _hr.Contracts(grace);
        Assert.Equal(new DateOnly(2026, 6, 30), contracts[0].EndDate);
        Assert.Equal(6500m, _hr.ContractOn(grace, new(2026, 9, 1))!.BasicPay);
        Assert.Throws<InvalidOperationException>(() =>
            _hr.AddContract(new Contract { EmployeeId = grace, StartDate = new(2026, 3, 1), BasicPay = 7000m, HoursPerWeek = 45 }, _ann));
        Assert.Throws<ArgumentException>(() =>
            _hr.AddContract(new Contract { EmployeeId = grace, StartDate = new(2026, 8, 1), BasicPay = 7000m, LeaveDaysPerYear = 12 }, _ann));
        Assert.Throws<ArgumentException>(() =>
            _hr.AddContract(new Contract { EmployeeId = grace, Type = ContractType.FixedTerm, StartDate = new(2026, 8, 1), BasicPay = 7000m }, _ann));
    }

    [Fact]
    public void Employee_changes_are_audited_without_personal_values()
    {
        var grace = Hire("Grace", new(2026, 1, 1), 6000m);
        var e = _hr.GetEmployee(grace)!;
        e.BankAccount = "9999999999";
        e.Status = EmployeeStatus.Left;
        e.LeftDate = new(2026, 10, 15);
        _hr.SaveEmployee(e, _ann);

        var entry = new AuditRepository(_db).Query(new AuditFilter(Action: AuditActions.EmployeeUpdated)).Entries.Single();
        Assert.Contains("payment details", entry.Details);
        Assert.Contains("LEFT", entry.Details);
        Assert.DoesNotContain("9999999999", entry.Details);
        Assert.Empty(_hr.Employees());
        Assert.Single(_hr.Employees(includeLeft: true));
    }

    [Fact]
    public void Business_can_add_newer_statutory_rates()
    {
        var current = _payroll.RatesFor(new(2026, 9, 30));
        _payroll.SaveRates(current with { EffectiveFrom = new(2027, 1, 1), NapsaEarningsCeiling = 40_000m }, _ann);
        Assert.Equal(40_000m, _payroll.RatesFor(new(2027, 3, 31)).NapsaEarningsCeiling);
        Assert.Equal(37_236m, _payroll.RatesFor(new(2026, 12, 31)).NapsaEarningsCeiling);
        Assert.Throws<ArgumentException>(() => _payroll.SaveRates(current with { EffectiveFrom = new(2027, 2, 1), PayeBands = [] }, _ann));
    }

    [Fact]
    public void Older_charts_have_their_paye_account_taken_over()
    {
        using (var conn = _db.Open())
        using (var cmd = conn.CreateCommand())
        {
            // How the 2200 account looked before this version.
            cmd.CommandText = "UPDATE accounts SET system_key = NULL, name = 'PAYE & NAPSA payable' WHERE system_key = 'paye_payable';";
            cmd.ExecuteNonQuery();
        }
        _db.Initialize();
        var paye = _ledger.Accounts().Single(a => a.SystemKey == SystemAccounts.PayePayable);
        Assert.Equal("2200", paye.Code);
        Assert.Equal("PAYE payable", paye.Name);
    }
}
