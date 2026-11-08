namespace MarketHub.Modules.Identity.Domain.Documents;

/// <summary>Verification status of an uploaded document.</summary>
public enum DocumentStatus
{
    /// <summary>Uploaded but not yet reviewed by an administrator.</summary>
    Pending = 0,

    /// <summary>Verified by an administrator.</summary>
    Verified = 1,

    /// <summary>Rejected by an administrator with a reason.</summary>
    Rejected = 2
}
