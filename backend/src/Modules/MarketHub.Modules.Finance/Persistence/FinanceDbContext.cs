using MarketHub.Shared;
using MarketHub.Modules.Finance.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketHub.Modules.Finance.Persistence;

/// <summary>Finance module store (schema "finance"): settings, commission, ledger, payouts, outbox.</summary>
public class FinanceDbContext(DbContextOptions<FinanceDbContext> options, MarketHub.Shared.ICurrentUser currentUser)
    : MarketHubDbContextBase<FinanceDbContext>(options, currentUser)
{
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<CommissionOverride> CommissionOverrides => Set<CommissionOverride>();
    public DbSet<LedgerAccount> LedgerAccounts => Set<LedgerAccount>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<PayoutRequest> PayoutRequests => Set<PayoutRequest>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("finance");
        b.Entity<Setting>(e =>
        {
            e.ToTable("settings");
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Key).HasMaxLength(80).IsRequired();
            e.Property(x => x.Value).HasMaxLength(500).IsRequired();
        });
        b.Entity<CommissionOverride>(e =>
        {
            e.ToTable("commission_overrides");
            e.HasIndex(x => new { x.VendorId, x.CategoryId });
            e.Property(x => x.Rate).HasPrecision(5, 4);
        });
        b.Entity<LedgerAccount>(e =>
        {
            e.ToTable("ledger_accounts");
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(10).IsRequired();
        });
        b.Entity<LedgerEntry>(e =>
        {
            e.ToTable("ledger_entries");
            e.HasIndex(x => new { x.OrderId });
            e.HasIndex(x => new { x.VendorId, x.CreatedAtUtc });
            e.Property(x => x.AmountDebitETB).HasPrecision(18, 2);
            e.Property(x => x.AmountCreditETB).HasPrecision(18, 2);
        });
        b.Entity<PayoutRequest>(e =>
        {
            e.ToTable("payout_requests");
            e.HasIndex(x => new { x.VendorId, x.Status });
            e.Property(x => x.AmountETB).HasPrecision(18, 2);
            e.Property(x => x.RowVersion).IsRowVersion();
        });
        b.Entity<OutboxMessage>(e => e.ToTable("outbox_messages", "outbox"));
    }
}
