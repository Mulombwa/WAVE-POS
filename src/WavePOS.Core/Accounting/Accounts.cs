namespace WavePOS.Core.Accounting;

public enum AccountType
{
    Asset,
    Liability,
    Equity,
    Income,
    Expense,
}

public static class AccountTypes
{
    /// <summary>Assets and expenses grow with debits; liabilities, equity and income grow with credits.</summary>
    public static bool IsDebitNormal(this AccountType type) => type is AccountType.Asset or AccountType.Expense;

    /// <summary>Income and expenses make up profit; the rest carry forward on the balance sheet.</summary>
    public static bool IsProfitAndLoss(this AccountType type) => type is AccountType.Income or AccountType.Expense;

    public static string Plural(this AccountType type) => type switch
    {
        AccountType.Asset => "Assets",
        AccountType.Liability => "Liabilities",
        AccountType.Equity => "Equity",
        AccountType.Income => "Income",
        _ => "Expenses",
    };
}

/// <summary>An account in the chart of accounts.</summary>
public class Account
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public AccountType Type { get; set; }

    /// <summary>Set for accounts the system posts to automatically (see <see cref="SystemAccounts"/>). They can't be removed.</summary>
    public string? SystemKey { get; set; }

    /// <summary>A bank account or mobile-money wallet whose statements can be imported and reconciled.</summary>
    public bool IsBank { get; set; }
    public bool IsActive { get; set; } = true;
    public string Description { get; set; } = "";

    public bool IsSystem => SystemKey is not null;

    /// <summary>Expense accounts numbered 5000–5999 are cost of sales (they sit above gross profit).</summary>
    public bool IsCostOfSales => Type == AccountType.Expense && Code.StartsWith('5');

    public string Display => $"{Code} · {Name}";
}

/// <summary>Accounts the system posts to automatically. Found by key, so they can be renamed or renumbered.</summary>
public static class SystemAccounts
{
    public const string Cash = "cash";
    public const string Bank = "bank";
    public const string MobileMoney = "mobile_money";
    public const string CardClearing = "card_clearing";
    public const string Receivables = "receivables";
    public const string Inventory = "inventory";
    public const string VatInput = "vat_input";
    public const string Payables = "payables";
    public const string VatOutput = "vat_output";
    public const string OwnerCapital = "owner_capital";
    public const string OpeningBalances = "opening_balances";
    public const string RetainedEarnings = "retained_earnings";
    public const string Sales = "sales";
    public const string SalesReturns = "sales_returns";
    public const string CostOfSales = "cost_of_sales";
    public const string StockLosses = "stock_losses";
    public const string BankCharges = "bank_charges";

    /// <summary>Where the money from a sale goes, by how the customer paid.</summary>
    public static string ForPayment(Models.PaymentMethod method) => method switch
    {
        Models.PaymentMethod.Card => CardClearing,
        Models.PaymentMethod.MobileMoney => MobileMoney,
        _ => Cash,
    };
}

public record DefaultAccount(string Code, string Name, AccountType Type, string? Key = null, bool IsBank = false, string Description = "");

/// <summary>The starting chart of accounts for a Zambian VAT-registered retailer. Businesses can add their own.</summary>
public static class DefaultChart
{
    public static readonly IReadOnlyList<DefaultAccount> Accounts =
    [
        new("1000", "Cash on hand", AccountType.Asset, SystemAccounts.Cash, Description: "Cash in the tills and safe."),
        new("1010", "Bank account", AccountType.Asset, SystemAccounts.Bank, IsBank: true, Description: "Main business bank account."),
        new("1020", "Mobile money wallet", AccountType.Asset, SystemAccounts.MobileMoney, IsBank: true, Description: "Airtel Money, MTN MoMo or Zamtel Kwacha takings."),
        new("1030", "Card payments clearing", AccountType.Asset, SystemAccounts.CardClearing, Description: "Card sales waiting to be paid into the bank."),
        new("1100", "Accounts receivable", AccountType.Asset, SystemAccounts.Receivables, Description: "Money customers owe the business."),
        new("1200", "Inventory", AccountType.Asset, SystemAccounts.Inventory, Description: "Stock on hand at cost."),
        new("1300", "VAT input (claimable)", AccountType.Asset, SystemAccounts.VatInput, Description: "VAT paid on purchases, claimed back from ZRA."),
        new("1500", "Equipment & fittings", AccountType.Asset),
        new("2000", "Accounts payable", AccountType.Liability, SystemAccounts.Payables, Description: "Money owed to suppliers."),
        new("2100", "VAT output (owed to ZRA)", AccountType.Liability, SystemAccounts.VatOutput, Description: "VAT charged on sales."),
        new("2200", "PAYE & NAPSA payable", AccountType.Liability),
        new("2300", "Loans", AccountType.Liability),
        new("3000", "Owner's capital", AccountType.Equity, SystemAccounts.OwnerCapital),
        new("3100", "Owner's drawings", AccountType.Equity),
        new("3200", "Opening balances", AccountType.Equity, SystemAccounts.OpeningBalances, Description: "Balances brought in when the books were started."),
        new("3300", "Retained earnings", AccountType.Equity, SystemAccounts.RetainedEarnings),
        new("4000", "Sales", AccountType.Income, SystemAccounts.Sales, Description: "Till sales, excluding VAT."),
        new("4100", "Sales returns & refunds", AccountType.Income, SystemAccounts.SalesReturns, Description: "Refunds, excluding VAT."),
        new("4200", "Other income", AccountType.Income),
        new("5000", "Cost of sales", AccountType.Expense, SystemAccounts.CostOfSales, Description: "Cost price of goods sold."),
        new("5100", "Stock losses & adjustments", AccountType.Expense, SystemAccounts.StockLosses, Description: "Damaged, expired, stolen and stock-count differences."),
        new("6000", "Rent", AccountType.Expense),
        new("6010", "Salaries & wages", AccountType.Expense),
        new("6020", "Electricity & water", AccountType.Expense),
        new("6030", "Phone & internet", AccountType.Expense),
        new("6040", "Transport & fuel", AccountType.Expense),
        new("6050", "Bank & mobile money charges", AccountType.Expense, SystemAccounts.BankCharges),
        new("6060", "Repairs & maintenance", AccountType.Expense),
        new("6070", "Marketing", AccountType.Expense),
        new("6080", "Licences & council levies", AccountType.Expense),
        new("6900", "Other expenses", AccountType.Expense),
    ];
}
