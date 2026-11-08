using MarketHub.Shared;
using MarketHub.Modules.Catalog.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketHub.Modules.Catalog.Persistence;

/// <summary>Catalog module store (schema "catalog"). Also hosts the shared outbox table.</summary>
public class CatalogDbContext(DbContextOptions<CatalogDbContext> options, MarketHub.Shared.ICurrentUser currentUser)
    : MarketHubDbContextBase<CatalogDbContext>(options, currentUser)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductColor> ProductColors => Set<ProductColor>();
    public DbSet<ProductImage> ProductImages => Set<ProductImage>();
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<WishlistItem> WishlistItems => Set<WishlistItem>();
    public DbSet<Banner> Banners => Set<Banner>();
    public DbSet<Coupon> Coupons => Set<Coupon>();
    public DbSet<CouponRedemption> CouponRedemptions => Set<CouponRedemption>();
    public DbSet<StockReservation> StockReservations => Set<StockReservation>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("catalog");
        b.Entity<Category>(e =>
        {
            e.ToTable("categories");
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
        });
        b.Entity<Product>(e =>
        {
            e.ToTable("products");
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => new { x.VendorId, x.Status });
            e.HasIndex(x => x.CategoryId);
            e.HasIndex(x => x.SaleEndsAtUtc);
            e.Property(x => x.Title).HasMaxLength(220).IsRequired();
            e.Property(x => x.PriceETB).HasPrecision(18, 2);
            e.Property(x => x.CompareAtPriceETB).HasPrecision(18, 2);
            e.Property(x => x.SalePriceETB).HasPrecision(18, 2);
            e.Property(x => x.WeightKg).HasPrecision(10, 2);
            e.Property(x => x.RatingAvg).HasPrecision(3, 2);
            e.Property(x => x.SKU).HasMaxLength(40);
        });
        b.Entity<ProductColor>(e =>
        {
            e.ToTable("product_colors");
            e.HasIndex(x => x.ProductId);
            e.Property(x => x.Name).HasMaxLength(40).IsRequired();
            e.Property(x => x.Hex).HasMaxLength(9).IsRequired();
            e.Property(x => x.ExtraPriceETB).HasPrecision(18, 2);
        });
        b.Entity<ProductImage>(e =>
        {
            e.ToTable("product_images");
            e.HasIndex(x => new { x.ProductId, x.SortOrder });
            e.Property(x => x.StorageKey).HasMaxLength(300).IsRequired();
            e.Property(x => x.Alt).HasMaxLength(60);
            e.HasOne<ProductColor>()
                .WithMany()
                .HasForeignKey(x => x.ColorId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ProductVariant>(e =>
        {
            e.ToTable("product_variants");
            e.HasIndex(x => x.SKU).IsUnique();
            e.HasIndex(x => x.ProductId);
            e.Property(x => x.SKU).HasMaxLength(40).IsRequired();
            e.Property(x => x.Size).HasMaxLength(40);
            e.Property(x => x.PriceETB).HasPrecision(18, 2);
            e.HasOne<ProductColor>()
                .WithMany()
                .HasForeignKey(x => x.ColorId)
                .OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<Review>(e =>
        {
            e.ToTable("reviews");
            e.HasIndex(x => new { x.UserId, x.ProductId, x.OrderId }).IsUnique();
            e.HasIndex(x => new { x.ProductId, x.Status });
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Body).HasMaxLength(4000).IsRequired();
            e.Property(x => x.VendorReply).HasMaxLength(2000);
            e.Property(x => x.ModerationNote).HasMaxLength(1000);
        });
        b.Entity<WishlistItem>(e =>
        {
            e.ToTable("wishlist_items");
            e.HasIndex(x => new { x.UserId, x.ProductId }).IsUnique();
        });
        b.Entity<Banner>(e =>
        {
            e.ToTable("banners");
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.ImageUrl).HasMaxLength(500).IsRequired();
            e.Property(x => x.Link).HasMaxLength(500);
            e.Property(x => x.Position).HasMaxLength(20).IsRequired();
        });
        b.Entity<Coupon>(e =>
        {
            e.ToTable("coupons");
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(40).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Value).HasPrecision(18, 2);
            e.Property(x => x.MinSubtotal).HasPrecision(18, 2);
            e.Property(x => x.MaxDiscount).HasPrecision(18, 2);
        });
        b.Entity<CouponRedemption>(e =>
        {
            e.ToTable("coupon_redemptions");
            e.HasIndex(x => x.CouponId);
            e.HasIndex(x => new { x.CouponId, x.OrderId }).IsUnique();
            e.Property(x => x.DiscountETB).HasPrecision(18, 2);
        });
        b.Entity<StockReservation>(e =>
        {
            e.ToTable("stock_reservations");
            e.HasIndex(x => new { x.UserId, x.Status });
            e.HasIndex(x => x.OrderId);
            e.HasIndex(x => x.ExpiresAtUtc);
        });
        b.Entity<OutboxMessage>(e => e.ToTable("outbox_messages", "outbox"));
    }
}
