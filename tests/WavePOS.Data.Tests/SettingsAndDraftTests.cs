using Microsoft.Data.Sqlite;
using WavePOS.Core.Documents;
using WavePOS.Core.Models;
using WavePOS.Core.Sales;

namespace WavePOS.Data.Tests;

public sealed class SettingsAndDraftTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wavepos-test-{Guid.NewGuid():N}.db");
    private readonly PosDatabase _db;

    public SettingsAndDraftTests()
    {
        _db = new PosDatabase(_path);
        _db.Initialize();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void New_install_uses_Wave_defaults()
    {
        var profile = new SettingsRepository(_db).GetBusinessProfile();

        Assert.Equal("Wave Cyber Security", profile.Name);
        Assert.False(profile.HasCustomLogo);
    }

    [Fact]
    public void Business_profile_round_trips()
    {
        var repo = new SettingsRepository(_db);
        repo.SaveBusinessProfile(new BusinessProfile
        {
            Name = " Lusaka Mart ", Address = "Cairo Road", Phone = "+260 97 000 0000",
            Email = "hi@mart.zm", Tpin = "1001234567", ReceiptFooter = "Come again", LogoPath = @"C:\logo.png",
        });

        var p = repo.GetBusinessProfile();

        Assert.Equal("Lusaka Mart", p.Name);
        Assert.Equal("1001234567", p.Tpin);
        Assert.Equal(@"C:\logo.png", p.LogoPath);

        // Clearing the logo goes back to the Wave logo.
        p.LogoPath = null;
        repo.SaveBusinessProfile(p);
        Assert.False(repo.GetBusinessProfile().HasCustomLogo);
    }

    [Fact]
    public void Print_settings_round_trip()
    {
        var repo = new SettingsRepository(_db);
        Assert.False(repo.GetPrintSettings().AutoPrintReceipt);

        repo.SavePrintSettings(new PrintSettings
        {
            PrinterName = "EPSON TM-T20", ReceiptPaper = PaperSize.A4, InvoicePaper = PaperSize.Receipt80mm, AutoPrintReceipt = true,
        });

        var s = repo.GetPrintSettings();
        Assert.Equal("EPSON TM-T20", s.PrinterName);
        Assert.Equal(PaperSize.A4, s.ReceiptPaper);
        Assert.Equal(PaperSize.Receipt80mm, s.InvoicePaper);
        Assert.True(s.AutoPrintReceipt);
    }

    [Fact]
    public void Draft_invoices_get_sequential_numbers()
    {
        var products = new ProductRepository(_db).GetAll();
        var cart = new Cart();
        cart.Add(products[0], 2);
        var drafts = new DraftInvoiceRepository(_db);

        var first = SalesDocument.DraftFromCart(cart, "Mary", "ABC Ltd", DateTime.Now);
        var second = SalesDocument.DraftFromCart(cart, "Mary", null, DateTime.Now);

        Assert.Equal("DRF-00001", drafts.Save(first, null));
        Assert.Equal("DRF-00002", drafts.Save(second, null));
        Assert.Equal("DRF-00001", first.Number);
        Assert.Equal(2, drafts.Count());
    }
}
