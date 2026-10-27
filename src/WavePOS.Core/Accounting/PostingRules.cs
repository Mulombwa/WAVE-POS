using WavePOS.Core.Documents;
using WavePOS.Core.Inventory;
using WavePOS.Core.Models;

namespace WavePOS.Core.Accounting;

/// <summary>
/// How shop activity turns into double-entry bookkeeping. Each rule builds a balanced entry;
/// <paramref name="account"/> looks up an account id from a <see cref="SystemAccounts"/> key.
/// </summary>
public static partial class PostingRules
{
    public const decimal StandardVatRate = 0.16m;

    /// <summary>
    /// A till sale: the money in (cash, card or mobile money), the sale excluding VAT, the VAT owed
    /// to ZRA, and the stock leaving the shelf at cost.
    /// </summary>
    public static JournalDraft Sale(long saleId, DateOnly date, PaymentMethod method, decimal total, decimal vat, decimal cost,
        Func<string, long> account)
    {
        var entry = New(date, $"Sale #{saleId:D6}", $"Till sale paid by {method.DisplayName()}", JournalSource.Sale, saleId);
        Debit(entry, account(SystemAccounts.ForPayment(method)), total, "Takings");
        Credit(entry, account(SystemAccounts.Sales), total - vat, "Sales excl. VAT");
        Credit(entry, account(SystemAccounts.VatOutput), vat, "VAT on sales");
        Debit(entry, account(SystemAccounts.CostOfSales), cost, "Cost of goods sold");
        Credit(entry, account(SystemAccounts.Inventory), cost, "Stock sold");
        return entry;
    }

    /// <summary>A refund: the reverse of a sale. Restocked items go back into inventory at their cost.</summary>
    public static JournalDraft Refund(long returnId, long saleId, DateOnly date, PaymentMethod method, decimal total, decimal vat,
        decimal restockedCost, Func<string, long> account)
    {
        var entry = New(date, $"Refund R{returnId:D6}", $"Refund against sale #{saleId:D6} by {method.DisplayName()}", JournalSource.Return, returnId);
        Debit(entry, account(SystemAccounts.SalesReturns), total - vat, "Refund excl. VAT");
        Debit(entry, account(SystemAccounts.VatOutput), vat, "VAT refunded");
        Credit(entry, account(SystemAccounts.ForPayment(method)), total, "Money refunded");
        Debit(entry, account(SystemAccounts.Inventory), restockedCost, "Back in stock");
        Credit(entry, account(SystemAccounts.CostOfSales), restockedCost, "Cost of returned goods");
        return entry;
    }

    /// <summary>
    /// A stock change other than a sale or refund, valued at cost. Deliveries are owed to the
    /// supplier, opening stock goes to opening balances, and anything else is a stock loss (or gain).
    /// Returns null when there is nothing to post (no cost price).
    /// </summary>
    public static JournalDraft? StockMovement(long productId, string productName, DateOnly date, StockReason reason, int change,
        decimal unitCost, Func<string, long> account)
    {
        if (reason is StockReason.Sale or StockReason.Return)
            throw new ArgumentException("Sales and refunds post their own stock entries.");
        var value = Money.Round(Math.Abs(change) * unitCost);
        if (value == 0) return null;

        var contraKey = reason switch
        {
            StockReason.Received => SystemAccounts.Payables,
            StockReason.InitialStock => SystemAccounts.OpeningBalances,
            _ => SystemAccounts.StockLosses,
        };
        var memo = $"{productName}: {change:+0;-0} ({reason}) at {Money.Format(unitCost)}";
        var entry = New(date, reason == StockReason.Received ? "Stock received" : "Stock adjustment", memo, JournalSource.Stock, productId);
        if (change > 0)
        {
            Debit(entry, account(SystemAccounts.Inventory), value, "Stock in");
            Credit(entry, account(contraKey), value, reason == StockReason.Received ? "Owed to supplier" : reason.ToString());
        }
        else
        {
            Debit(entry, account(contraKey), value, reason.ToString());
            Credit(entry, account(SystemAccounts.Inventory), value, "Stock out");
        }
        return entry;
    }

    /// <summary>
    /// Stock on hand is worth more (positive) or less (negative), e.g. after a cost price change
    /// or rounding on the average cost. The difference goes to stock losses &amp; adjustments.
    /// Null when there is no difference.
    /// </summary>
    public static JournalDraft? Revaluation(long productId, string productName, DateOnly date, decimal difference, string why,
        Func<string, long> account)
    {
        difference = Money.Round(difference);
        if (difference == 0) return null;
        var entry = New(date, "Stock revaluation", $"{productName}: {why}", JournalSource.Stock, productId);
        Debit(entry, account(SystemAccounts.Inventory), difference, "Revaluation");
        Credit(entry, account(SystemAccounts.StockLosses), difference, "Revaluation");
        return entry;
    }

    /// <summary>
    /// A bank or wallet statement line given an account. Money in is a debit to the bank;
    /// money out is a credit. With VAT included, 16/116 of the amount goes to VAT
    /// (input VAT on spending, output VAT on income).
    /// </summary>
    public static JournalDraft BankLine(long bankLineId, DateOnly date, string description, decimal amount, long bankAccountId,
        long otherAccountId, bool includesVat, Func<string, long> account)
    {
        if (amount == 0) throw new ArgumentException("This statement line has no amount.");
        var gross = Math.Abs(amount);
        var vat = includesVat ? VatIncluded(gross) : 0m;
        var entry = New(date, "Bank statement", description, JournalSource.Bank, bankLineId);
        if (amount > 0)
        {
            Debit(entry, bankAccountId, gross, description);
            Credit(entry, otherAccountId, gross - vat, description);
            Credit(entry, account(SystemAccounts.VatOutput), vat, "VAT");
        }
        else
        {
            Debit(entry, otherAccountId, gross - vat, description);
            Debit(entry, account(SystemAccounts.VatInput), vat, "VAT");
            Credit(entry, bankAccountId, gross, description);
        }
        return entry;
    }

    /// <summary>The VAT inside a VAT-inclusive amount at 16%: amount × 16 / 116.</summary>
    public static decimal VatIncluded(decimal grossAmount, decimal rate = StandardVatRate) =>
        Money.Round(grossAmount - grossAmount / (1 + rate));

    private static JournalDraft New(DateOnly date, string reference, string memo, JournalSource source, long sourceId) => new()
    {
        Date = date,
        Reference = reference,
        Memo = memo,
        Source = source,
        SourceId = sourceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    // Zero amounts are skipped, so e.g. a zero-rated sale has no VAT line.
    private static void Debit(JournalDraft e, long accountId, decimal amount, string description)
    {
        amount = Money.Round(amount);
        if (amount > 0) e.Lines.Add(new JournalLineDraft(accountId, amount, 0, description));
        else if (amount < 0) e.Lines.Add(new JournalLineDraft(accountId, 0, -amount, description));
    }

    private static void Credit(JournalDraft e, long accountId, decimal amount, string description) => Debit(e, accountId, -amount, description);
}
