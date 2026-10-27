using WavePOS.Core.Accounting;
using WavePOS.Core.HR;

namespace WavePOS.Core.Tests;

public class PayrollTests
{
    private static readonly PayrollRates Rates2026 = PayrollRates.For(new DateOnly(2026, 10, 31), PayrollRates.BuiltIn);

    [Theory]
    [InlineData(5100, 0)]
    [InlineData(5100.01, 0)]       // 20% of 1 ngwee rounds to nothing
    [InlineData(7100, 400)]        // 20% of 2,000
    [InlineData(8000, 670)]        // + 30% of 900
    [InlineData(9200, 1030)]       // + 30% of 2,100
    [InlineData(15000, 3176)]      // + 37% of 5,800
    [InlineData(0, 0)]
    public void Paye_taxes_each_slice_at_its_band_rate(double taxable, double expected) =>
        Assert.Equal((decimal)expected, PayrollCalculator.Paye((decimal)taxable, Rates2026.PayeBands));

    [Fact]
    public void A_typical_payslip()
    {
        var pay = PayrollCalculator.Calculate(new PayInput { Basic = 6000m, Allowances = 2000m }, Rates2026);

        Assert.Equal(8000m, pay.Gross);
        Assert.Equal(670m, pay.Paye);
        Assert.Equal(400m, pay.NapsaEmployee);
        Assert.Equal(400m, pay.NapsaEmployer);
        Assert.Equal(60m, pay.NhimaEmployee); // 1% of basic
        Assert.Equal(40m, pay.SkillsLevy);    // 0.5% of gross, employer only
        Assert.Equal(6870m, pay.Net);
        Assert.Equal(8000m + 400m + 60m + 40m, pay.EmployerCost);
    }

    [Fact]
    public void Napsa_stops_at_the_earnings_ceiling()
    {
        var pay = PayrollCalculator.Calculate(new PayInput { Basic = 50_000m }, Rates2026);
        Assert.Equal(1861.80m, pay.NapsaEmployee); // 5% of K37,236
        var pay2025 = PayrollCalculator.Calculate(new PayInput { Basic = 50_000m }, PayrollRates.For(new DateOnly(2025, 6, 30), PayrollRates.BuiltIn));
        Assert.Equal(1708.20m, pay2025.NapsaEmployee); // 5% of K34,164
    }

    [Fact]
    public void Rate_settings_change_the_tax_base()
    {
        var rates = Rates2026 with { NapsaReducesTaxablePay = true, NhimaOnBasicPay = false };
        var pay = PayrollCalculator.Calculate(new PayInput { Basic = 6000m, Allowances = 2000m }, rates);
        Assert.Equal(7600m, pay.Taxable);
        Assert.Equal(550m, pay.Paye); // 400 + 30% of 500
        Assert.Equal(80m, pay.NhimaEmployee); // 1% of gross
    }

    [Fact]
    public void Unpaid_days_bonus_and_deductions()
    {
        var pay = PayrollCalculator.Calculate(new PayInput
        {
            Basic = 6000m, OvertimePay = 200m, Bonus = 300m, UnpaidDeduction = 1000m, OtherDeductions = 250m,
        }, Rates2026);
        Assert.Equal(5500m, pay.Gross);
        Assert.Equal(50m, pay.NhimaEmployee); // 1% of basic actually earned (5,000)
        Assert.Equal(250m, pay.OtherDeductions);
        Assert.Equal(5500m - 80m - 275m - 50m - 250m, pay.Net);

        Assert.Throws<ArgumentException>(() => PayrollCalculator.Calculate(new PayInput { Basic = 1000m, OtherDeductions = 2000m }, Rates2026));
        Assert.Throws<ArgumentException>(() => PayrollCalculator.Calculate(new PayInput { Basic = 1000m, UnpaidDeduction = 1500m }, Rates2026));
    }

    [Fact]
    public void Rates_in_force_are_the_latest_table_starting_on_or_before_the_date()
    {
        Assert.Equal(34_164m, PayrollRates.For(new DateOnly(2025, 12, 31), PayrollRates.BuiltIn).NapsaEarningsCeiling);
        Assert.Equal(37_236m, PayrollRates.For(new DateOnly(2026, 1, 1), PayrollRates.BuiltIn).NapsaEarningsCeiling);
        Assert.All(PayrollRates.BuiltIn, r => Assert.Null(r.Check()));
        Assert.NotNull((Rates2026 with { PayeBands = [new(5000m, 0m)] }).Check()); // last band must be open-ended
        Assert.Equal(Rates2026.NapsaEarningsCeiling, PayrollRates.FromJson(Rates2026.ToJson()).NapsaEarningsCeiling);
    }

    [Fact]
    public void Working_days_months_and_pro_rata_pay()
    {
        Assert.Equal(22, HrRules.WorkingDays(new(2026, 9, 1), new(2026, 9, 30)));
        Assert.Equal(0, HrRules.WorkingDays(new(2026, 10, 24), new(2026, 10, 25))); // a weekend
        Assert.Equal(8, HrRules.CompletedMonths(new(2026, 1, 1), new(2026, 9, 30)));
        Assert.Equal(1, HrRules.CompletedMonths(new(2026, 1, 15), new(2026, 3, 14)));

        var joinedMidMonth = new Contract { StartDate = new(2026, 9, 16), BasicPay = 3000m };
        Assert.Equal(1500m, HrRules.ProRatedBasic(joinedMidMonth, 2026, 9));   // 15 of 30 days
        Assert.Equal(3000m, HrRules.ProRatedBasic(joinedMidMonth, 2026, 10));
        Assert.Equal(0m, HrRules.ProRatedBasic(joinedMidMonth, 2026, 8));
    }

    [Fact]
    public void Overtime_is_time_and_a_half_of_the_hourly_rate()
    {
        // K6,000 × 12 ÷ (52 × 45 hours) = K30.769 an hour; × 1.5 × 4 hours
        Assert.Equal(184.62m, HrRules.OvertimePay(6000m, 45m, 4m));
        Assert.Equal(0m, HrRules.OvertimePay(6000m, 45m, 0m));
    }

    [Fact]
    public void Annual_leave_is_two_days_a_month_less_what_was_taken()
    {
        Assert.Equal(16m, HrRules.AnnualLeaveBalance(new(2026, 1, 1), new(2026, 9, 30), 24m, 0m));
        Assert.Equal(11m, HrRules.AnnualLeaveBalance(new(2026, 1, 1), new(2026, 9, 30), 24m, 5m));
        Assert.Contains("Only 3", HrRules.CheckLeave(LeaveType.Annual, new(2026, 10, 5), new(2026, 10, 9), 3m));
        Assert.Null(HrRules.CheckLeave(LeaveType.Sick, new(2026, 10, 5), new(2026, 10, 9), 0m));
        Assert.NotNull(HrRules.CheckLeave(LeaveType.Annual, new(2026, 10, 9), new(2026, 10, 5), 10m));
        Assert.NotNull(HrRules.CheckLeave(LeaveType.Sick, new(2026, 10, 24), new(2026, 10, 25), 10m));
    }

    [Fact]
    public void Payroll_posting_balances_and_splits_what_is_owed()
    {
        var ids = new Dictionary<string, long>();
        long Acct(string k) => ids.TryGetValue(k, out var id) ? id : ids[k] = ids.Count + 1;
        decimal Net(JournalDraft e, string k) => e.Lines.Where(l => l.AccountId == Acct(k)).Sum(l => l.Debit - l.Credit);

        var slips = new[]
        {
            PayrollCalculator.Calculate(new PayInput { Basic = 6000m, Allowances = 2000m }, Rates2026),
            PayrollCalculator.Calculate(new PayInput { Basic = 3000m, OtherDeductions = 500m }, Rates2026),
        };
        var e = PostingRules.Payroll(1, "2026-09", new(2026, 9, 30), slips, Acct);

        Assert.Null(JournalValidator.Check(e));
        Assert.Equal(11000m, Net(e, SystemAccounts.Salaries));
        Assert.Equal(-670m, Net(e, SystemAccounts.PayePayable));
        Assert.Equal(-1100m, Net(e, SystemAccounts.NapsaPayable));  // (400 + 150) × 2
        Assert.Equal(-180m, Net(e, SystemAccounts.NhimaPayable));   // (60 + 30) × 2
        Assert.Equal(-55m, Net(e, SystemAccounts.SkillsLevyPayable));
        Assert.Equal(-500m, Net(e, SystemAccounts.StaffAdvances));
        Assert.Equal(-(6870m + 2320m), Net(e, SystemAccounts.WagesPayable)); // 3000 − 150 − 30 − 500, no PAYE

        var paid = PostingRules.PayWages(1, "2026-09", new(2026, 10, 1), 9190m, 99, Acct);
        Assert.Equal(9190m, Net(paid, SystemAccounts.WagesPayable));
        Assert.True(JournalSource.Payroll.IsAutomatic());
    }
}
