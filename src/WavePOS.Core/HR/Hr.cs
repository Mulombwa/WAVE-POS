namespace WavePOS.Core.HR;

public enum EmployeeStatus { Active, Left }

public enum PayMethod { Bank, MobileMoney, Cash }

/// <summary>
/// A person on the payroll. Separate from sign-in accounts: not every employee uses WAVE POS.
/// Holds personal data (NRC, bank details), so it is only shown to HR and payroll staff and every
/// view is recorded (Data Protection Act 2021).
/// </summary>
public class Employee
{
    public long Id { get; set; }
    public string Number { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string JobTitle { get; set; } = "";
    public string Department { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string Address { get; set; } = "";
    public DateOnly? DateOfBirth { get; set; }
    public DateOnly HireDate { get; set; }
    public DateOnly? LeftDate { get; set; }
    public EmployeeStatus Status { get; set; } = EmployeeStatus.Active;

    // Statutory numbers
    public string Nrc { get; set; } = "";
    public string Tpin { get; set; } = "";
    public string NapsaNumber { get; set; } = "";
    public string NhimaNumber { get; set; } = "";

    // How they're paid
    public PayMethod PayMethod { get; set; } = PayMethod.Bank;
    public string BankName { get; set; } = "";
    public string BankBranch { get; set; } = "";
    public string BankAccount { get; set; } = "";
    public string MobileMoneyNumber { get; set; } = "";
    public string Notes { get; set; } = "";

    public string Name => $"{FirstName} {LastName}".Trim();
}

public enum ContractType { Permanent, FixedTerm, Casual }

public static class ContractTypes
{
    public static string Label(this ContractType t) => t switch
    {
        ContractType.FixedTerm => "Fixed term",
        _ => t.ToString(),
    };
}

/// <summary>Terms of employment: pay, hours and leave. A new contract replaces the previous one from its start date.</summary>
public class Contract
{
    public long Id { get; set; }
    public long EmployeeId { get; set; }
    public ContractType Type { get; set; } = ContractType.Permanent;
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public decimal BasicPay { get; set; }
    public decimal HousingAllowance { get; set; }
    public decimal TransportAllowance { get; set; }
    public decimal OtherAllowance { get; set; }
    public decimal HoursPerWeek { get; set; } = 45;

    /// <summary>Annual leave days a year. The Employment Code Act 2019 minimum is 2 days a month (24 a year).</summary>
    public decimal LeaveDaysPerYear { get; set; } = 24;
    public string Notes { get; set; } = "";

    public decimal Allowances => HousingAllowance + TransportAllowance + OtherAllowance;
    public decimal MonthlyPay => BasicPay + Allowances;

    public bool CoversDay(DateOnly d) => d >= StartDate && (EndDate is null || d <= EndDate);
}

public enum LeaveType { Annual, Sick, Maternity, Paternity, Compassionate, FamilyResponsibility, Unpaid }

public enum LeaveStatus { Pending, Approved, Rejected, Cancelled }

public record LeaveRequest(long Id, long EmployeeId, string EmployeeName, LeaveType Type, DateOnly Start, DateOnly End,
    decimal Days, string Reason, LeaveStatus Status, string RequestedBy, string? DecidedBy, string DecisionNote);

public enum AttendanceStatus { Present, Late, Absent, OnLeave, DayOff }

public record AttendanceRecord(long EmployeeId, DateOnly Date, AttendanceStatus Status, decimal HoursWorked, decimal OvertimeHours, string Note);

public static class HrRules
{
    public const decimal OvertimeMultiplier = 1.5m;

    public static string Label(this LeaveType t) => t switch
    {
        LeaveType.FamilyResponsibility => "Family responsibility",
        _ => t.ToString(),
    };

    /// <summary>Standard entitlements under the Employment Code Act 2019, for guidance on the leave screens.</summary>
    public static string Entitlement(this LeaveType t) => t switch
    {
        LeaveType.Annual => "2 days per month worked",
        LeaveType.Maternity => "14 weeks, paid",
        LeaveType.Paternity => "5 working days, paid",
        LeaveType.Compassionate => "12 days a year",
        LeaveType.FamilyResponsibility => "7 days a year",
        LeaveType.Sick => "With a medical certificate",
        _ => "Not paid: deducted from pay",
    };

    public static string Label(this AttendanceStatus s) => s switch
    {
        AttendanceStatus.OnLeave => "On leave",
        AttendanceStatus.DayOff => "Day off",
        _ => s.ToString(),
    };

    /// <summary>Monday to Friday. Public holidays aren't counted out yet.</summary>
    public static bool IsWorkingDay(DateOnly d) => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    public static int WorkingDays(DateOnly from, DateOnly to)
    {
        var n = 0;
        for (var d = from; d <= to; d = d.AddDays(1)) if (IsWorkingDay(d)) n++;
        return n;
    }

    public static (DateOnly First, DateOnly Last) Month(int year, int month)
    {
        var first = new DateOnly(year, month, 1);
        return (first, first.AddMonths(1).AddDays(-1));
    }

    /// <summary>Whole months between two dates (e.g. 15 Jan → 14 Mar = 1, → 15 Mar = 2).</summary>
    public static int CompletedMonths(DateOnly from, DateOnly to)
    {
        if (to < from) return 0;
        var months = (to.Year - from.Year) * 12 + to.Month - from.Month;
        if (to.Day < from.Day) months--;
        return Math.Max(months, 0);
    }

    /// <summary>Annual leave earned so far (pro rata per completed month) less approved annual leave taken.</summary>
    public static decimal AnnualLeaveBalance(DateOnly hireDate, DateOnly asAt, decimal daysPerYear, decimal daysTaken) =>
        Math.Round(CompletedMonths(hireDate, asAt) * daysPerYear / 12m, 1) - daysTaken;

    /// <summary>Basic pay for the part of the month the contract covers, by calendar days.</summary>
    public static decimal ProRatedBasic(Contract c, int year, int month)
    {
        var (first, last) = Month(year, month);
        var from = c.StartDate > first ? c.StartDate : first;
        var to = c.EndDate is { } end && end < last ? end : last;
        if (to < from) return 0;
        var days = to.DayNumber - from.DayNumber + 1;
        var inMonth = last.Day;
        return days == inMonth ? c.BasicPay : Money.Round(c.BasicPay * days / inMonth);
    }

    /// <summary>Pay per working day this month: basic ÷ working days in the month.</summary>
    public static decimal DailyRate(decimal basic, int year, int month)
    {
        var (first, last) = Month(year, month);
        return basic / WorkingDays(first, last);
    }

    /// <summary>Hourly rate: basic × 12 ÷ (52 weeks × hours per week).</summary>
    public static decimal HourlyRate(decimal basic, decimal hoursPerWeek) =>
        hoursPerWeek <= 0 ? 0 : basic * 12m / (52m * hoursPerWeek);

    public static decimal OvertimePay(decimal basic, decimal hoursPerWeek, decimal overtimeHours) =>
        Money.Round(HourlyRate(basic, hoursPerWeek) * OvertimeMultiplier * overtimeHours);

    /// <summary>Checks a leave request. Returns the problem or null.</summary>
    public static string? CheckLeave(LeaveType type, DateOnly start, DateOnly end, decimal annualBalance)
    {
        if (end < start) return "The leave can't end before it starts.";
        var days = WorkingDays(start, end);
        if (days == 0) return "Those dates are all weekend days.";
        if (type == LeaveType.Annual && days > annualBalance)
            return $"Only {annualBalance:0.#} day(s) of annual leave available; this request is {days}.";
        return null;
    }
}
