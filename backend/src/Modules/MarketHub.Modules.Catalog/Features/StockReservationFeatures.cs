using MarketHub.Shared;
using MarketHub.Shared.Contracts;
using MarketHub.Modules.Catalog.Domain;
using MarketHub.Modules.Catalog.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace MarketHub.Modules.Catalog.Features;

/// <summary>
/// Reservation book for SKU/color-level stock (owned by Catalog, orchestrated by Sales).
/// Availability shown to buyers is <c>stock − Σ active reservations</c>; catalog stock is only
/// decremented when payment is confirmed (<see cref="OrderFulfillmentService"/>).
/// </summary>
public sealed class StockReservationService(CatalogDbContext db) : IStockReservations
{
    public async Task<IReadOnlyList<ReservationLineResult>> ReserveForUserAsync(Guid userId, IReadOnlyList<ReservationRequestLine> lines, TimeSpan ttl, Guid? orderId = null, CancellationToken ct = default)
    {
        // a fresh reservation round replaces the user's previous Active ones (cart edited / re-reserved)
        await ReleaseForUserAsync(userId, ct);

        var now = DateTime.UtcNow;
        var results = new List<ReservationLineResult>();
        foreach (var line in lines)
        {
            if (line.Quantity <= 0)
            {
                results.Add(new ReservationLineResult(ReservationMath.LineKey(line.ProductId, line.VariantId, line.ColorId), false, 0));
                continue;
            }

            int available;
            bool ok;
            if (line.VariantId.HasValue)
            {
                available = await AvailableForVariantAsync(line.VariantId.Value, ct);
                ok = ReservationMath.CanReserve(available, 0, line.Quantity);
            }
            else if (line.ColorId.HasValue)
            {
                available = await AvailableForColorAsync(line.ColorId.Value, ct);
                ok = ReservationMath.CanReserve(available, 0, line.Quantity);
            }
            else
            {
                available = await AvailableForProductAsync(line.ProductId, ct);
                ok = ReservationMath.CanReserve(available, 0, line.Quantity);
            }

            if (!ok)
            {
                results.Add(new ReservationLineResult(ReservationMath.LineKey(line.ProductId, line.VariantId, line.ColorId), false, available));
                continue;
            }

            db.StockReservations.Add(new StockReservation
            {
                UserId = userId,
                ProductId = line.ProductId,
                VariantId = line.VariantId,
                ColorId = line.ColorId,
                Quantity = line.Quantity,
                Status = StockReservationStatus.Active,
                ExpiresAtUtc = now.Add(ttl),
                OrderId = orderId,
            });
            results.Add(new ReservationLineResult(ReservationMath.LineKey(line.ProductId, line.VariantId, line.ColorId), true, available - line.Quantity));
        }

        await db.SaveChangesAsync(ct);
        return results;
    }

    public async Task<int> ReleaseForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var active = await db.StockReservations.Where(r => r.UserId == userId && r.Status == StockReservationStatus.Active).ToListAsync(ct);
        foreach (var r in active) r.Status = StockReservationStatus.Released;
        if (active.Count > 0) await db.SaveChangesAsync(ct);
        return active.Count;
    }

    public async Task<int> ReleaseForOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        var active = await db.StockReservations.Where(r => r.OrderId == orderId && r.Status == StockReservationStatus.Active).ToListAsync(ct);
        foreach (var r in active) r.Status = StockReservationStatus.Released;
        if (active.Count > 0) await db.SaveChangesAsync(ct);
        return active.Count;
    }

    public async Task<IReadOnlyList<ConsumedReservationLine>> TryConsumeForOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        var active = await db.StockReservations.Where(r => r.OrderId == orderId && r.Status == StockReservationStatus.Active).ToListAsync(ct);
        var consumed = new List<ConsumedReservationLine>();
        foreach (var r in active)
        {
            var product = await db.Products.FirstOrDefaultAsync(p => p.Id == r.ProductId, ct);
            if (product is null || !product.TryDecreaseStock(r.Quantity)) continue; // stock vanished → skip (admin alert territory)
            product.SoldCount += r.Quantity;
            product.ModifiedAtUtc = DateTime.UtcNow;

            if (r.VariantId.HasValue)
            {
                var variant = await db.ProductVariants.FirstOrDefaultAsync(v => v.Id == r.VariantId.Value, ct);
                variant?.TryDecreaseStock(r.Quantity);
            }
            if (r.ColorId.HasValue)
            {
                var color = await db.ProductColors.FirstOrDefaultAsync(c => c.Id == r.ColorId.Value, ct);
                color?.TryDecreaseStock(r.Quantity);
            }

            r.Status = StockReservationStatus.Consumed;
            consumed.Add(new ConsumedReservationLine(r.ProductId, r.VariantId, r.ColorId, r.Quantity));
        }
        if (consumed.Count > 0) await db.SaveChangesAsync(ct);
        return consumed;
    }

    public async Task<int> ExpireStaleAsync(CancellationToken ct = default)
    {
        var stale = await db.StockReservations
            .Where(r => r.Status == StockReservationStatus.Active && r.ExpiresAtUtc <= DateTime.UtcNow)
            .ToListAsync(ct);
        foreach (var r in stale) r.Status = StockReservationStatus.Expired;
        if (stale.Count > 0) await db.SaveChangesAsync(ct);
        return stale.Count;
    }

    public async Task<int> GetActiveReservedQtyAsync(Guid productId, Guid? variantId, Guid? colorId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var query = db.StockReservations.AsNoTracking()
            .Where(r => r.Status == StockReservationStatus.Active && r.ExpiresAtUtc > now);
        if (variantId.HasValue) query = query.Where(r => r.VariantId == variantId);
        else if (colorId.HasValue) query = query.Where(r => r.ColorId == colorId);
        else query = query.Where(r => r.ProductId == productId && r.VariantId == null && r.ColorId == null);
        return await query.SumAsync(r => r.Quantity, ct);
    }

    // availability computed INSIDE the service: active reservations are summed per level
    private async Task<int> AvailableForProductAsync(Guid productId, CancellationToken ct)
    {
        var stock = await db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => (int?)p.Stock).FirstOrDefaultAsync(ct) ?? 0;
        var reserved = await db.StockReservations.AsNoTracking()
            .Where(r => r.ProductId == productId && r.Status == StockReservationStatus.Active && r.ExpiresAtUtc > DateTime.UtcNow)
            .SumAsync(r => r.Quantity, ct);
        return ReservationMath.Available(stock, reserved);
    }

    private async Task<int> AvailableForVariantAsync(Guid variantId, CancellationToken ct)
    {
        var stock = await db.ProductVariants.AsNoTracking().Where(v => v.Id == variantId).Select(v => (int?)v.Stock).FirstOrDefaultAsync(ct) ?? 0;
        var reserved = await db.StockReservations.AsNoTracking()
            .Where(r => r.VariantId == variantId && r.Status == StockReservationStatus.Active && r.ExpiresAtUtc > DateTime.UtcNow)
            .SumAsync(r => r.Quantity, ct);
        return ReservationMath.Available(stock, reserved);
    }

    private async Task<int> AvailableForColorAsync(Guid colorId, CancellationToken ct)
    {
        var stock = await db.ProductColors.AsNoTracking().Where(c => c.Id == colorId).Select(c => (int?)c.Stock).FirstOrDefaultAsync(ct) ?? 0;
        var reserved = await db.StockReservations.AsNoTracking()
            .Where(r => r.ColorId == colorId && r.Status == StockReservationStatus.Active && r.ExpiresAtUtc > DateTime.UtcNow)
            .SumAsync(r => r.Quantity, ct);
        return ReservationMath.Available(stock, reserved);
    }
}

/// <summary>
/// Consumes catalog stock for a PAID order (called from the payment-confirmed path via the outbox).
/// Decrements product + variant/color stock, bumps SoldCount and marks the order's reservations Consumed.
/// Idempotent: an order whose reservations are already Consumed is a no-op (returns false).
/// </summary>
public sealed class OrderFulfillmentService(IStockReservations reservations) : IOrderFulfillment
{
    public async Task<bool> TryConsumeOrderStockAsync(Guid orderId, CancellationToken ct = default)
    {
        var consumed = await reservations.TryConsumeForOrderAsync(orderId, ct);
        return consumed.Count > 0;
    }
}

/// <summary>Coupon lookup + redemption recording (Catalog owns the tables; Sales validates via CouponCalculator).</summary>
public sealed class CouponService(CatalogDbContext db) : ICouponService
{
    public async Task<CouponQuote?> FindActiveAsync(string code, CancellationToken ct = default)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        var c = await db.Coupons.AsNoTracking().FirstOrDefaultAsync(x => x.Code == normalized, ct);
        return c is null ? null : new CouponQuote(c.Code, c.Type.ToString(), c.Value, c.MinSubtotal, c.MaxDiscount, c.MaxUses, c.UsedCount, c.StartsAtUtc, c.ExpiresAtUtc, c.IsActive);
    }

    public async Task RecordRedemptionAsync(string code, Guid orderId, Guid userId, decimal discountETB, CancellationToken ct = default)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        var coupon = await db.Coupons.FirstOrDefaultAsync(x => x.Code == normalized, ct);
        if (coupon is null) return;
        if (await db.CouponRedemptions.AnyAsync(r => r.CouponId == coupon.Id && r.OrderId == orderId, ct)) return; // idempotent per (coupon, order)
        db.CouponRedemptions.Add(new CouponRedemption { CouponId = coupon.Id, OrderId = orderId, UserId = userId, DiscountETB = discountETB });
        coupon.UsedCount++;
        coupon.ModifiedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Quartz job (every minute): expires Active reservations past their TTL so buyers elsewhere can buy.</summary>
public sealed class ReservationSweeperJob(IServiceScopeFactory? scopeFactory = null) : IJob
{
    public static readonly JobKey Key = new("reservation-sweeper");

    public async Task Execute(IJobExecutionContext ctx)
    {
        var factory = scopeFactory
            ?? ctx.JobDetail.JobDataMap.Get("scopeFactory") as IServiceScopeFactory
            ?? throw new InvalidOperationException("scopeFactory unavailable for ReservationSweeperJob");
        using var scope = factory.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<ReservationSweeperJob>>();
        try
        {
            var reservations = scope.ServiceProvider.GetRequiredService<IStockReservations>();
            var expired = await reservations.ExpireStaleAsync(ctx.CancellationToken);
            if (expired > 0) logger.LogInformation("ReservationSweeper expired {Count} stale reservations", expired);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "ReservationSweeper failed");
        }
    }
}
