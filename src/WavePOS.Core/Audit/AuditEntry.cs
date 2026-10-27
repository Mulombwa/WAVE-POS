using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WavePOS.Core.Audit;

/// <summary>Who did something. Null user id = the system or a failed sign-in.</summary>
public record Actor(long? UserId, string Name)
{
    public static readonly Actor System = new(null, "System");
}

/// <summary>
/// One line in the audit trail. Entries are append-only and chained: each stores a hash of
/// the previous entry, so any later edit or deletion shows up when the chain is verified.
/// </summary>
public class AuditEntry
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public long? UserId { get; set; }
    public string UserName { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public string Details { get; set; } = "";
    public string PreviousHash { get; set; } = "";
    public string Hash { get; set; } = "";

    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>SHA-256 over the entry's content and the previous entry's hash.</summary>
    public string ComputeHash()
    {
        var text = string.Join("\u001f",
            Id.ToString(CultureInfo.InvariantCulture),
            TimestampUtc.ToString("o", CultureInfo.InvariantCulture),
            UserId?.ToString(CultureInfo.InvariantCulture) ?? "",
            UserName, Action, EntityType, EntityId ?? "", Details, PreviousHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}

/// <summary>Standard action names, so the audit log can be filtered reliably.</summary>
public static class AuditActions
{
    public const string LoginSucceeded = "auth.login";
    public const string LoginFailed = "auth.login_failed";
    public const string AccountLocked = "auth.locked";
    public const string Logout = "auth.logout";

    public const string SaleCompleted = "sale.completed";
    public const string DraftInvoiceCreated = "sale.draft_invoice";
    public const string ReturnProcessed = "sale.return";

    public const string ProductCreated = "inventory.product_created";
    public const string ProductUpdated = "inventory.product_updated";
    public const string StockReceived = "inventory.stock_received";
    public const string StockAdjusted = "inventory.stock_adjusted";

    public const string CustomerCreated = "customer.created";
    public const string CustomerUpdated = "customer.updated";
    public const string CustomerViewed = "customer.viewed";
    public const string CustomerExported = "customer.data_exported";
    public const string CustomerAnonymised = "customer.anonymised";

    public const string UserCreated = "staff.created";
    public const string UserUpdated = "staff.updated";
    public const string PasswordReset = "staff.password_reset";

    public const string SettingsChanged = "settings.changed";
}
