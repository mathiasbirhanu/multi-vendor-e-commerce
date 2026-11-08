namespace MarketHub.Modules.Identity.Domain.Documents;

/// <summary>
/// An uploaded document (registration or vendor re-apply). The binary content lives in
/// <c>IFileStorage</c> under <see cref="StorageKey"/>; this entity keeps the metadata and verification state.
/// </summary>
public sealed class Document : BaseEntity
{
    /// <summary>EF Core constructor.</summary>
    private Document()
    {
    }

    /// <summary>Internal constructor; <paramref name="id"/> allows deterministic seeding.</summary>
    private Document(Guid? id)
    {
        Id = id ?? Guid.NewGuid();
        CreatedAtUtc = DateTime.UtcNow;
        ModifiedAtUtc = CreatedAtUtc;
    }

    /// <summary>Owner user id (vendor or buyer).</summary>
    public Guid OwnerId { get; private set; }

    /// <summary>Referenced document type.</summary>
    public Guid DocumentTypeId { get; private set; }

    /// <summary>Original file name.</summary>
    public string FileName { get; private set; } = null!;

    /// <summary>MIME content type (application/pdf, image/jpeg, image/png).</summary>
    public string ContentType { get; private set; } = null!;

    /// <summary>Storage key returned by <c>IFileStorage</c>.</summary>
    public string StorageKey { get; private set; } = null!;

    /// <summary>SHA-256 hex digest of the uploaded content (integrity/dedup).</summary>
    public string SHA256 { get; private set; } = null!;

    /// <summary>Size of the decoded content in bytes.</summary>
    public long SizeBytes { get; private set; }

    /// <summary>Verification status.</summary>
    public DocumentStatus Status { get; private set; }

    /// <summary>Reason recorded when the document was rejected.</summary>
    public string? RejectionReason { get; private set; }

    /// <summary>UTC instant of upload.</summary>
    public DateTime UploadedAtUtc { get; private set; }

    /// <summary>Administrator who verified the document.</summary>
    public Guid? VerifiedByUserId { get; private set; }

    /// <summary>UTC instant of verification.</summary>
    public DateTime? VerifiedAtUtc { get; private set; }

    /// <summary>Creates a document in <see cref="DocumentStatus.Pending"/>.</summary>
    /// <param name="ownerId">Owner user id.</param>
    /// <param name="documentTypeId">Document type id.</param>
    /// <param name="fileName">Original file name.</param>
    /// <param name="contentType">MIME type.</param>
    /// <param name="storageKey">Storage key returned by <c>IFileStorage</c>.</param>
    /// <param name="sha256">SHA-256 hex digest of the content.</param>
    /// <param name="sizeBytes">Decoded content size in bytes.</param>
    /// <param name="id">Optional deterministic id (used by the seeder).</param>
    public static Document Create(Guid ownerId, Guid documentTypeId, string fileName, string contentType, string storageKey, string sha256, long sizeBytes, Guid? id = null)
    {
        return new Document(id)
        {
            OwnerId = ownerId,
            DocumentTypeId = documentTypeId,
            FileName = fileName.Trim(),
            ContentType = contentType.Trim(),
            StorageKey = storageKey,
            SHA256 = sha256,
            SizeBytes = sizeBytes,
            Status = DocumentStatus.Pending,
            UploadedAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>Marks a pending document as verified.</summary>
    /// <param name="verifiedByUserId">Administrator performing the verification.</param>
    public void MarkVerified(Guid verifiedByUserId)
    {
        if (Status is not DocumentStatus.Pending)
        {
            throw new DomainException($"Only pending documents can be verified (current status '{Status}').");
        }

        Status = DocumentStatus.Verified;
        VerifiedByUserId = verifiedByUserId;
        VerifiedAtUtc = DateTime.UtcNow;
        ModifiedAtUtc = VerifiedAtUtc.Value;
    }

    /// <summary>Marks a pending document as rejected with a reason.</summary>
    /// <param name="reason">Human readable rejection reason.</param>
    public void MarkRejected(string reason)
    {
        if (Status is not DocumentStatus.Pending)
        {
            throw new DomainException($"Only pending documents can be rejected (current status '{Status}').");
        }

        Status = DocumentStatus.Rejected;
        RejectionReason = reason.Trim();
        ModifiedAtUtc = DateTime.UtcNow;
    }
}
