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
}

/// <summary>Who may do what. One place to change it, used by both the desktop and web apps.</summary>
public static class Permissions
{
    private static readonly Dictionary<Permission, UserRole> Minimum = new()
    {
        [Permission.Sell] = UserRole.Cashier,
        [Permission.ViewCustomers] = UserRole.Cashier,
        [Permission.ViewInventory] = UserRole.Cashier,
        [Permission.ManageCustomers] = UserRole.Supervisor,
        [Permission.ProcessReturns] = UserRole.Supervisor,
        [Permission.AdjustStock] = UserRole.Supervisor,
        [Permission.ManageInventory] = UserRole.Manager,
        [Permission.ManageStaff] = UserRole.Manager,
        [Permission.ViewAuditLog] = UserRole.Manager,
        [Permission.ManageSettings] = UserRole.Manager,
        [Permission.HandleDataRequests] = UserRole.Manager,
    };

    public static bool Can(this UserRole role, Permission permission) => role >= Minimum[permission];

    public static bool Can(this User user, Permission permission) => user.IsActive && user.Role.Can(permission);

    /// <summary>All roles allowed a permission (for web authorisation policies).</summary>
    public static IEnumerable<UserRole> RolesFor(Permission permission) =>
        Enum.GetValues<UserRole>().Where(r => r.Can(permission));
}
