using MarketHub.Shared;
using Microsoft.EntityFrameworkCore;

namespace MarketHub.Modules.Notifications.Persistence;

/// <summary>Notifications module store (schema "notifications"). Also hosts the shared outbox table.</summary>
public class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options, MarketHub.Shared.ICurrentUser currentUser)
    : MarketHubDbContextBase<NotificationsDbContext>(options, currentUser)
{
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();
    public DbSet<OutboundMessage> OutboundMessages => Set<OutboundMessage>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("notifications");
        b.Entity<NotificationLog>(e =>
        {
            e.ToTable("notification_logs");
            e.HasIndex(x => new { x.UserId, x.CreatedAtUtc });
            e.Property(x => x.Subject).HasMaxLength(200).IsRequired();
            e.Property(x => x.Body).HasMaxLength(2000).IsRequired();
        });
        b.Entity<OutboundMessage>(e =>
        {
            e.ToTable("outbound_messages");
            e.HasIndex(x => new { x.Channel, x.Status });
            e.HasIndex(x => x.UserId);
            e.Property(x => x.Template).HasMaxLength(60).IsRequired();
            e.Property(x => x.Destination).HasMaxLength(320).IsRequired();
            e.Property(x => x.PayloadJson);
            e.Property(x => x.Provider).HasMaxLength(40).IsRequired();
            e.Property(x => x.ProviderRef).HasMaxLength(120);
            e.Property(x => x.Error).HasMaxLength(1000);
        });
        b.Entity<OutboxMessage>(e => e.ToTable("outbox_messages", "outbox"));
    }
}
