using System.Security.Cryptography;

namespace WavePOS.Core.Security;

/// <summary>
/// Hashes passwords/PINs with PBKDF2-SHA256 and a random salt.
/// Stored format: "pbkdf2-sha256$iterations$salt$hash" (base64), so the cost can be raised later.
/// </summary>
public static class PasswordHasher
{
    public const int MinLength = 4;

    private const string Scheme = "pbkdf2-sha256";
    private const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        if (password is null || string.IsNullOrEmpty(stored)) return false;

        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations)) return false;

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Returns an error message, or null if the password is acceptable.</summary>
    public static string? Validate(string password) =>
        string.IsNullOrWhiteSpace(password) || password.Length < MinLength
            ? $"Password must be at least {MinLength} characters."
            : null;
}
