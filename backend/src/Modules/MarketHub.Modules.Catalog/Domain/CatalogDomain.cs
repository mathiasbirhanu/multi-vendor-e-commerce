using MarketHub.Shared;

namespace MarketHub.Modules.Catalog.Domain;

public enum ProductStatus { Draft, PendingReview, Active, Rejected, Archived }

public class Category : BaseEntity
{
    public string Name { get; set; } = default!;
    public string Slug { get; set; } = default!;
    public Guid? ParentId { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A sellable product. VendorId == null → platform (first-party) product owned by MarketHub Store.</summary>
public class Product : BaseEntity
{
    public Guid? VendorId { get; set; }
    public string? VendorNameSnapshot { get; set; }
    public Guid CategoryId { get; set; }
    public string Title { get; set; } = default!;
    public string Slug { get; set; } = default!;
    public string Description { get; set; } = default!;
    public decimal PriceETB { get; set; }
    public decimal? CompareAtPriceETB { get; set; }
    /// <summary>Flash-sale price (set by POST /api/admin/flash-sale; cleared by the FlashSaleScheduler after SaleEndsAtUtc).</summary>
    public decimal? SalePriceETB { get; set; }
    public DateTime? SaleEndsAtUtc { get; set; }
    /// <summary>Authoritative total stock. With color variants this is the sum of per-color stock; with SKU variants the sum of per-variant stock.</summary>
    public int Stock { get; set; }
    public string SKU { get; set; } = default!;
    public decimal WeightKg { get; set; } = 1m;
    public ProductStatus Status { get; set; }
    public string? ModerationNote { get; set; }
    public bool IsFeatured { get; set; }
    public decimal RatingAvg { get; set; } = 4.5m;
    public int RatingCount { get; set; }
    public int ViewCount { get; set; }
    /// <summary>Completed units sold (incremented on payment confirmation).</summary>
    public int SoldCount { get; set; }

    public IReadOnlyList<ProductColor> Colors { get; set; } = Array.Empty<ProductColor>();
    public IReadOnlyList<ProductImage> Images { get; set; } = Array.Empty<ProductImage>();

    public bool TryDecreaseStock(int quantity)
    {
        if (Stock < quantity) return false;
        Stock -= quantity;
        return true;
    }

    /// <summary>Effective selling price right now (flash sale price while the sale window is open).</summary>
    public decimal EffectivePriceUtc(DateTime nowUtc)
        => SalePriceETB.HasValue && SaleEndsAtUtc.HasValue && SaleEndsAtUtc.Value > nowUtc ? SalePriceETB.Value : PriceETB;
}

/// <summary>
/// Color variant of a product (e.g. "Midnight Blue" #1b2a4a). Carries its own stock and an
/// optional price delta on top of the base price; owns its own gallery subset.
/// </summary>
public class ProductColor : BaseEntity
{
    public Guid ProductId { get; set; }
    public string Name { get; set; } = default!;
    /// <summary>Swatch hex like #1a1a1a.</summary>
    public string Hex { get; set; } = default!;
    public int Stock { get; set; }
    public decimal ExtraPriceETB { get; set; }
    public int SortOrder { get; set; }

    public bool TryDecreaseStock(int quantity)
    {
        if (Stock < quantity) return false;
        Stock -= quantity;
        return true;
    }
}

/// <summary>
/// Product photo. ColorId == null → shared shot shown for every color; otherwise the photo
/// belongs to that color variant's gallery. StorageKey is either an object-storage key
/// (served via /api/media/{key}) or an absolute https URL when externally hosted.
/// Alt is the angle/direction label ("Front", "Back", "Side"…).
/// </summary>
public class ProductImage : BaseEntity
{
    public Guid ProductId { get; set; }
    public Guid? ColorId { get; set; }
    public string StorageKey { get; set; } = default!;
    public string Alt { get; set; } = string.Empty;
    public int SortOrder { get; set; }

    /// <summary>Browser-facing URL: external https URLs pass through; storage keys stream from the media endpoint.</summary>
    public string PublicUrl =>
        StorageKey.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? StorageKey
        : StorageKey.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? StorageKey
        : $"/api/media/{StorageKey}";
}

// ==================== GAP-FILL v2 ENTITIES ====================

/// <summary>
/// SKU-level variant (color × size). VariantId on cart lines / order items takes precedence over
/// the color-only path. PriceETB == null means "use the product base price".
/// </summary>
public class ProductVariant : BaseEntity
{
    public Guid ProductId { get; set; }
    public string SKU { get; set; } = default!;
    public Guid? ColorId { get; set; }
    public string? Size { get; set; }
    public decimal? PriceETB { get; set; }
    public int Stock { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    public bool TryDecreaseStock(int quantity)
    {
        if (Stock < quantity) return false;
        Stock -= quantity;
        return true;
    }

    /// <summary>"Black / 42"-style label from the linked color name + size.</summary>
    public string? Label(string? colorName)
    {
        var parts = new[] { colorName, Size }.Where(s => !string.IsNullOrWhiteSpace(s));
        var label = string.Join(" / ", parts);
        return label.Length == 0 ? null : label;
    }
}

public enum ReviewStatus { Pending, Approved, Rejected }

/// <summary>Verified-purchase product review. One per (user, product, order); visible only after approval.</summary>
public class Review : BaseEntity
{
    public Guid ProductId { get; set; }
    public Guid UserId { get; set; }
    public Guid OrderId { get; set; }
    public int Rating { get; set; } // 1..5
    public string? Title { get; set; }
    public string Body { get; set; } = default!;
    /// <summary>JSON string[] of evidence/photo URLs (null when the reviewer attached none).</summary>
    public string? ImageUrlsJson { get; set; }
    public ReviewStatus Status { get; set; } = ReviewStatus.Pending;
    public string? ModerationNote { get; set; }
    public string? VendorReply { get; set; }
    public DateTime? VendorRepliedAtUtc { get; set; }
}

/// <summary>Buyer wishlist entry (unique per user + product).</summary>
public class WishlistItem : BaseEntity
{
    public Guid UserId { get; set; }
    public Guid ProductId { get; set; }
}

/// <summary>Home-page marketing banner.</summary>
public class Banner : BaseEntity
{
    public string Title { get; set; } = default!;
    public string ImageUrl { get; set; } = default!;
    public string? Link { get; set; }
    public string Position { get; set; } = "hero"; // hero | strip
    public bool IsActive { get; set; } = true;
    public DateTime? StartsAtUtc { get; set; }
    public DateTime? EndsAtUtc { get; set; }
}

public enum CouponType { Percent, Fixed }

/// <summary>Discount coupon validated at checkout (window / min subtotal / max uses / max discount).</summary>
public class Coupon : BaseEntity
{
    public string Code { get; set; } = default!;
    public string? Description { get; set; }
    public CouponType Type { get; set; }
    public decimal Value { get; set; }
    public decimal MinSubtotal { get; set; }
    public decimal? MaxDiscount { get; set; }
    public int MaxUses { get; set; } = 1;
    public int UsedCount { get; set; }
    public DateTime StartsAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>One redemption row per (coupon, order) — written by Sales checkout via ICouponService.</summary>
public class CouponRedemption : BaseEntity
{
    public Guid CouponId { get; set; }
    public Guid OrderId { get; set; }
    public Guid UserId { get; set; }
    public decimal DiscountETB { get; set; }
}

public enum StockReservationStatus { Active, Consumed, Released, Expired }

/// <summary>
/// Protects stock between "add to cart / reserve" and payment. Availability shown to buyers is
/// <c>stock − Σ active reservations</c>; stock itself is only decremented on payment confirmation.
/// </summary>
public class StockReservation : BaseEntity
{
    public Guid UserId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? VariantId { get; set; }
    public Guid? ColorId { get; set; }
    public int Quantity { get; set; }
    public StockReservationStatus Status { get; set; } = StockReservationStatus.Active;
    public DateTime ExpiresAtUtc { get; set; }
    public Guid? OrderId { get; set; }
}

/// <summary>Pure availability math for reservations (unit-tested).</summary>
public static class ReservationMath
{
    /// <summary>available = stock − active reserved, clamped at zero.</summary>
    public static int Available(int stock, int activeReserved) => Math.Max(0, stock - Math.Max(0, activeReserved));

    /// <summary>A reservation of <paramref name="quantity"/> fits when it does not exceed availability.</summary>
    public static bool CanReserve(int stock, int activeReserved, int quantity)
        => quantity > 0 && quantity <= Available(stock, activeReserved);

    /// <summary>Stable per-line key used in reservation API responses.</summary>
    public static string LineKey(Guid productId, Guid? variantId, Guid? colorId)
        => $"p:{productId}" + (variantId.HasValue ? $":v:{variantId.Value}" : colorId.HasValue ? $":c:{colorId.Value}" : "");
}
