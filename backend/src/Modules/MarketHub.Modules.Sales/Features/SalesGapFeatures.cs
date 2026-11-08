using System.Text.Json;
using FluentValidation;
using MarketHub.Shared;
using MarketHub.Shared.Contracts;
using MarketHub.Shared.Contracts.Events;
using MarketHub.Shared.Infrastructure;
using MarketHub.Modules.Sales.Domain;
using MarketHub.Modules.Sales.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace MarketHub.Modules.Sales.Features;

// ==================== CART RESERVATIONS ====================
public sealed record ReserveCartCommand(Guid UserId) : IRequest<Result<ReservationQuoteDto>>;
public sealed record ReservationQuoteDto(DateTime ExpiresAt, IReadOnlyList<ReservationLineDto> Reservations, bool AllOk);
public sealed record ReservationLineDto(string Key, bool Ok, int Available);

/// <summary>Reserves every cart line for the buyer (TTL = Settings.ReservationMinutes). Releases the previous Active batch first.</summary>
public sealed class ReserveCartHandler(SalesDbContext db, IStockReservations reservations, ISettingsProvider settings)
    : IRequestHandler<ReserveCartCommand, Result<ReservationQuoteDto>>
{
    public async Task<Result<ReservationQuoteDto>> Handle(ReserveCartCommand c, CancellationToken ct)
    {
        var cartItems = await db.CartItems.AsNoTracking().Where(x => x.UserId == c.UserId).ToListAsync(ct);
        if (cartItems.Count == 0) return Result.Fail<ReservationQuoteDto>("empty_cart", "Your cart is empty.");

        var ttlMinutes = await settings.GetValueAsync<int>(SettingKeys.ReservationMinutes, 20, ct);
        var ttl = TimeSpan.FromMinutes(Math.Max(1, ttlMinutes));
        var lines = cartItems.Select(i => new ReservationRequestLine(i.ProductId, i.VariantId, i.VariantId.HasValue ? null : i.ColorId, i.Quantity)).ToList();
        var results = await reservations.ReserveForUserAsync(c.UserId, lines, ttl, orderId: null, ct);
        return Result.Ok(new ReservationQuoteDto(DateTime.UtcNow.Add(ttl),
            results.Select(r => new ReservationLineDto(r.Key, r.Ok, r.Available)).ToList(),
            results.All(r => r.Ok)));
    }
}

public sealed record ReleaseCartCommand(Guid UserId) : IRequest<Result<int>>;

/// <summary>Releases every Active reservation of the buyer (cart edit / leave). Returns the released count.</summary>
public sealed class ReleaseCartHandler(IStockReservations reservations) : IRequestHandler<ReleaseCartCommand, Result<int>>
{
    public async Task<Result<int>> Handle(ReleaseCartCommand c, CancellationToken ct)
        => Result.Ok(await reservations.ReleaseForUserAsync(c.UserId, ct));
}

public sealed record CartSaveItemDto(Guid ProductId, Guid? VariantId, Guid? ColorId, int Qty);
public sealed record SaveCartCommand(Guid UserId, IReadOnlyList<CartSaveItemDto> Items) : IRequest<Result>;

/// <summary>Upserts the buyer's cart snapshot (abandoned-cart recovery; client debounces this call).</summary>
public sealed class SaveCartCommandHandler(SalesDbContext db) : IRequestHandler<SaveCartCommand, Result>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<Result> Handle(SaveCartCommand c, CancellationToken ct)
    {
        if (c.Items.Count > 100) return Result.Failure(new Error("items", "At most 100 cart lines can be saved."));
        var itemsJson = JsonSerializer.Serialize(c.Items, Json);
        var snapshot = await db.Carts.FirstOrDefaultAsync(s => s.UserId == c.UserId, ct);
        if (snapshot is null)
            db.Carts.Add(new Cart { UserId = c.UserId, ItemsJson = itemsJson, UpdatedAtUtc = DateTime.UtcNow });
        else
        {
            snapshot.ItemsJson = itemsJson;
            snapshot.UpdatedAtUtc = DateTime.UtcNow;
            snapshot.ModifiedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

// ==================== COUPON QUOTE ====================
public sealed record QuoteCouponCommand(Guid UserId, string Code, decimal Subtotal) : IRequest<Result<CouponQuoteDto>>;
public sealed record CouponQuoteDto(bool Ok, decimal DiscountETB, string Message);

/// <summary>Validates a coupon against a proposed subtotal (window / uses / min / max-discount) without redeeming it.</summary>
public sealed class QuoteCouponHandler(ICouponService coupons) : IRequestHandler<QuoteCouponCommand, Result<CouponQuoteDto>>
{
    public async Task<Result<CouponQuoteDto>> Handle(QuoteCouponCommand c, CancellationToken ct)
    {
        var quote = await coupons.FindActiveAsync(c.Code, ct);
        var (ok, discount, message) = Pricing.CouponCalculator.Validate(quote!, c.Subtotal, DateTime.UtcNow);
        return Result.Ok(new CouponQuoteDto(ok, discount, message));
    }
}

// ==================== ADDRESSES ====================
public sealed record ListAddressesQuery(Guid UserId) : IRequest<Result<IReadOnlyList<AddressDto>>>;
public sealed record AddressDto(Guid Id, string Label, string FullName, string Phone, string Region, string City, string SubCity, string Woreda, string? Kebele, string? Landmark, bool IsDefault);

public sealed class ListAddressesHandler(SalesDbContext db) : IRequestHandler<ListAddressesQuery, Result<IReadOnlyList<AddressDto>>>
{
    public async Task<Result<IReadOnlyList<AddressDto>>> Handle(ListAddressesQuery q, CancellationToken ct)
    {
        var rows = await db.Addresses.AsNoTracking().Where(a => a.UserId == q.UserId)
            .OrderByDescending(a => a.IsDefault).ThenByDescending(a => a.CreatedAtUtc).ToListAsync(ct);
        return Result.Ok((IReadOnlyList<AddressDto>)rows.Select(ToDto).ToList());
    }

    internal static AddressDto ToDto(Address a) => new(a.Id, a.Label, a.FullName, a.Phone, a.Region, a.City, a.SubCity, a.Woreda, a.Kebele, a.Landmark, a.IsDefault);
}

public sealed record CreateAddressCommand(Guid UserId, string Label, string FullName, string Phone, string Region, string City, string SubCity, string Woreda, string? Kebele, string? Landmark) : IRequest<Result<AddressDto>>;

public sealed class CreateAddressValidator : AbstractValidator<CreateAddressCommand>
{
    public CreateAddressValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Phone).Matches(@"^\+251[79]\d{8}$").WithMessage("Phone must match +2519XXXXXXXX format.");
        RuleFor(x => x.Region).NotEmpty().MaximumLength(120);
        RuleFor(x => x.City).NotEmpty().MaximumLength(120);
        RuleFor(x => x.SubCity).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Woreda).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Kebele).MaximumLength(120);
        RuleFor(x => x.Landmark).MaximumLength(300);
    }
}

public sealed class CreateAddressHandler(SalesDbContext db, IValidator<CreateAddressCommand> validator) : IRequestHandler<CreateAddressCommand, Result<AddressDto>>
{
    public async Task<Result<AddressDto>> Handle(CreateAddressCommand c, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(c, ct);
        if (!validation.IsValid) return Result.Fail<AddressDto>("validation", string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));

        var isFirst = !await db.Addresses.AnyAsync(a => a.UserId == c.UserId, ct);
        var address = new Address
        {
            UserId = c.UserId,
            Label = string.IsNullOrWhiteSpace(c.Label) ? "Home" : c.Label.Trim(),
            FullName = c.FullName.Trim(),
            Phone = c.Phone.Trim(),
            Region = c.Region.Trim(),
            City = c.City.Trim(),
            SubCity = c.SubCity.Trim(),
            Woreda = c.Woreda.Trim(),
            Kebele = string.IsNullOrWhiteSpace(c.Kebele) ? null : c.Kebele.Trim(),
            Landmark = string.IsNullOrWhiteSpace(c.Landmark) ? null : c.Landmark.Trim(),
            IsDefault = isFirst,
        };
        db.Addresses.Add(address);
        await db.SaveChangesAsync(ct);
        return Result.Ok(ListAddressesHandler.ToDto(address));
    }
}

// ==================== BUYER CANCEL ====================
public sealed record CancelOrderCommand(Guid BuyerId, Guid OrderId) : IRequest<Result<OrderDto>>;

/// <summary>Buyer cancel — only while Placed. Releases the order's stock reservations and records the timeline event.</summary>
public sealed class CancelOrderHandler(SalesDbContext db, IStockReservations reservations) : IRequestHandler<CancelOrderCommand, Result<OrderDto>>
{
    public async Task<Result<OrderDto>> Handle(CancelOrderCommand c, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == c.OrderId && o.BuyerId == c.BuyerId, ct)
            ?? throw new NotFoundException("Order", c.OrderId);
        if (order.Status != OrderStatus.Placed)
            return Result.Fail<OrderDto>("state", "Only orders that have not been paid yet can be cancelled.");

        order.Transition(OrderStatus.Cancelled);
        db.OrderEvents.Add(new OrderEvent { OrderId = order.Id, Status = OrderStatus.Cancelled.ToString(), Note = "Cancelled by the buyer before payment.", ByRole = "Buyer" });
        _ = await reservations.ReleaseForOrderAsync(order.Id, ct);
        db.AddToOutbox(new OrderCancelledEvent(order.Id, order.BuyerId, order.OrderNumber));
        await db.SaveChangesAsync(ct);
        return Result.Ok(OrderDto.From(order));
    }
}

// ==================== RETURNS (buyer) ====================
public sealed record CreateReturnCommand(Guid BuyerId, Guid OrderItemId, string Reason, string Description, IReadOnlyList<string>? EvidenceUrls) : IRequest<Result<ReturnRequestDto>>;

public sealed class CreateReturnValidator : AbstractValidator<CreateReturnCommand>
{
    public CreateReturnValidator()
    {
        RuleFor(x => x.Reason).Must(r => Enum.TryParse<ReturnReason>(r, true, out _))
            .WithMessage("Reason must be one of: Damaged, WrongItem, NotAsDescribed, ChangedMind, Other.");
        RuleFor(x => x.Description).NotEmpty().MinimumLength(10).WithMessage("Please describe the issue (at least 10 characters).").MaximumLength(2000);
        RuleFor(x => x.EvidenceUrls).Must(list => list == null || list.Count <= 5).WithMessage("At most 5 evidence photos per return.");
        RuleForEach(x => x.EvidenceUrls).MaximumLength(500).Must(u => u == null || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Evidence photos must be https URLs.");
    }
}

public sealed record ReturnRequestDto(Guid Id, Guid OrderItemId, Guid OrderId, string? OrderNumber, Guid BuyerId, Guid? VendorId,
    string Reason, string Description, IReadOnlyList<string>? EvidenceUrls, string Status, string? VendorNote, string? AdminDecision,
    decimal? RefundETB, DateTime CreatedAtUtc, DateTime? ResolvedAtUtc, Guid ProductId, string? ProductTitle, int Quantity, decimal LineNetETB)
{
    public static ReturnRequestDto From(ReturnRequest r, Order? order, OrderItem? item) => new(
        r.Id, r.OrderItemId, r.OrderId, order?.OrderNumber, r.BuyerId, r.VendorId,
        r.Reason.ToString(), r.Description, ReturnRequest.ParseUrls(r.EvidenceUrlsJson), r.Status.ToString(),
        r.VendorNote, r.AdminDecision, r.RefundETB, r.CreatedAtUtc, r.ResolvedAtUtc,
        item?.ProductId ?? Guid.Empty, item?.TitleSnapshot, item?.Quantity ?? 0, item?.LineNetETB ?? 0m);
}

/// <summary>Opens a per-item return (one open return per item; window = ReturnWindowDays from DeliveredAt).</summary>
public sealed class CreateReturnHandler(SalesDbContext db, ISettingsProvider settings, IValidator<CreateReturnCommand> validator)
    : IRequestHandler<CreateReturnCommand, Result<ReturnRequestDto>>
{
    public async Task<Result<ReturnRequestDto>> Handle(CreateReturnCommand c, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(c, ct);
        if (!validation.IsValid) return Result.Fail<ReturnRequestDto>("validation", string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));

        var item = await db.OrderItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == c.OrderItemId, ct)
            ?? throw new NotFoundException("OrderItem", c.OrderItemId);
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == item.OrderId && o.BuyerId == c.BuyerId, ct)
            ?? throw new NotFoundException("Order", item.OrderId);

        if (order.Status is not (OrderStatus.Delivered or OrderStatus.Completed))
            return Result.Fail<ReturnRequestDto>("state", $"Returns open after delivery — this order is {order.Status}.");

        var windowDays = await settings.GetValueAsync<int>(SettingKeys.ReturnWindowDays, 7, ct);
        if (!Pricing.ReturnPolicy.IsWithinWindow(order.DeliveredAtUtc, windowDays, DateTime.UtcNow))
            return Result.Fail<ReturnRequestDto>("window", $"The {windowDays}-day return window for this delivery has passed.");

        var open = new[] { ReturnStatus.Requested, ReturnStatus.VendorApproved, ReturnStatus.AdminReview, ReturnStatus.Approved };
        if (await db.ReturnRequests.AnyAsync(r => r.OrderItemId == c.OrderItemId && open.Contains(r.Status), ct))
            return Result.Fail<ReturnRequestDto>("duplicate", "A return request for this item is already in progress.");

        var request = new ReturnRequest
        {
            OrderItemId = item.Id,
            OrderId = order.Id,
            BuyerId = c.BuyerId,
            VendorId = item.VendorId,
            Reason = Enum.Parse<ReturnReason>(c.Reason, true),
            Description = c.Description.Trim(),
            EvidenceUrlsJson = c.EvidenceUrls is { Count: > 0 } ? JsonSerializer.Serialize(c.EvidenceUrls) : null,
            Status = ReturnStatus.Requested,
        };
        db.ReturnRequests.Add(request);

        // flip the item's refund status so vendors/admins see the pending claim in their queues
        var trackedItem = await db.OrderItems.FirstAsync(i => i.Id == item.Id, ct);
        trackedItem.RefundStatus = "Requested";

        db.AddToOutbox(new ReturnRequestedEvent(request.Id, order.Id, c.BuyerId, item.VendorId, request.Reason.ToString()));
        await db.SaveChangesAsync(ct);
        return Result.Ok(ReturnRequestDto.From(request, order, item));
    }
}

public sealed record ListMyReturnsQuery(Guid BuyerId) : IRequest<Result<IReadOnlyList<ReturnRequestDto>>>;

public sealed class ListMyReturnsHandler(SalesDbContext db) : IRequestHandler<ListMyReturnsQuery, Result<IReadOnlyList<ReturnRequestDto>>>
{
    public async Task<Result<IReadOnlyList<ReturnRequestDto>>> Handle(ListMyReturnsQuery q, CancellationToken ct)
    {
        var returns = await db.ReturnRequests.AsNoTracking().Where(r => r.BuyerId == q.BuyerId)
            .OrderByDescending(r => r.CreatedAtUtc).ToListAsync(ct);
        var itemIds = returns.Select(r => r.OrderItemId).Distinct().ToList();
        var items = await db.OrderItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var orderIds = returns.Select(r => r.OrderId).Distinct().ToList();
        var orders = await db.Orders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        return Result.Ok((IReadOnlyList<ReturnRequestDto>)returns
            .Select(r => ReturnRequestDto.From(r, orders.GetValueOrDefault(r.OrderId), items.GetValueOrDefault(r.OrderItemId))).ToList());
    }
}

// ==================== RETURNS (vendor) ====================
public sealed record ListVendorReturnsQuery(Guid VendorId, string? Status) : IRequest<Result<IReadOnlyList<ReturnRequestDto>>>;

public sealed class ListVendorReturnsHandler(SalesDbContext db) : IRequestHandler<ListVendorReturnsQuery, Result<IReadOnlyList<ReturnRequestDto>>>
{
    public async Task<Result<IReadOnlyList<ReturnRequestDto>>> Handle(ListVendorReturnsQuery q, CancellationToken ct)
    {
        var query = db.ReturnRequests.AsNoTracking().Where(r => r.VendorId == q.VendorId);
        if (Enum.TryParse<ReturnStatus>(q.Status, true, out var s)) query = query.Where(r => r.Status == s);
        var returns = await query.OrderByDescending(r => r.CreatedAtUtc).Take(200).ToListAsync(ct);
        var itemIds = returns.Select(r => r.OrderItemId).Distinct().ToList();
        var items = await db.OrderItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var orderIds = returns.Select(r => r.OrderId).Distinct().ToList();
        var orders = await db.Orders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        return Result.Ok((IReadOnlyList<ReturnRequestDto>)returns
            .Select(r => ReturnRequestDto.From(r, orders.GetValueOrDefault(r.OrderId), items.GetValueOrDefault(r.OrderItemId))).ToList());
    }
}

public sealed record VendorReturnDecisionCommand(Guid VendorId, Guid ReturnId, bool Approve, string? Note) : IRequest<Result<ReturnRequestDto>>;

/// <summary>Vendor stage decision. Admin arbitration + refund follows (ReturnDecidedEvent notifies buyer + admins).</summary>
public sealed class VendorReturnDecisionHandler(SalesDbContext db) : IRequestHandler<VendorReturnDecisionCommand, Result<ReturnRequestDto>>
{
    public async Task<Result<ReturnRequestDto>> Handle(VendorReturnDecisionCommand c, CancellationToken ct)
    {
        var request = await db.ReturnRequests.FirstOrDefaultAsync(r => r.Id == c.ReturnId, ct)
            ?? throw new NotFoundException("ReturnRequest", c.ReturnId);
        if (request.VendorId != c.VendorId) throw new NotFoundException("ReturnRequest", c.ReturnId);
        if (request.Status != ReturnStatus.Requested)
            return Result.Fail<ReturnRequestDto>("state", $"This return is already {request.Status}.");

        if (c.Approve)
        {
            request.Status = ReturnStatus.VendorApproved;
            request.VendorNote = string.IsNullOrWhiteSpace(c.Note) ? "Vendor accepted the return." : c.Note.Trim();
        }
        else
        {
            if (string.IsNullOrWhiteSpace(c.Note) || c.Note.Trim().Length < 5)
                return Result.Fail<ReturnRequestDto>("note", "A rejection note (at least 5 characters) is required.");
            request.Status = ReturnStatus.VendorRejected;
            request.VendorNote = c.Note.Trim();
        }
        request.ModifiedAtUtc = DateTime.UtcNow;

        db.AddToOutbox(new ReturnDecidedEvent(request.Id, request.OrderId, request.BuyerId, request.VendorId, c.Approve, 0m, request.VendorNote));
        await db.SaveChangesAsync(ct);

        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == request.OrderId, ct);
        var item = await db.OrderItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == request.OrderItemId, ct);
        return Result.Ok(ReturnRequestDto.From(request, order, item));
    }
}

// ==================== RETURNS (admin arbitration) ====================
public sealed record AdminListReturnsQuery(string? Status) : IRequest<Result<IReadOnlyList<ReturnRequestDto>>>;

public sealed class AdminListReturnsHandler(SalesDbContext db) : IRequestHandler<AdminListReturnsQuery, Result<IReadOnlyList<ReturnRequestDto>>>
{
    public async Task<Result<IReadOnlyList<ReturnRequestDto>>> Handle(AdminListReturnsQuery q, CancellationToken ct)
    {
        var query = db.ReturnRequests.AsNoTracking().AsQueryable();
        if (Enum.TryParse<ReturnStatus>(q.Status, true, out var s)) query = query.Where(r => r.Status == s);
        var returns = await query.OrderByDescending(r => r.CreatedAtUtc).Take(200).ToListAsync(ct);
        var itemIds = returns.Select(r => r.OrderItemId).Distinct().ToList();
        var items = await db.OrderItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var orderIds = returns.Select(r => r.OrderId).Distinct().ToList();
        var orders = await db.Orders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        return Result.Ok((IReadOnlyList<ReturnRequestDto>)returns
            .Select(r => ReturnRequestDto.From(r, orders.GetValueOrDefault(r.OrderId), items.GetValueOrDefault(r.OrderItemId))).ToList());
    }
}

public sealed record ArbitrateReturnCommand(Guid AdminId, Guid ReturnId, bool Approve, decimal RefundETB, string? Note) : IRequest<Result<ReturnRequestDto>>;

/// <summary>
/// Final admin decision on a return. On approval: posts BALANCED refund ledger legs via the Finance
/// refund-leg pattern, restocks the item (product + variant/color), flips the return to Refunded and
/// notifies buyer + vendor through the outbox. On rejection: closes the claim without a refund.
/// </summary>
public sealed class ArbitrateReturnHandler(SalesDbContext db, ILedgerPoster ledger, ICatalogReader catalog)
    : IRequestHandler<ArbitrateReturnCommand, Result<ReturnRequestDto>>
{
    public async Task<Result<ReturnRequestDto>> Handle(ArbitrateReturnCommand c, CancellationToken ct)
    {
        var request = await db.ReturnRequests.FirstOrDefaultAsync(r => r.Id == c.ReturnId, ct)
            ?? throw new NotFoundException("ReturnRequest", c.ReturnId);
        if (request.Status is ReturnStatus.Refunded or ReturnStatus.Rejected)
            return Result.Fail<ReturnRequestDto>("state", $"This return is already {request.Status}.");

        var item = await db.OrderItems.FirstOrDefaultAsync(i => i.Id == request.OrderItemId, ct)
            ?? throw new NotFoundException("OrderItem", request.OrderItemId);
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == request.OrderId, ct)
            ?? throw new NotFoundException("Order", request.OrderId);

        request.AdminId = c.AdminId;
        request.AdminDecision = string.IsNullOrWhiteSpace(c.Note) ? (c.Approve ? "Approved by MarketHub support." : "Rejected after inspection.") : c.Note.Trim();
        request.ModifiedAtUtc = DateTime.UtcNow;

        if (!c.Approve)
        {
            request.Status = ReturnStatus.Rejected;
            request.RefundETB = 0m;
            request.ResolvedAtUtc = DateTime.UtcNow;
            item.RefundStatus = "Rejected";
            db.AddToOutbox(new ReturnDecidedEvent(request.Id, order.Id, request.BuyerId, request.VendorId, false, 0m, request.AdminDecision));
            await db.SaveChangesAsync(ct);
            return Result.Ok(ReturnRequestDto.From(request, order, item));
        }

        var refund = Math.Round(c.RefundETB, 2, MidpointRounding.AwayFromZero);
        if (refund <= 0 || refund > order.GrandTotalETB)
            return Result.Fail<ReturnRequestDto>("amount", $"Refund must be between 0 and {order.GrandTotalETB:0.##} Br.");

        // balanced double-entry refund legs (DR refund expense / VAT reversal / CR cash — Finance pattern)
        await ledger.PostRefundAsync(order.Id, refund, ct);

        // restock: product total + SKU variant / color when the line carried one
        await catalog.RestockAsync(item.ProductId, item.VariantId, item.VariantId.HasValue ? null : item.ColorId, item.Quantity, ct);

        request.Status = ReturnStatus.Refunded;
        request.RefundETB = refund;
        request.ResolvedAtUtc = DateTime.UtcNow;
        item.RefundStatus = "Approved";

        db.AddToOutbox(new ReturnDecidedEvent(request.Id, order.Id, request.BuyerId, request.VendorId, true, refund, request.AdminDecision));
        await db.SaveChangesAsync(ct);
        return Result.Ok(ReturnRequestDto.From(request, order, item));
    }
}

// ==================== VENDOR PERFORMANCE & DASHBOARD ====================
public sealed record VendorPerformanceQuery(Guid VendorId) : IRequest<Result<VendorPerformanceDto>>;
public sealed record VendorPerformanceDto(decimal RatingAvg, int RatingCount, int TotalSold, decimal FulfillmentRate, decimal CancelRate, int OpenReturns, decimal DisputeRate);

public sealed class VendorPerformanceHandler(SalesDbContext db, ICatalogReader catalog) : IRequestHandler<VendorPerformanceQuery, Result<VendorPerformanceDto>>
{
    public async Task<Result<VendorPerformanceDto>> Handle(VendorPerformanceQuery q, CancellationToken ct)
    {
        var stats = await ComputeAsync(db, catalog, q.VendorId, ct);
        return Result.Ok(stats);
    }

    internal static async Task<VendorPerformanceDto> ComputeAsync(SalesDbContext db, ICatalogReader catalog, Guid vendorId, CancellationToken ct)
    {
        // paid orders = any state past Placed that is not Cancelled; fulfillment = Shipped/Delivered/Completed
        var statuses = await db.Orders.AsNoTracking()
            .Where(o => o.Items.Any(i => i.VendorId == vendorId))
            .Select(o => o.Status)
            .ToListAsync(ct);
        var cancelled = statuses.Count(s => s == OrderStatus.Cancelled);
        var disputed = statuses.Count(s => s == OrderStatus.Disputed);
        var paid = statuses.Count(s => s is not OrderStatus.Placed and not OrderStatus.Cancelled);
        var fulfilled = statuses.Count(s => s is OrderStatus.Shipped or OrderStatus.Delivered or OrderStatus.Completed);

        var totalSold = await db.OrderItems.AsNoTracking()
            .Join(db.Orders.AsNoTracking().Where(o => o.Status != OrderStatus.Placed && o.Status != OrderStatus.Cancelled),
                i => i.OrderId, o => o.Id, (i, _) => i)
            .Where(i => i.VendorId == vendorId)
            .SumAsync(i => (int?)i.Quantity, ct) ?? 0;

        var openReturnStatuses = new[] { ReturnStatus.Requested, ReturnStatus.VendorApproved, ReturnStatus.AdminReview };
        var openReturns = await db.ReturnRequests.AsNoTracking()
            .Where(r => r.VendorId == vendorId && openReturnStatuses.Contains(r.Status))
            .CountAsync(ct);

        var (ratingAvg, ratingCount) = await catalog.GetVendorReviewStatsAsync(vendorId, ct);

        decimal Rate(int numerator, int denominator) => denominator == 0 ? 0m : Math.Round(numerator * 100m / denominator, 1, MidpointRounding.AwayFromZero);
        return new VendorPerformanceDto(ratingAvg, ratingCount, totalSold,
            Rate(fulfilled, paid), Rate(cancelled, statuses.Count), openReturns, Rate(disputed, paid));
    }
}

public sealed record VendorDashboardQuery(Guid VendorId) : IRequest<Result<VendorDashboardDto>>;
public sealed record VendorDailyPointDto(string Date, decimal Revenue, int Orders);
public sealed record VendorDashboardTotalsDto(int Products, int ActiveOrders, int PendingReturns, int LowStock);
public sealed record VendorDashboardDto(IReadOnlyList<VendorDailyPointDto> Daily, VendorDashboardTotalsDto Totals);

public sealed class VendorDashboardHandler(SalesDbContext db, ICatalogReader catalog) : IRequestHandler<VendorDashboardQuery, Result<VendorDashboardDto>>
{
    public async Task<Result<VendorDashboardDto>> Handle(VendorDashboardQuery q, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var from = now.Date.AddDays(-13);

        var paidStatuses = new[] { OrderStatus.PaymentConfirmed, OrderStatus.VendorAccepted, OrderStatus.Shipped, OrderStatus.Delivered, OrderStatus.Completed, OrderStatus.RefundRequested, OrderStatus.Refunded };
        var items = await db.OrderItems.AsNoTracking()
            .Where(i => i.VendorId == q.VendorId && i.CreatedAtUtc >= from)
            .Join(db.Orders.AsNoTracking().Where(o => paidStatuses.Contains(o.Status)),
                i => i.OrderId, o => o.Id, (i, _) => new { i.CreatedAtUtc, i.LineNetETB, i.OrderId })
            .ToListAsync(ct);

        var daily = new List<VendorDailyPointDto>();
        for (var d = 13; d >= 0; d--)
        {
            var day = now.Date.AddDays(-d);
            var rows = items.Where(x => x.CreatedAtUtc.Date == day).ToList();
            daily.Add(new VendorDailyPointDto(day.ToString("yyyy-MM-dd"),
                Math.Round(rows.Sum(x => x.LineNetETB), 2, MidpointRounding.AwayFromZero),
                rows.Select(x => x.OrderId).Distinct().Count()));
        }

        var activeStatuses = new[] { OrderStatus.PaymentConfirmed, OrderStatus.VendorAccepted, OrderStatus.Shipped };
        var activeOrders = await db.Orders.AsNoTracking().Where(o => activeStatuses.Contains(o.Status) && o.Items.Any(i => i.VendorId == q.VendorId)).CountAsync(ct);
        var openReturnStatuses = new[] { ReturnStatus.Requested, ReturnStatus.VendorApproved, ReturnStatus.AdminReview };
        var pendingReturns = await db.ReturnRequests.AsNoTracking().Where(r => r.VendorId == q.VendorId && openReturnStatuses.Contains(r.Status)).CountAsync(ct);
        var (products, lowStock) = await catalog.GetVendorProductStatsAsync(q.VendorId, ct);

        return Result.Ok(new VendorDashboardDto(daily, new VendorDashboardTotalsDto(products, activeOrders, pendingReturns, lowStock)));
    }
}

// ==================== OUTBOX EVENT HANDLERS ====================
/// <summary>
/// Payment-confirmed side effects (fired from the Payments webhook via the outbox):
/// transitions the order Placed → PaymentConfirmed, records the timeline event and CONSUMES the
/// order's stock reservations (the actual stock decrement — checkout only reserves).
/// </summary>
public sealed class OrderPaymentConfirmedHandler(SalesDbContext db, IOrderFulfillment fulfillment, ILogger<OrderPaymentConfirmedHandler> logger)
    : INotificationHandler<OrderPaymentConfirmedEvent>
{
    public async Task Handle(OrderPaymentConfirmedEvent e, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == e.OrderId, ct);
        if (order is null)
        {
            logger.LogWarning("OrderPaymentConfirmed: order {OrderId} not found", e.OrderId);
            return;
        }
        if (order.Status == OrderStatus.Placed)
        {
            order.Transition(OrderStatus.PaymentConfirmed);
            db.OrderEvents.Add(new OrderEvent { OrderId = order.Id, Status = OrderStatus.PaymentConfirmed.ToString(), Note = "Payment confirmed — vendors notified.", ByRole = "System" });
            await db.SaveChangesAsync(ct);
        }
        // idempotent: reservations already Consumed short-circuit inside the fulfillment service
        var consumed = await fulfillment.TryConsumeOrderStockAsync(order.Id, ct);
        if (!consumed) logger.LogInformation("OrderPaymentConfirmed: no active reservations for order {OrderId} (already consumed)", order.Id);
    }
}

// ==================== ABANDONED-CART PROJECTION ====================
/// <summary>Abandoned-cart snapshots consumed by the Notifications dispatcher and its Quartz reminder job.</summary>
public sealed class AbandonedCartReader(IServiceProvider sp) : IAbandonedCartReader
{
    public async Task<IReadOnlyList<AbandonedCartRow>> GetStaleCartsAsync(TimeSpan idleFor, CancellationToken ct = default)
    {
        var scope = sp.CreateScope();
        try
        {
            var cutoff = DateTime.UtcNow - idleFor;
            var rows = await scope.ServiceProvider.GetRequiredService<SalesDbContext>().Carts.AsNoTracking()
                .Where(s => s.UpdatedAtUtc < cutoff && s.ReminderSentAtUtc == null)
                .OrderBy(s => s.UpdatedAtUtc)
                .Select(s => new AbandonedCartRow(s.UserId, s.ItemsJson, s.UpdatedAtUtc, s.ReminderSentAtUtc))
                .ToListAsync(ct);
            return rows;
        }
        finally { scope.Dispose(); }
    }

    public async Task MarkRemindedAsync(Guid userId, CancellationToken ct = default)
    {
        var scope = sp.CreateScope();
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<SalesDbContext>();
            var snapshot = await db.Carts.FirstOrDefaultAsync(s => s.UserId == userId, ct);
            if (snapshot is null) return;
            snapshot.ReminderSentAtUtc = DateTime.UtcNow;
            snapshot.ModifiedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        finally { scope.Dispose(); }
    }
}
