using MarketHub.Shared;
using MarketHub.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketHub.Modules.Identity.Persistence;

/// <summary>Identity module store (schema "identity"). Also hosts the shared outbox table.</summary>
public class IdentityDbContext(DbContextOptions<IdentityDbContext> options, MarketHub.Shared.ICurrentUser currentUser)
    : MarketHubDbContextBase<IdentityDbContext>(options, currentUser)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<VendorProfile> VendorProfiles => Set<VendorProfile>();
    public DbSet<DocumentType> DocumentTypes => Set<DocumentType>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<Policy> Policies => Set<Policy>();
    public DbSet<Consent> Consents => Set<Consent>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("identity");
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.Property(x => x.Phone).HasMaxLength(20).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(200).IsRequired();
            e.Property(x => x.FullName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Lang).HasMaxLength(5).HasDefaultValue("en");
            e.HasIndex(x => new { x.Role, x.AdminRole });
        });
        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.UserId);
        });
        b.Entity<VendorProfile>(e =>
        {
            e.ToTable("vendor_profiles");
            e.HasKey(x => x.Id);
            e.Property(x => x.BusinessName).HasMaxLength(200).IsRequired();
            e.Property(x => x.TIN).HasMaxLength(10).IsRequired();
            e.HasIndex(x => x.TIN);
        });
        b.Entity<DocumentType>(e =>
        {
            e.ToTable("document_types");
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(60).IsRequired();
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
        });
        b.Entity<Document>(e =>
        {
            e.ToTable("documents");
            e.HasIndex(x => new { x.OwnerId, x.DocumentTypeId });
            e.HasIndex(x => x.ExpiresAtUtc);
            e.Property(x => x.StorageKey).HasMaxLength(300).IsRequired();
            e.Property(x => x.SHA256).HasMaxLength(64).IsRequired();
        });
        b.Entity<Policy>(e =>
        {
            e.ToTable("policies");
            e.HasIndex(x => new { x.Key, x.Version }).IsUnique();
        });
        b.Entity<Consent>(e =>
        {
            e.ToTable("consents");
            e.HasIndex(x => new { x.UserId, x.PolicyKey, x.PolicyVersion }).IsUnique();
        });
        b.Entity<OutboxMessage>(e => e.ToTable("outbox_messages", "outbox"));
    }
}
