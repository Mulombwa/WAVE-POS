namespace WavePOS.Core.Accounting;

/// <summary>Total debits and credits posted to an account over a period.</summary>
public record AccountBalance(Account Account, decimal Debit, decimal Credit)
{
    /// <summary>Debits minus credits.</summary>
    public decimal Net => Debit - Credit;

    /// <summary>The balance the usual way round for the account type (a positive bank balance, positive sales, …).</summary>
    public decimal Balance => Account.Type.IsDebitNormal() ? Debit - Credit : Credit - Debit;
}

public record ReportRow(Account Account, decimal Amount);

public record ReportSection(string Title, List<ReportRow> Rows)
{
    public decimal Total => Rows.Sum(r => r.Amount);
}

public record TrialBalanceRow(Account Account, decimal Debit, decimal Credit);

public record TrialBalance(DateOnly AsAt, List<TrialBalanceRow> Rows)
{
    public decimal TotalDebit => Rows.Sum(r => r.Debit);
    public decimal TotalCredit => Rows.Sum(r => r.Credit);
    public bool Balances => TotalDebit == TotalCredit;
}

public record ProfitAndLoss(DateOnly From, DateOnly To, ReportSection Income, ReportSection CostOfSales, ReportSection Expenses)
{
    public decimal GrossProfit => Income.Total - CostOfSales.Total;
    public decimal NetProfit => GrossProfit - Expenses.Total;

    /// <summary>Gross profit as a share of income, or null with no income.</summary>
    public decimal? GrossMargin => Income.Total == 0 ? null : GrossProfit / Income.Total;
}

public record BalanceSheet(DateOnly AsAt, ReportSection Assets, ReportSection Liabilities, ReportSection Equity, decimal ProfitToDate)
{
    public decimal TotalEquity => Equity.Total + ProfitToDate;
    public decimal LiabilitiesAndEquity => Liabilities.Total + TotalEquity;
    public bool Balances => Assets.Total == LiabilitiesAndEquity;
}

/// <summary>VAT for a ZRA return, from the ledger (output VAT on sales less input VAT on purchases).</summary>
public record VatSummary(DateOnly From, DateOnly To, decimal OutputVat, decimal InputVat,
    decimal StandardRatedSales, decimal ZeroRatedSales)
{
    /// <summary>Positive = pay ZRA; negative = claim a refund.</summary>
    public decimal NetPayable => OutputVat - InputVat;
}

/// <summary>Builds the standard financial reports from account balances.</summary>
public static class FinancialReports
{
    /// <summary>Every account with a balance, in the debit or credit column. Debits always equal credits.</summary>
    public static TrialBalance TrialBalance(DateOnly asAt, IEnumerable<AccountBalance> balances) => new(asAt,
        balances.Where(b => b.Net != 0)
            .OrderBy(b => b.Account.Code, StringComparer.Ordinal)
            .Select(b => new TrialBalanceRow(b.Account, b.Net > 0 ? b.Net : 0, b.Net < 0 ? -b.Net : 0))
            .ToList());

    /// <summary>
    /// Income less cost of sales gives gross profit; less other expenses gives net profit.
    /// Pass the balances for the period only.
    /// </summary>
    public static ProfitAndLoss ProfitAndLoss(DateOnly from, DateOnly to, IEnumerable<AccountBalance> periodBalances)
    {
        var list = periodBalances.Where(b => b.Net != 0).OrderBy(b => b.Account.Code, StringComparer.Ordinal).ToList();
        return new ProfitAndLoss(from, to,
            Section("Income", list.Where(b => b.Account.Type == AccountType.Income)),
            Section("Cost of sales", list.Where(b => b.Account.IsCostOfSales)),
            Section("Expenses", list.Where(b => b.Account.Type == AccountType.Expense && !b.Account.IsCostOfSales)));
    }

    /// <summary>
    /// What the business owns and owes on a date. Pass balances from the start of the books to that date.
    /// Profit not yet moved to retained earnings is shown as "Profit to date" under equity.
    /// </summary>
    public static BalanceSheet BalanceSheet(DateOnly asAt, IEnumerable<AccountBalance> balancesToDate)
    {
        var list = balancesToDate.Where(b => b.Net != 0).OrderBy(b => b.Account.Code, StringComparer.Ordinal).ToList();
        var profit = list.Where(b => b.Account.Type == AccountType.Income).Sum(b => b.Balance)
                   - list.Where(b => b.Account.Type == AccountType.Expense).Sum(b => b.Balance);
        return new BalanceSheet(asAt,
            Section("Assets", list.Where(b => b.Account.Type == AccountType.Asset)),
            Section("Liabilities", list.Where(b => b.Account.Type == AccountType.Liability)),
            Section("Equity", list.Where(b => b.Account.Type == AccountType.Equity)),
            profit);
    }

    private static ReportSection Section(string title, IEnumerable<AccountBalance> balances) =>
        new(title, balances.Select(b => new ReportRow(b.Account, b.Balance)).ToList());
}
