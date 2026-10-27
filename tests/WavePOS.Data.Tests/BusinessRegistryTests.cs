using Microsoft.Data.Sqlite;
using WavePOS.Core.Audit;
using WavePOS.Core.Models;
using WavePOS.Core.Security;

namespace WavePOS.Data.Tests;

public sealed class BusinessRegistryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"wavepos-reg-{Guid.NewGuid():N}");
    private readonly BusinessRegistry _registry;

    public BusinessRegistryTests() => _registry = new BusinessRegistry(_folder);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private (Business Business, User Owner) SignUp(string name, BusinessSize size = BusinessSize.Informal, bool samples = false) =>
        _registry.Register(new BusinessSignUp
        {
            BusinessName = name, Size = size, Phone = "0977000000", OwnerName = "Owner of " + name,
            Username = "owner", Password = "4321", SampleProducts = samples, Town = "Lusaka",
        });

    [Fact]
    public void Signing_up_creates_a_private_business_with_a_super_admin()
    {
        var (shop, owner) = SignUp("Mwila's Salon & Spa");

        Assert.Equal("mwilas-salon-spa", shop.Code);
        Assert.Equal(UserRole.SuperAdmin, owner.Role);
        Assert.False(shop.Modules.Has(Module.Accounting));
        Assert.True(File.Exists(shop.DatabasePath));
        Assert.StartsWith(Path.Combine(_folder, "businesses"), shop.DatabasePath);

        var db = _registry.Database(shop);
        Assert.Equal("Mwila's Salon & Spa", new SettingsRepository(db).GetBusinessProfile().Name);
        Assert.Empty(new ProductRepository(db).GetAll()); // no sample products unless asked
        Assert.Equal(1, new AuditRepository(db).Query(new AuditFilter(Action: AuditActions.BusinessCreated)).Total);
        Assert.Same(shop.Code, _registry.Find("MWILAS-SALON-SPA")!.Code);
    }

    [Fact]
    public void Businesses_never_see_each_others_data()
    {
        var (a, _) = SignUp("Chanda Hardware", BusinessSize.Small, samples: true);
        var (b, _) = SignUp("Chanda Hardware"); // same name: still a different business

        Assert.NotEqual(a.Code, b.Code);
        Assert.Equal("chanda-hardware-2", b.Code);
        Assert.NotEqual(a.DatabasePath, b.DatabasePath);
        Assert.NotEmpty(new ProductRepository(_registry.Database(a)).GetAll());
        Assert.Empty(new ProductRepository(_registry.Database(b)).GetAll());

        // Each has its own "owner" account; the PIN for one doesn't open the other.
        new UserRepository(_registry.Database(b)).ResetPassword(1, "9999", Actor.System);
        Assert.Equal(LoginStatus.Success, new UserRepository(_registry.Database(a)).Authenticate("owner", "4321").Status);
        Assert.NotEqual(LoginStatus.Success, new UserRepository(_registry.Database(b)).Authenticate("owner", "4321").Status);
    }

    [Theory]
    [InlineData("  ", "Enter the business name")]
    [InlineData("Ok Shop", null)]
    public void Sign_up_checks_the_details(string name, string? problem)
    {
        if (problem is null) { SignUp(name); return; }
        Assert.Contains(problem, Assert.Throws<ArgumentException>(() => SignUp(name)).Message);
    }

    [Fact]
    public void Sign_up_needs_a_contact_and_a_valid_pin()
    {
        Assert.Throws<ArgumentException>(() => _registry.Register(new BusinessSignUp
        {
            BusinessName = "No Contact", OwnerName = "A", Username = "a", Password = "4321",
        }));
        Assert.Throws<ArgumentException>(() => _registry.Register(new BusinessSignUp
        {
            BusinessName = "Short Pin", OwnerName = "A", Username = "a", Password = "12", Phone = "0977",
        }));
        Assert.Empty(_registry.All()); // nothing half-made left behind
    }

    [Theory]
    [InlineData("Mwila's Salon", "mwilas-salon")]
    [InlineData("A1", "shop-a1")]
    [InlineData("Lusaka   Fresh  Produce & Grocery Supplies Ltd", "lusaka-fresh-produce-gro")]
    public void Business_codes_are_short_and_readable(string name, string code) =>
        Assert.Equal(code, BusinessRegistry.CodeFrom(name));

    [Fact]
    public void Reserved_words_arent_used_as_codes() => Assert.Equal("admin-shop", SignUp("Admin").Business.Code);

    [Fact]
    public void Modules_can_be_changed_but_dependencies_are_kept()
    {
        var (shop, owner) = SignUp("Tembo Traders", BusinessSize.Small);
        var actor = new Actor(owner.Id, owner.DisplayName);

        Assert.Throws<ArgumentException>(() =>
            _registry.SetModules(shop.Id, BusinessSize.Small, ModuleSet.ForSize(BusinessSize.Informal).With(Module.Purchasing, true), actor));

        var updated = _registry.SetModules(shop.Id, BusinessSize.Large, ModuleSet.ForSize(BusinessSize.Small).With(Module.HR, true), actor);
        Assert.True(updated.Modules.Has(Module.HR));
        Assert.Equal(BusinessSize.Large, updated.Size);
        Assert.True(new BusinessRegistry(_folder).Get(shop.Id)!.Modules.Has(Module.HR)); // saved, not just cached
    }

    [Fact]
    public void An_existing_single_business_database_becomes_the_first_business()
    {
        var legacyPath = Path.Combine(_folder, "wavepos.db");
        var legacy = new PosDatabase(legacyPath);
        legacy.Initialize();
        new UserRepository(legacy).Create("mary", "Mary Banda", "4321", UserRole.Admin);
        SqliteConnection.ClearAllPools();

        var adopted = _registry.AdoptExisting(legacyPath)!;
        Assert.Equal("wave-cyber-security", adopted.Code);
        Assert.All(Enum.GetValues<Module>(), m => Assert.True(adopted.Modules.Has(m)));
        Assert.Equal(UserRole.SuperAdmin, new UserRepository(_registry.Database(adopted)).GetAll().Single().Role);
        Assert.Null(_registry.AdoptExisting(legacyPath)); // only once
    }

    [Fact]
    public void Product_pictures_belong_to_the_whole_product_family()
    {
        var (shop, owner) = SignUp("Kabwe Boutique");
        var db = _registry.Database(shop);
        var products = new ProductRepository(db);
        var actor = new Actor(owner.Id, owner.DisplayName);
        var shirts = products.Create(new NewProductRequest("Shirt", "Clothing", "SH", 150m, 80m, 0.16m, 2, 3,
            WavePOS.Core.Inventory.ProductOption.Parse("Size", "S,M")), actor);

        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
        products.SetImage(shirts[0].Id, jpeg, ".jpg", actor);
        var path = products.ImagePath(shirts[1].Id)!;
        Assert.True(File.Exists(path));
        Assert.StartsWith(db.DataDirectory, path);

        products.SetImage(shirts[0].Id, jpeg, ".jpg", actor); // replace
        Assert.False(File.Exists(path));                       // old picture cleaned up
        products.RemoveImage(shirts[0].Id, actor);
        Assert.Null(products.ImagePath(shirts[1].Id));
        Assert.Empty(Directory.GetFiles(products.ImageFolder));
    }
}
