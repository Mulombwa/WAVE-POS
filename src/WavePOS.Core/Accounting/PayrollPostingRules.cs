using WavePOS.Core.HR;

namespace WavePOS.Core.Accounting;

/// <summary>
/// Payroll postings. Approving a payroll records the cost (wages plus the employer's NAPSA,
/// NHIMA and skills levy) and what's now owed: tax and contributions to ZRA, NAPSA and NHIMA,
/// and net pay to staff. Paying wages then settles the net pay. The authorities are paid
/// later from the bank, and those payments are given the payable accounts on reconciliation.
/// </summary>
public static partial class PostingRules
{
    public static JournalDraft Payroll(long runId, string period, DateOnly date, IReadOnlyCollection<PayResult> slips, Func<string, long> account)
    {
        var e = New(date, $"Payroll {period}", $"Wages and statutory contributions for {period} ({slips.Count} employee(s))", JournalSource.Payroll, runId);
        Debit(e, account(SystemAccounts.Salaries), slips.Sum(s => s.Gross), "Gross pay");
        Debit(e, account(SystemAccounts.EmployerContributions), slips.Sum(s => s.NapsaEmployer + s.NhimaEmployer + s.SkillsLevy), "Employer NAPSA, NHIMA and skills levy");
        Credit(e, account(SystemAccounts.PayePayable), slips.Sum(s => s.Paye), "PAYE due to ZRA");
        Credit(e, account(SystemAccounts.NapsaPayable), slips.Sum(s => s.NapsaEmployee + s.NapsaEmployer), "NAPSA due");
        Credit(e, account(SystemAccounts.NhimaPayable), slips.Sum(s => s.NhimaEmployee + s.NhimaEmployer), "NHIMA due");
        Credit(e, account(SystemAccounts.SkillsLevyPayable), slips.Sum(s => s.SkillsLevy), "Skills development levy due to ZRA");
        Credit(e, account(SystemAccounts.StaffAdvances), slips.Sum(s => s.OtherDeductions), "Advances and loans recovered");
        Credit(e, account(SystemAccounts.WagesPayable), slips.Sum(s => s.Net), "Net pay owed to staff");
        return e;
    }

    public static JournalDraft PayWages(long runId, string period, DateOnly date, decimal net, long paidFromAccountId, Func<string, long> account)
    {
        var e = New(date, $"Wages paid {period}", $"Net pay for {period}", JournalSource.Payroll, runId);
        Debit(e, account(SystemAccounts.WagesPayable), net, "Net pay");
        Credit(e, paidFromAccountId, net, "Wages paid");
        return e;
    }
}
