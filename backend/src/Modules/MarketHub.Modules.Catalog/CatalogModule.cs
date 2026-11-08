using MarketHub.Shared;
using MarketHub.Shared.Contracts;
using MarketHub.Modules.Catalog.Domain;
using MarketHub.Modules.Catalog.Features;
using MarketHub.Modules.Catalog.Infrastructure;
using MarketHub.Modules.Catalog.Persistence;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MarketHub.Modules.Catalog;

/// <summary>Catalog module composition.</summary>
public static class CatalogModule
{
    public static IServiceCollection AddCatalogModule(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddDbContext<CatalogDbContext>(o => o.UseNpgsql(cfg.GetConnectionString("Default")));
        services.AddValidatorsFromAssemblyContaining<CreateProductValidator>();
        services.AddScoped<ICatalogReader, CatalogReader>();
        services.AddScoped<IStockReservations, StockReservationService>();
        services.AddScoped<ICouponService, CouponService>();
        services.AddScoped<IOrderFulfillment, OrderFulfillmentService>();
        services.AddHttpClient("meilisearch");

        // search provider: Meilisearch when configured, otherwise the null indexer (SQL ILIKE fallback)
        if (string.Equals(cfg["Search:Provider"], "Meilisearch", StringComparison.OrdinalIgnoreCase))
            services.AddScoped<ISearchIndexer>(sp => new MeilisearchIndexer(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("meilisearch"),
                cfg["Meilisearch:Host"] ?? "http://localhost:7700",
                cfg["Meilisearch:ApiKey"],
                cfg["Meilisearch:IndexPrefix"] ?? "markethub"));
        else
            services.AddScoped<ISearchIndexer, NullSearchIndexer>();

        services.AddTransient<IModuleSeeder, CatalogSeeder>();
        return services;
    }

    /// <summary>Read-side implementation consumed by Sales (cart/checkout) and Finance (reports).</summary>
    private sealed class CatalogReader(IServiceProvider sp) : ICatalogReader
    {
        public async Task<CatalogProductSnapshot?> FindActiveAsync(Guid productId, CancellationToken ct = default)
        {
            var scope = sp.CreateScope();
            try
            {
                var p = await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Products.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == productId && x.Status == ProductStatus.Active, ct);
                return p is null ? null : new CatalogProductSnapshot(p.Id, p.Title, p.PriceETB, p.WeightKg, p.VendorId, p.Stock);
            }
            finally { scope.Dispose(); }
        }

        public async Task<bool> TryDecreaseStockAsync(Guid productId, int quantity, CancellationToken ct = default)
        {
            var scope = sp.CreateScope();
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
                var p = await db.Products.FirstOrDefaultAsync(x => x.Id == productId && x.Status == ProductStatus.Active, ct);
                if (p is null || !p.TryDecreaseStock(quantity)) return false;
                await db.SaveChangesAsync(ct);
                return true;
            }
            finally { scope.Dispose(); }
        }

        public async Task<CatalogColorSnapshot?> FindColorAsync(Guid colorId, CancellationToken ct = default)
        {
            var scope = sp.CreateScope();
            try
            {
                var c = await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().ProductColors.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == colorId, ct);
                return c is null ? null : new CatalogColorSnapshot(c.Id, c.ProductId, c.Name, c.Hex, c.ExtraPriceETB, c.Stock);
            }
            finally { scope.Dispose(); }
        }

        public async Task<bool> TryDecreaseColorStockAsync(Guid colorId, int quantity, CancellationToken ct = default)
        {
            var scope = sp.CreateScope();
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
                var c = await db.ProductColors.FirstOrDefaultAsync(x => x.Id == colorId, ct);
                if (c is null || !c.TryDecreaseStock(quantity)) return false;
                await db.SaveChangesAsync(ct);
                return true;
            }
            finally { scope.Dispose(); }
        }

        public async Task<IReadOnlyDictionary<Guid, string>> GetProductTitlesAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
        {
            var scope = sp.CreateScope();
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
                var titles = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).Select(p => new { p.Id, p.Title }).ToListAsync(ct);
                return titles.ToDictionary(x => x.Id, x => x.Title);
            }
            finally { scope.Dispose(); }
        }

        // ---- gap-fill v2: variants, reservation-aware availability, restock, vendor stats ----

        public async Task<CatalogVariantSnapshot?> FindVariantAsync(Guid variantId, CancellationToken ct = default)
        {
            var scope = sp.CreateScope();
            try
            {
                var v = await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().ProductVariants.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == variantId, ct);
                return v is null ? null : new CatalogVariantSnapshot(v.Id, v.ProductId, v.SKU, v.Size, v.PriceETB, v.Stock, v.IsActive, v.ColorId);
            }
            finally { scope.Dispose(); }
        }

        public async Task<bool> TryDecreaseVariantStockAsync(Guid variantId, int quantity, CancellationToken ct = default)
        {
            var scope = sp.CreateScope();
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
                var v = await db.ProductVariants.FirstOrDefaultAsync(x => x.Id == variantId, ct);
                if (v is null || !v.IsActive || !v.TryDecreaseStock(quantity)) return false;
                await db.SaveChangesAsync(ct);
                return true;
            }
            finally { scope.Dispose(); }
        }

        public async Task<int> GetAvailableStockAsync(Guid productId, CancellationToken ct = default)
        {
            var scope = sp.CreateScope();
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
                var stock = await db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => (int?)p.Stock).FirstOrDefaultAsync(ct) ?? 0;
                var reserved = await db.StockReservations.AsNoTracking()
                    .Where(r => r.ProductId == productId && r.Status == StockReservationStatus.Active && r.ExpiresAtUtc > DateTime.UtcNow)
                    .SumAsync(r => r.Quantity, ct);
                return ReservationMath.Available(stock, reserved);
            }
            finally { scope.Dispose(); }
        }

        public async Task<int> GetAvailableVariantStockAsync(Guid variantId, CancellationToken ct = default)
        {
            var scope = sp.CreateScope();
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
                var stock = await db.ProductVariants.AsNoTracking().Where(v => v.Id == variantId).Select(v => (int?)v.Stock).FirstOrDefaultAsync(ct) ?? 0;
                var reserved = await db.StockReservations.AsNoTracking()
                    .Where(r => r.VariantId == variantId && r.Status == StockReservationStatus.Active && r.ExpiresAtUtc > DateTime.UtcNow)
                    .SumAsync(r => r.Quantity, ct);
                return ReservationMath.Available(stock, reserved);
            }
            finally { scope.Dispose(); }
        }

        public async Task<int> GetAvailableColorStockAsync(Guid colorId, CancellationToken ct = default)
        {
            var scope = sp.CreateScope();
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
                var stock = await db.ProductColors.AsNoTracking().Where(c => c.Id == colorId).Select(c => (int?)c.Stock).FirstOrDefaultAsync(ct) ?? 0;
                var reserved = await db.StockReservations.AsNoTracking()
                    .Where(r => r.ColorId == colorId && r.Status == StockReservationStatus.Active && r.ExpiresAtUtc > DateTime.UtcNow)
                    .SumAsync(r => r.Quantity, ct);
                return ReservationMath.Available(stock, reserved);
            }
            finally { scope.Dispose(); }
        }

        public async Task RestockAsync(Guid productId, Guid? variantId, Guid? colorId, int quantity, CancellationToken ct = default)
        {
            if (quantity <= 0) return;
            var scope = sp.CreateScope();
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
                var product = await db.Products.FirstOrDefaultAsync(p => p.Id == productId, ct);
                if (product is null) return;
                product.Stock += quantity;
                product.ModifiedAtUtc = DateTime.UtcNow;
                if (variantId.HasValue)
                {
                    var variant = await db.ProductVariants.FirstOrDefaultAsync(v => v.Id == variantId.Value, ct);
                    if (variant is not null) variant.Stock += quantity;
                }
                if (colorId.HasValue)
                {
                    var color = await db.ProductColors.FirstOrDefaultAsync(c => c.Id == colorId.Value, ct);
                    if (color is not null) color.Stock += quantity;
                }
                await db.SaveChangesAsync(ct);
            }
            finally { scope.Dispose(); }
        }

        public async Task<(decimal RatingAvg, int RatingCount)> GetVendorReviewStatsAsync(Guid vendorId, CancellationToken ct = default)
        {
            var scope = sp.CreateScope();
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
                var productIds = await db.Products.AsNoTracking().Where(p => p.VendorId == vendorId).Select(p => p.Id).ToListAsync(ct);
                var ratings = await db.Reviews.AsNoTracking()
                    .Where(r => productIds.Contains(r.ProductId) && r.Status == ReviewStatus.Approved)
                    .Select(r => (decimal)r.Rating)
                    .ToListAsync(ct);
                return ratings.Count == 0 ? (0m, 0) : (Math.Round(ratings.Average(), 2, MidpointRounding.AwayFromZero), ratings.Count);
            }
            finally { scope.Dispose(); }
        }

        public async Task<(int Products, int LowStock)> GetVendorProductStatsAsync(Guid vendorId, CancellationToken ct = default)
        {
            var scope = sp.CreateScope();
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
                var products = await db.Products.AsNoTracking().Where(p => p.VendorId == vendorId && p.Status == ProductStatus.Active).ToListAsync(ct);
                var variantStocks = await db.ProductVariants.AsNoTracking()
                    .Where(v => products.Select(p => p.Id).Contains(v.ProductId))
                    .GroupBy(v => v.ProductId).Select(g => new { g.Key, Stock = g.Sum(x => x.Stock) }).ToListAsync(ct);
                var lowStock = products.Count(p =>
                {
                    var total = variantStocks.FirstOrDefault(v => v.Key == p.Id)?.Stock ?? p.Stock;
                    return total <= 5;
                });
                return (products.Count, lowStock);
            }
            finally { scope.Dispose(); }
        }
    }
}

/// <summary>HTTP endpoints for the Catalog module.</summary>
public static class CatalogEndpoints
{
    public static void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/catalog").WithTags("Catalog");
        group.MapGet("/home", async (IMediator m) => (await m.Send(new HomeQuery())).ToHttp()).AllowAnonymous();
        group.MapGet("/products", async (IMediator m, string? q, Guid? cat, Guid? vendorId, bool platform, decimal? min, decimal? max, decimal? rating, string sort, int page, int pageSize) =>
            (await m.Send(new ListProductsQuery(q, cat, vendorId, platform, min, max, rating, sort ?? "featured", page <= 0 ? 1 : page, pageSize <= 0 ? 12 : Math.Min(pageSize, 48)))).ToHttp()).AllowAnonymous();
        group.MapGet("/search/suggest", async (IMediator m, string? q) => (await m.Send(new SuggestQuery(q))).ToHttp()).AllowAnonymous();
        group.MapGet("/products/{slug}", async (IMediator m, string slug) => (await m.Send(new GetProductBySlugQuery(slug))).ToHttp()).AllowAnonymous();
        group.MapGet("/products/{id}/reviews", async (IMediator m, Guid id, int page, int pageSize) =>
            (await m.Send(new ListProductReviewsQuery(id, page <= 0 ? 1 : page, pageSize <= 0 ? 10 : Math.Min(pageSize, 50)))).ToHttp()).AllowAnonymous();
        group.MapGet("/categories", async (IMediator m) => (await m.Send(new ListCategoriesQuery())).ToHttp()).AllowAnonymous();
        group.MapPost("/products", async (IMediator m, CreateProductRequest req, ICurrentUser user) =>
        {
            if (!user.IsAuthenticated) return Results.Unauthorized();
            var isPlatform = user.IsInRole("Admin");
            var result = await m.Send(new CreateProductCommand(isPlatform ? null : user.VendorId ?? user.UserId, req.CategoryId, req.Title, req.Description,
                req.PriceETB, req.CompareAtPriceETB, req.Stock, req.WeightKg, isPlatform,
                req.Images, req.Colors));
            return result.Created($"/api/catalog/products/{result.Value?.Slug}");
        }).RequireAuthorization("VendorOrAdmin");
        group.MapPost("/reviews", async (IMediator m, CreateReviewRequest req, ICurrentUser user) =>
        {
            if (!user.IsAuthenticated) return Results.Unauthorized();
            return (await m.Send(new CreateReviewCommand(user.UserId!.Value, req.ProductId, req.OrderId, req.Rating, req.Title, req.Body, req.ImageUrls))).ToHttp();
        }).RequireAuthorization();

        // wishlist lives under the sales route family (buyer storefront context) but is owned by Catalog
        var wishlist = app.MapGroup("/api/sales/wishlist").WithTags("Sales").RequireAuthorization();
        wishlist.MapGet("/", async (IMediator m, ICurrentUser user) => (await m.Send(new ListWishlistQuery(user.UserId!.Value))).ToHttp());
        wishlist.MapPost("/{productId}", async (IMediator m, Guid productId, ICurrentUser user) =>
            (await m.Send(new ToggleWishlistCommand(user.UserId!.Value, productId))).ToHttp());

        var admin = app.MapGroup("/api/admin").WithTags("Admin").RequireAuthorization("Admin");
        admin.MapPost("/catalog/products/{id}/moderate", async (IMediator m, Guid id, ModerateRequest req, ICurrentUser user) =>
            (await m.Send(new ModerateProductCommand(user.UserId!.Value, id, req.Approve, req.Note))).ToHttp());
        admin.MapGet("/reviews", async (IMediator m, string? status) => (await m.Send(new ListReviewsForModerationQuery(status))).ToHttp());
        admin.MapPost("/reviews/{id}/moderate", async (IMediator m, Guid id, ModerateRequest req, ICurrentUser user) =>
            (await m.Send(new ModerateReviewCommand(user.UserId!.Value, id, req.Approve, req.Note))).ToHttp());
        admin.MapGet("/banners", async (IMediator m) => (await m.Send(new ListBannersQuery())).ToHttp());
        admin.MapPost("/banners", async (IMediator m, BannerRequest req) =>
            (await m.Send(new UpsertBannerCommand(null, req.Title, req.ImageUrl, req.Link, req.Position, req.IsActive, req.StartsAtUtc, req.EndsAtUtc))).ToHttp());
        admin.MapPatch("/banners/{id}", async (IMediator m, Guid id, BannerPatchRequest req) =>
            (await m.Send(new PatchBannerCommand(id, req.Title, req.ImageUrl, req.Link, req.Position, req.IsActive, req.StartsAtUtc, req.EndsAtUtc))).ToHttp());
        admin.MapDelete("/banners/{id}", async (IMediator m, Guid id) => (await m.Send(new DeleteBannerCommand(id))).ToHttp());
        admin.MapPost("/flash-sale", async (IMediator m, FlashSaleRequest req) =>
            (await m.Send(new SetFlashSaleCommand(req.ProductIds, req.DiscountPct, req.EndsAt))).ToHttp());
        admin.MapDelete("/flash-sale", async (IMediator m) => (await m.Send(new EndFlashSaleCommand())).ToHttp());
        admin.MapGet("/coupons", async (IMediator m) => (await m.Send(new ListCouponsQuery())).ToHttp());
        admin.MapPost("/coupons", async (IMediator m, CouponRequest req) =>
            (await m.Send(new UpsertCouponCommand(null, req.Code, req.Description, req.Type, req.Value, req.MinSubtotal, req.MaxDiscount, req.MaxUses, req.StartsAtUtc, req.ExpiresAtUtc, req.IsActive))).ToHttp());
        admin.MapPatch("/coupons/{id}", async (IMediator m, Guid id, CouponPatchRequest req) =>
            (await m.Send(new PatchCouponCommand(id, req.IsActive))).ToHttp());
        // manual trigger for the ReservationSweeper (Ops tab parity with the Quartz job)
        admin.MapPost("/jobs/stock-sweeper", async (IStockReservations reservations) =>
            Results.Ok(new { ok = true, released = await reservations.ExpireStaleAsync(CancellationToken.None) }));

        var vendor = app.MapGroup("/api/vendor").WithTags("Vendor").RequireAuthorization("Vendor");
        vendor.MapPatch("/variants/{id}", async (IMediator m, Guid id, PatchVariantRequest req, ICurrentUser user) =>
            (await m.Send(new PatchVariantCommand(user.UserId!.Value, id, req.PriceETB, req.Stock, req.IsActive))).ToHttp());
        vendor.MapPost("/products/bulk", async (IMediator m, BulkUploadRequest req, ICurrentUser user, IMarketplaceVendorChecker vendorChecker) =>
            (await m.Send(new BulkUploadCommand(user.UserId!.Value, await vendorChecker.GetBusinessNameAsync(user.UserId.Value) ?? "Vendor", req.Csv))).ToHttp());
        vendor.MapPost("/reviews/{id}/reply", async (IMediator m, Guid id, VendorReplyRequest req, ICurrentUser user) =>
            (await m.Send(new VendorReplyToReviewCommand(user.UserId!.Value, id, req.Reply))).ToHttp());

        // Product media streaming: storage keys resolve here; external https URLs never hit this endpoint.
        app.MapGet("/api/media/{*key}", async (string key, MarketHub.Shared.Infrastructure.IFileStorage storage) =>
        {
            if (key.Contains("..") || key.Contains('/') || key.Contains('\\')) return Results.BadRequest();
            try
            {
                var bytes = await storage.ReadAsync(key);
                var ext = Path.GetExtension(key).ToLowerInvariant();
                var contentType = ext switch
                {
                    ".png" => "image/png",
                    ".webp" => "image/webp",
                    ".gif" => "image/gif",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    _ => "application/octet-stream",
                };
                return Results.File(bytes, contentType);
            }
            catch (FileNotFoundException) { return Results.NotFound(); }
        }).AllowAnonymous();
    }

    public sealed record CreateProductRequest(Guid CategoryId, string Title, string Description, decimal PriceETB,
        decimal? CompareAtPriceETB, int Stock, decimal WeightKg = 1m,
        IReadOnlyList<MarketHub.Modules.Catalog.Features.ProductImageInput>? Images = null,
        IReadOnlyList<MarketHub.Modules.Catalog.Features.ProductColorInput>? Colors = null);
    public sealed record ModerateRequest(bool Approve, string? Note);
    public sealed record CreateReviewRequest(Guid ProductId, Guid OrderId, int Rating, string? Title, string Body, IReadOnlyList<string>? ImageUrls);
    public sealed record BannerRequest(string Title, string ImageUrl, string? Link, string Position, bool IsActive, DateTime? StartsAtUtc, DateTime? EndsAtUtc);
    public sealed record BannerPatchRequest(string? Title, string? ImageUrl, string? Link, string? Position, bool? IsActive, DateTime? StartsAtUtc, DateTime? EndsAtUtc);
    public sealed record FlashSaleRequest(IReadOnlyList<Guid> ProductIds, int DiscountPct, DateTime EndsAt);
    public sealed record CouponRequest(string Code, string? Description, string Type, decimal Value, decimal MinSubtotal,
        decimal? MaxDiscount, int MaxUses, DateTime StartsAtUtc, DateTime ExpiresAtUtc, bool IsActive);
    public sealed record CouponPatchRequest(bool? IsActive);
    public sealed record PatchVariantRequest(decimal? PriceETB, int? Stock, bool? IsActive);
    public sealed record BulkUploadRequest(string Csv);
    public sealed record VendorReplyRequest(string Reply);
}

/// <summary>Idempotent catalog seeder: categories + representative first-party/vendor products in ETB.</summary>
public sealed class CatalogSeeder : IModuleSeeder
{
    public string ModuleName => "Catalog";

    private static readonly (Guid Id, string Name, string Slug)[] Categories =
    {
        new(Guid.Parse("00000000-0000-0000-0000-00000000a001"), "Electronics", "electronics"),
        new(Guid.Parse("00000000-0000-0000-0000-00000000a002"), "Fashion", "fashion"),
        new(Guid.Parse("00000000-0000-0000-0000-00000000a003"), "Home & Kitchen", "home-kitchen"),
        new(Guid.Parse("00000000-0000-0000-0000-00000000a004"), "Beauty", "beauty"),
        new(Guid.Parse("00000000-0000-0000-0000-00000000a005"), "Groceries", "groceries"),
        new(Guid.Parse("00000000-0000-0000-0000-00000000a006"), "Baby & Toys", "baby-toys"),
    };

    public async Task SeedAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<CatalogDbContext>();
        if (await db.Categories.AnyAsync(ct)) return;

        for (var i = 0; i < Categories.Length; i++)
            db.Categories.Add(new Category { Id = Categories[i].Id, Name = Categories[i].Name, Slug = Categories[i].Slug, SortOrder = i });

        Product P(Guid vendor, string? vendorName, Guid cat, string title, string desc, decimal price, decimal? cmp, int stock, decimal w, bool feat, decimal rating, int rc) =>
            new() { VendorId = vendor == Guid.Empty ? null : vendor, VendorNameSnapshot = vendorName, CategoryId = cat, Title = title, Slug = title.ToLowerInvariant().Replace(" ", "-").Replace("'", "") + "-" + Guid.NewGuid().ToString("N")[..6], Description = desc, PriceETB = price, CompareAtPriceETB = cmp, Stock = stock, WeightKg = w, Status = ProductStatus.Active, IsFeatured = feat, RatingAvg = rating, RatingCount = rc, SKU = $"MH-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}" };

        var v1 = SeedIds.Vendor1UserId;
        var v2 = SeedIds.Vendor2UserId;
        var electronics = Categories[0].Id; var fashion = Categories[1].Id; var home = Categories[2].Id;
        var beauty = Categories[3].Id; var groceries = Categories[4].Id; var baby = Categories[5].Id;

        db.Products.AddRange(
            P(Guid.Empty, null, electronics, "Samsung Galaxy A15 128GB", "Brand-new Samsung Galaxy A15 with 128GB storage, 6.5\" Super AMOLED display and 5000mAh battery. 1-year official warranty.", 24999.00m, 27999.00m, 35, 0.4m, true, 4.8m, 214),
            P(Guid.Empty, null, electronics, "Huawei Y9 Prime (Refurbished)", "Certified refurbished Huawei Y9 Prime, 128GB, pop-up camera. Fully tested with 6-month warranty.", 8999.00m, 10500.00m, 18, 0.4m, false, 4.3m, 87),
            P(v1, "Sheba Electronics Trading PLC", electronics, "BassPro Bluetooth Speaker", "Portable 20W Bluetooth speaker with deep bass, 12-hour battery and IPX6 water resistance.", 3200.00m, 3800.00m, 42, 1.2m, false, 4.6m, 156),
            P(v1, "Sheba Electronics Trading PLC", electronics, "Fast Charger 33W USB-C", "Original 33W SuperCharge USB-C wall charger with cable. Compatible with all major Android phones.", 899.00m, null, 120, 0.2m, false, 4.5m, 302),
            P(v1, "Sheba Electronics Trading PLC", electronics, "Wireless Earbuds Pro", "Active noise-cancelling TWS earbuds with 28h total playtime and transparent case.", 2450.00m, 2900.00m, 64, 0.3m, true, 4.4m, 198),
            P(Guid.Empty, null, fashion, "Men's Habesha Kemis Cotton Shirt", "Hand-woven 100% cotton shirt with traditional tibeb embroidery. Made in Addis Ababa.", 1850.00m, null, 50, 0.3m, false, 4.7m, 64),
            P(Guid.Empty, null, fashion, "Habesha Kemis Traditional Dress", "Elegant handwoven habesha kemis with full tibeb design, ideal for holidays and weddings.", 4500.00m, 5200.00m, 22, 0.8m, false, 4.9m, 41),
            P(Guid.Empty, null, home, "Non-Stick Injera Mitad Pan", "56cm electric non-stick mitad for perfect injera at home. 1500W with temperature control.", 1250.00m, null, 30, 2.5m, false, 4.5m, 118),
            P(v2, "Habesha Home & Kitchen", home, "Traditional Clay Coffee Set (Jebena)", "Handcrafted clay jebena with six sini cups — the complete traditional coffee ceremony set.", 2100.00m, null, 25, 1.5m, true, 4.8m, 93),
            P(v2, "Habesha Home & Kitchen", home, "Handwoven Mesob Basket", "Colorful handwoven mesob table basket from Harar artisans. Diameter 55cm.", 1750.00m, null, 15, 2.2m, false, 4.6m, 57),
            P(v2, "Habesha Home & Kitchen", groceries, "Ethiopian Coffee Beans Yirgacheffe 1kg", "Single-origin washed Yirgacheffe arabica, medium roast, notes of citrus and jasmine.", 1150.00m, 1350.00m, 80, 1.0m, true, 4.9m, 340),
            P(Guid.Empty, null, groceries, "Kolo Grains Mix 5kg", "Roasted barley, chickpea and sunflower kolo mix. Freshly packed 5kg family size.", 640.00m, null, 100, 5.0m, false, 4.4m, 76),
            P(Guid.Empty, null, beauty, "Shea Butter & Beeswax Balm 250g", "Natural shea butter and beeswax balm for skin and hair. No artificial additives.", 480.00m, null, 90, 0.3m, false, 4.6m, 122),
            P(Guid.Empty, null, baby, "Amharic Alphabet Wooden Blocks", "30-piece wooden block set with Amharic fidel letters. Safe water-based paint.", 780.00m, null, 45, 1.0m, false, 4.7m, 35));

        await db.SaveChangesAsync(ct);
        await SeedGalleriesAsync(db, ct);
    }

    /// <summary>Attaches demo photos (web-sourced CDN shots) and color variants to the seeded products.</summary>
    private static async Task SeedGalleriesAsync(CatalogDbContext db, CancellationToken ct)
    {
        const string cdn = "https://z-cdn.chatglm.cn/image-search-mcp/images-ppt";
        var products = await db.Products.Include(p => p.Colors).ToDictionaryAsync(p => p.Title, p => p, ct);

        static ProductImage Img(Guid productId, Guid? colorId, string url, string alt, int sort) =>
            new() { ProductId = productId, ColorId = colorId, StorageKey = url, Alt = alt, SortOrder = sort };

        void AddGallery(string title, (string url, string alt)[] shots, (string name, string hex, int stock, string[] urls)[]? colors = null)
        {
            if (!products.TryGetValue(title, out var p) || p.Images.Count > 0) return;
            var sort = 0;
            var colorList = new List<ProductColor>();
            if (colors is not null)
            {
                foreach (var (name, hex, stock, urls) in colors)
                {
                    var color = new ProductColor { ProductId = p.Id, Name = name, Hex = hex, Stock = stock, ExtraPriceETB = 0m, SortOrder = colorList.Count };
                    db.ProductColors.Add(color);
                    colorList.Add(color);
                    foreach (var url in urls)
                    {
                        db.ProductImages.Add(Img(p.Id, null, url, name, sort++)); // colorId wired after SaveChanges below
                    }
                }
            }
            foreach (var (url, alt) in shots)
                db.ProductImages.Add(Img(p.Id, null, url, alt, sort++));
        }

        AddGallery("Samsung Galaxy A15 128GB", Array.Empty<(string, string)>(),
            colors: new[]
            {
                ("Blue Black", "#1b1f24", 20, new[] { $"{cdn}/2acd03551ec2.jpg", $"{cdn}/da09b50eab57.jpg", $"{cdn}/18886654c865.jpg", $"{cdn}/41c57ee5ee0f.jpg" }),
                ("Light Blue", "#7db4d8", 15, new[] { $"{cdn}/97adfd30a83d.jpg", $"{cdn}/3445c601f702.jpg" }),
            });
        AddGallery("Huawei Y9 Prime (Refurbished)", new[] { ($"{cdn}/9694e7bd0968.jpg", ""), ($"{cdn}/c699d1db8880.jpg", ""), ($"{cdn}/866c90b2474a.jpg", ""), ($"{cdn}/3b79c9dc867a.jpg", "") });
        AddGallery("BassPro Bluetooth Speaker", Array.Empty<(string, string)>(),
            colors: new[]
            {
                ("Black", "#17181c", 25, new[] { $"{cdn}/6030cc0ead6f.jpg", $"{cdn}/fa2fad2c8f40.jpg", $"{cdn}/c076ab2f218c.png", $"{cdn}/e03679599543.png" }),
                ("Blue", "#1e40af", 17, new[] { $"{cdn}/6d775a588f96.jpg", $"{cdn}/649c89c3e2fd.jpg", $"{cdn}/82ffbcb78e0e.jpg" }),
            });
        AddGallery("Fast Charger 33W USB-C", new[] { ($"{cdn}/3616b2add597.jpg", ""), ($"{cdn}/fe0ecb3d7b9a.png", ""), ($"{cdn}/a6df23037cf2.jpg", ""), ($"{cdn}/89d7a5c09b78.jpeg", "") });
        AddGallery("Wireless Earbuds Pro", Array.Empty<(string, string)>(),
            colors: new[]
            {
                ("Black", "#141414", 40, new[] { $"{cdn}/f025da1193b3.jpg", $"{cdn}/991114c12d1c.jpg", $"{cdn}/525f0adfac3f.jpg", $"{cdn}/897f94d1711a.jpg" }),
                ("White", "#f4f4f5", 24, new[] { $"{cdn}/39f75b8ca8fc.jpg", $"{cdn}/a6c915ca1892.jpg", $"{cdn}/28afe80ee59a.jpg" }),
            });
        AddGallery("Men's Habesha Kemis Cotton Shirt", Array.Empty<(string, string)>(),
            colors: new[]
            {
                ("White", "#fafaf7", 30, new[] { $"{cdn}/9ed2a9e730e4.jpg", $"{cdn}/7739ad90f339.jpg", $"{cdn}/5263a7e240fa.jpg", $"{cdn}/99a930a1857b.jpg" }),
                ("Beige", "#d9c7a7", 20, new[] { $"{cdn}/54f6ec9e522c.jpg", $"{cdn}/3c3f9e1ebe56.jpg", $"{cdn}/76982ccc164b.jpg" }),
            });
        AddGallery("Habesha Kemis Traditional Dress", new[] { ($"{cdn}/77545225c386.jpg", ""), ($"{cdn}/a997b68d9d42.jpg", ""), ($"{cdn}/a590f8e3e448.jpg", ""), ($"{cdn}/ca7d9d92edbd.jpg", ""), ($"{cdn}/f06c758d49a9.jpg", "") });
        AddGallery("Non-Stick Injera Mitad Pan", new[] { ($"{cdn}/871e24a7c710.jpg", ""), ($"{cdn}/5e08ad724873.jpg", ""), ($"{cdn}/4e8150a060e0.jpg", ""), ($"{cdn}/516de17a593c.jpg", "") });
        AddGallery("Traditional Clay Coffee Set (Jebena)", new[] { ($"{cdn}/0ad117452313.png", ""), ($"{cdn}/644fa9f386b0.jpg", ""), ($"{cdn}/fe6e2700e06e.png", ""), ($"{cdn}/d8094f782b02.jpg", ""), ($"{cdn}/af5bfbe8807e.jpg", "") });
        AddGallery("Handwoven Mesob Basket", new[] { ($"{cdn}/9a13ad22ed7d.png", ""), ($"{cdn}/41f5a8498fa6.png", ""), ($"{cdn}/febeb8c2701b.jpg", ""), ($"{cdn}/7242e5703c21.jpg", "") });
        AddGallery("Ethiopian Coffee Beans Yirgacheffe 1kg", new[] { ($"{cdn}/df692c321057.webp", ""), ($"{cdn}/636c8bdcbbef.jpg", ""), ($"{cdn}/342feb6bbb94.png", ""), ($"{cdn}/dc0f06d34feb.jpg", "") });
        AddGallery("Kolo Grains Mix 5kg", new[] { ($"{cdn}/770ab1b2d9a8.jpg", ""), ($"{cdn}/cbfc9f15db2a.jpg", ""), ($"{cdn}/1a911f706916.jpg", ""), ($"{cdn}/ff01430c104d.jpg", "") });
        AddGallery("Shea Butter & Beeswax Balm 250g", new[] { ($"{cdn}/ad0ad55ae7bb.png", ""), ($"{cdn}/2639a82a56f6.jpg", ""), ($"{cdn}/be6dfa0ea485.jpg", ""), ($"{cdn}/a108daf85fe5.jpg", "") });
        AddGallery("Amharic Alphabet Wooden Blocks", new[] { ($"{cdn}/63d113d1f3ba.jpg", ""), ($"{cdn}/a70c5d7654e9.png", ""), ($"{cdn}/99f33e3e1093.jpeg", ""), ($"{cdn}/bb9037079a00.jpg", "") });

        await db.SaveChangesAsync(ct);

        // Second pass: link color-tagged photos (alt == color name) to their just-created color rows.
        var allColors = await db.ProductColors.ToListAsync(ct);
        foreach (var img in await db.ProductImages.Where(i => i.ColorId == null && i.Alt != "").ToListAsync(ct))
        {
            var color = allColors.FirstOrDefault(c => c.ProductId == img.ProductId && c.Name == img.Alt);
            if (color is not null) img.ColorId = color.Id;
        }
        await db.SaveChangesAsync(ct);
    }
}
