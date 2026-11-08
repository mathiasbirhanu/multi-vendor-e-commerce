using MarketHub.Shared;

namespace MarketHub.Modules.Identity.Domain;

public enum UserRole { Buyer, Vendor, Admin }
public enum UserStatus { Active, Suspended }
public enum VendorStatus { PendingReview, UnderReview, Approved, Rejected, Suspended }
public enum DocumentStatus { Pending, Verified, Rejected }
public enum PolicyKey { TermsOfService, RulesAndRegulations, ReturnAndRefundPolicy }

/// <summary>Back-office sub-role of an Admin user (null for Buyer/Vendor accounts).</summary>
public enum AdminRole { SuperAdmin, Support, Finance, Moderator }

/// <summary>Platform account. Vendors additionally own a <see cref="VendorProfile"/>.</summary>
public class User : BaseEntity
{
    public string Email { get; set; } = default!;
    public string Phone { get; set; } = default!;
    public string PasswordHash { get; set; } = default!;
    public string FullName { get; set; } = default!;
    public UserRole Role { get; set; }
    public UserStatus Status { get; set; } = UserStatus.Active;
    /// <summary>Back-office sub-role; only meaningful when Role == Admin.</summary>
    public AdminRole? AdminRole { get; set; }
    /// <summary>Preferred UI language: "en" | "am" (default en).</summary>
    public string Lang { get; set; } = "en";
}

/// <summary>Rotating refresh token (one active chain per user).</summary>
public class RefreshToken : BaseEntity
{
    public Guid UserId { get; set; }
    public string Token { get; set; } = default!;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? ReplacedByToken { get; set; }
    public string? CreatedByIp { get; set; }
    public bool IsActive => RevokedAtUtc is null && DateTime.UtcNow < ExpiresAtUtc;
}

/// <summary>Vendor store profile with the admin-driven KYC lifecycle.</summary>
public class VendorProfile : BaseEntity
{
    public Guid Id { get; set; } // == user id
    public string BusinessName { get; set; } = default!;
    public string TIN { get; set; } = default!;
    public VendorStatus Status { get; set; } = VendorStatus.PendingReview;
    public string? RejectionReason { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public string Region { get; set; } = default!;
    public string City { get; set; } = default!;
    public string? StoreDescription { get; set; }
    /// <summary>Denormalized approved-review count across the vendor's products (recomputed by Catalog).</summary>
    public int RatingCount { get; set; }

    public void Approve(Guid adminId)
    {
        if (Status is not (VendorStatus.PendingReview or VendorStatus.UnderReview or VendorStatus.Rejected))
            throw new DomainException($"Cannot approve a vendor in status {Status}.");
        Status = VendorStatus.Approved;
        ReviewedByUserId = adminId;
        ReviewedAtUtc = DateTime.UtcNow;
        ApprovedAtUtc = DateTime.UtcNow;
        RejectionReason = null;
    }

    public void Reject(Guid adminId, string reason)
    {
        if (Status is VendorStatus.Approved or VendorStatus.Suspended)
            throw new DomainException($"Cannot reject a vendor in status {Status}.");
        Status = VendorStatus.Rejected;
        ReviewedByUserId = adminId;
        ReviewedAtUtc = DateTime.UtcNow;
        RejectionReason = reason;
    }

    public void Suspend(Guid adminId, string reason)
    {
        if (Status != VendorStatus.Approved) throw new DomainException("Only approved vendors can be suspended.");
        Status = VendorStatus.Suspended;
        ReviewedByUserId = adminId;
        ReviewedAtUtc = DateTime.UtcNow;
        RejectionReason = reason;
    }
}

/// <summary>Configurable per-role document requirement (data-driven KYC, admin-manageable).</summary>
public class DocumentType : BaseEntity
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public UserRole AppliesTo { get; set; }
    public bool IsRequired { get; set; }
    public int MinFiles { get; set; }
    public int MaxFiles { get; set; } = 2;
    public string? Description { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>An uploaded legal document (stored privately in object storage).</summary>
public class Document : BaseEntity
{
    public Guid OwnerId { get; set; }
    public Guid DocumentTypeId { get; set; }
    public string FileName { get; set; } = default!;
    public string ContentType { get; set; } = default!;
    public string StorageKey { get; set; } = default!;
    public string SHA256 { get; set; } = default!;
    public long SizeBytes { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;
    public string? RejectionReason { get; set; }
    public Guid? VerifiedByUserId { get; set; }
    public DateTime? VerifiedAtUtc { get; set; }
    /// <summary>UTC instant after which the document is no longer valid (KYC re-submission required).</summary>
    public DateTime? ExpiresAtUtc { get; set; }
    /// <summary>Set by the DocumentExpiryMonitor once the expired document was flagged and the owner notified.</summary>
    public DateTime? ExpiryFlaggedAtUtc { get; set; }
}

/// <summary>Versioned platform policy (Terms, Rules & Regulations, Return & Refund).</summary>
public class Policy : BaseEntity
{
    public PolicyKey Key { get; set; }
    public string Version { get; set; } = default!;
    public string ContentMarkdown { get; set; } = default!;
    public DateTime EffectiveFromUtc { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Audit-grade consent: which user accepted which policy version, when, from where.</summary>
public class Consent : BaseEntity
{
    public Guid UserId { get; set; }
    public PolicyKey PolicyKey { get; set; }
    public string PolicyVersion { get; set; } = default!;
    public DateTime AcceptedAtUtc { get; set; } = DateTime.UtcNow;
    public string? IpAddress { get; set; }
}
