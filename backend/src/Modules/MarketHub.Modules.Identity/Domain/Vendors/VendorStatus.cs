namespace MarketHub.Modules.Identity.Domain.Vendors;

/// <summary>KYC lifecycle status of a vendor application/profile.</summary>
public enum VendorStatus
{
    /// <summary>Newly submitted; no admin has looked at it yet.</summary>
    PendingReview = 0,

    /// <summary>An administrator is actively reviewing documents.</summary>
    UnderReview = 1,

    /// <summary>Approved; the vendor may publish products (catalog enforces this).</summary>
    Approved = 2,

    /// <summary>Rejected with a reason; the vendor may re-apply by uploading new documents.</summary>
    Rejected = 3,

    /// <summary>Previously approved but suspended by an administrator.</summary>
    Suspended = 4
}
