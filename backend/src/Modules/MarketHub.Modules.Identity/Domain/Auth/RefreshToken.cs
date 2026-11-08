namespace MarketHub.Modules.Identity.Domain.Auth;

/// <summary>
/// Opaque refresh token issued on login and rotated on every refresh.
/// Development note: the token value is stored in plain text for the development environment;
/// swap in hashing (e.g. SHA-256 of the token value) before production hardening.
/// </summary>
public sealed class RefreshToken : BaseEntity
{
    /// <summary>EF Core constructor.</summary>
    private RefreshToken()
    {
    }

    /// <summary>Internal constructor; <paramref name="id"/> allows deterministic seeding.</summary>
    private RefreshToken(Guid? id)
    {
        Id = id ?? Guid.NewGuid();
        CreatedAtUtc = DateTime.UtcNow;
        ModifiedAtUtc = CreatedAtUtc;
    }

    /// <summary>The user the token belongs to.</summary>
    public Guid UserId { get; private set; }

    /// <summary>The opaque token value (unique index).</summary>
    public string Token { get; private set; } = null!;

    /// <summary>UTC instant after which the token can no longer be exchanged.</summary>
    public DateTime ExpiresAtUtc { get; private set; }

    /// <summary>UTC instant at which the token was rotated/revoked (null while valid).</summary>
    public DateTime? RevokedAtUtc { get; private set; }

    /// <summary>Token value that replaced this one during rotation.</summary>
    public string? ReplacedByToken { get; private set; }

    /// <summary>IP address that requested the token (audit).</summary>
    public string? CreatedByIp { get; private set; }

    /// <summary>Creates a new refresh token.</summary>
    /// <param name="userId">Owning user id.</param>
    /// <param name="token">Opaque token value (256-bit random string).</param>
    /// <param name="expiresAtUtc">Expiry instant.</param>
    /// <param name="createdByIp">Requesting IP address (optional).</param>
    /// <param name="id">Optional deterministic id (used by the seeder).</param>
    public static RefreshToken Create(Guid userId, string token, DateTime expiresAtUtc, string? createdByIp = null, Guid? id = null)
    {
        return new RefreshToken(id)
        {
            UserId = userId,
            Token = token,
            ExpiresAtUtc = expiresAtUtc,
            CreatedByIp = createdByIp
        };
    }

    /// <summary>Revokes (rotates) the token, recording the replacement token value.</summary>
    /// <param name="replacedByToken">The token value issued in place of this one.</param>
    public void Revoke(string? replacedByToken)
    {
        RevokedAtUtc = DateTime.UtcNow;
        ReplacedByToken = replacedByToken;
        ModifiedAtUtc = RevokedAtUtc.Value;
    }

    /// <summary>Returns true when the token is past its expiry.</summary>
    public bool IsExpiredAt(DateTime utcNow) => ExpiresAtUtc <= utcNow;

    /// <summary>Returns true when the token has been revoked.</summary>
    public bool HasBeenRevoked => RevokedAtUtc is not null;

    /// <summary>Returns true while the token can still be exchanged for a new pair.</summary>
    public bool IsValidAt(DateTime utcNow) => !HasBeenRevoked && !IsExpiredAt(utcNow);
}
