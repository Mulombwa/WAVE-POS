namespace WavePOS.Core.Models;

/// <summary>
/// Staff roles. What each role may do is defined in <see cref="Security.Permissions"/>. Cashier is
/// first on purpose: the default value is the least powerful role. Saved by name, so the order
/// doesn't matter to the database.
/// </summary>
public enum UserRole
{
    Cashier,
    /// <summary>Was "Supervisor" before roles were expanded.</summary>
    OperationsManager,
    /// <summary>Was "Manager" before roles were expanded.</summary>
    Admin,
    /// <summary>Accountant or bookkeeper: the books, reports and bank reconciliation, but not the till.</summary>
    Accountant,
    /// <summary>The business owner: everything, including modules and admins.</summary>
    SuperAdmin,
    Sales,
    Warehousing,
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

    /// <summary>Super admin or admin.</summary>
    public bool IsManager => Role is UserRole.SuperAdmin or UserRole.Admin;
}
