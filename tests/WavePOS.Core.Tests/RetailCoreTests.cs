using WavePOS.Core.Audit;
using WavePOS.Core.Inventory;
using WavePOS.Core.Models;
using WavePOS.Core.Returns;
using WavePOS.Core.Security;

namespace WavePOS.Core.Tests;

public class PermissionTests
{
    [Fact]
    public void Cashier_can_sell_but_not_refund_or_manage()
    {
        Assert.True(UserRole.Cashier.Can(Permission.Sell));
        Assert.True(UserRole.Cashier.Can(Permission.ViewCustomers));
        Assert.False(UserRole.Cashier.Can(Permission.ProcessReturns));
        Assert.False(UserRole.Cashier.Can(Permission.AdjustStock));
        Assert.False(UserRole.Cashier.Can(Permission.ManageStaff));
    }

    [Fact]
    public void Operations_manager_runs_the_floor_but_not_staff_books_or_pay()
    {
        Assert.True(UserRole.OperationsManager.Can(Permission.ProcessReturns));
        Assert.True(UserRole.OperationsManager.Can(Permission.AdjustStock));
        Assert.True(UserRole.OperationsManager.Can(Permission.ApprovePurchases));
        Assert.True(UserRole.OperationsManager.Can(Permission.ViewAuditLog));
        Assert.False(UserRole.OperationsManager.Can(Permission.ManageStaff));
        Assert.False(UserRole.OperationsManager.Can(Permission.ViewAccounts));
        Assert.False(UserRole.OperationsManager.Can(Permission.ManageEmployees));
    }

    [Fact]
    public void Super_admin_can_do_everything_and_admin_everything_but_modules()
    {
        Assert.All(Enum.GetValues<Permission>(), p => Assert.True(UserRole.SuperAdmin.Can(p)));
        Assert.All(Enum.GetValues<Permission>().Where(p => p != Permission.ManageModules), p => Assert.True(UserRole.Admin.Can(p)));
        Assert.False(UserRole.Admin.Can(Permission.ManageModules));
    }

    [Fact]
    public void Disabled_user_can_do_nothing()
    {
        var user = new User { Role = UserRole.Admin, IsActive = false };
        Assert.False(user.Can(Permission.Sell));
    }
}

public class VariantBuilderTests
{
    [Fact]
    public void Builds_every_size_and_colour_combination()
    {
        var variants = VariantBuilder.Build("Polo Shirt", "",
            ProductOption.Parse("Size", "S, M, L"), ProductOption.Parse("Colour", "Navy Blue, White"));

        Assert.Equal(6, variants.Count);
        Assert.Contains(variants, v => v.Name == "Polo Shirt — M / Navy Blue" && v.Sku == "POLOSHIRT-M-NAVYBLUE");
        Assert.All(variants, v => Assert.NotNull(v.Option2));
        Assert.Equal(6, variants.Select(v => v.Sku).Distinct().Count());
    }

    [Fact]
    public void No_options_gives_one_plain_product()
    {
        var variants = VariantBuilder.Build("Sugar 2kg", "GRO-005", null, null);
        var only = Assert.Single(variants);
        Assert.Equal("Sugar 2kg", only.Name);
        Assert.Equal("GRO005", only.Sku);
    }

    [Fact]
    public void Duplicate_option_values_are_ignored()
    {
        Assert.Equal(2, ProductOption.Parse("Size", "S, s, M,, ").Values.Count);
    }

    [Fact]
    public void Too_many_variants_are_rejected()
    {
        var many = string.Join(",", Enumerable.Range(1, 30));
        Assert.Throws<ArgumentException>(() =>
            VariantBuilder.Build("Shoe", "", ProductOption.Parse("Size", many), ProductOption.Parse("Width", many)));
    }
}

public class ReturnCalculatorTests
{
    private static readonly ReturnableLine Shirt = new(1, 10, "Shirt — M", 200m, 0.16m, QuantitySold: 3, QuantityAlreadyReturned: 1);
    private static readonly ReturnableLine Bread = new(2, 11, "Bread", 25m, 0m, QuantitySold: 1, QuantityAlreadyReturned: 0);

    [Fact]
    public void Valid_return_totals_amount_and_vat()
    {
        var request = new ReturnRequest
        {
            SaleId = 1,
            Lines = [new(1, 2, Restock: true), new(2, 1, Restock: false)],
        };

        var lines = ReturnCalculator.Validate(request, [Shirt, Bread]);

        Assert.Equal(425m, ReturnCalculator.Total(lines));
        Assert.Equal(55.17m, ReturnCalculator.Vat(lines)); // 400 incl. 16% VAT
    }

    [Fact]
    public void Cannot_return_more_than_remaining()
    {
        var request = new ReturnRequest { SaleId = 1, Lines = [new(1, 3, true)] };
        var ex = Assert.Throws<InvalidOperationException>(() => ReturnCalculator.Validate(request, [Shirt]));
        Assert.Contains("Only 2", ex.Message);
    }

    [Fact]
    public void Must_return_at_least_one_item_from_this_sale()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ReturnCalculator.Validate(new ReturnRequest { Lines = [new(1, 0, true)] }, [Shirt]));
        Assert.Throws<InvalidOperationException>(() =>
            ReturnCalculator.Validate(new ReturnRequest { Lines = [new(99, 1, true)] }, [Shirt]));
    }
}

public class AuditHashTests
{
    [Fact]
    public void Any_change_to_an_entry_changes_its_hash()
    {
        var entry = new AuditEntry
        {
            Id = 1, TimestampUtc = new DateTime(2026, 10, 27, 8, 0, 0, DateTimeKind.Utc),
            UserId = 1, UserName = "Mary", Action = AuditActions.StockAdjusted,
            EntityType = "product", EntityId = "5", Details = "-2 Damaged", PreviousHash = AuditEntry.GenesisHash,
        };
        var original = entry.ComputeHash();

        entry.Details = "-20 Damaged";
        Assert.NotEqual(original, entry.ComputeHash());

        entry.Details = "-2 Damaged";
        Assert.Equal(original, entry.ComputeHash());
        Assert.Equal(64, original.Length);
    }
}
