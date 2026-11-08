namespace MarketHub.Modules.Identity.Domain.Users;

/// <summary>Role assigned to a MarketHub account.</summary>
public enum UserRole
{
    /// <summary>Regular buyer account.</summary>
    Buyer = 0,

    /// <summary>Vendor selling on the marketplace (requires an approved <c>VendorProfile</c>).</summary>
    Vendor = 1,

    /// <summary>Platform administrator.</summary>
    Admin = 2
}

/// <summary>Lifecycle status of a user account.</summary>
public enum UserStatus
{
    /// <summary>Account is usable.</summary>
    Active = 0,

    /// <summary>Account is temporarily blocked (login and refresh are rejected).</summary>
    Suspended = 1
}
