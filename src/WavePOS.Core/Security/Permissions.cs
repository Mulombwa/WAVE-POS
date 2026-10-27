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
    /// <summary>Post journals, edit the chart of accounts, reconcile the bank and lock periods.</summary>
    ManageAccounts,
}

/// <summary>Who may do what. One place to change it, used by both the desktop and web apps.</summary>
public static class Permissions
{
    private static readonly Dictionary<UserRole, HashSet<Permission>> Allowed = Build();

    private static Dictionary<UserRole, HashSet<Permission>> Build()
    {
        HashSet<Permission> cashier = [Permission.Sell, Permission.ViewCustomers, Permission.ViewInventory];
        HashSet<Permission> supervisor = [.. cashier, Permission.ManageCustomers, Permission.ProcessReturns, Permission.AdjustStock];
        HashSet<Permission> manager = [.. Enum.GetValues<Permission>()];
        // Accountants and bookkeepers keep the books but don't ring up sales or manage staff.
        HashSet<Permission> accountant =
            [Permission.ViewAccounts, Permission.ManageAccounts, Permission.ViewAuditLog, Permission.ViewInventory, Permission.ViewCustomers];

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
