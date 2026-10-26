namespace WavePOS.Core.Models;

/// <summary>
/// The shop using WAVE POS. Shown on the login screen, till header, receipts and invoices.
/// Each business can set its own name, details and logo; until then the Wave brand is used.
/// </summary>
public class BusinessProfile
{
    public string Name { get; set; } = "Wave Cyber Security";
    public string Address { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";

    /// <summary>ZRA Taxpayer Identification Number, printed on receipts and invoices.</summary>
    public string Tpin { get; set; } = "";

    public string ReceiptFooter { get; set; } = "Thank you for your business!";

    /// <summary>Path to the business's own logo image. Null means use the Wave logo.</summary>
    public string? LogoPath { get; set; }

    public bool HasCustomLogo => !string.IsNullOrWhiteSpace(LogoPath);
}

public enum PaperSize
{
    /// <summary>80mm thermal receipt roll.</summary>
    Receipt80mm,
    A4,
}

public class PrintSettings
{
    /// <summary>Printer to use without asking. Null means show the print dialog each time.</summary>
    public string? PrinterName { get; set; }

    public PaperSize ReceiptPaper { get; set; } = PaperSize.Receipt80mm;
    public PaperSize InvoicePaper { get; set; } = PaperSize.A4;

    /// <summary>Print a receipt automatically when a sale is completed.</summary>
    public bool AutoPrintReceipt { get; set; }
}
