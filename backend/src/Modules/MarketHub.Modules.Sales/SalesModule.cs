using MarketHub.Shared;
using MarketHub.Shared.Contracts;
using MarketHub.Modules.Sales.Domain;
using MarketHub.Modules.Sales.Features;
using MarketHub.Modules.Sales.Persistence;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MarketHub.Modules.Sales;

/// <summary>Sales module composition: cart, checkout (VAT + delivery + coupon), reservations, orders, returns.</summary>
public static class SalesModule
{
    public static IServiceCollection AddSalesModule(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddDbContext<SalesDbContext>(o => o.UseNpgsql(cfg.GetConnectionString("Default")));
        services.AddValidatorsFromAssemblyContaining<CheckoutValidator>();
        services.AddScoped<IOrderReader, OrderReader>();
        services.AddScoped<IAbandonedCartReader, AbandonedCartReader>();
        services.AddTransient<IModuleSeeder, SalesSeeder>();
        return services;
    }

    /// <summary>Read-side implementation consumed by Finance (ledger posting, reports) and Catalog (review gate).</summary>
    private sealed class OrderReader(IServiceProvider sp) : IOrderReader
    {
        public async Task<OrderFinancials?> GetFinancialsAsync(Guid orderId, CancellationToken ct = default)
        {
            var scope = sp.CreateScope();
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<SalesDbContext>();
                var o = await db.Orders.AsNoTracking().Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == orderId, ct);
                if (o is null) return null;
                return new OrderFinancials(o.Id, o.BuyerId, o.SubtotalETB, o.DeliveryFeeETB, o.VatETB, o.GrandTotalETB,
                    o.Items.Select(i => new OrderItemFinancials(i.Id, i.VendorId, i.LineNetETB, i.CommissionETB, i.DeliveryFeeAllocationETB)).ToList());
            }
            finally { scope.Dispose(); }
        }

        public async Task<long> CountOrdersAsync(DateTime? fromUtc = null, Guid? vendorId = null, CancellationToken ct = default)
        {
            var scope = sp.CreateScope();
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<SalesDbContext>();
                if (vendorId.HasValue)
                    return await db.Orders.AsNoTracking().CountAsync(o => o.Items.Any(i => i.VendorId == vendorId) && (fromUtc == null || o.CreatedAtUtc >= fromUtc), ct);
                return await db.Orders.AsNoTracking().CountAsync(o => fromUtc == null || o.CreatedAtUtc >= fromUtc, ct);
            }
            finally { scope.Dispose(); }
        }

        public async Task<IReadOnlyList<(Guid ProductId, int SoldQty, decimal RevenueETB)>> GetTopProductAggregatesAsync(int limit, DateTime? fromUtc = null, CancellationToken ct = default)
        {
            var scope = sp.CreateScope();
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<SalesDbContext>();
                var paid = db.Orders.AsNoTracking().Where(o => o.Status != OrderStatus.Placed && o.Status != OrderStatus.Cancelled);
                var query = db.OrderItems.AsNoTracking().Join(paid, i => i.OrderId, o => o.Id, (i, _) => i);
                if (fromUtc.HasValue)
                {
                    var from = fromUtc.Value;
                    var paidIds = await paid.Where(o => o.CreatedAtUtc >= from).Select(o => o.Id).ToListAsync(ct);
                    query = db.OrderItems.AsNoTracking().Where(i => paidIds.Contains(i.OrderId));
                }
                var rows = await query.GroupBy(i => i.ProductId)
                    .Select(g => new { ProductId = g.Key, SoldQty = g.Sum(x => x.Quantity), Revenue = g.Sum(x => x.LineNetETB) })
                    .OrderByDescending(x => x.Revenue).Take(limit).ToListAsync(ct);
                return rows.Select(r => (r.ProductId, r.SoldQty, r.Revenue)).ToList();
            }
            finally { scope.Dispose(); }
        }

        public async Task<string?> GetOrderStatusForBuyerProductAsync(Guid buyerId, Guid orderId, Guid productId, CancellationToken ct = default)
        {
            var scope = sp.CreateScope();
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<SalesDbContext>();
                // verified-purchase gate: order belongs to the buyer AND contains the product; else null
                var status = await db.Orders.AsNoTracking()
                    .Where(o => o.Id == orderId && o.BuyerId == buyerId && o.Items.Any(i => i.ProductId == productId))
                    .Select(o => (OrderStatus?)o.Status)
                    .FirstOrDefaultAsync(ct);
                return status?.ToString();
            }
            finally { scope.Dispose(); }
        }
    }
}

/// <summary>HTTP endpoints for the Sales module.</summary>
public static class SalesEndpoints
{
    public static void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sales").WithTags("Sales");
        group.MapGet("/delivery-zones", async (IMediator m) => (await m.Send(new ListZonesQuery())).ToHttp()).AllowAnonymous();

        var cart = group.MapGroup("/cart").RequireAuthorization();
        cart.MapGet("/", async (IMediator m, ICurrentUser user) => (await m.Send(new GetCartQuery(user.UserId!.Value))).ToHttp());
        cart.MapPost("/", async (IMediator m, AddToCartRequest req, ICurrentUser user) =>
            (await m.Send(new AddToCartCommand(user.UserId!.Value, req.ProductId, req.VariantId, req.ColorId, req.Quantity))).ToHttp());
        cart.MapPatch("/", async (IMediator m, UpdateCartRequest req, ICurrentUser user) =>
            (await m.Send(new UpdateCartItemCommand(user.UserId!.Value, req.CartItemId, req.Quantity))).ToHttp());
        cart.MapDelete("/{id}", async (IMediator m, Guid id, ICurrentUser user) =>
            (await m.Send(new RemoveCartItemCommand(user.UserId!.Value, id))).ToHttp());
        cart.MapPost("/reserve", async (IMediator m, ICurrentUser user) =>
            (await m.Send(new ReserveCartCommand(user.UserId!.Value))).ToHttp());
        cart.MapPost("/release", async (IMediator m, ICurrentUser user) =>
            (await m.Send(new ReleaseCartCommand(user.UserId!.Value))).ToHttp());
        cart.MapPost("/save", async (IMediator m, SaveCartRequest req, ICurrentUser user) =>
            (await m.Send(new SaveCartCommand(user.UserId!.Value, req.Items.Select(i => new CartSaveItemDto(i.ProductId, i.VariantId, i.ColorId, i.Qty)).ToList()))).ToHttp());
        cart.MapPost("/coupon", async (IMediator m, CouponQuoteRequest req, ICurrentUser user) =>
            (await m.Send(new QuoteCouponCommand(user.UserId!.Value, req.Code, req.Subtotal))).ToHttp());

        group.MapPost("/checkout", async (IMediator m, CheckoutRequest req, ICurrentUser user) =>
            (await m.Send(new CheckoutCommand(user.UserId!.Value, req.AddressId,
                req.Address is null ? null : new CheckoutAddressInput(req.Address.FullName, req.Address.Phone, req.Address.Region,
                    req.Address.City, req.Address.SubCity, req.Address.Woreda, req.Address.Kebele, req.Address.Landmark),
                req.SaveAddress, req.ZoneId, req.Phone, req.ItemIds, req.CouponCode))).ToHttp()).RequireAuthorization("Buyer");
        group.MapGet("/orders", async (IMediator m, ICurrentUser user, string? status) =>
            (await m.Send(new GetMyOrdersQuery(user.UserId!.Value, status))).ToHttp()).RequireAuthorization();
        group.MapPost("/orders/{id}/cancel", async (IMediator m, Guid id, ICurrentUser user) =>
            (await m.Send(new CancelOrderCommand(user.UserId!.Value, id))).ToHttp()).RequireAuthorization();
        group.MapPost("/orders/{id}/refund-request", async (IMediator m, Guid id, RefundRequest req, ICurrentUser user) =>
            (await m.Send(new BuyerRequestRefundCommand(user.UserId!.Value, id, req.Reason))).ToHttp()).RequireAuthorization();

        var addresses = group.MapGroup("/addresses").RequireAuthorization();
        addresses.MapGet("/", async (IMediator m, ICurrentUser user) => (await m.Send(new ListAddressesQuery(user.UserId!.Value))).ToHttp());
        addresses.MapPost("/", async (IMediator m, AddressRequest req, ICurrentUser user) =>
            (await m.Send(new CreateAddressCommand(user.UserId!.Value, req.Label ?? "Home", req.FullName, req.Phone, req.Region, req.City, req.SubCity, req.Woreda, req.Kebele, req.Landmark))).ToHttp());

        var returns = group.MapGroup("/returns").RequireAuthorization();
        returns.MapPost("/", async (IMediator m, CreateReturnRequest req, ICurrentUser user) =>
            (await m.Send(new CreateReturnCommand(user.UserId!.Value, req.OrderItemId, req.Reason, req.Description, req.EvidenceUrls))).ToHttp());
        returns.MapGet("/", async (IMediator m, ICurrentUser user) => (await m.Send(new ListMyReturnsQuery(user.UserId!.Value))).ToHttp());

        var vendor = app.MapGroup("/api/vendor").WithTags("Vendor").RequireAuthorization("Vendor");
        vendor.MapGet("/sales/orders", async (IMediator m, ICurrentUser user) => (await m.Send(new GetVendorOrdersQuery(user.UserId!.Value))).ToHttp());
        vendor.MapPost("/sales/orders/{id}/{action}", async (IMediator m, Guid id, string action, VendorActionRequest? req, ICurrentUser user) =>
            (await m.Send(new VendorOrderActionCommand(user.UserId!.Value, id, action, req?.TrackingCode, req?.Pin))).ToHttp());
        vendor.MapGet("/returns", async (IMediator m, ICurrentUser user, string? status) =>
            (await m.Send(new ListVendorReturnsQuery(user.UserId!.Value, status))).ToHttp());
        vendor.MapPost("/returns/{id}/decision", async (IMediator m, Guid id, VendorReturnDecisionRequest req, ICurrentUser user) =>
            (await m.Send(new VendorReturnDecisionCommand(user.UserId!.Value, id, req.Approve, req.Note))).ToHttp());
        vendor.MapGet("/performance", async (IMediator m, ICurrentUser user) => (await m.Send(new VendorPerformanceQuery(user.UserId!.Value))).ToHttp());
        vendor.MapGet("/dashboard", async (IMediator m, ICurrentUser user) => (await m.Send(new VendorDashboardQuery(user.UserId!.Value))).ToHttp());

        var admin = app.MapGroup("/api/admin/sales").WithTags("Admin").RequireAuthorization("Admin");
        admin.MapGet("/orders", async (IMediator m, string? status) => (await m.Send(new AdminListOrdersQuery(status))).ToHttp());
        admin.MapPost("/orders/{id}/refund-decision", async (IMediator m, Guid id, RefundDecisionRequest req) =>
            (await m.Send(new AdminDecideRefundCommand(id, req.Approve, req.Note))).ToHttp());

        var adminRoot = app.MapGroup("/api/admin").WithTags("Admin").RequireAuthorization("Admin");
        adminRoot.MapGet("/returns", async (IMediator m, string? status) => (await m.Send(new AdminListReturnsQuery(status))).ToHttp());
        adminRoot.MapPost("/returns/{id}/arbitrate", async (IMediator m, Guid id, ArbitrateRequest req, ICurrentUser user) =>
            (await m.Send(new ArbitrateReturnCommand(user.UserId!.Value, id, req.Approve, req.RefundETB, req.Note))).ToHttp());
        // same shape as the vendor self-view (admin console vendor drill-down)
        adminRoot.MapGet("/vendors/{id}/performance", async (IMediator m, Guid id) => (await m.Send(new VendorPerformanceQuery(id))).ToHttp());
    }

    public sealed record AddToCartRequest(Guid ProductId, Guid? VariantId, Guid? ColorId, int Quantity);
    public sealed record UpdateCartRequest(Guid CartItemId, int Quantity);
    public sealed record SaveCartItem(Guid ProductId, Guid? VariantId, Guid? ColorId, int Qty);
    public sealed record SaveCartRequest(IReadOnlyList<SaveCartItem> Items);
    public sealed record CouponQuoteRequest(string Code, decimal Subtotal);
    public sealed record CheckoutAddressBody(string FullName, string Phone, string Region, string City, string SubCity, string Woreda, string? Kebele, string? Landmark);
    public sealed record CheckoutRequest(Guid? AddressId, CheckoutAddressBody? Address, bool SaveAddress, Guid ZoneId, string Phone,
        IReadOnlyList<Guid>? ItemIds, string? CouponCode);
    public sealed record AddressRequest(string? Label, string FullName, string Phone, string Region, string City, string SubCity, string Woreda, string? Kebele, string? Landmark);
    public sealed record RefundRequest(string Reason);
    public sealed record RefundDecisionRequest(bool Approve, string? Note);
    public sealed record VendorActionRequest(string? TrackingCode, string? Pin);
    public sealed record CreateReturnRequest(Guid OrderItemId, string Reason, string Description, IReadOnlyList<string>? EvidenceUrls);
    public sealed record VendorReturnDecisionRequest(bool Approve, string? Note);
    public sealed record ArbitrateRequest(bool Approve, decimal RefundETB, string? Note);
}

/// <summary>Idempotent seeder: Ethiopian delivery zones + rates and two sample orders (completed + placed).</summary>
public sealed class SalesSeeder : IModuleSeeder
{
    public string ModuleName => "Sales";

    public async Task SeedAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<SalesDbContext>();
        if (await db.DeliveryZones.AnyAsync(ct)) return;

        var zones = new (Guid Id, string Name, decimal Base, decimal PerKg, decimal Free)[]
        {
            new(Guid.Parse("00000000-0000-0000-0000-00000000c001"), "Addis Ababa", 150m, 20m, 2000m),
            new(Guid.Parse("00000000-0000-0000-0000-00000000c002"), "Oromia", 250m, 25m, 0m),
            new(Guid.Parse("00000000-0000-0000-0000-00000000c003"), "Amhara", 250m, 25m, 0m),
            new(Guid.Parse("00000000-0000-0000-0000-00000000c004"), "Tigray", 300m, 30m, 0m),
            new(Guid.Parse("00000000-0000-0000-0000-00000000c005"), "Sidama", 250m, 25m, 0m),
            new(Guid.Parse("00000000-0000-0000-0000-00000000c006"), "Dire Dawa", 300m, 30m, 0m),
            new(Guid.Parse("00000000-0000-0000-0000-00000000c007"), "Somali", 350m, 30m, 0m),
        };
        foreach (var (id, name, baseFee, perKg, free) in zones)
        {
            db.DeliveryZones.Add(new DeliveryZone { Id = id, Name = name });
            db.DeliveryRates.Add(new DeliveryRate { ZoneId = id, BaseFeeETB = baseFee, PerKgSurchargeETB = perKg, FreeThresholdETB = free });
        }

        // Sample completed order for finance reporting: 2 vendor items, Oromia zone
        var subtotal = 5650m;
        var delivery = 250m;
        var vat = Pricing.VatCalculator.CalculateVat(subtotal, delivery, 15m);
        var completed = new Order
        {
            OrderNumber = $"MH-{DateTime.UtcNow.AddDays(-2):yyyyMMdd}-000001",
            BuyerId = SeedIds.BuyerUserId,
            Status = OrderStatus.Completed,
            AddressLine = "Gerji Mebrat Hail, House 88",
            City = "Adama",
            ZoneId = zones[1].Id,
            ZoneNameSnapshot = zones[1].Name,
            Phone = "+251921000005",
            SubtotalETB = subtotal,
            DeliveryFeeETB = delivery,
            VatRatePercent = 15m,
            VatETB = vat,
            GrandTotalETB = subtotal + delivery + vat,
            DeliveredAtUtc = DateTime.UtcNow.AddDays(-2),
        };
        var i1 = new OrderItem { OrderId = completed.Id, ProductId = Guid.Empty, TitleSnapshot = "BassPro Bluetooth Speaker", VendorId = SeedIds.Vendor1UserId, VendorNameSnapshot = "Sheba Electronics Trading PLC", UnitPriceETB = 3200m, Quantity = 1, LineNetETB = 3200m, VatETB = Math.Round(3200m / subtotal * vat, 2), CommissionRateSnapshot = 0.10m, CommissionETB = 320m, DeliveryFeeAllocationETB = Math.Round(3200m / subtotal * delivery, 2) };
        var i2 = new OrderItem { OrderId = completed.Id, ProductId = Guid.Empty, TitleSnapshot = "Wireless Earbuds Pro", VendorId = SeedIds.Vendor1UserId, VendorNameSnapshot = "Sheba Electronics Trading PLC", UnitPriceETB = 2450m, Quantity = 1, LineNetETB = 2450m, VatETB = Math.Round(2450m / subtotal * vat, 2), CommissionRateSnapshot = 0.10m, CommissionETB = 245m, DeliveryFeeAllocationETB = Math.Round(2450m / subtotal * delivery, 2) };
        completed.Items = new[] { i1, i2 };
        db.Orders.Add(completed);

        var placed = new Order
        {
            OrderNumber = $"MH-{DateTime.UtcNow:yyyyMMdd}-000002",
            BuyerId = SeedIds.BuyerUserId,
            Status = OrderStatus.Placed,
            AddressLine = "Bole Road, House 42",
            City = "Addis Ababa",
            ZoneId = zones[0].Id,
            ZoneNameSnapshot = zones[0].Name,
            Phone = "+251921000005",
            SubtotalETB = 1150m,
            DeliveryFeeETB = 0m,
            VatRatePercent = 15m,
            VatETB = Pricing.VatCalculator.CalculateVat(1150m, 0m, 15m),
            GrandTotalETB = Pricing.VatCalculator.CalculateVat(1150m, 0m, 15m) + 1150m,
        };
        placed.Items = new[] { new OrderItem { OrderId = placed.Id, ProductId = Guid.Empty, TitleSnapshot = "Ethiopian Coffee Beans Yirgacheffe 1kg", VendorId = SeedIds.Vendor2UserId, VendorNameSnapshot = "Habesha Home & Kitchen", UnitPriceETB = 1150m, Quantity = 1, LineNetETB = 1150m, VatETB = Math.Round(1150m / 1150m * placed.VatETB, 2), CommissionRateSnapshot = 0.10m, CommissionETB = 115m, DeliveryFeeAllocationETB = 0m } };
        db.Orders.Add(placed);

        await db.SaveChangesAsync(ct);
    }
}
