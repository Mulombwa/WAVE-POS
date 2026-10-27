using WavePOS.Core.Models;

namespace WavePOS.Core.Security;

public enum Permission
{
    Sell,
    ViewCustomers,
    ManageCustomers,
    ProcessReturns,
    ViewInventory,
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
    /// supplier bills, raise tax invoices and record payments (accounts staff, not buyers or sales staff).
    /// </summary>
    ManageAccounts,
    /// <summary>See suppliers, purchase orders and supplier bills.</summary>
    ViewPurchasing,
    /// <summary>Add suppliers, raise purchase orders and receive goods.</summary>
    ManagePurchasing,
    /// <summary>Approve purchase orders over the approval limit.</summary>
    ApprovePurchases,
    /// <summary>See quotes, sales orders and customer invoices.</summary>
    ViewSalesOrders,
    /// <summary>Raise quotes, confirm sales orders and deliver goods.</summary>
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
}

/// <summary>Who may do what. One place to change it, used by both the desktop and web apps.</summary>
public static class Permissions
{
    private static readonly Dictionary<UserRole, HashSet<Permission>> Allowed = Build();

    private static Dictionary<UserRole, HashSet<Permission>> Build()
    {
        HashSet<Permission> cashier = [Permission.Sell, Permission.ViewCustomers, Permission.ViewInventory];
        // Supervisors buy and sell on account (raise orders, receive and deliver goods); they don't approve
        // their own big purchases, set credit limits or handle the money side.
        HashSet<Permission> supervisor =
        [
            .. cashier, Permission.ManageCustomers, Permission.ProcessReturns, Permission.AdjustStock,
            Permission.ViewPurchasing, Permission.ManagePurchasing, Permission.ViewSalesOrders, Permission.ManageSalesOrders,
            Permission.ViewEmployees, Permission.RecordAttendance,
        ];
        HashSet<Permission> manager = [.. Enum.GetValues<Permission>()];
        // Accountants and bookkeepers keep the books (bills, invoices, payments) but don't ring up sales,
        // place orders or manage staff.
        HashSet<Permission> accountant =
        [
            Permission.ViewAccounts, Permission.ManageAccounts, Permission.ViewAuditLog, Permission.ViewInventory, Permission.ViewCustomers,
            Permission.ViewPurchasing, Permission.ViewSalesOrders, Permission.ViewEmployees, Permission.RunPayroll,
        ];

        return new()
        {
            [UserRole.Cashier] = cashier,
            [UserRole.Supervisor] = supervisor,
            [UserRole.Manager] = manager,
            [UserRole.Accountant] = accountant,
        };
    }

    public static bool Can(this UserRole role, Permission permission) =>
        Allowed.TryGetValue(role, out var set) && set.Contains(permission);

    public static bool Can(this User user, Permission permission) => user.IsActive && user.Role.Can(permission);

    /// <summary>All roles allowed a permission (for web authorisation policies).</summary>
    public static IEnumerable<UserRole> RolesFor(Permission permission) =>
        Enum.GetValues<UserRole>().Where(r => r.Can(permission));

    /// <summary>Where a role lands after signing in: the till, or the books for accountants.</summary>
    public static string HomePage(this UserRole role) => role.Can(Permission.Sell) ? "/" : "/accounting";

    public static string Label(this UserRole role) => role == UserRole.Accountant ? "Accountant / bookkeeper" : role.ToString();
}
