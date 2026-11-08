namespace MarketHub.Modules.Identity.Domain.Users;

/// <summary>
/// A MarketHub account (buyer, vendor or administrator).
/// Email is the login identifier and is stored lower-cased and unique.
/// </summary>
public sealed class User : BaseEntity
{
    /// <summary>EF Core constructor.</summary>
    private User()
    {
    }

    /// <summary>Internal constructor; <paramref name="id"/> allows deterministic seeding.</summary>
    private User(Guid? id)
    {
        Id = id ?? Guid.NewGuid();
        CreatedAtUtc = DateTime.UtcNow;
        ModifiedAtUtc = CreatedAtUtc;
    }

    /// <summary>Unique, lower-cased email address (login identifier).</summary>
    public string Email { get; private set; } = null!;

    /// <summary>Ethiopian mobile number in the format +2519XXXXXXXX or +2517XXXXXXXX.</summary>
    public string Phone { get; private set; } = null!;

    /// <summary>BCrypt password hash (never the plain password).</summary>
    public string PasswordHash { get; private set; } = null!;

    /// <summary>Display name of the account holder.</summary>
    public string FullName { get; private set; } = null!;

    /// <summary>Account role.</summary>
    public UserRole Role { get; private set; }

    /// <summary>Account lifecycle status.</summary>
    public UserStatus Status { get; private set; }

    /// <summary>Fast flag mirroring <see cref="Status"/> == <see cref="UserStatus.Active"/>.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Creates a new active user account.</summary>
    /// <param name="email">Email address (normalised to lower-case).</param>
    /// <param name="phone">Phone number in +251 format.</param>
    /// <param name="passwordHash">Pre-computed BCrypt hash.</param>
    /// <param name="fullName">Display name.</param>
    /// <param name="role">Account role.</param>
    /// <param name="id">Optional deterministic id (used by the seeder).</param>
    public static User Create(string email, string phone, string passwordHash, string fullName, UserRole role, Guid? id = null)
    {
        return new User(id)
        {
            Email = email.Trim().ToLowerInvariant(),
            Phone = phone.Trim(),
            PasswordHash = passwordHash,
            FullName = fullName.Trim(),
            Role = role,
            Status = UserStatus.Active,
            IsActive = true
        };
    }

    /// <summary>Suspends the account; login and token refresh are rejected while suspended.</summary>
    public void Suspend()
    {
        Status = UserStatus.Suspended;
        IsActive = false;
        ModifiedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Re-activates a suspended account.</summary>
    public void Reactivate()
    {
        Status = UserStatus.Active;
        IsActive = true;
        ModifiedAtUtc = DateTime.UtcNow;
    }
}
