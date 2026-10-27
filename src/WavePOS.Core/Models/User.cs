namespace WavePOS.Core.Models;

/// <summary>Staff roles. What each role may do is defined in <see cref="Security.Permissions"/>.</summary>
public enum UserRole
{
    Cashier,
    Supervisor,
    Manager,
    /// <summary>Accountant or bookkeeper: the books, reports and bank reconciliation, but not the till.</summary>
    Accountant,
}

/// <summary>A staff member who can log in. The password hash is never loaded into this object.</summary>
public class User
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Cashier;
    public bool IsActive { get; set; } = true;

    // Employee details
    public string EmployeeCode { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public DateTime? LastLogin { get; set; }

    public bool IsManager => Role == UserRole.Manager;
}
