using System.Security.Claims;
using WavePOS.Core.Models;

namespace WavePOS.Web.Services;

/// <summary>Turns a WAVE POS user into sign-in claims and back.</summary>
public static class UserClaims
{
    public const string ManagerPolicy = "Manager";
    private const string UsernameClaim = "wavepos:username";

    public static ClaimsPrincipal ToPrincipal(User user, string scheme) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.DisplayName),
        new Claim(UsernameClaim, user.Username),
        new Claim(ClaimTypes.Role, user.Role.ToString()),
    ], scheme));

    /// <summary>
    /// True if the sign-in still matches an active account in the database
    /// (same id, username and role). Used to end sessions for deleted, disabled or changed accounts.
    /// </summary>
    public static bool StillValid(ClaimsPrincipal? principal, WavePOS.Data.UserRepository users)
    {
        if (principal?.Identity?.IsAuthenticated != true) return false;
        var claimed = principal.ToUser();
        if (claimed.Id <= 0) return false;

        var current = users.GetById(claimed.Id);
        return current is { IsActive: true }
            && string.Equals(current.Username, claimed.Username, StringComparison.OrdinalIgnoreCase)
            && current.Role == claimed.Role;
    }

    public static User ToUser(this ClaimsPrincipal principal) => new()
    {
        Id = long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0,
        DisplayName = principal.Identity?.Name ?? "",
        Username = principal.FindFirstValue(UsernameClaim) ?? "",
        Role = Enum.TryParse<UserRole>(principal.FindFirstValue(ClaimTypes.Role), out var role) ? role : UserRole.Cashier,
    };
}

public static class Branding
{
    /// <summary>Logo URL with a version so browsers pick up a newly uploaded logo straight away.</summary>
    public static string LogoUrl(BusinessProfile profile) =>
        "/branding/logo?v=" + Uri.EscapeDataString(profile.HasCustomLogo ? Path.GetFileName(profile.LogoPath!) : "wave");

    public static string PaperParam(PaperSize paper) => paper == PaperSize.A4 ? "a4" : "80mm";

    public static PaperSize ParsePaper(string? value, PaperSize fallback) => value?.ToLowerInvariant() switch
    {
        "a4" => PaperSize.A4,
        "80mm" => PaperSize.Receipt80mm,
        _ => fallback,
    };
}

/// <summary>Checks and stores uploaded logos. Only PNG and JPEG are accepted (checked by file content, not name).</summary>
public static class LogoUpload
{
    public const long MaxBytes = 2 * 1024 * 1024;

    public static string? DetectExtension(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return ".png";
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return ".jpg";
        return null;
    }

    public static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".bmp" => "image/bmp",
        _ => "application/octet-stream",
    };

    /// <summary>Saves the logo in the app's data folder under a new name and returns its path.</summary>
    public static string Save(byte[] data, string extension, string dataDirectory)
    {
        var dir = Path.Combine(dataDirectory, "branding");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"logo-{DateTime.Now:yyyyMMddHHmmss}{extension}");
        File.WriteAllBytes(path, data);
        return path;
    }
}
