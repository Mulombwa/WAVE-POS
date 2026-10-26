namespace WavePOS.Core.Models;

public enum UserRole
{
    Cashier,
    Manager,
}

/// <summary>A person who can log in to the till. The password hash is never loaded into this object.</summary>
public class User
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Cashier;
    public bool IsActive { get; set; } = true;

    public bool IsManager => Role == UserRole.Manager;
}
