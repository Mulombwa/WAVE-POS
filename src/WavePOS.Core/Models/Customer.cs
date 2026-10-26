namespace WavePOS.Core.Models;

/// <summary>
/// A customer. Personal data is handled under Zambia's Data Protection Act 2021:
/// marketing needs recorded consent, and a customer can ask for their data or to be forgotten
/// (anonymised — sales records stay for tax purposes, without personal details).
/// </summary>
public class Customer
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string Address { get; set; } = "";
    public string Notes { get; set; } = "";

    public bool MarketingConsent { get; set; }
    public DateTime? ConsentDate { get; set; }

    public DateTime CreatedAt { get; set; }
    public bool IsAnonymised { get; set; }

    // Filled in when loading a profile
    public int Visits { get; set; }
    public decimal TotalSpent { get; set; }
    public DateTime? LastVisit { get; set; }
}
