using WavePOS.Core.Audit;
using WavePOS.Core.Models;

namespace WavePOS.Data;

/// <summary>Simple key/value settings: business details, logo and printing options.</summary>
public class SettingsRepository(PosDatabase db)
{
    public string? Get(string key)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void Set(string key, string? value) => SetMany([(key, value)]);

    public void SetMany(IEnumerable<(string Key, string? Value)> values)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var (key, value) in values)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public BusinessProfile GetBusinessProfile()
    {
        var p = new BusinessProfile();
        p.Name = Get("business.name") ?? p.Name;
        p.Address = Get("business.address") ?? p.Address;
        p.Phone = Get("business.phone") ?? p.Phone;
        p.Email = Get("business.email") ?? p.Email;
        p.Tpin = Get("business.tpin") ?? p.Tpin;
        p.ReceiptFooter = Get("business.receipt_footer") ?? p.ReceiptFooter;
        var logo = Get("business.logo_path");
        p.LogoPath = string.IsNullOrWhiteSpace(logo) ? null : logo;
        return p;
    }

    /// <summary>Saves the business details and records who changed them.</summary>
    public void SaveBusinessProfile(BusinessProfile p, Actor actor)
    {
        SaveBusinessProfile(p);
        new AuditRepository(db).Write(actor, AuditActions.SettingsChanged, "settings", "business",
            $"Business details saved (name \"{p.Name.Trim()}\", {(p.HasCustomLogo ? "custom logo" : "Wave logo")})");
    }

    public void SavePrintSettings(PrintSettings s, Actor actor)
    {
        SavePrintSettings(s);
        new AuditRepository(db).Write(actor, AuditActions.SettingsChanged, "settings", "printing",
            $"Printing: receipts {s.ReceiptPaper}, invoices {s.InvoicePaper}, auto-print {(s.AutoPrintReceipt ? "on" : "off")}");
    }

    public void SaveBusinessProfile(BusinessProfile p) => SetMany(
    [
        ("business.name", p.Name.Trim()),
        ("business.address", p.Address.Trim()),
        ("business.phone", p.Phone.Trim()),
        ("business.email", p.Email.Trim()),
        ("business.tpin", p.Tpin.Trim()),
        ("business.receipt_footer", p.ReceiptFooter.Trim()),
        ("business.logo_path", p.LogoPath),
    ]);

    public PrintSettings GetPrintSettings()
    {
        var s = new PrintSettings();
        var printer = Get("print.printer");
        s.PrinterName = string.IsNullOrWhiteSpace(printer) ? null : printer;
        if (Enum.TryParse<PaperSize>(Get("print.receipt_paper"), out var rp)) s.ReceiptPaper = rp;
        if (Enum.TryParse<PaperSize>(Get("print.invoice_paper"), out var ip)) s.InvoicePaper = ip;
        s.AutoPrintReceipt = Get("print.auto_receipt") == "1";
        return s;
    }

    public void SavePrintSettings(PrintSettings s) => SetMany(
    [
        ("print.printer", s.PrinterName),
        ("print.receipt_paper", s.ReceiptPaper.ToString()),
        ("print.invoice_paper", s.InvoicePaper.ToString()),
        ("print.auto_receipt", s.AutoPrintReceipt ? "1" : "0"),
    ]);
}
