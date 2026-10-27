using WavePOS.Core.Models;

namespace WavePOS.Core.Security;

public enum Permission
{
    Sell,
    ViewCustomers,
    ManageCustomers,
    ProcessReturns,
    ViewInventory,
    /// <summary>Add products, change prices and costs, add product pictures.</summary>
    ManageInventory,
    AdjustStock,
    ManageStaff,
    ViewAuditLog,
    ManageSettings,
    /// <summary>Export or anonymise a customer's personal data (Data Protection Act requests).</summary>
    HandleDataRequests,
    /// <summary>See the books: chart of accounts, journals, financial reports, bank reconciliation.</summary>
    ViewAccounts,
    /// <summary>
    /// Post journals, edit the chart of accounts, reconcile the bank and lock periods. Also enter
    /// supplier bills, raise invoices and record payments (accounts staff, not buyers or sales staff).
    /// </summary>
    ManageAccounts,
    /// <summary>See suppliers, purchase orders and supplier bills.</summary>
    ViewPurchasing,
    /// <summary>Add suppliers and raise purchase orders.</summary>
    ManagePurchasing,
    /// <summary>Approve purchase orders over the approval limit.</summary>
    ApprovePurchases,
    /// <summary>See quotes, sales orders and customer invoices.</summary>
    ViewSalesOrders,
    /// <summary>Raise quotes and confirm sales orders.</summary>
    ManageSalesOrders,
    /// <summary>Set customer credit limits and confirm orders over a customer's limit.</summary>
    ManageCredit,
    /// <summary>See the staff directory (names, jobs, departments, leave) without pay or personal details.</summary>
    ViewEmployees,
    /// <summary>Record daily attendance and overtime, and request leave for staff.</summary>
    RecordAttendance,
    /// <summary>Add and edit employees and contracts (with personal and pay details) and approve leave.</summary>
    ManageEmployees,
    /// <summary>Prepare the monthly payroll and see payslips.</summary>
    RunPayroll,
    /// <summary>Approve payroll (posts it to the books) and change statutory rates.</summary>
    ApprovePayroll,
    /// <summary>Receive goods into stock against purchase orders (warehouse work).</summary>
    ReceiveGoods,
    /// <summary>Deliver sales orders out of stock (warehouse work).</summary>
    DeliverOrders,
    /// <summary>Switch modules on or off and manage admins. Super admin only.</summary>
    ManageModules,
}

/// <summary>
/// Who may do what. One place to change it, used by both the desktop and web apps. What a person can
/// actually do is their role's permissions limited to the modules the business has switched on
/// (see <see cref="Modules"/>).
/// </summary>
public static class Permissions
{
    private static readonly Dictionary<UserRole, HashSet<Permission>> Allowed = Build();

    private static Dictionary<UserRole, HashSet<Permission>> Build()
    {
        HashSet<Permission> cashier = [Permission.Sell, Permission.ViewCustomers, Permission.ViewInventory];

        // Sales: the till and selling on account to customers.
        HashSet<Permission> sales =
        [
            .. cashier, Permission.ManageCustomers, Permission.ViewSalesOrders, Permission.ManageSalesOrders,
        ];

        // Warehousing: stock in and out. Receives deliveries from suppliers and sends out customer orders.
        HashSet<Permission> warehousing =
        [
            Permission.ViewInventory, Permission.AdjustStock, Permission.ViewPurchasing, Permission.ReceiveGoods,
            Permission.ViewSalesOrders, Permission.DeliverOrders,
        ];

        // Operations manager: runs the shop floor, stock, buying and selling day to day, including approving
        // purchase orders. Doesn't see the books, pay or staff personal details.
        HashSet<Permission> operations =
        [
            .. sales, .. warehousing, Permission.ProcessReturns, Permission.ManageInventory,
            Permission.ManagePurchasing, Permission.ApprovePurchases, Permission.ViewEmployees, Permission.RecordAttendance,
            Permission.ViewAuditLog,
        ];

        // Accountants and bookkeepers keep the books (bills, invoices, payments, payroll) but don't sell,
        // place orders or manage staff.
        HashSet<Permission> accountant =
        [
            Permission.ViewAccounts, Permission.ManageAccounts, Permission.ViewAuditLog, Permission.ViewInventory, Permission.ViewCustomers,
            Permission.ViewPurchasing, Permission.ViewSalesOrders, Permission.ViewEmployees, Permission.RunPayroll,
        ];

        // Admin: everything except choosing modules and managing other admins.
        HashSet<Permission> admin = [.. Enum.GetValues<Permission>().Where(p => p != Permission.ManageModules)];
        HashSet<Permission> superAdmin = [.. Enum.GetValues<Permission>()];

        return new()
        {
            [UserRole.Cashier] = cashier,
            [UserRole.Sales] = sales,
            [UserRole.Warehousing] = warehousing,
            [UserRole.OperationsManager] = operations,
            [UserRole.Accountant] = accountant,
            [UserRole.Admin] = admin,
            [UserRole.SuperAdmin] = superAdmin,
        };
    }

    /// <summary>What the role allows, whatever modules are on.</summary>
    public static bool Can(this UserRole role, Permission permission) =>
        Allowed.TryGetValue(role, out var set) && set.Contains(permission);

    /// <summary>What the role allows within the modules the business has switched on.</summary>
    public static bool Can(this UserRole role, Permission permission, ModuleSet modules) =>
        role.Can(permission) && modules.Allows(permission);

    public static bool Can(this User user, Permission permission) => user.IsActive && user.Role.Can(permission);

    /// <summary>All roles allowed a permission.</summary>
    public static IEnumerable<UserRole> RolesFor(Permission permission) =>
        Enum.GetValues<UserRole>().Where(r => r.Can(permission));

    /// <summary>Where a person lands after signing in: the till if they sell, otherwise the first area they can use.</summary>
    public static string HomePage(this UserRole role, ModuleSet? modules = null)
    {
        var m = modules ?? ModuleSet.All;
        (Permission P, string Href)[] areas =
        [
            (Permission.Sell, "/"), (Permission.ViewAccounts, "/accounting"), (Permission.AdjustStock, "/inventory"),
            (Permission.ViewSalesOrders, "/sales"), (Permission.ViewPurchasing, "/purchasing"), (Permission.ViewInventory, "/inventory"),
            (Permission.ViewEmployees, "/hr"),
        ];
        return areas.FirstOrDefault(a => role.Can(a.P, m)).Href ?? "/start";
    }

    /// <summary>The overload kept for existing callers: assumes every module is on.</summary>
    public static string HomePage(this UserRole role) => role.HomePage(null);

    public static string Label(this UserRole role) => role switch
    {
        UserRole.SuperAdmin => "Super admin",
        UserRole.OperationsManager => "Operations manager",
        UserRole.Accountant => "Accountant / bookkeeper",
        _ => role.ToString(),
    };

    /// <summary>What each role is for, shown when choosing one.</summary>
    public static string Description(this UserRole role) => role switch
    {
        UserRole.SuperAdmin => "The business owner: everything, including switching modules on or off and managing admins.",
        UserRole.Admin => "Runs the business: everything except modules and other admins.",
        UserRole.OperationsManager => "Runs stock, buying and selling day to day; approves purchase orders and refunds.",
        UserRole.Accountant => "The books, bills, invoices, payments and payroll; no selling or ordering.",
        UserRole.Sales => "The till and selling on account: customers, quotes and sales orders.",
        UserRole.Warehousing => "Stock in and out: receives deliveries, sends out orders, counts and adjusts stock.",
        _ => "The till only: sells and looks up customers.",
    };

    /// <summary>Highest first, for lists and drop-downs.</summary>
    public static readonly IReadOnlyList<UserRole> Ordered =
        [UserRole.SuperAdmin, UserRole.Admin, UserRole.OperationsManager, UserRole.Accountant, UserRole.Sales, UserRole.Warehousing, UserRole.Cashier];

    /// <summary>Super admins and admins can only be created or changed by a super admin.</summary>
    public static bool IsAdminLevel(this UserRole role) => role is UserRole.SuperAdmin or UserRole.Admin;

    /// <summary>Whether someone with <paramref name="actor"/> may give or take away <paramref name="target"/>.</summary>
    public static bool CanAssign(this UserRole actor, UserRole target) =>
        actor == UserRole.SuperAdmin || (actor.Can(Permission.ManageStaff) && !target.IsAdminLevel());
}
