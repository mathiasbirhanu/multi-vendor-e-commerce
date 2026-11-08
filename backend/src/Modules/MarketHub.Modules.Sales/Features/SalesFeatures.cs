using FluentValidation;
using MarketHub.Shared;
using MarketHub.Shared.Contracts;
using MarketHub.Shared.Contracts.Events;
using MarketHub.Shared.Infrastructure;
using MarketHub.Modules.Sales.Domain;
using MarketHub.Modules.Sales.Persistence;
using MarketHub.Modules.Sales.Pricing;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace MarketHub.Modules.Sales.Features;

// ==================== CART ====================
public sealed record AddToCartCommand(Guid UserId, Guid ProductId, Guid? VariantId, Guid? ColorId, int Quantity) : IRequest<Result>;

public sealed class AddToCartHandler(SalesDbContext db, ICatalogReader catalog) : IRequestHandler<AddToCartCommand, Result>
{
    public async Task<Result> Handle(AddToCartCommand c, CancellationToken ct)
    {
        var product = await catalog.FindActiveAsync(c.ProductId, ct)
            ?? throw new NotFoundException("Product", c.ProductId);
        if (c.Quantity < 1) return Result.Failure(new Error("quantity", "Quantity must be at least 1."));

        // variant takes precedence over the color-only path when present
        if (c.VariantId.HasValue)
        {
            var variant = await catalog.FindVariantAsync(c.VariantId.Value, ct);
            if (variant is null || variant.ProductId != c.ProductId)
                return Result.Failure(new Error("variant", "Selected variant is not available for this product."));
            if (!variant.IsActive)
                return Result.Failure(new Error("variant_inactive", "This variant is no longer available."));
            if (variant.Stock < c.Quantity)
                return Result.Failure(new Error("variant_stock", $"Only {variant.Stock} left for {variant.Sku}."));
        }

        CatalogColorSnapshot? color = null;
        if (!c.VariantId.HasValue && c.ColorId.HasValue)
        {
            color = await catalog.FindColorAsync(c.ColorId.Value, ct);
            if (color is null || color.ProductId != c.ProductId)
                return Result.Failure(new Error("color", "Selected color is not available for this product."));
            if (color.Stock < c.Quantity)
                return Result.Failure(new Error("color_stock", $"Only {color.Stock} left in {color.Name}."));
        }

        var existing = await db.CartItems.FirstOrDefaultAsync(
            x => x.UserId == c.UserId && x.ProductId == c.ProductId && x.VariantId == c.VariantId && x.ColorId == c.ColorId, ct);
        if (existing is null)
            db.CartItems.Add(new CartItem { UserId = c.UserId, ProductId = c.ProductId, VariantId = c.VariantId, ColorId = c.ColorId, Quantity = c.Quantity });
        else existing.Quantity += c.Quantity;
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed record UpdateCartItemCommand(Guid UserId, Guid CartItemId, int Quantity) : IRequest<Result>;

public sealed class UpdateCartItemHandler(SalesDbContext db) : IRequestHandler<UpdateCartItemCommand, Result>
{
    public async Task<Result> Handle(UpdateCartItemCommand c, CancellationToken ct)
    {
        var item = await db.CartItems.FirstOrDefaultAsync(x => x.Id == c.CartItemId && x.UserId == c.UserId, ct)
            ?? throw new NotFoundException("CartItem", c.CartItemId);
        if (c.Quantity <= 0) db.CartItems.Remove(item);
        else item.Quantity = c.Quantity;
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed record RemoveCartItemCommand(Guid UserId, Guid CartItemId) : IRequest<Result>;

public sealed class RemoveCartItemHandler(SalesDbContext db) : IRequestHandler<RemoveCartItemCommand, Result>
{
    public async Task<Result> Handle(RemoveCartItemCommand c, CancellationToken ct)
    {
        var item = await db.CartItems.FirstOrDefaultAsync(x => x.Id == c.CartItemId && x.UserId == c.UserId, ct)
            ?? throw new NotFoundException("CartItem", c.CartItemId);
        db.CartItems.Remove(item);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed record GetCartQuery(Guid UserId) : IRequest<Result<CartDto>>;
public sealed record CartLineDto(Guid Id, Guid ProductId, string Title, decimal UnitPriceETB, int Quantity, decimal WeightKg, Guid? VendorId,
    Guid? VariantId, string? VariantSku, string? VariantLabel, Guid? ColorId, string? ColorName, string? ColorHex);
public sealed record CartDto(IReadOnlyList<CartLineDto> Items, decimal SubtotalETB);

public sealed class GetCartHandler(SalesDbContext db, ICatalogReader catalog) : IRequestHandler<GetCartQuery, Result<CartDto>>
{
    public async Task<Result<CartDto>> Handle(GetCartQuery q, CancellationToken ct)
    {
        var items = await db.CartItems.AsNoTracking().Where(x => x.UserId == q.UserId).ToListAsync(ct);
        var products = new Dictionary<Guid, CatalogProductSnapshot>();
        foreach (var id in items.Select(x => x.ProductId).Distinct())
        {
            var p = await catalog.FindActiveAsync(id, ct);
            if (p is not null) products[id] = p;
        }
        var variants = new Dictionary<Guid, CatalogVariantSnapshot>();
        foreach (var id in items.Where(x => x.VariantId.HasValue).Select(x => x.VariantId!.Value).Distinct())
        {
            var v = await catalog.FindVariantAsync(id, ct);
            if (v is not null) variants[id] = v;
        }
        var colorIds = items.Where(x => x.ColorId.HasValue).Select(x => x.ColorId!.Value)
            .Concat(variants.Values.Where(v => v.ColorId.HasValue).Select(v => v.ColorId!.Value))
            .Distinct().ToList();
        var colors = new Dictionary<Guid, CatalogColorSnapshot>();
        foreach (var id in colorIds)
        {
            var c = await catalog.FindColorAsync(id, ct);
            if (c is not null) colors[id] = c;
        }
        // Line price: explicit variant price → else base price + the color's optional extra.
        var lines = items.Where(i => products.ContainsKey(i.ProductId))
            .Select(i =>
            {
                var p = products[i.ProductId];
                var variant = i.VariantId.HasValue ? variants.GetValueOrDefault(i.VariantId.Value) : null;
                var color = i.ColorId.HasValue ? colors.GetValueOrDefault(i.ColorId.Value)
                    : variant?.ColorId.HasValue == true ? colors.GetValueOrDefault(variant.ColorId.Value) : null;
                var unit = Math.Round(variant?.PriceETB ?? p.PriceETB + (color?.ExtraPriceETB ?? 0m), 2, MidpointRounding.AwayFromZero);
                var labelParts = new[] { color?.Name, variant?.Size }.Where(s => !string.IsNullOrWhiteSpace(s));
                var variantLabel = string.Join(" / ", labelParts) is { Length: > 0 } label ? label : variant?.Sku;
                return new CartLineDto(i.Id, i.ProductId, p.Title, unit, i.Quantity, p.WeightKg, p.VendorId,
                    variant?.Id, variant?.Sku, variantLabel, color?.Id, color?.Name, color?.Hex);
            }).ToList();
        return Result.Ok(new CartDto(lines, Math.Round(lines.Sum(l => l.UnitPriceETB * l.Quantity), 2, MidpointRounding.AwayFromZero)));
    }
}

// ==================== DELIVERY ZONES ====================
public sealed record ListZonesQuery : IRequest<Result<IReadOnlyList<ZoneDto>>>;
public sealed record ZoneDto(Guid Id, string Name, decimal BaseFeeETB, decimal PerKgSurchargeETB, decimal FreeThresholdETB);

public sealed class ListZonesHandler(SalesDbContext db) : IRequestHandler<ListZonesQuery, Result<IReadOnlyList<ZoneDto>>>
{
    public async Task<Result<IReadOnlyList<ZoneDto>>> Handle(ListZonesQuery q, CancellationToken ct)
    {
        var zones = await db.DeliveryZones.AsNoTracking().Where(z => z.IsActive).OrderBy(z => z.Name).ToListAsync(ct);
        var rates = await db.DeliveryRates.AsNoTracking().ToListAsync(ct);
        return Result.Ok(zones.Select(z =>
        {
            var r = rates.FirstOrDefault(x => x.ZoneId == z.Id);
            return new ZoneDto(z.Id, z.Name, r?.BaseFeeETB ?? 0m, r?.PerKgSurchargeETB ?? 0m, r?.FreeThresholdETB ?? 0m);
        }).ToList());
    }
}

// ==================== CHECKOUT ====================
public sealed record CheckoutAddressInput(string FullName, string Phone, string Region, string City, string SubCity, string Woreda, string? Kebele, string? Landmark);

public sealed record CheckoutCommand(Guid BuyerId, Guid? AddressId, CheckoutAddressInput? Address, bool SaveAddress,
    Guid ZoneId, string Phone, IReadOnlyList<Guid>? ItemIds, string? CouponCode) : IRequest<Result<OrderDto>>;

public sealed class CheckoutValidator : AbstractValidator<CheckoutCommand>
{
    public CheckoutValidator()
    {
        RuleFor(x => x.ZoneId).NotEmpty();
        RuleFor(x => x.Phone).Matches(@"^\+251[79]\d{8}$").WithMessage("Phone must match +2519XXXXXXXX format.");
        RuleFor(x => x.AddressId).NotNull().When(x => x.Address is null)
            .WithMessage("Either addressId or an inline address is required.");
        RuleFor(x => x.Address).NotNull().When(x => x.AddressId is null)
            .WithMessage("Either addressId or an inline address is required.");
        RuleFor(x => x.Address!.Value.FullName).NotEmpty().MaximumLength(200).When(x => x.Address is not null);
        RuleFor(x => x.Address!.Value.Region).NotEmpty().MaximumLength(120).When(x => x.Address is not null);
        RuleFor(x => x.Address!.Value.City).NotEmpty().MaximumLength(120).When(x => x.Address is not null);
        RuleFor(x => x.Address!.Value.SubCity).NotEmpty().MaximumLength(120).When(x => x.Address is not null);
        RuleFor(x => x.Address!.Value.Woreda).NotEmpty().MaximumLength(120).When(x => x.Address is not null);
        RuleFor(x => x.Address!.Value.Phone).Matches(@"^\+251[79]\d{8}$").When(x => x.Address is not null && !string.IsNullOrWhiteSpace(x.Address.Value.Phone))
            .WithMessage("Address phone must match +2519XXXXXXXX format.");
    }
}

public sealed record OrderEventDto(Guid Id, string Status, string? Note, string ByRole, DateTime CreatedAtUtc);
public sealed record OrderItemDto(Guid Id, Guid ProductId, string TitleSnapshot, string? ColorName, string? ColorHex, Guid? VendorId, string? VendorName,
    Guid? VariantId, string? VariantSku, string? VariantLabel,
    decimal UnitPriceETB, int Quantity, decimal LineNetETB, decimal VatETB, decimal CommissionRateSnapshot, decimal CommissionETB, decimal DeliveryFeeAllocationETB, string RefundStatus);
public sealed record OrderDto(Guid Id, string OrderNumber, Guid BuyerId, string Status, string? PreviousStatus,
    string AddressLine, string City, string ZoneName, string Phone, decimal SubtotalETB, decimal DeliveryFeeETB,
    decimal VatRatePercent, decimal VatETB, decimal GrandTotalETB, decimal CouponDiscountETB, string? CouponCode,
    Guid? AddressId, string? DeliveryPin, string? TrackingCode, string? RefundReason, DateTime? DeliveredAtUtc, DateTime CreatedAtUtc,
    IReadOnlyList<OrderItemDto> Items, IReadOnlyList<OrderEventDto> Events)
{
    public static OrderDto From(Order o) => new(o.Id, o.OrderNumber, o.BuyerId, o.Status.ToString(), o.PreviousStatus,
        o.AddressLine, o.City, o.ZoneNameSnapshot, o.Phone, o.SubtotalETB, o.DeliveryFeeETB, o.VatRatePercent, o.VatETB, o.GrandTotalETB,
        o.CouponDiscountETB, o.CouponCode, o.AddressId, o.DeliveryPin, o.TrackingCode, o.RefundReason, o.DeliveredAtUtc, o.CreatedAtUtc,
        o.Items.Select(i => new OrderItemDto(i.Id, i.ProductId, i.TitleSnapshot, i.ColorNameSnapshot, i.ColorHexSnapshot, i.VendorId, i.VendorNameSnapshot,
            i.VariantId, i.VariantSku, i.VariantLabel, i.UnitPriceETB,
            i.Quantity, i.LineNetETB, i.VatETB, i.CommissionRateSnapshot, i.CommissionETB, i.DeliveryFeeAllocationETB, i.RefundStatus)).ToList(),
        o.Events.OrderBy(e => e.CreatedAtUtc).Select(e => new OrderEventDto(e.Id, e.Status, e.Note, e.ByRole, e.CreatedAtUtc)).ToList());
}

public sealed class CheckoutHandler(SalesDbContext db, ICatalogReader catalog, IStockReservations reservations, ICouponService coupons,
    ICommissionResolver commissionResolver, MarketHub.Shared.ISettingsProvider settings, IValidator<CheckoutCommand> validator)
    : IRequestHandler<CheckoutCommand, Result<OrderDto>>
{
    public async Task<Result<OrderDto>> Handle(CheckoutCommand c, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(c, ct);
        if (!validation.IsValid) return Result.Fail<OrderDto>("validation", string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));

        var cartItems = await db.CartItems.AsNoTracking().Where(x => x.UserId == c.BuyerId).ToListAsync(ct);
        if (c.ItemIds is { Count: > 0 })
            cartItems = cartItems.Where(i => c.ItemIds.Contains(i.Id)).ToList();
        if (cartItems.Count == 0) return Result.Fail<OrderDto>("empty_cart", "Your cart is empty.");

        var zone = await db.DeliveryZones.AsNoTracking().FirstOrDefaultAsync(z => z.Id == c.ZoneId && z.IsActive, ct)
            ?? throw new NotFoundException("DeliveryZone", c.ZoneId);
        var rate = await db.DeliveryRates.AsNoTracking().FirstOrDefaultAsync(r => r.ZoneId == c.ZoneId, ct);
        if (rate is null) return Result.Fail<OrderDto>("zone_unavailable", "Delivery is not configured for the selected zone.");

        // ---- address: saved entry OR inline (optionally persisted to the address book) ----
        Address? savedAddress = null;
        string addressLine;
        if (c.AddressId.HasValue)
        {
            savedAddress = await db.Addresses.AsNoTracking().FirstOrDefaultAsync(a => a.Id == c.AddressId.Value && a.UserId == c.BuyerId, ct)
                ?? throw new NotFoundException("Address", c.AddressId.Value);
            addressLine = savedAddress.ToAddressLine();
        }
        else
        {
            var inline = c.Address!;
            if (string.IsNullOrWhiteSpace(inline.FullName) || string.IsNullOrWhiteSpace(inline.Region)
                || string.IsNullOrWhiteSpace(inline.City) || string.IsNullOrWhiteSpace(inline.SubCity) || string.IsNullOrWhiteSpace(inline.Woreda))
                return Result.Fail<OrderDto>("address", "Full name, region, city, sub-city and woreda are required.");
            var inlineAddress = new Address
            {
                UserId = c.BuyerId,
                FullName = inline.FullName.Trim(),
                Phone = string.IsNullOrWhiteSpace(inline.Phone) ? c.Phone : inline.Phone.Trim(),
                Region = inline.Region.Trim(),
                City = inline.City.Trim(),
                SubCity = inline.SubCity.Trim(),
                Woreda = inline.Woreda.Trim(),
                Kebele = string.IsNullOrWhiteSpace(inline.Kebele) ? null : inline.Kebele.Trim(),
                Landmark = string.IsNullOrWhiteSpace(inline.Landmark) ? null : inline.Landmark.Trim(),
            };
            addressLine = inlineAddress.ToAddressLine();
            if (c.SaveAddress)
            {
                var isFirst = !await db.Addresses.AnyAsync(a => a.UserId == c.BuyerId, ct);
                inlineAddress.IsDefault = isFirst;
                db.Addresses.Add(inlineAddress);
                savedAddress = inlineAddress;
            }
        }

        // ---- stock snapshots + reservation-aware availability ----
        var snapshots = new List<CatalogProductSnapshot>();
        var variantSnapshots = new Dictionary<Guid, CatalogVariantSnapshot>();
        var colorSnapshots = new Dictionary<Guid, CatalogColorSnapshot>();
        foreach (var item in cartItems)
        {
            var p = await catalog.FindActiveAsync(item.ProductId, ct) ?? throw new NotFoundException("Product", item.ProductId);
            snapshots.Add(p);
            if (item.VariantId.HasValue)
            {
                var v = await catalog.FindVariantAsync(item.VariantId.Value, ct) ?? throw new NotFoundException("ProductVariant", item.VariantId.Value);
                if (!v.IsActive) return Result.Fail<OrderDto>("variant_inactive", $"'{p.Title}' ({v.Sku}) is no longer available.");
                if (v.ProductId != item.ProductId) return Result.Fail<OrderDto>("variant", "Variant does not belong to the product in your cart.");
                if (await catalog.GetAvailableVariantStockAsync(v.Id, ct) < item.Quantity)
                    return Result.Fail<OrderDto>("stock", $"Insufficient stock for '{p.Title}' ({v.Sku}).");
                variantSnapshots[v.Id] = v;
            }
            else if (item.ColorId.HasValue)
            {
                var col = await catalog.FindColorAsync(item.ColorId.Value, ct) ?? throw new NotFoundException("ProductColor", item.ColorId.Value);
                if (await catalog.GetAvailableColorStockAsync(col.Id, ct) < item.Quantity)
                    return Result.Fail<OrderDto>("stock", $"Only {await catalog.GetAvailableColorStockAsync(col.Id, ct)} left in {p.Title} ({col.Name}).");
                colorSnapshots[col.Id] = col;
            }
            else if (await catalog.GetAvailableStockAsync(p.Id, ct) < item.Quantity)
            {
                return Result.Fail<OrderDto>("stock", $"Insufficient stock for '{p.Title}'.");
            }
        }

        // ---- money: subtotal → coupon discount → delivery → VAT on (subtotal − discount + delivery) ----
        decimal UnitPrice(CartItem item, CatalogProductSnapshot p)
        {
            if (item.VariantId.HasValue) return Math.Round(variantSnapshots[item.VariantId.Value].PriceETB ?? p.PriceETB, 2, MidpointRounding.AwayFromZero);
            var extra = item.ColorId.HasValue ? colorSnapshots[item.ColorId.Value].ExtraPriceETB : 0m;
            return Math.Round(p.PriceETB + extra, 2, MidpointRounding.AwayFromZero);
        }

        var subtotal = Math.Round(cartItems.Zip(snapshots, (i, p) => Math.Round(UnitPrice(i, p) * i.Quantity, 2, MidpointRounding.AwayFromZero))
            .Sum(), 2, MidpointRounding.AwayFromZero);

        string? couponCode = null;
        var couponDiscount = 0m;
        if (!string.IsNullOrWhiteSpace(c.CouponCode))
        {
            var quote = await coupons.FindActiveAsync(c.CouponCode.Trim(), ct);
            if (quote is null) return Result.Fail<OrderDto>("coupon", "Coupon code not found.");
            var (ok, discount, message) = Pricing.CouponCalculator.Validate(quote, subtotal, DateTime.UtcNow);
            if (!ok) return Result.Fail<OrderDto>("coupon", message);
            couponCode = quote.Code;
            couponDiscount = discount;
        }

        var totalWeight = snapshots.Zip(cartItems, (p, i) => p.WeightKg * i.Quantity).Sum();
        var deliveryFee = Pricing.DeliveryFeeCalculator.Calculate(rate.BaseFeeETB, rate.PerKgSurchargeETB, rate.FreeThresholdETB, totalWeight, subtotal);
        var vatRate = await settings.GetValueAsync<decimal>(SettingKeys.VatRate, 15m, ct);
        var vat = Pricing.VatCalculator.CalculateVat(subtotal - couponDiscount, deliveryFee, vatRate);
        var grandTotal = Math.Round(subtotal - couponDiscount + deliveryFee + vat, 2, MidpointRounding.AwayFromZero);

        var order = new Order
        {
            OrderNumber = $"MH-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(100000, 999999)}",
            BuyerId = c.BuyerId,
            AddressLine = addressLine,
            City = savedAddress?.City ?? c.Address!.City.Trim(),
            ZoneId = c.ZoneId,
            ZoneNameSnapshot = zone.Name,
            Phone = c.Phone,
            SubtotalETB = subtotal,
            DeliveryFeeETB = deliveryFee,
            VatRatePercent = vatRate,
            VatETB = vat,
            GrandTotalETB = grandTotal,
            DeliveryPin = Pricing.OrderCodes.NewDeliveryPin(),
            TrackingCode = Pricing.OrderCodes.NewTrackingCode(), // "MH-TRK-xxxxxx" issued at placement (ship may override)
            AddressId = savedAddress?.Id,
            CouponCode = couponCode,
            CouponDiscountETB = couponDiscount,
            Status = OrderStatus.Placed,
        };

        var items = new List<OrderItem>();
        foreach (var (item, p) in cartItems.Zip(snapshots))
        {
            var variant = item.VariantId.HasValue ? variantSnapshots[item.VariantId.Value] : null;
            var color = item.ColorId.HasValue ? colorSnapshots[item.ColorId.Value] : null;
            var unitPrice = UnitPrice(item, p);
            var grossLine = Math.Round(unitPrice * item.Quantity, 2, MidpointRounding.AwayFromZero);
            // coupon discount is distributed proportionally across lines (deterministic, auditable)
            var discountShare = subtotal == 0m ? 0m : Math.Round(grossLine / subtotal * couponDiscount, 2, MidpointRounding.AwayFromZero);
            var lineNet = Math.Round(grossLine - discountShare, 2, MidpointRounding.AwayFromZero);
            var commissionRate = await commissionResolver.ResolveAsync(p.VendorId, null, ct);
            // first-party items carry no platform commission — the platform keeps its own margin
            var effectiveRate = p.VendorId is null ? 0m : commissionRate;
            var commission = Math.Round(lineNet * effectiveRate, 2, MidpointRounding.AwayFromZero);
            var colorForLabel = variant?.ColorId.HasValue == true ? colorSnapshots.GetValueOrDefault(variant.ColorId.Value) : color;
            var labelParts = new[] { colorForLabel?.Name, variant?.Size }.Where(s => !string.IsNullOrWhiteSpace(s));
            var variantLabel = string.Join(" / ", labelParts) is { Length: > 0 } label ? label : variant?.Sku;
            items.Add(new OrderItem
            {
                OrderId = order.Id,
                ProductId = p.Id,
                TitleSnapshot = p.Title,
                ColorNameSnapshot = color?.Name,
                ColorHexSnapshot = color?.Hex,
                VariantId = variant?.Id,
                VariantSku = variant?.Sku,
                VariantLabel = variantLabel,
                VendorId = p.VendorId,
                VendorNameSnapshot = p.VendorId is null ? "MarketHub Store" : null,
                UnitPriceETB = unitPrice,
                Quantity = item.Quantity,
                LineNetETB = lineNet,
                VatETB = subtotal == 0m ? 0m : Math.Round(lineNet / subtotal * vat, 2, MidpointRounding.AwayFromZero),
                CommissionRateSnapshot = effectiveRate,
                CommissionETB = commission,
                DeliveryFeeAllocationETB = subtotal == 0m ? 0m : Math.Round(grossLine / subtotal * deliveryFee, 2, MidpointRounding.AwayFromZero),
            });
        }
        order.Items = items;
        order.Events = new[]
        {
            new OrderEvent { OrderId = order.Id, Status = OrderStatus.Placed.ToString(), Note = "Order placed — awaiting payment.", ByRole = "System" },
        };
        db.Orders.Add(order);

        // ---- reserve stock for this order (stock itself decrements on payment confirmation) ----
        var ttlMinutes = await settings.GetValueAsync<int>(SettingKeys.ReservationMinutes, 20, ct);
        var lines = cartItems.Select(i => new ReservationRequestLine(
            i.ProductId, i.VariantId, i.VariantId.HasValue ? null : i.ColorId, i.Quantity)).ToList();
        var results = await reservations.ReserveForUserAsync(c.BuyerId, lines, TimeSpan.FromMinutes(Math.Max(1, ttlMinutes)), order.Id, ct);
        if (results.Any(r => !r.Ok))
        {
            // roll the just-created reservations back (ReleaseForUser clears every Active row of the buyer)
            await reservations.ReleaseForUserAsync(c.BuyerId, ct);
            return Result.Fail<OrderDto>("stock", "Stock changed while placing your order — please review your cart and retry.");
        }

        db.CartItems.RemoveRange(cartItems);
        if (await db.Carts.AnyAsync(s => s.UserId == c.BuyerId, ct))
            db.Carts.RemoveRange(db.Carts.Where(s => s.UserId == c.BuyerId));
        db.AddToOutbox(new OrderPlacedEvent(order.Id, order.BuyerId, grandTotal));
        await db.SaveChangesAsync(ct);

        // redemption ledger row + used-count increment (own context; idempotent per coupon+order)
        if (couponCode is not null)
            await coupons.RecordRedemptionAsync(couponCode, order.Id, c.BuyerId, couponDiscount, ct);
        return Result.Ok(OrderDto.From(order));
    }
}

// ==================== ORDER QUERIES & VENDOR ACTIONS ====================
public sealed record GetMyOrdersQuery(Guid BuyerId, string? Status) : IRequest<Result<IReadOnlyList<OrderDto>>>;

public sealed class GetMyOrdersHandler(SalesDbContext db) : IRequestHandler<GetMyOrdersQuery, Result<IReadOnlyList<OrderDto>>>
{
    public async Task<Result<IReadOnlyList<OrderDto>>> Handle(GetMyOrdersQuery q, CancellationToken ct)
    {
        var query = db.Orders.AsNoTracking().Include(o => o.Items).Include(o => o.Events).Where(o => o.BuyerId == q.BuyerId);
        if (Enum.TryParse<OrderStatus>(q.Status, true, out var s)) query = query.Where(o => o.Status == s);
        var orders = await query.OrderByDescending(o => o.CreatedAtUtc).Take(100).ToListAsync(ct);
        return Result.Ok(orders.Select(OrderDto.From).ToList());
    }
}

public sealed record GetVendorOrdersQuery(Guid VendorId) : IRequest<Result<IReadOnlyList<OrderDto>>>;

public sealed class GetVendorOrdersHandler(SalesDbContext db) : IRequestHandler<GetVendorOrdersQuery, Result<IReadOnlyList<OrderDto>>>
{
    public async Task<Result<IReadOnlyList<OrderDto>>> Handle(GetVendorOrdersQuery q, CancellationToken ct)
    {
        var orders = await db.Orders.AsNoTracking().Include(o => o.Items).Include(o => o.Events)
            .Where(o => o.Items.Any(i => i.VendorId == q.VendorId))
            .OrderByDescending(o => o.CreatedAtUtc).Take(100).ToListAsync(ct);
        var dtos = orders.Select(o =>
        {
            o.Items = o.Items.Where(i => i.VendorId == q.VendorId).ToList(); // vendor sees only their lines
            return OrderDto.From(o);
        }).ToList();
        return Result.Ok(dtos);
    }
}

public sealed record VendorOrderActionCommand(Guid VendorId, Guid OrderId, string Action, string? TrackingCode, string? Pin) : IRequest<Result<OrderDto>>;

public sealed class VendorOrderActionHandler(SalesDbContext db, IPublisher publisher) : IRequestHandler<VendorOrderActionCommand, Result<OrderDto>>
{
    public async Task<Result<OrderDto>> Handle(VendorOrderActionCommand c, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == c.OrderId, ct)
            ?? throw new NotFoundException("Order", c.OrderId);
        if (!order.Items.Any(i => i.VendorId == c.VendorId)) throw new NotFoundException("Order", c.OrderId);

        switch (c.Action.ToLowerInvariant())
        {
            case "accept":
                order.Transition(OrderStatus.VendorAccepted);
                db.OrderEvents.Add(new OrderEvent { OrderId = order.Id, Status = OrderStatus.VendorAccepted.ToString(), Note = "Vendor accepted the order.", ByRole = "Vendor" });
                break;
            case "reject":
                order.Transition(OrderStatus.RefundRequested);
                order.RefundReason = "Vendor rejected the order — full refund initiated.";
                foreach (var item in order.Items) item.RefundStatus = "Requested";
                db.OrderEvents.Add(new OrderEvent { OrderId = order.Id, Status = OrderStatus.RefundRequested.ToString(), Note = order.RefundReason, ByRole = "Vendor" });
                break;
            case "packed":
            case "ship":
                // shipping requires a courier tracking code (MH-TRK-…)
                if (string.IsNullOrWhiteSpace(c.TrackingCode))
                    return Result.Fail<OrderDto>("tracking_code", "A trackingCode is required to ship the order.");
                if (!Pricing.OrderCodes.LooksLikeTrackingCode(c.TrackingCode.Trim()))
                    return Result.Fail<OrderDto>("tracking_code", "Tracking code must look like MH-TRK-XXXXXX.");
                order.Transition(OrderStatus.Shipped);
                order.TrackingCode = c.TrackingCode.Trim().ToUpperInvariant();
                db.OrderEvents.Add(new OrderEvent { OrderId = order.Id, Status = OrderStatus.Shipped.ToString(), Note = $"Shipped via courier — tracking {order.TrackingCode}.", ByRole = "Vendor" });
                db.AddToOutbox(new OrderShippedEvent(order.Id, order.BuyerId, order.TrackingCode));
                break;
            case "deliver":
                // hand-over only with the buyer's 4-digit delivery PIN (shown to the buyer after payment)
                if (!Pricing.OrderCodes.PinMatches(order.DeliveryPin, c.Pin))
                    return Result.Fail<OrderDto>("pin", "The delivery PIN does not match. Ask the buyer for the 4-digit code.");
                order.Transition(OrderStatus.Delivered);
                db.OrderEvents.Add(new OrderEvent { OrderId = order.Id, Status = OrderStatus.Delivered.ToString(), Note = "Delivered — PIN verified at hand-over.", ByRole = "Vendor" });
                db.AddToOutbox(new OrderDeliveredEvent(order.Id, order.BuyerId));
                break;
            default:
                throw new DomainException($"Unknown vendor order action '{c.Action}'.");
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok(OrderDto.From(order));
    }
}

public sealed record BuyerRequestRefundCommand(Guid BuyerId, Guid OrderId, string Reason) : IRequest<Result<OrderDto>>;

public sealed class BuyerRequestRefundHandler(SalesDbContext db, MarketHub.Shared.ISettingsProvider settings) : IRequestHandler<BuyerRequestRefundCommand, Result<OrderDto>>
{
    public async Task<Result<OrderDto>> Handle(BuyerRequestRefundCommand c, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.Reason) || c.Reason.Trim().Length < 10)
            return Result.Fail<OrderDto>("reason", "Please describe the refund reason (at least 10 characters).");
        var order = await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == c.OrderId && o.BuyerId == c.BuyerId, ct)
            ?? throw new NotFoundException("Order", c.OrderId);
        if (!OrderStateMachine.CanTransition(order.Status, OrderStatus.RefundRequested))
            return Result.Fail<OrderDto>("state", $"A refund cannot be requested while the order is {order.Status}. See the Return & Refund Policy.");

        var windowDays = await settings.GetValueAsync<int>(SettingKeys.ReturnWindowDays, 7, ct);
        if (order.DeliveredAtUtc is not null && DateTime.UtcNow - order.DeliveredAtUtc.Value > TimeSpan.FromDays(windowDays))
            return Result.Fail<OrderDto>("window", $"The {windowDays}-day return window has passed for this order.");

        order.Transition(OrderStatus.RefundRequested);
        order.RefundReason = c.Reason.Trim();
        foreach (var item in order.Items) item.RefundStatus = "Requested";
        await db.SaveChangesAsync(ct);
        return Result.Ok(OrderDto.From(order));
    }
}

public sealed record AdminDecideRefundCommand(Guid OrderId, bool Approve, string? Note) : IRequest<Result<OrderDto>>;

public sealed class AdminDecideRefundHandler(SalesDbContext db, IPublisher publisher) : IRequestHandler<AdminDecideRefundCommand, Result<OrderDto>>
{
    public async Task<Result<OrderDto>> Handle(AdminDecideRefundCommand c, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == c.OrderId, ct)
            ?? throw new NotFoundException("Order", c.OrderId);
        if (order.Status != OrderStatus.RefundRequested) return Result.Fail<OrderDto>("state", $"Order is {order.Status} — no pending refund request.");

        var amount = order.GrandTotalETB;
        if (c.Approve)
        {
            order.Transition(OrderStatus.Refunded);
            foreach (var item in order.Items) item.RefundStatus = "Approved";
            db.AddToOutbox(new RefundDecidedEvent(order.Id, true, amount));
        }
        else
        {
            var restored = Enum.TryParse<OrderStatus>(order.PreviousStatus, true, out var prev) ? prev : OrderStatus.Completed;
            if (!OrderStateMachine.CanTransition(order.Status, restored)) restored = OrderStatus.Completed;
            order.Status = restored;
            foreach (var item in order.Items) item.RefundStatus = "Rejected";
            db.AddToOutbox(new RefundDecidedEvent(order.Id, false, amount));
        }
        await db.SaveChangesAsync(ct);
        await publisher.Publish(new RefundDecidedNotification(order.Id, c.Approve, amount, c.Note), ct);
        return Result.Ok(OrderDto.From(order));
    }
}

public sealed record RefundDecidedNotification(Guid OrderId, bool Approved, decimal Amount, string? Note) : MediatR.INotification;

public sealed record AdminListOrdersQuery(string? Status) : IRequest<Result<IReadOnlyList<OrderDto>>>;

public sealed class AdminListOrdersHandler(SalesDbContext db) : IRequestHandler<AdminListOrdersQuery, Result<IReadOnlyList<OrderDto>>>
{
    public async Task<Result<IReadOnlyList<OrderDto>>> Handle(AdminListOrdersQuery q, CancellationToken ct)
    {
        var query = db.Orders.AsNoTracking().Include(o => o.Items).Include(o => o.Events).AsQueryable();
        if (Enum.TryParse<OrderStatus>(q.Status, true, out var s)) query = query.Where(o => o.Status == s);
        var orders = await query.OrderByDescending(o => o.CreatedAtUtc).Take(100).ToListAsync(ct);
        return Result.Ok(orders.Select(OrderDto.From).ToList());
    }
}
