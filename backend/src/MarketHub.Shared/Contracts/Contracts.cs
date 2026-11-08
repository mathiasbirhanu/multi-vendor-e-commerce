namespace MarketHub.Shared.Contracts;

// Cross-module read/write contracts. Modules depend ONLY on these interfaces (defined here in Shared),
// and each consumer module registers its implementation in DI — no direct module-to-module references.

/// <summary>Top-product aggregate row for reports.</summary>
public sealed record TopProductRow(Guid ProductId, string Title, int SoldQty, decimal RevenueETB);

/// <summary>Financial snapshot of a product color variant used by the Sales module.</summary>
public sealed record CatalogColorSnapshot(Guid Id, Guid ProductId, string Name, string Hex, decimal ExtraPriceETB, int Stock);

/// <summary>SKU-level variant snapshot (size × optional color) used by Sales cart/checkout.</summary>
public sealed record CatalogVariantSnapshot(Guid Id, Guid ProductId, string Sku, string? Size, decimal? PriceETB, int Stock, bool IsActive, Guid? ColorId);

/// <summary>Catalog operations needed by other modules.</summary>
public interface ICatalogReader
{
    Task<CatalogProductSnapshot?> FindActiveAsync(Guid productId, CancellationToken ct = default);
    Task<bool> TryDecreaseStockAsync(Guid productId, int quantity, CancellationToken ct = default);
    Task<CatalogColorSnapshot?> FindColorAsync(Guid colorId, CancellationToken ct = default);
    Task<bool> TryDecreaseColorStockAsync(Guid colorId, int quantity, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, string>> GetProductTitlesAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default);

    // ---- gap-fill v2: variants, reservation-aware availability, restock, vendor stats ----
    Task<CatalogVariantSnapshot?> FindVariantAsync(Guid variantId, CancellationToken ct = default);
    Task<bool> TryDecreaseVariantStockAsync(Guid variantId, int quantity, CancellationToken ct = default);
    /// <summary>Reservation-aware availability = stock − Σ active reservations.</summary>
    Task<int> GetAvailableStockAsync(Guid productId, CancellationToken ct = default);
    Task<int> GetAvailableVariantStockAsync(Guid variantId, CancellationToken ct = default);
    Task<int> GetAvailableColorStockAsync(Guid colorId, CancellationToken ct = default);
    /// <summary>Returns stock after a refund (product + variant/color when present).</summary>
    Task RestockAsync(Guid productId, Guid? variantId, Guid? colorId, int quantity, CancellationToken ct = default);
    /// <summary>Approved-review aggregates across a vendor's products.</summary>
    Task<(decimal RatingAvg, int RatingCount)> GetVendorReviewStatsAsync(Guid vendorId, CancellationToken ct = default);
    /// <summary>Active product count + how many are low on stock (≤5 units available).</summary>
    Task<(int Products, int LowStock)> GetVendorProductStatsAsync(Guid vendorId, CancellationToken ct = default);
}

/// <summary>SKU-level stock reservation book (owned by Catalog, orchestrated by Sales).</summary>
public sealed record ReservationRequestLine(Guid ProductId, Guid? VariantId, Guid? ColorId, int Quantity);
public sealed record ReservationLineResult(string Key, bool Ok, int Available);
public sealed record ConsumedReservationLine(Guid ProductId, Guid? VariantId, Guid? ColorId, int Quantity);

public interface IStockReservations
{
    /// <summary>Releases the user's previous Active reservations, then reserves each line while availability lasts.</summary>
    Task<IReadOnlyList<ReservationLineResult>> ReserveForUserAsync(Guid userId, IReadOnlyList<ReservationRequestLine> lines, TimeSpan ttl, Guid? orderId = null, CancellationToken ct = default);
    /// <summary>Releases every Active reservation of the user (cart edited/abandoned). Returns released count.</summary>
    Task<int> ReleaseForUserAsync(Guid userId, CancellationToken ct = default);
    /// <summary>Releases Active reservations bound to an order (buyer cancelled before payment).</summary>
    Task<int> ReleaseForOrderAsync(Guid orderId, CancellationToken ct = default);
    /// <summary>Decrements catalog stock and marks the order's reservations Consumed (payment confirmed). Returns the consumed lines.</summary>
    Task<IReadOnlyList<ConsumedReservationLine>> TryConsumeForOrderAsync(Guid orderId, CancellationToken ct = default);
    /// <summary>Expires Active reservations past their TTL (ReservationSweeper). Returns expired count.</summary>
    Task<int> ExpireStaleAsync(CancellationToken ct = default);
    Task<int> GetActiveReservedQtyAsync(Guid productId, Guid? variantId, Guid? colorId, CancellationToken ct = default);
}

/// <summary>Consumes stock for a paid order (called from the payment-confirmed path). Idempotent per order.</summary>
public interface IOrderFulfillment
{
    Task<bool> TryConsumeOrderStockAsync(Guid orderId, CancellationToken ct = default);
}

/// <summary>Coupon projection for cross-module validation/redemption (owned by Catalog).</summary>
public sealed record CouponQuote(string Code, string Type, decimal Value, decimal MinSubtotal, decimal? MaxDiscount, int MaxUses, int UsedCount, DateTime? StartsAt, DateTime? ExpiresAt, bool IsActive);

public interface ICouponService
{
    Task<CouponQuote?> FindActiveAsync(string code, CancellationToken ct = default);
    /// <summary>Records a redemption row and increments the coupon's used count (checkout time).</summary>
    Task RecordRedemptionAsync(string code, Guid orderId, Guid userId, decimal discountETB, CancellationToken ct = default);
}

/// <summary>Contact + locale projection of a user (owned by Identity, consumed by Notifications).</summary>
public interface IUserDirectory
{
    Task<(string? Email, string? Phone, string Lang)> GetContactAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> GetAdminUserIdsAsync(CancellationToken ct = default);
}

/// <summary>Denormalized vendor review counter (Catalog recomputes → Identity stores).</summary>
public interface IVendorStatsUpdater
{
    Task UpdateRatingCountAsync(Guid vendorId, int ratingCount, CancellationToken ct = default);
}

/// <summary>Abandoned-cart snapshots (owned by Sales, consumed by Notifications dispatcher/jobs).</summary>
public sealed record AbandonedCartRow(Guid UserId, string ItemsJson, DateTime UpdatedAtUtc, DateTime? ReminderSentAtUtc);

public interface IAbandonedCartReader
{
    /// <summary>Carts idle longer than <paramref name="idleFor"/> without a reminder.</summary>
    Task<IReadOnlyList<AbandonedCartRow>> GetStaleCartsAsync(TimeSpan idleFor, CancellationToken ct = default);
    Task MarkRemindedAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>Product document pushed to the search engine (Meilisearch) on create/update/moderation.</summary>
public sealed record ProductSearchDoc(Guid Id, string Title, string Description, string Category, decimal Price, string? Vendor, decimal Rating, int Sold, string Slug, string? Thumb);

/// <summary>Search index abstraction. <see cref="SearchAsync"/> returns null when the provider cannot serve queries (SQL fallback).</summary>
public interface ISearchIndexer
{
    Task IndexProductAsync(ProductSearchDoc doc, CancellationToken ct = default);
    Task RemoveAsync(Guid productId, CancellationToken ct = default);
    Task<IReadOnlyList<ProductSearchDoc>?> SearchAsync(string query, int limit, CancellationToken ct = default);
}

/// <summary>Commission resolution precedence: vendor+category → vendor → category → global default.</summary>
public interface ICommissionResolver
{
    Task<decimal> ResolveAsync(Guid? vendorId, Guid? categoryId, CancellationToken ct = default);
}

/// <summary>Financial posting of confirmed payments and refunds into the double-entry ledger.</summary>
public interface ILedgerPoster
{
    Task PostOrderPaymentAsync(Guid orderId, CancellationToken ct = default);
    Task PostRefundAsync(Guid orderId, decimal amount, CancellationToken ct = default);
}

/// <summary>Financial snapshot of an order used by the ledger poster.</summary>
public sealed record OrderFinancials(Guid OrderId, Guid BuyerId, decimal SubtotalETB, decimal DeliveryFeeETB, decimal VatETB, decimal GrandTotalETB, IReadOnlyList<OrderItemFinancials> Items);
public sealed record OrderItemFinancials(Guid OrderItemId, Guid? VendorId, decimal LineNetETB, decimal CommissionETB, decimal DeliveryFeeAllocationETB);

/// <summary>Sales-module read operations needed by Finance.</summary>
public interface IOrderReader
{
    Task<OrderFinancials?> GetFinancialsAsync(Guid orderId, CancellationToken ct = default);
    Task<long> CountOrdersAsync(DateTime? fromUtc = null, Guid? vendorId = null, CancellationToken ct = default);
    Task<IReadOnlyList<(Guid ProductId, int SoldQty, decimal RevenueETB)>> GetTopProductAggregatesAsync(int limit, DateTime? fromUtc = null, CancellationToken ct = default);
    /// <summary>Status string when the order belongs to the buyer AND contains the product (review verified-purchase gate); otherwise null.</summary>
    Task<string?> GetOrderStatusForBuyerProductAsync(Guid buyerId, Guid orderId, Guid productId, CancellationToken ct = default);
}

/// <summary>Vendor status check needed by Catalog (only approved vendors may list).</summary>
public interface IMarketplaceVendorChecker
{
    Task<bool> IsApprovedVendorAsync(Guid userId, CancellationToken ct = default);
    Task<string?> GetBusinessNameAsync(Guid userId, CancellationToken ct = default);
}
