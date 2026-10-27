using System.Text.Json;

namespace WavePOS.Core.HR;

/// <summary>One PAYE band: income up to <see cref="UpTo"/> (monthly; null = no upper limit) is taxed at <see cref="Rate"/>.</summary>
public record TaxBand(decimal? UpTo, decimal Rate);

/// <summary>
/// Zambian statutory payroll rates in force from a date. Rates change with each national budget and
/// NAPSA's yearly ceiling review, so they're kept as dated tables a manager can add to.
/// </summary>
public record PayrollRates
{
    public DateOnly EffectiveFrom { get; init; }
    public List<TaxBand> PayeBands { get; init; } = [];

    /// <summary>NAPSA pension: employee and employer each pay this share of gross pay, up to the earnings ceiling.</summary>
    public decimal NapsaRate { get; init; } = 0.05m;
    public decimal NapsaEarningsCeiling { get; init; }

    /// <summary>NHIMA health insurance: employee and employer each pay this share.</summary>
    public decimal NhimaRate { get; init; } = 0.01m;

    /// <summary>True: NHIMA on basic pay. False: on gross pay.</summary>
    public bool NhimaOnBasicPay { get; init; } = true;

    /// <summary>Skills Development Levy, employer only, on gross pay.</summary>
    public decimal SkillsLevyRate { get; init; } = 0.005m;

    /// <summary>True: the employee's NAPSA is taken off gross pay before PAYE is worked out.</summary>
    public bool NapsaReducesTaxablePay { get; init; }

    public string Source { get; init; } = "";

    public string ToJson() => JsonSerializer.Serialize(this);
    public static PayrollRates FromJson(string json) => JsonSerializer.Deserialize<PayrollRates>(json)!;

    /// <summary>Checks the table makes sense. Returns the problem or null.</summary>
    public string? Check()
    {
        if (PayeBands.Count == 0) return "Add at least one PAYE band.";
        if (PayeBands[^1].UpTo is not null) return "The last PAYE band must have no upper limit.";
        for (var i = 0; i < PayeBands.Count; i++)
        {
            if (PayeBands[i].Rate is < 0 or > 1) return "PAYE rates must be between 0% and 100%.";
            if (i > 0 && PayeBands[i - 1].UpTo is { } prev && PayeBands[i].UpTo is { } cur && cur <= prev) return "PAYE bands must go up in order.";
        }
        if (NapsaRate is < 0 or > 1 || NhimaRate is < 0 or > 1 || SkillsLevyRate is < 0 or > 1) return "Rates must be between 0% and 100%.";
        if (NapsaEarningsCeiling <= 0) return "Enter the NAPSA earnings ceiling.";
        return null;
    }

    private static readonly List<TaxBand> Bands2025 = [new(5100m, 0m), new(7100m, 0.20m), new(9200m, 0.30m), new(null, 0.37m)];

    /// <summary>
    /// Built-in tables. PAYE bands per ZRA (exempt K5,100/month, top rate 37% above K9,200).
    /// NAPSA ceiling K34,164 (2025) and K37,236 (from 1 Jan 2026). Check these against ZRA and
    /// NAPSA notices and add a new table when they change.
    /// </summary>
    public static readonly IReadOnlyList<PayrollRates> BuiltIn =
    [
        new() { EffectiveFrom = new(2025, 1, 1), PayeBands = Bands2025, NapsaEarningsCeiling = 34_164m, Source = "Built in: ZRA PAYE bands 2025, NAPSA ceiling 2025" },
        new() { EffectiveFrom = new(2026, 1, 1), PayeBands = Bands2025, NapsaEarningsCeiling = 37_236m, Source = "Built in: ZRA PAYE bands, NAPSA ceiling 2026" },
    ];

    /// <summary>The table in force on a date: the latest one starting on or before it.</summary>
    public static PayrollRates For(DateOnly date, IEnumerable<PayrollRates> tables) =>
        tables.Where(t => t.EffectiveFrom <= date).OrderByDescending(t => t.EffectiveFrom).FirstOrDefault()
        ?? tables.OrderBy(t => t.EffectiveFrom).First();
}

/// <summary>What goes into one employee's pay for a month.</summary>
public record PayInput
{
    /// <summary>Basic pay for the month (already pro-rated if they joined or left part-way).</summary>
    public decimal Basic { get; init; }
    /// <summary>Taxable allowances: housing, transport, lunch and others.</summary>
    public decimal Allowances { get; init; }
    public decimal OvertimePay { get; init; }
    public decimal Bonus { get; init; }
    /// <summary>Pay taken off for unpaid leave and unauthorised absence.</summary>
    public decimal UnpaidDeduction { get; init; }
    /// <summary>After-tax deductions, e.g. a salary advance or loan repayment.</summary>
    public decimal OtherDeductions { get; init; }
}

/// <summary>A worked-out payslip. All amounts in kwacha, rounded to the ngwee.</summary>
public record PayResult
{
    public decimal Gross { get; init; }
    public decimal Taxable { get; init; }
    public decimal Paye { get; init; }
    public decimal NapsaEmployee { get; init; }
    public decimal NapsaEmployer { get; init; }
    public decimal NhimaEmployee { get; init; }
    public decimal NhimaEmployer { get; init; }
    public decimal SkillsLevy { get; init; }
    public decimal OtherDeductions { get; init; }

    public decimal TotalDeductions => Paye + NapsaEmployee + NhimaEmployee + OtherDeductions;
    public decimal Net => Gross - TotalDeductions;
    public decimal EmployerCost => Gross + NapsaEmployer + NhimaEmployer + SkillsLevy;
}

public static class PayrollCalculator
{
    /// <summary>PAYE on a month's taxable pay: each band's slice at that band's rate.</summary>
    public static decimal Paye(decimal taxable, IReadOnlyList<TaxBand> bands)
    {
        decimal tax = 0, lower = 0;
        foreach (var band in bands)
        {
            if (taxable <= lower) break;
            var top = band.UpTo ?? decimal.MaxValue;
            tax += (Math.Min(taxable, top) - lower) * band.Rate;
            lower = top;
        }
        return Money.Round(tax);
    }

    public static PayResult Calculate(PayInput input, PayrollRates rates)
    {
        if (input.Basic < 0 || input.Allowances < 0 || input.OvertimePay < 0 || input.Bonus < 0 || input.UnpaidDeduction < 0 || input.OtherDeductions < 0)
            throw new ArgumentException("Pay amounts can't be negative.");

        var gross = Money.Round(input.Basic + input.Allowances + input.OvertimePay + input.Bonus - input.UnpaidDeduction);
        if (gross < 0) throw new ArgumentException("Unpaid days come to more than the pay for the month.");

        var napsa = Money.Round(Math.Min(gross, rates.NapsaEarningsCeiling) * rates.NapsaRate);
        var basicEarned = Math.Max(input.Basic - input.UnpaidDeduction, 0);
        var nhima = Money.Round((rates.NhimaOnBasicPay ? basicEarned : gross) * rates.NhimaRate);
        var taxable = rates.NapsaReducesTaxablePay ? gross - napsa : gross;

        var result = new PayResult
        {
            Gross = gross,
            Taxable = taxable,
            Paye = Paye(taxable, rates.PayeBands),
            NapsaEmployee = napsa,
            NapsaEmployer = napsa,
            NhimaEmployee = nhima,
            NhimaEmployer = nhima,
            SkillsLevy = Money.Round(gross * rates.SkillsLevyRate),
            OtherDeductions = Money.Round(input.OtherDeductions),
        };
        if (result.Net < 0) throw new ArgumentException("Deductions come to more than the pay. Reduce the other deductions.");
        return result;
    }
}
