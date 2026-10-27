using WavePOS.Core.Models;
using WavePOS.Core.Security;

namespace WavePOS.Core.Tests;

public class ModuleTests
{
    [Fact]
    public void Each_size_starts_with_the_right_modules()
    {
        var informal = ModuleSet.ForSize(BusinessSize.Informal);
        Assert.True(informal.Has(Module.Till));
        Assert.True(informal.Has(Module.Inventory));
        Assert.False(informal.Has(Module.Accounting));
        Assert.False(informal.Has(Module.HR));

        var small = ModuleSet.ForSize(BusinessSize.Small);
        Assert.True(small.Has(Module.Customers) && small.Has(Module.Returns) && small.Has(Module.Accounting));
        Assert.False(small.Has(Module.Purchasing));

        Assert.All(Enum.GetValues<Module>(), m => Assert.True(ModuleSet.ForSize(BusinessSize.Large).Has(m)));
    }

    [Fact]
    public void Till_and_stock_cant_be_switched_off()
    {
        var set = ModuleSet.All.With(Module.Till, false).With(Module.Inventory, false).With(Module.HR, false);
        Assert.True(set.Has(Module.Till));
        Assert.True(set.Has(Module.Inventory));
        Assert.False(set.Has(Module.HR));
    }

    [Fact]
    public void Modules_save_and_load_as_text()
    {
        var set = ModuleSet.ForSize(BusinessSize.Small).With(Module.HR, true);
        var loaded = ModuleSet.Parse(set.ToString());
        Assert.All(Enum.GetValues<Module>(), m => Assert.Equal(set.Has(m), loaded.Has(m)));
        Assert.True(ModuleSet.Parse("Till,Nonsense").Has(Module.Inventory)); // unknown names ignored, core stays on
    }

    [Fact]
    public void A_role_only_reaches_switched_on_modules()
    {
        var informal = ModuleSet.ForSize(BusinessSize.Informal);
        Assert.True(UserRole.SuperAdmin.Can(Permission.Sell, informal));
        Assert.False(UserRole.SuperAdmin.Can(Permission.ViewAccounts, informal));
        Assert.False(UserRole.SuperAdmin.Can(Permission.RunPayroll, informal));
        Assert.True(UserRole.SuperAdmin.Can(Permission.ManageStaff, informal));   // staff and settings belong to no module
        Assert.True(UserRole.SuperAdmin.Can(Permission.ManageModules, informal));
        Assert.False(UserRole.Cashier.Can(Permission.ViewCustomers, informal));
    }

    [Fact]
    public void Some_modules_need_the_books()
    {
        Assert.Contains(Module.Accounting, ModuleSet.Needs(Module.Purchasing));
        Assert.Contains(Module.Accounting, ModuleSet.Needs(Module.HR));
        Assert.Empty(ModuleSet.Needs(Module.Customers));
    }

    [Fact]
    public void New_roles_have_the_right_access()
    {
        Assert.True(UserRole.Warehousing.Can(Permission.ReceiveGoods));
        Assert.True(UserRole.Warehousing.Can(Permission.DeliverOrders));
        Assert.True(UserRole.Warehousing.Can(Permission.AdjustStock));
        Assert.False(UserRole.Warehousing.Can(Permission.Sell));
        Assert.False(UserRole.Warehousing.Can(Permission.ManagePurchasing)); // receives, doesn't place orders

        Assert.True(UserRole.Sales.Can(Permission.Sell));
        Assert.True(UserRole.Sales.Can(Permission.ManageSalesOrders));
        Assert.False(UserRole.Sales.Can(Permission.DeliverOrders));
        Assert.False(UserRole.Sales.Can(Permission.ProcessReturns));

        Assert.True(UserRole.OperationsManager.Can(Permission.ReceiveGoods));
        Assert.True(UserRole.OperationsManager.Can(Permission.DeliverOrders));
        Assert.False(UserRole.OperationsManager.Can(Permission.ManageStaff));
    }

    [Fact]
    public void Only_a_super_admin_hands_out_admin_roles()
    {
        Assert.True(UserRole.SuperAdmin.CanAssign(UserRole.SuperAdmin));
        Assert.True(UserRole.SuperAdmin.CanAssign(UserRole.Admin));
        Assert.False(UserRole.Admin.CanAssign(UserRole.Admin));
        Assert.False(UserRole.Admin.CanAssign(UserRole.SuperAdmin));
        Assert.True(UserRole.Admin.CanAssign(UserRole.Warehousing));
        Assert.False(UserRole.OperationsManager.CanAssign(UserRole.Cashier)); // can't manage staff at all
        Assert.Equal(UserRole.Cashier, default(UserRole));                    // the default is the weakest role
        Assert.Equal(Enum.GetValues<UserRole>().Length, Permissions.Ordered.Count);
    }
}
