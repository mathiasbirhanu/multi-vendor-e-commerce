using MarketHub.Shared.Infrastructure;
using MarketHub.Modules.Identity;
using MarketHub.Modules.Catalog.Domain;
using MarketHub.Modules.Catalog.Features;
using MarketHub.Modules.Catalog.Persistence;
using MarketHub.Modules.Sales.Domain;
using MarketHub.Modules.Sales.Persistence;
using MarketHub.Modules.Notifications.Domain;
using MarketHub.Modules.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketHub.Api;

/// <summary>Development startup migration + idempotent seeding across all module contexts.</summary>
public static class DatabaseSeeder
{
    public static async Task MigrateAndSeedAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILogger<MarketHub.Modules.Identity.IdentityModule>>();

        await sp.GetRequiredService<OutboxDbContext>().Database.MigrateAsync();
        foreach (var seeder in sp.GetServices<IModuleSeeder>())
        {
            await seeder.SeedAsync(sp, CancellationToken.None);
            logger.LogInformation("Seeded module {Module}", seeder.ModuleName);
        }

        // gap-fill v2 demo data — runs AFTER the module seeders (needs their products/users/orders)
        await new GapFillSeeder().SeedAsync(sp, CancellationToken.None);
        logger.LogInformation("Seeded module {Module}", "GapFill v2");
    }
}

/// <summary>
/// Gap-fill v2 demo data (idempotent): marketing banners, an active flash sale, launch coupons,
/// approved product reviews (some with vendor replies + recomputed product ratings), size variants,
/// the demo buyer's address book entry and one sample outbound message for the admin Ops view.
/// </summary>
public sealed class GapFillSeeder
{
    public string ModuleName => "GapFill v2";

    private const string Cdn = "https://z-cdn.chatglm.cn/image-search-mcp/images-ppt";

    public async Task SeedAsync(IServiceProvider sp, CancellationToken ct)
    {
        var catalog = sp.GetRequiredService<CatalogDbContext>();
        var sales = sp.GetRequiredService<SalesDbContext>();
        var notifications = sp.GetRequiredService<NotificationsDbContext>();

        await SeedBannersAsync(catalog, ct);
        await SeedVariantsAsync(catalog, ct);
        await SeedFlashSaleAsync(catalog, ct);
        await SeedCouponsAsync(catalog, ct);
        await SeedReviewsAsync(sp, catalog, ct);
        await SeedBuyerAddressAsync(sales, ct);
        await SeedOutboundSampleAsync(notifications, ct);
    }

    // ---- 2 marketing banners (hero carousel + promo strip) ----
    private static async Task SeedBannersAsync(CatalogDbContext db, CancellationToken ct)
    {
        if (await db.Banners.AnyAsync(ct)) return;
        db.Banners.AddRange(
            new Banner
            {
                Title = "Timket Sale — up to 40% off electronics",
                ImageUrl = $"{Cdn}/2acd03551ec2.jpg",
                Link = "/catalog?sort=sold",
                Position = "hero",
                IsActive = true,
            },
            new Banner
            {
                Title = "Free delivery in Addis Ababa over 1000 Br",
                ImageUrl = $"{Cdn}/df692c321057.webp",
                Link = "/catalog/electronics",
                Position = "strip",
                IsActive = true,
            });
        await db.SaveChangesAsync(ct);
    }

    // ---- size variants on products without color variants (Product.stock resynced to Σ variant stock) ----
    private static async Task SeedVariantsAsync(CatalogDbContext db, CancellationToken ct)
    {
        if (await db.ProductVariants.AnyAsync(ct)) return;

        var byTitle = await db.Products.ToDictionaryAsync(p => p.Title, p => p, ct);

        void AddVariants(string productTitle, params (string Size, int Stock)[] sizes)
        {
            if (!byTitle.TryGetValue(productTitle, out var p) || p is null) return;
            var sort = 0;
            foreach (var (size, stock) in sizes)
            {
                db.ProductVariants.Add(new ProductVariant
                {
                    ProductId = p.Id,
                    SKU = $"MHV-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
                    ColorId = null,
                    Size = size,
                    PriceETB = null, // default variants sell at the base price
                    Stock = stock,
                    IsActive = true,
                    SortOrder = sort++,
                });
            }
            p.Stock = sizes.Sum(s => s.Stock); // product total stays the denormalized Σ variant stock
            p.ModifiedAtUtc = DateTime.UtcNow;
        }

        AddVariants("Habesha Kemis Traditional Dress", ("S", 6), ("M", 10), ("L", 6));
        AddVariants("Kolo Grains Mix 5kg", ("1kg", 60), ("5kg", 40));
        AddVariants("Non-Stick Injera Mitad Pan", ("50cm", 12), ("56cm", 18));
        await db.SaveChangesAsync(ct);
    }

    // ---- active flash sale on 3 products (bounds + price math from FlashSaleCalculator) ----
    private static async Task SeedFlashSaleAsync(CatalogDbContext db, CancellationToken ct)
    {
        var hasActiveSale = await db.Products.AnyAsync(p => p.SalePriceETB != null && p.SaleEndsAtUtc != null && p.SaleEndsAtUtc > DateTime.UtcNow, ct);
        if (hasActiveSale) return;

        var titles = new[]
        {
            (Title: "Samsung Galaxy A15 128GB", Pct: 10),
            (Title: "Wireless Earbuds Pro", Pct: 20),
            (Title: "Ethiopian Coffee Beans Yirgacheffe 1kg", Pct: 15),
        };
        var endsAt = DateTime.UtcNow.AddDays(3);
        foreach (var (title, pct) in titles)
        {
            var p = await db.Products.FirstOrDefaultAsync(x => x.Title == title && x.Status == ProductStatus.Active, ct);
            if (p is null) continue;
            p.SalePriceETB = FlashSaleCalculator.ComputeSalePrice(p.PriceETB, pct);
            p.SaleEndsAtUtc = endsAt;
            p.ModifiedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    // ---- launch coupons: WELCOME10 (10% capped at 300 Br) + SAVE200 (fixed 200 Br) ----
    private static async Task SeedCouponsAsync(CatalogDbContext db, CancellationToken ct)
    {
        if (await db.Coupons.AnyAsync(ct)) return;
        db.Coupons.AddRange(
            new Coupon
            {
                Code = "WELCOME10",
                Description = "Welcome offer — 10% off your order (max 300 Br)",
                Type = CouponType.Percent,
                Value = 10m,
                MinSubtotal = 0m,
                MaxDiscount = 300m,
                MaxUses = 1000,
                StartsAtUtc = DateTime.UtcNow.AddDays(-1),
                ExpiresAtUtc = DateTime.UtcNow.AddDays(90),
                IsActive = true,
            },
            new Coupon
            {
                Code = "SAVE200",
                Description = "Flat 200 Br off orders above 1500 Br",
                Type = CouponType.Fixed,
                Value = 200m,
                MinSubtotal = 1500m,
                MaxDiscount = null,
                MaxUses = 500,
                StartsAtUtc = DateTime.UtcNow.AddDays(-1),
                ExpiresAtUtc = DateTime.UtcNow.AddDays(60),
                IsActive = true,
            });
        await db.SaveChangesAsync(ct);
    }

    // ---- ~14 approved reviews (some with vendor replies) + recomputed product ratings ----
    private static async Task SeedReviewsAsync(IServiceProvider sp, CatalogDbContext db, CancellationToken ct)
    {
        if (await db.Reviews.AnyAsync(ct)) return;

        var buyer = MarketHub.Shared.SeedData.SeedIds.BuyerUserId;
        var support = MarketHub.Shared.SeedData.SeedIds.SupportAdminUserId;
        var vendor1 = MarketHub.Shared.SeedData.SeedIds.Vendor1UserId;
        var products = await db.Products.AsNoTracking().ToDictionaryAsync(p => p.Title, p => p.Id, ct);

        Guid P(string title) => products.TryGetValue(title, out var id) ? id : Guid.Empty;

        var now = DateTime.UtcNow;
        var seeds = new (string Product, Guid User, int Rating, string Title, string Body, string? Reply)[]
        {
            ("Samsung Galaxy A15 128GB", buyer, 5, "Excellent value", "Battery lasts almost two days and the AMOLED screen is superb for the price. Delivery to Bole took one day.", "Thank you Abebe! Enjoy your new phone — MarketHub Store"),
            ("Samsung Galaxy A15 128GB", support, 4, "Great phone, minor niggle", "Camera is solid in daylight, a bit soft at night. Charging with the bundled 33W brick is quick.", null),
            ("Wireless Earbuds Pro", buyer, 5, "ANC punches way above the price", "Noise cancelling on the minibus is genuinely useful and the case is pocketable. Pairing is instant.", "We appreciate the detailed feedback — firmware update adds multipoint next month!"),
            ("BassPro Bluetooth Speaker", buyer, 4, "Loud and sturdy", "Filled the whole living room at 60% volume. A little heavy for hiking but perfect for home.", null),
            ("Ethiopian Coffee Beans Yirgacheffe 1kg", buyer, 5, "Proper Yirgacheffe", "Bright citrus and floral notes, roasted perfectly medium. Will reorder every month.", "Thanks! New harvest arrives next week — see you soon."),
            ("Ethiopian Coffee Beans Yirgacheffe 1kg", support, 5, "Gift-worthy", "Bought for my father in Bahir Dar; the beans arrived fresh with the roast date printed. He approved!", null),
            ("Traditional Clay Coffee Set (Jebena)", buyer, 5, "Beautiful craftsmanship", "The jebena and six sini cups arrived double-boxed with zero chips. Coffee tastes better from clay, honestly.", "Handmade with care by our Harar artisans — thank you for the lovely words."),
            ("Habesha Kemis Traditional Dress", buyer, 5, "Perfect for Meskel", "The tibeb patterns are exquisite and the cotton is soft. Ordered M, fits exactly as described.", null),
            ("Men's Habesha Kemis Cotton Shirt", buyer, 4, "Comfortable everyday shirt", "Weaving quality is great and it breathes well in the afternoon heat. Slightly long sleeves for me.", "Thanks for the note — size L runs 1cm longer, we now note this on the listing."),
            ("Non-Stick Injera Mitad Pan", buyer, 5, "Injera like in the countryside", "Even heating, easy cleanup, and the temperature dial is simple. First injera came out perfect.", null),
            ("Handwoven Mesob Basket", support, 4, "Striking table piece", "Colors are vivid and the weave is tight. It is slightly smaller than I imagined but well made.", null),
            ("Amharic Alphabet Wooden Blocks", buyer, 5, "My daughter loves them", "Smooth edges, non-toxic paint and she is already recognising fidel. Educational and fun.", null),
            ("Shea Butter & Beeswax Balm 250g", buyer, 4, "Great for dry season", "Absorbs well without feeling greasy and the smell is mild and natural. The tub is generous.", "Thank you — bergamot variant arrives next month."),
            ("Fast Charger 33W USB-C", buyer, 5, "Charges to 50% in 25 minutes", "Genuine article, cable included, and it does not get warm. Exactly as advertised.", null),
        };

        var orderSeed = 0;
        foreach (var (productTitle, user, rating, title, body, reply) in seeds)
        {
            var productId = P(productTitle);
            if (productId == Guid.Empty) continue;
            orderSeed++;
            db.Reviews.Add(new Review
            {
                ProductId = productId,
                UserId = user,
                OrderId = Guid.Parse($"00000000-0000-0000-0000-{orderSeed:D12}"), // synthetic verified-purchase order
                Rating = rating,
                Title = title,
                Body = body,
                Status = ReviewStatus.Approved,
                VendorReply = reply,
                VendorRepliedAtUtc = reply is null ? null : now.AddDays(-1),
                CreatedAtUtc = now.AddDays(-14 + orderSeed), // spread over two weeks
            });
        }
        await db.SaveChangesAsync(ct);

        // recomputed denormalized ratings from the approved rows (per reviewed product)
        foreach (var productId in await db.Reviews.AsNoTracking().Select(r => r.ProductId).Distinct().ToListAsync(ct))
            await ReviewAggregates.RecomputeProductRatingAsync(db, productId, ct);
        await db.SaveChangesAsync(ct); // persist ratings before mirroring vendor counters

        // mirror the approved-review count into the vendors' denormalized RatingCount
        var identity = sp.GetRequiredService<MarketHub.Modules.Identity.Persistence.IdentityDbContext>();
        foreach (var vendorId in new[] { vendor1, MarketHub.Shared.SeedData.SeedIds.Vendor2UserId })
        {
            var count = await db.Products.AsNoTracking().Where(p => p.VendorId == vendorId).SumAsync(p => p.RatingCount, ct);
            var vendor = await identity.VendorProfiles.FirstOrDefaultAsync(v => v.Id == vendorId, ct);
            if (vendor is not null)
            {
                vendor.RatingCount = count;
                vendor.ModifiedAtUtc = DateTime.UtcNow;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    // ---- demo buyer address book entry (Bole, Addis Ababa) ----
    private static async Task SeedBuyerAddressAsync(SalesDbContext db, CancellationToken ct)
    {
        var buyer = MarketHub.Shared.SeedData.SeedIds.BuyerUserId;
        if (await db.Addresses.AnyAsync(a => a.UserId == buyer, ct)) return;
        db.Addresses.Add(new Address
        {
            UserId = buyer,
            Label = "Home",
            FullName = "Abebe Kebede",
            Phone = "+251921000005",
            Region = "Addis Ababa",
            City = "Addis Ababa",
            SubCity = "Bole",
            Woreda = "03",
            Kebele = "08",
            Landmark = "Near Bole Medhane Alem Church",
            IsDefault = true,
        });
        await db.SaveChangesAsync(ct);
    }

    // ---- one sample outbound message so the admin Ops table is not empty on first boot ----
    private static async Task SeedOutboundSampleAsync(NotificationsDbContext db, CancellationToken ct)
    {
        if (await db.OutboundMessages.AnyAsync(ct)) return;
        db.OutboundMessages.Add(new OutboundMessage
        {
            UserId = MarketHub.Shared.SeedData.SeedIds.BuyerUserId,
            Channel = OutboundChannel.Sms,
            Template = NotificationTemplates.OrderConfirmed,
            Destination = "+251921000005",
            PayloadJson = """{"userId":"00000000-0000-0000-0000-000000000003","template":"OrderConfirmed","subject":"Payment confirmed"}""",
            Provider = "Mock",
            ProviderRef = $"MOCK-SMS-{Guid.NewGuid():N}"[..24],
            Status = OutboundStatus.Sent,
            SentAtUtc = DateTime.UtcNow.AddHours(-3),
        });
        await db.SaveChangesAsync(ct);
    }
}
