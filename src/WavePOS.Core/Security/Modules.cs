namespace WavePOS.Core.Security;

/// <summary>Parts of WAVE POS a business can switch on or off.</summary>
public enum Module
{
    Till,
    Inventory,
    Customers,
    Returns,
    Accounting,
    Purchasing,
    SalesOrders,
    HR,
}

/// <summary>How big the business is. Picks a starting set of modules, which the super admin can change.</summary>
public enum BusinessSize
{
    /// <summary>Market stalls, salons, small shops: sell and keep track of stock.</summary>
    Informal,
    /// <summary>A registered shop or small company: adds customers, refunds and the books.</summary>
    Small,
    /// <summary>A larger organisation: adds purchasing, sales orders and HR &amp; payroll.</summary>
    Large,
}

/// <summary>The modules switched on for a business.</summary>
public sealed record ModuleSet(IReadOnlySet<Module> On)
{
    public static readonly ModuleSet All = new(Enum.GetValues<Module>().ToHashSet());

    /// <summary>The till and stock are the core of WAVE POS and can't be switched off.</summary>
    public static readonly IReadOnlySet<Module> AlwaysOn = new HashSet<Module> { Module.Till, Module.Inventory };

    public static ModuleSet ForSize(BusinessSize size) => size switch
    {
        BusinessSize.Informal => new(new HashSet<Module> { Module.Till, Module.Inventory }),
        BusinessSize.Small => new(new HashSet<Module> { Module.Till, Module.Inventory, Module.Customers, Module.Returns, Module.Accounting }),
        _ => All,
    };

    public bool Has(Module m) => AlwaysOn.Contains(m) || On.Contains(m);

    /// <summary>
    /// Whether a permission's module is on. Staff, settings and the audit trail belong to no module and
    /// are always available.
    /// </summary>
    public bool Allows(Permission p) => ModuleOf(p) is not { } m || Has(m);

    public static Module? ModuleOf(Permission p) => p switch
    {
        Permission.Sell => Module.Till,
        Permission.ViewInventory or Permission.ManageInventory or Permission.AdjustStock => Module.Inventory,
        Permission.ViewCustomers or Permission.ManageCustomers or Permission.HandleDataRequests => Module.Customers,
        Permission.ProcessReturns => Module.Returns,
        Permission.ViewAccounts or Permission.ManageAccounts => Module.Accounting,
        Permission.ViewPurchasing or Permission.ManagePurchasing or Permission.ApprovePurchases or Permission.ReceiveGoods => Module.Purchasing,
        Permission.ViewSalesOrders or Permission.ManageSalesOrders or Permission.ManageCredit or Permission.DeliverOrders => Module.SalesOrders,
        Permission.ViewEmployees or Permission.RecordAttendance or Permission.ManageEmployees or Permission.RunPayroll or Permission.ApprovePayroll => Module.HR,
        _ => null,
    };

    /// <summary>Saved as a comma-separated list, e.g. "Till,Inventory,Customers".</summary>
    public override string ToString() => string.Join(',', Enum.GetValues<Module>().Where(Has));

    public static ModuleSet Parse(string? text) => string.IsNullOrWhiteSpace(text)
        ? All
        : new(text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Enum.TryParse<Module>(s, out var m) ? m : (Module?)null).OfType<Module>().ToHashSet());

    public ModuleSet With(Module m, bool on)
    {
        var set = On.ToHashSet();
        if (on) set.Add(m); else if (!AlwaysOn.Contains(m)) set.Remove(m);
        return new(set);
    }

    /// <summary>Modules that need another to make sense (e.g. purchasing posts bills to the books).</summary>
    public static IReadOnlyList<Module> Needs(Module m) => m switch
    {
        Module.Purchasing or Module.SalesOrders or Module.HR => [Module.Accounting],
        Module.Returns => [Module.Till],
        _ => [],
    };
}

public static class ModuleNames
{
    public static string Label(this Module m) => m switch
    {
        Module.SalesOrders => "Sales orders",
        Module.HR => "HR & payroll",
        Module.Returns => "Returns & refunds",
        _ => m.ToString(),
    };

    public static string Description(this Module m) => m switch
    {
        Module.Till => "Ring up sales by cash, card or mobile money; print receipts.",
        Module.Inventory => "Products with pictures, prices, sizes and colours; stock levels.",
        Module.Customers => "Customer records, purchase history and data-protection tools.",
        Module.Returns => "Refunds and exchanges against a receipt.",
        Module.Accounting => "Double-entry books, bank reconciliation, profit & loss, VAT.",
        Module.Purchasing => "Suppliers, purchase orders with approvals, goods received, bills.",
        Module.SalesOrders => "Quotes, sales orders with credit limits, deliveries, invoices.",
        _ => "Employees, contracts, leave, attendance and Zambian payroll.",
    };

    public static string Label(this BusinessSize s) => s switch
    {
        BusinessSize.Informal => "Informal business",
        BusinessSize.Small => "Small business",
        _ => "Large organisation",
    };

    public static string Description(this BusinessSize s) => s switch
    {
        BusinessSize.Informal => "A stall, salon, tuckshop or one-person business. Just sell and keep track of stock.",
        BusinessSize.Small => "A registered shop with a few staff. Adds customers, refunds and the books.",
        _ => "Several departments or branches. Everything, including purchasing, sales orders and HR & payroll.",
    };
}
