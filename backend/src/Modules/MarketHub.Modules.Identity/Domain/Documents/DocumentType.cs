using MarketHub.Modules.Identity.Domain.Users;

namespace MarketHub.Modules.Identity.Domain.Documents;

/// <summary>
/// Catalog of KYC document types (business licence, TIN certificate, ...) with per-role requirements.
/// </summary>
public sealed class DocumentType : BaseEntity
{
    /// <summary>EF Core constructor.</summary>
    private DocumentType()
    {
    }

    /// <summary>Internal constructor; <paramref name="id"/> allows deterministic seeding.</summary>
    private DocumentType(Guid? id)
    {
        Id = id ?? Guid.NewGuid();
        CreatedAtUtc = DateTime.UtcNow;
        ModifiedAtUtc = CreatedAtUtc;
    }

    /// <summary>Stable code used by API payloads, e.g. "business_license".</summary>
    public string Code { get; private set; } = null!;

    /// <summary>Human readable name.</summary>
    public string Name { get; private set; } = null!;

    /// <summary>Role the document type applies to (Vendor or Buyer).</summary>
    public UserRole AppliesTo { get; private set; }

    /// <summary>True when the registration cannot complete without this document.</summary>
    public bool IsRequired { get; private set; }

    /// <summary>Minimum number of files that must be uploaded.</summary>
    public int MinFiles { get; private set; }

    /// <summary>Maximum number of files accepted.</summary>
    public int MaxFiles { get; private set; }

    /// <summary>Short description of what the document must contain.</summary>
    public string? Description { get; private set; }

    /// <summary>Display ordering.</summary>
    public int SortOrder { get; private set; }

    /// <summary>Creates a document type definition.</summary>
    /// <param name="code">Stable code, e.g. "business_license".</param>
    /// <param name="name">Human readable name.</param>
    /// <param name="appliesTo">Role the type applies to.</param>
    /// <param name="isRequired">Whether the type is mandatory.</param>
    /// <param name="minFiles">Minimum files required (0 for optional types).</param>
    /// <param name="maxFiles">Maximum files accepted.</param>
    /// <param name="description">What the document must contain.</param>
    /// <param name="sortOrder">Display ordering.</param>
    /// <param name="id">Optional deterministic id (used by the seeder).</param>
    public static DocumentType Create(string code, string name, UserRole appliesTo, bool isRequired, int minFiles, int maxFiles, string? description, int sortOrder, Guid? id = null)
    {
        if (minFiles < 0)
        {
            throw new DomainException("MinFiles cannot be negative.");
        }

        if (maxFiles < minFiles)
        {
            throw new DomainException("MaxFiles must be greater than or equal to MinFiles.");
        }

        return new DocumentType(id)
        {
            Code = code.Trim(),
            Name = name.Trim(),
            AppliesTo = appliesTo,
            IsRequired = isRequired,
            MinFiles = minFiles,
            MaxFiles = maxFiles,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            SortOrder = sortOrder
        };
    }
}
