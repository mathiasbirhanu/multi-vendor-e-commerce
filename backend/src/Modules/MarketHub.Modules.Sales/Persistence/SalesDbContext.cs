using MarketHub.Shared;
using MarketHub.Modules.Sales.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketHub.Modules.Sales.Persistence;

/// <summary>Sales module store (schema "sales"). Also hosts the shared outbox table.</summary>
public class SalesDbContext(DbContextOptions<SalesDbContext> options, MarketHub.Shared.ICurrentUser currentUser)
    : MarketHubDbContextBase<SalesDbContext>(options, currentUser)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderEvent> OrderEvents => Set<OrderEvent>();
    public DbSet<Address> Addresses => Set<Address>();
    public DbSet<CartItem> CartItems => Set<CartItem>();
    public DbSet<Cart> Carts => Set<Cart>();
    public DbSet<ReturnRequest> ReturnRequests => Set<ReturnRequest>();
    public DbSet<DeliveryZone> DeliveryZones => Set<DeliveryZone>();
    public DbSet<DeliveryRate> DeliveryRates => Set<DeliveryRate>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("sales");
        b.Entity<Order>(e =>
        {
            e.ToTable("orders");
            e.HasIndex(x => x.OrderNumber).IsUnique();
            e.HasIndex(x => new { x.BuyerId, x.Status });
            e.Property(x => x.OrderNumber).HasMaxLength(24).IsRequired();
            e.Property(x => x.AddressLine).HasMaxLength(300).IsRequired();
            e.Property(x => x.City).HasMaxLength(120).IsRequired();
            e.Property(x => x.ZoneNameSnapshot).HasMaxLength(120).IsRequired();
            e.Property(x => x.Phone).HasMaxLength(20).IsRequired();
            e.Property(x => x.SubtotalETB).HasPrecision(18, 2);
            e.Property(x => x.DeliveryFeeETB).HasPrecision(18, 2);
            e.Property(x => x.VatRatePercent).HasPrecision(5, 2);
            e.Property(x => x.VatETB).HasPrecision(18, 2);
            e.Property(x => x.GrandTotalETB).HasPrecision(18, 2);
            e.Property(x => x.CouponDiscountETB).HasPrecision(18, 2);
            e.Property(x => x.DeliveryPin).HasMaxLength(4);
            e.Property(x => x.TrackingCode).HasMaxLength(24);
            e.Property(x => x.CouponCode).HasMaxLength(40);
            e.Property(x => x.RowVersion).IsRowVersion();
        });
        b.Entity<OrderItem>(e =>
        {
            e.ToTable("order_items");
            e.HasIndex(x => x.OrderId);
            e.HasIndex(x => x.VendorId);
            e.HasIndex(x => x.ProductId);
            e.Property(x => x.TitleSnapshot).HasMaxLength(220).IsRequired();
            e.Property(x => x.ColorNameSnapshot).HasMaxLength(40);
            e.Property(x => x.ColorHexSnapshot).HasMaxLength(9);
            e.Property(x => x.VariantSku).HasMaxLength(40);
            e.Property(x => x.VariantLabel).HasMaxLength(90);
            e.Property(x => x.UnitPriceETB).HasPrecision(18, 2);
            e.Property(x => x.LineNetETB).HasPrecision(18, 2);
            e.Property(x => x.VatETB).HasPrecision(18, 2);
            e.Property(x => x.CommissionRateSnapshot).HasPrecision(5, 4);
            e.Property(x => x.CommissionETB).HasPrecision(18, 2);
            e.Property(x => x.DeliveryFeeAllocationETB).HasPrecision(18, 2);
            e.HasOne<Order>().WithMany(o => o.Items).HasForeignKey(x => x.OrderId);
        });
        b.Entity<OrderEvent>(e =>
        {
            e.ToTable("order_events");
            e.HasIndex(x => new { x.OrderId, x.CreatedAtUtc });
            e.Property(x => x.Status).HasMaxLength(30).IsRequired();
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.ByRole).HasMaxLength(20).IsRequired();
            e.HasOne<Order>().WithMany(o => o.Events).HasForeignKey(x => x.OrderId);
        });
        b.Entity<Address>(e =>
        {
            e.ToTable("addresses");
            e.HasIndex(x => x.UserId);
            e.Property(x => x.Label).HasMaxLength(40).IsRequired();
            e.Property(x => x.FullName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Phone).HasMaxLength(20).IsRequired();
            e.Property(x => x.Region).HasMaxLength(120).IsRequired();
            e.Property(x => x.City).HasMaxLength(120).IsRequired();
            e.Property(x => x.SubCity).HasMaxLength(120).IsRequired();
            e.Property(x => x.Woreda).HasMaxLength(120).IsRequired();
            e.Property(x => x.Kebele).HasMaxLength(120);
            e.Property(x => x.Landmark).HasMaxLength(300);
        });
        b.Entity<CartItem>(e =>
        {
            e.ToTable("cart_items");
            // One line per (user, product, variant/color): the same product in two variants = two lines.
            e.HasIndex(x => new { x.UserId, x.ProductId, x.VariantId, x.ColorId });
        });
        b.Entity<Cart>(e =>
        {
            e.ToTable("carts");
            e.HasIndex(x => x.UserId).IsUnique();
            e.HasIndex(x => x.UpdatedAtUtc);
        });
        b.Entity<ReturnRequest>(e =>
        {
            e.ToTable("return_requests");
            e.HasIndex(x => x.OrderId);
            e.HasIndex(x => x.OrderItemId);
            e.HasIndex(x => new { x.Status, x.CreatedAtUtc });
            e.Property(x => x.Description).HasMaxLength(2000).IsRequired();
            e.Property(x => x.VendorNote).HasMaxLength(1000);
            e.Property(x => x.AdminDecision).HasMaxLength(1000);
            e.Property(x => x.RefundETB).HasPrecision(18, 2);
        });
        b.Entity<DeliveryZone>(e =>
        {
            e.ToTable("delivery_zones");
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
        });
        b.Entity<DeliveryRate>(e =>
        {
            e.ToTable("delivery_rates");
            e.HasIndex(x => x.ZoneId).IsUnique();
            e.Property(x => x.BaseFeeETB).HasPrecision(18, 2);
            e.Property(x => x.PerKgSurchargeETB).HasPrecision(18, 2);
            e.Property(x => x.FreeThresholdETB).HasPrecision(18, 2);
        });
        b.Entity<OutboxMessage>(e => e.ToTable("outbox_messages", "outbox"));
    }
}
