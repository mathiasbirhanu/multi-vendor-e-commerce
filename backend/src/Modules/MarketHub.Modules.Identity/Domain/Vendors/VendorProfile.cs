namespace MarketHub.Modules.Identity.Domain.Vendors;

/// <summary>
/// KYC profile of a vendor. The primary key equals the owning user id (no auto generation),
/// so vendor id and user id are the same value across the platform.
/// </summary>
public sealed class VendorProfile : BaseEntity
{
    /// <summary>EF Core constructor.</summary>
    private VendorProfile()
    {
    }

    /// <summary>Internal constructor; the profile id IS the vendor user id.</summary>
    private VendorProfile(Guid userId)
    {
        Id = userId;
        CreatedAtUtc = DateTime.UtcNow;
        ModifiedAtUtc = CreatedAtUtc;
    }

    /// <summary>Registered trading name.</summary>
    public string BusinessName { get; private set; } = null!;

    /// <summary>Ethiopian Taxpayer Identification Number (10 digits).</summary>
    public string TIN { get; private set; } = null!;

    /// <summary>Current application status.</summary>
    public VendorStatus Status { get; private set; }

    /// <summary>Reason recorded when the application was rejected.</summary>
    public string? RejectionReason { get; private set; }

    /// <summary>Reason recorded when an approved vendor was suspended.</summary>
    public string? SuspensionReason { get; private set; }

    /// <summary>Administrator who last reviewed the application.</summary>
    public Guid? ReviewedByUserId { get; private set; }

    /// <summary>UTC instant of the last review decision.</summary>
    public DateTime? ReviewedAtUtc { get; private set; }

    /// <summary>Ethiopian region of the business (e.g. Addis Ababa, Oromia).</summary>
    public string Region { get; private set; } = null!;

    /// <summary>City of the business.</summary>
    public string City { get; private set; } = null!;

    /// <summary>Short public description of the store.</summary>
    public string? StoreDescription { get; private set; }

    /// <summary>UTC instant of approval (null until approved).</summary>
    public DateTime? ApprovedAtUtc { get; private set; }

    /// <summary>Creates a vendor profile in <see cref="VendorStatus.PendingReview"/>.</summary>
    /// <param name="userId">Vendor user id (also used as the profile primary key).</param>
    /// <param name="businessName">Registered trading name.</param>
    /// <param name="tin">10-digit TIN.</param>
    /// <param name="region">Ethiopian region.</param>
    /// <param name="city">City.</param>
    /// <param name="storeDescription">Optional public store description.</param>
    public static VendorProfile Create(Guid userId, string businessName, string tin, string region, string city, string? storeDescription = null)
    {
        return new VendorProfile(userId)
        {
            BusinessName = businessName.Trim(),
            TIN = tin.Trim(),
            Region = region.Trim(),
            City = city.Trim(),
            StoreDescription = string.IsNullOrWhiteSpace(storeDescription) ? null : storeDescription.Trim(),
            Status = VendorStatus.PendingReview
        };
    }

    /// <summary>
    /// Moves a freshly submitted (or resubmitted after rejection) application into review.
    /// Legal sources: PendingReview, Rejected (re-apply path).
    /// </summary>
    public void StartReview()
    {
        if (Status is not (VendorStatus.PendingReview or VendorStatus.Rejected))
        {
            throw new DomainException($"A vendor application in status '{Status}' cannot move to UnderReview.");
        }

        Status = VendorStatus.UnderReview;
        Touch();
    }

    /// <summary>
    /// Approves the application. Primary legal sources are PendingReview and UnderReview;
    /// Rejected is additionally allowed as the documented re-apply path (the vendor has resubmitted
    /// documents via <c>UploadAdditionalDocumentCommand</c> and the admin re-approves).
    /// </summary>
    /// <param name="adminUserId">Id of the approving administrator.</param>
    public void Approve(Guid adminUserId)
    {
        if (Status is not (VendorStatus.PendingReview or VendorStatus.UnderReview or VendorStatus.Rejected))
        {
            throw new DomainException($"A vendor application in status '{Status}' cannot be approved.");
        }

        Status = VendorStatus.Approved;
        ReviewedByUserId = adminUserId;
        ReviewedAtUtc = DateTime.UtcNow;
        ApprovedAtUtc = ReviewedAtUtc;
        RejectionReason = null;
        Touch();
    }

    /// <summary>Rejects the application with a mandatory reason. Legal sources: PendingReview, UnderReview.</summary>
    /// <param name="adminUserId">Id of the rejecting administrator.</param>
    /// <param name="reason">Human readable rejection reason shown to the vendor.</param>
    public void Reject(Guid adminUserId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("A rejection reason is required.");
        }

        if (Status is not (VendorStatus.PendingReview or VendorStatus.UnderReview))
        {
            throw new DomainException($"A vendor application in status '{Status}' cannot be rejected.");
        }

        Status = VendorStatus.Rejected;
        RejectionReason = reason.Trim();
        ReviewedByUserId = adminUserId;
        ReviewedAtUtc = DateTime.UtcNow;
        Touch();
    }

    /// <summary>Suspends an approved vendor. Legal source: Approved.</summary>
    /// <param name="reason">Human readable suspension reason.</param>
    public void Suspend(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("A suspension reason is required.");
        }

        if (Status is not VendorStatus.Approved)
        {
            throw new DomainException($"Only approved vendors can be suspended (current status '{Status}').");
        }

        Status = VendorStatus.Suspended;
        SuspensionReason = reason.Trim();
        Touch();
    }

    /// <summary>Bumps the modified timestamp after a state transition.</summary>
    private void Touch() => ModifiedAtUtc = DateTime.UtcNow;
}
