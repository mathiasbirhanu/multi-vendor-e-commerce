using System.Text.Json;
using FluentValidation;
using MarketHub.Shared;
using MarketHub.Shared.Contracts;
using MarketHub.Modules.Catalog.Domain;
using MarketHub.Modules.Catalog.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace MarketHub.Modules.Catalog.Features;

// ==================== PRODUCT CARD (shared shape) ====================
/// <summary>Card shape mirroring the Next.js storefront: { id, slug, title, priceETB, salePriceETB?, … }.</summary>
public sealed record ProductCardDto(Guid Id, string Slug, string Title, decimal PriceETB, decimal? SalePriceETB,
    decimal? CompareAtPriceETB, string? Thumb, decimal RatingAvg, int RatingCount, int SoldCount, string? VendorName,
    bool Platform, int Stock, IReadOnlyList<ProductCardColorDto>? Colors);
public sealed record ProductCardColorDto(string Name, string Hex);

/// <summary>Builds the shared product-card DTO from a tracked/untracked product aggregate.</summary>
public static class ProductCard
{
    public static ProductCardDto From(Product p) => new(
        p.Id, p.Slug, p.Title, p.PriceETB, p.SalePriceETB, p.CompareAtPriceETB,
        p.Images.OrderBy(i => i.SortOrder).Select(i => (string?)i.PublicUrl).FirstOrDefault(),
        p.RatingAvg, p.RatingCount, p.SoldCount, p.VendorNameSnapshot, p.VendorId == null, p.Stock,
        p.Colors.OrderBy(c => c.SortOrder).Select(c => new ProductCardColorDto(c.Name, c.Hex)).ToList());

    /// <summary>Projection variant that loads only the columns the card needs (list endpoints).</summary>
    public static async Task<IReadOnlyList<ProductCardDto>> FromQueryAsync(IQueryable<Product> query, int take, CancellationToken ct)
    {
        var rows = await query
            .OrderByDescending(p => p.IsFeatured).ThenByDescending(p => p.RatingAvg)
            .Take(take)
            .Select(p => new
            {
                p.Id, p.Slug, p.Title, p.PriceETB, p.SalePriceETB, p.CompareAtPriceETB, p.RatingAvg,
                p.RatingCount, p.SoldCount, p.VendorNameSnapshot, VendorId = (Guid?)p.VendorId, p.Stock,
                Thumb = p.Images.OrderBy(i => i.SortOrder).Select(i => i.StorageKey).FirstOrDefault(),
                Colors = p.Colors.OrderBy(c => c.SortOrder).Select(c => new { c.Name, c.Hex }).ToList(),
            })
            .ToListAsync(ct);
        return rows.Select(r => new ProductCardDto(r.Id, r.Slug, r.Title, r.PriceETB, r.SalePriceETB, r.CompareAtPriceETB,
            ResolveThumb(r.Thumb), r.RatingAvg, r.RatingCount, r.SoldCount, r.VendorNameSnapshot, r.VendorId == null, r.Stock,
            r.Colors.Select(c => new ProductCardColorDto(c.Name, c.Hex)).ToList())).ToList();
    }

    private static string? ResolveThumb(string? storageKey) => storageKey switch
    {
        null => null,
        var k when k.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || k.StartsWith("https://", StringComparison.OrdinalIgnoreCase) => k,
        _ => $"/api/media/{k}",
    };
}

// ==================== HOME ====================
public sealed record HomeQuery : IRequest<Result<HomeDto>>;
public sealed record BannerDto(Guid Id, string Title, string ImageUrl, string? Link, string Position, bool IsActive);
public sealed record FlashSaleDto(DateTime EndsAt, IReadOnlyList<ProductCardDto> Products);
public sealed record HomeDto(IReadOnlyList<BannerDto> Banners, FlashSaleDto? FlashSale,
    IReadOnlyList<ProductCardDto> Featured, IReadOnlyList<ProductCardDto> TopRated,
    IReadOnlyList<ProductCardDto> NewArrivals, IReadOnlyList<CategoryDto> Categories);

public sealed class HomeHandler(CatalogDbContext db) : IRequestHandler<HomeQuery, Result<HomeDto>>
{
    public async Task<Result<HomeDto>> Handle(HomeQuery q, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var active = db.Products.AsNoTracking().Where(p => p.Status == ProductStatus.Active);

        var banners = await db.Banners.AsNoTracking()
            .Where(b => b.IsActive
                && (b.StartsAtUtc == null || b.StartsAtUtc <= now)
                && (b.EndsAtUtc == null || b.EndsAtUtc > now))
            .OrderBy(b => b.CreatedAtUtc)
            .Select(b => new BannerDto(b.Id, b.Title, b.ImageUrl, b.Link, b.Position, b.IsActive))
            .ToListAsync(ct);

        FlashSaleDto? flashSale = null;
        var saleEnds = await active.Where(p => p.SalePriceETB != null && p.SaleEndsAtUtc != null && p.SaleEndsAtUtc > now)
            .OrderBy(p => p.SaleEndsAtUtc).Select(p => p.SaleEndsAtUtc!.Value).FirstOrDefaultAsync(ct);
        if (saleEnds != default)
        {
            var saleQuery = active.Where(p => p.SalePriceETB != null && p.SaleEndsAtUtc != null && p.SaleEndsAtUtc > now);
            flashSale = new FlashSaleDto(saleEnds, await ProductCard.FromQueryAsync(saleQuery, 8, ct));
        }

        var featured = await ProductCard.FromQueryAsync(active.Where(p => p.IsFeatured), 8, ct);
        var topRatedRows = await active.Where(p => p.RatingCount > 0)
            .OrderByDescending(p => p.RatingAvg).ThenByDescending(p => p.RatingCount).Take(8)
            .Select(p => new { p.Id, p.Slug, p.Title, p.PriceETB, p.SalePriceETB, p.CompareAtPriceETB, p.RatingAvg, p.RatingCount, p.SoldCount, p.VendorNameSnapshot, VendorId = (Guid?)p.VendorId, p.Stock, Thumb = p.Images.OrderBy(i => i.SortOrder).Select(i => i.StorageKey).FirstOrDefault(), Colors = p.Colors.OrderBy(c => c.SortOrder).Select(c => new { c.Name, c.Hex }).ToList() })
            .ToListAsync(ct);
        var newArrivals = await ProductCard.FromQueryAsync(active.OrderByDescending(p => p.CreatedAtUtc), 8, ct);

        var cats = await db.Categories.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.SortOrder)
            .Select(c => new CategoryDto(c.Id, c.Name, c.Slug, 0)).ToListAsync(ct);

        var topRated = topRatedRows.Select(r => new ProductCardDto(r.Id, r.Slug, r.Title, r.PriceETB, r.SalePriceETB, r.CompareAtPriceETB,
            r.Thumb == null ? null : r.Thumb.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? r.Thumb : $"/api/media/{r.Thumb}",
            r.RatingAvg, r.RatingCount, r.SoldCount, r.VendorNameSnapshot, r.VendorId == null, r.Stock,
            r.Colors.Select(c => new ProductCardColorDto(c.Name, c.Hex)).ToList())).ToList();

        return Result.Ok(new HomeDto(banners, flashSale, featured, topRated, newArrivals, cats));
    }
}

// ==================== SEARCH SUGGEST ====================
public sealed record SuggestQuery(string? Q) : IRequest<Result<SuggestDto>>;
public sealed record SuggestProductDto(string Slug, string Title, string? Thumb, decimal PriceETB);
public sealed record SuggestCategoryDto(string Slug, string Name);
public sealed record SuggestDto(IReadOnlyList<SuggestProductDto> Products, IReadOnlyList<SuggestCategoryDto> Categories);

public sealed class SuggestHandler(CatalogDbContext db, ISearchIndexer searchIndexer) : IRequestHandler<SuggestQuery, Result<SuggestDto>>
{
    private static string? ResolveThumb(string? storageKey) => storageKey switch
    {
        null => null,
        var k when k.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || k.StartsWith("https://", StringComparison.OrdinalIgnoreCase) => k,
        _ => $"/api/media/{k}",
    };

    public async Task<Result<SuggestDto>> Handle(SuggestQuery q, CancellationToken ct)
    {
        var term = (q.Q ?? string.Empty).Trim();
        if (term.Length == 0) return Result.Ok(new SuggestDto(Array.Empty<SuggestProductDto>(), Array.Empty<SuggestCategoryDto>()));

        IReadOnlyList<SuggestProductDto> products;
        var hits = await searchIndexer.SearchAsync(term, 6, ct); // null → provider not configured, SQL fallback
        if (hits is not null)
        {
            products = hits.Select(h => new SuggestProductDto(h.Slug, h.Title, h.Thumb, h.Price)).ToList();
        }
        else
        {
            var rows = await db.Products.AsNoTracking()
                .Where(p => p.Status == ProductStatus.Active && EF.Functions.ILike(p.Title, $"%{term}%"))
                .OrderByDescending(p => p.SoldCount).Take(6)
                .Select(p => new { p.Slug, p.Title, p.PriceETB, Thumb = p.Images.OrderBy(i => i.SortOrder).Select(i => i.StorageKey).FirstOrDefault() })
                .ToListAsync(ct);
            products = rows.Select(r => new SuggestProductDto(r.Slug, r.Title, ResolveThumb(r.Thumb), r.PriceETB)).ToList();
        }

        var categories = await db.Categories.AsNoTracking()
            .Where(c => c.IsActive && EF.Functions.ILike(c.Name, $"%{term}%"))
            .OrderBy(c => c.SortOrder).Take(3)
            .Select(c => new SuggestCategoryDto(c.Slug, c.Name))
            .ToListAsync(ct);

        return Result.Ok(new SuggestDto(products, categories));
    }
}

// ==================== PRODUCT DETAIL ====================
public sealed record GetProductBySlugQuery(string Slug) : IRequest<Result<ProductDetailDto>>;
public sealed record ProductVariantDto(Guid Id, string Sku, Guid? ColorId, string? ColorHex, string? Size, decimal? PriceETB,
    int Stock, bool IsActive, int SortOrder, string? Label);
public sealed record ReviewsSummaryDto(decimal Avg, int Count, IReadOnlyDictionary<int, int> Breakdown);
public sealed record ProductDetailDto(
    Guid Id, Guid? VendorId, string? VendorName, Guid CategoryId, string CategoryName,
    string Title, string Slug, string Description, decimal PriceETB, decimal? CompareAtPriceETB,
    decimal? SalePriceETB, DateTime? SaleEndsAtUtc, int SoldCount, int ViewCount, int VariantsCount, int Stock,
    string SKU, decimal WeightKg, string Status, bool IsFeatured, decimal RatingAvg, int RatingCount, DateTime CreatedAtUtc,
    IReadOnlyList<ProductColorDto> Colors, IReadOnlyList<ProductImageDto> Gallery,
    IReadOnlyList<ProductVariantDto> Variants, ReviewsSummaryDto ReviewsSummary,
    IReadOnlyList<ReviewDto> RecentReviews, IReadOnlyList<ProductCardDto> Related);

public sealed class GetProductBySlugHandler(CatalogDbContext db) : IRequestHandler<GetProductBySlugQuery, Result<ProductDetailDto>>
{
    public async Task<Result<ProductDetailDto>> Handle(GetProductBySlugQuery q, CancellationToken ct)
    {
        var p = await db.Products.AsNoTracking()
            .Include(x => x.Colors)
            .Include(x => x.Images)
            .FirstOrDefaultAsync(x => x.Slug == q.Slug && x.Status == ProductStatus.Active, ct)
            ?? throw new NotFoundException("Product", q.Slug);

        // fire-and-forget view counter (never blocks or fails the read)
        await db.Products.Where(x => x.Id == p.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewCount, x => x.ViewCount + 1), ct);

        var cat = await db.Categories.AsNoTracking().Where(c => c.Id == p.CategoryId)
            .Select(c => new { c.Name, c.Slug }).FirstOrDefaultAsync(ct);

        var colorById = p.Colors.ToDictionary(c => c.Id);
        var variants = await db.ProductVariants.AsNoTracking()
            .Where(v => v.ProductId == p.Id && v.IsActive)
            .OrderBy(v => v.SortOrder).ToListAsync(ct);
        var variantDtos = variants.Select(v =>
        {
            colorById.TryGetValue(v.ColorId ?? Guid.Empty, out var color);
            return new ProductVariantDto(v.Id, v.SKU, v.ColorId, color?.Hex, v.Size, v.PriceETB, v.Stock, v.IsActive, v.SortOrder, v.Label(color?.Name));
        }).ToList();

        var approved = await db.Reviews.AsNoTracking()
            .Where(r => r.ProductId == p.Id && r.Status == ReviewStatus.Approved)
            .OrderByDescending(r => r.CreatedAtUtc)
            .ToListAsync(ct);
        var summary = ReviewAggregates.Summary(approved);
        var recent = approved.Take(3).Select(ReviewDto.From).ToList();

        var related = await ProductCard.FromQueryAsync(
            db.Products.AsNoTracking().Where(x => x.Status == ProductStatus.Active && x.CategoryId == p.CategoryId && x.Id != p.Id), 6, ct);

        return Result.Ok(new ProductDetailDto(
            p.Id, p.VendorId, p.VendorNameSnapshot, p.CategoryId, cat?.Name ?? "", p.Title, p.Slug, p.Description,
            p.PriceETB, p.CompareAtPriceETB, p.SalePriceETB, p.SaleEndsAtUtc, p.SoldCount, p.ViewCount,
            variantDtos.Count, p.Stock, p.SKU, p.WeightKg, p.Status.ToString(), p.IsFeatured, p.RatingAvg, p.RatingCount, p.CreatedAtUtc,
            p.Colors.OrderBy(c => c.SortOrder).Select(c => new ProductColorDto(c.Id, c.Name, c.Hex, c.Stock, c.ExtraPriceETB, c.SortOrder)).ToList(),
            p.Images.OrderBy(i => i.SortOrder).Select(i => new ProductImageDto(i.Id, i.ColorId, i.PublicUrl, i.Alt, i.SortOrder)).ToList(),
            variantDtos, summary, recent, related));
    }
}

/// <summary>Shared rating aggregation (summary + paged list use the same breakdown math).</summary>
public static class ReviewAggregates
{
    public static ReviewsSummaryDto Summary(IReadOnlyCollection<Review> approvedReviews)
    {
        var breakdown = new Dictionary<int, int> { { 5, 0 }, { 4, 0 }, { 3, 0 }, { 2, 0 }, { 1, 0 } };
        foreach (var r in approvedReviews)
            if (r.Rating is >= 1 and <= 5) breakdown[r.Rating]++;
        var avg = approvedReviews.Count == 0
            ? 0m
            : Math.Round(approvedReviews.Sum(r => (decimal)r.Rating) / approvedReviews.Count, 2, MidpointRounding.AwayFromZero);
        return new ReviewsSummaryDto(avg, approvedReviews.Count, breakdown);
    }

    /// <summary>Recomputes Product.RatingAvg/RatingCount from approved reviews and persists both.</summary>
    public static async Task RecomputeProductRatingAsync(CatalogDbContext db, Guid productId, CancellationToken ct)
    {
        var ratings = await db.Reviews.AsNoTracking()
            .Where(r => r.ProductId == productId && r.Status == ReviewStatus.Approved)
            .Select(r => (decimal)r.Rating)
            .ToListAsync(ct);
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null) return;
        product.RatingAvg = ratings.Count == 0 ? 0m : Math.Round(ratings.Average(), 2, MidpointRounding.AwayFromZero);
        product.RatingCount = ratings.Count;
        product.ModifiedAtUtc = DateTime.UtcNow;
    }
}

// ==================== REVIEWS ====================
public sealed record ReviewDto(Guid Id, Guid ProductId, Guid UserId, int Rating, string? Title, string Body,
    IReadOnlyList<string>? ImageUrls, string Status, string? VendorReply, DateTime? VendorRepliedAtUtc, DateTime CreatedAtUtc, bool Verified)
{
    public static ReviewDto From(Review r) => new(r.Id, r.ProductId, r.UserId, r.Rating, r.Title, r.Body,
        ParseUrls(r.ImageUrlsJson), r.Status.ToString(), r.VendorReply, r.VendorRepliedAtUtc, r.CreatedAtUtc, true);

    public static IReadOnlyList<string>? ParseUrls(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<List<string>>(json);
}

public sealed record ListProductReviewsQuery(Guid ProductId, int Page = 1, int PageSize = 10) : IRequest<Result<ProductReviewsPageDto>>;
public sealed record ProductReviewsPageDto(IReadOnlyList<ReviewDto> Items, int Page, int PageSize, long TotalCount, IReadOnlyDictionary<int, int> Breakdown);

public sealed class ListProductReviewsHandler(CatalogDbContext db) : IRequestHandler<ListProductReviewsQuery, Result<ProductReviewsPageDto>>
{
    public async Task<Result<ProductReviewsPageDto>> Handle(ListProductReviewsQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var pageSize = Math.Clamp(q.PageSize, 1, 50);
        var query = db.Reviews.AsNoTracking().Where(r => r.ProductId == q.ProductId && r.Status == ReviewStatus.Approved);
        var total = await query.LongCountAsync(ct);
        var items = await query.OrderByDescending(r => r.CreatedAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var ratings = await query.Select(r => (int)r.Rating).ToListAsync(ct);
        var breakdown = new Dictionary<int, int> { { 5, 0 }, { 4, 0 }, { 3, 0 }, { 2, 0 }, { 1, 0 } };
        foreach (var r in ratings)
            if (r is >= 1 and <= 5) breakdown[r]++;
        return Result.Ok(new ProductReviewsPageDto(items.Select(ReviewDto.From).ToList(), page, pageSize, total, breakdown));
    }
}

/// <summary>Pure verified-purchase predicate — only Delivered/Completed orders count (unit-tested).</summary>
public static class ReviewPolicy
{
    public static bool IsVerifiedPurchase(string? orderStatus)
        => orderStatus is nameof(OrderLifecycleStatus.Delivered) or nameof(OrderLifecycleStatus.Completed);
}

/// <summary>Status names mirrored as strings so the Catalog module needs no Sales reference.</summary>
public static class OrderLifecycleStatus
{
    public const string Delivered = "Delivered";
    public const string Completed = "Completed";
}

public sealed record CreateReviewCommand(Guid UserId, Guid ProductId, Guid OrderId, int Rating, string? Title, string Body, IReadOnlyList<string>? ImageUrls) : IRequest<Result<ReviewDto>>;

public sealed class CreateReviewValidator : AbstractValidator<CreateReviewCommand>
{
    public CreateReviewValidator()
    {
        RuleFor(x => x.Rating).InclusiveBetween(1, 5).WithMessage("Rating must be between 1 and 5.");
        RuleFor(x => x.Body).NotEmpty().MinimumLength(10).WithMessage("Review body must be at least 10 characters.").MaximumLength(4000);
        RuleFor(x => x.Title).MaximumLength(200);
        RuleFor(x => x.ImageUrls).Must(list => list == null || list.Count <= 5).WithMessage("At most 5 photos per review.");
        RuleForEach(x => x.ImageUrls).MaximumLength(500).Must(u => u == null || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Review photos must be https URLs.");
    }
}

public sealed class CreateReviewHandler(CatalogDbContext db, IOrderReader orders, IValidator<CreateReviewCommand> validator)
    : IRequestHandler<CreateReviewCommand, Result<ReviewDto>>
{
    public async Task<Result<ReviewDto>> Handle(CreateReviewCommand c, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(c, ct);
        if (!validation.IsValid) return Result.Fail<ReviewDto>("validation", string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));

        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == c.ProductId && p.Status == ProductStatus.Active, ct)
            ?? throw new NotFoundException("Product", c.ProductId);

        // verified-purchase gate: the order must belong to the buyer, contain the product and be Delivered/Completed
        var orderStatus = await orders.GetOrderStatusForBuyerProductAsync(c.UserId, c.OrderId, c.ProductId, ct);
        if (!ReviewPolicy.IsVerifiedPurchase(orderStatus))
            return Result.Fail<ReviewDto>("not_verified", "You can only review products that were delivered to you from this order.");

        if (await db.Reviews.AnyAsync(r => r.UserId == c.UserId && r.ProductId == c.ProductId && r.OrderId == c.OrderId, ct))
            return Result.Fail<ReviewDto>("duplicate", "You already reviewed this product from that order.");

        var review = new Review
        {
            ProductId = c.ProductId,
            UserId = c.UserId,
            OrderId = c.OrderId,
            Rating = c.Rating,
            Title = string.IsNullOrWhiteSpace(c.Title) ? null : c.Title.Trim(),
            Body = c.Body.Trim(),
            ImageUrlsJson = c.ImageUrls is { Count: > 0 } ? JsonSerializer.Serialize(c.ImageUrls) : null,
            Status = ReviewStatus.Pending, // every review goes through moderation before it is public
        };
        db.Reviews.Add(review);
        db.AddToOutbox(new MarketHub.Shared.Contracts.Events.ReviewCreatedEvent(review.Id, product.Id, product.VendorId, c.UserId, c.Rating));
        await db.SaveChangesAsync(ct);
        return Result.Ok(ReviewDto.From(review));
    }
}

public sealed record ModerateReviewCommand(Guid AdminId, Guid ReviewId, bool Approve, string? Note) : IRequest<Result<ReviewDto>>;

public sealed class ModerateReviewHandler(CatalogDbContext db, IVendorStatsUpdater vendorStats)
    : IRequestHandler<ModerateReviewCommand, Result<ReviewDto>>
{
    public async Task<Result<ReviewDto>> Handle(ModerateReviewCommand c, CancellationToken ct)
    {
        var review = await db.Reviews.FirstOrDefaultAsync(r => r.Id == c.ReviewId, ct)
            ?? throw new NotFoundException("Review", c.ReviewId);
        if (review.Status != ReviewStatus.Pending)
            return Result.Failure(new Error("state", $"Review is already {review.Status}."));

        review.Status = c.Approve ? ReviewStatus.Approved : ReviewStatus.Rejected;
        review.ModerationNote = c.Note;
        review.ModifiedAtUtc = DateTime.UtcNow;

        // approval recomputes the denormalized product rating from approved reviews
        await ReviewAggregates.RecomputeProductRatingAsync(db, review.ProductId, ct);
        await db.SaveChangesAsync(ct);

        // denormalize the vendor's review counter into the Identity module
        var vendorId = await db.Products.AsNoTracking().Where(p => p.Id == review.ProductId).Select(p => p.VendorId).FirstOrDefaultAsync(ct);
        if (vendorId.HasValue)
        {
            var count = await db.Products.AsNoTracking().Where(p => p.VendorId == vendorId && p.Status == ProductStatus.Active).SumAsync(p => p.RatingCount, ct);
            await vendorStats.UpdateRatingCountAsync(vendorId.Value, count, ct);
        }

        db.AddToOutbox(new MarketHub.Shared.Contracts.Events.ReviewModeratedEvent(review.Id, review.UserId, review.ProductId, c.Approve));
        await db.SaveChangesAsync(ct);
        return Result.Ok(ReviewDto.From(review));
    }
}

public sealed record ListReviewsForModerationQuery(string? Status) : IRequest<Result<IReadOnlyList<ReviewDto>>>;

public sealed class ListReviewsForModerationHandler(CatalogDbContext db) : IRequestHandler<ListReviewsForModerationQuery, Result<IReadOnlyList<ReviewDto>>>
{
    public async Task<Result<IReadOnlyList<ReviewDto>>> Handle(ListReviewsForModerationQuery q, CancellationToken ct)
    {
        var query = db.Reviews.AsNoTracking().AsQueryable();
        if (Enum.TryParse<ReviewStatus>(q.Status, true, out var status)) query = query.Where(r => r.Status == status);
        var items = await query.OrderByDescending(r => r.CreatedAtUtc).Take(100).ToListAsync(ct);
        return Result.Ok((IReadOnlyList<ReviewDto>)items.Select(ReviewDto.From).ToList());
    }
}

public sealed record VendorReplyToReviewCommand(Guid VendorId, Guid ReviewId, string Reply) : IRequest<Result<ReviewDto>>;

public sealed class VendorReplyToReviewHandler(CatalogDbContext db) : IRequestHandler<VendorReplyToReviewCommand, Result<ReviewDto>>
{
    public async Task<Result<ReviewDto>> Handle(VendorReplyToReviewCommand c, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.Reply) || c.Reply.Trim().Length < 2)
            return Result.Fail<ReviewDto>("reply", "A reply of at least 2 characters is required.");
        var review = await db.Reviews.FirstOrDefaultAsync(r => r.Id == c.ReviewId, ct)
            ?? throw new NotFoundException("Review", c.ReviewId);
        var ownsProduct = await db.Products.AsNoTracking().AnyAsync(p => p.Id == review.ProductId && p.VendorId == c.VendorId, ct);
        if (!ownsProduct) throw new NotFoundException("Review", c.ReviewId);

        review.VendorReply = c.Reply.Trim();
        review.VendorRepliedAtUtc = DateTime.UtcNow;
        review.ModifiedAtUtc = DateTime.UtcNow;
        db.AddToOutbox(new MarketHub.Shared.Contracts.Events.ReviewRepliedEvent(review.Id, review.ProductId, c.VendorId, review.UserId, review.VendorReply!));
        await db.SaveChangesAsync(ct);
        return Result.Ok(ReviewDto.From(review));
    }
}

// ==================== WISHLIST ====================
public sealed record ListWishlistQuery(Guid UserId) : IRequest<Result<WishlistDto>>;
public sealed record WishlistDto(IReadOnlyList<WishlistEntryDto> Items);
public sealed record WishlistEntryDto(Guid Id, Guid ProductId, ProductCardDto Product);

public sealed class ListWishlistHandler(CatalogDbContext db) : IRequestHandler<ListWishlistQuery, Result<WishlistDto>>
{
    public async Task<Result<WishlistDto>> Handle(ListWishlistQuery q, CancellationToken ct)
    {
        var entries = await db.WishlistItems.AsNoTracking()
            .Where(w => w.UserId == q.UserId)
            .OrderByDescending(w => w.CreatedAtUtc)
            .Select(w => new { w.Id, w.ProductId })
            .ToListAsync(ct);
        var productIds = entries.Select(e => e.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking()
            .Include(p => p.Colors)
            .Include(p => p.Images)
            .Where(p => productIds.Contains(p.Id) && p.Status == ProductStatus.Active)
            .ToDictionaryAsync(p => p.Id, ct);
        return Result.Ok(new WishlistDto(entries
            .Where(e => products.ContainsKey(e.ProductId))
            .Select(e => new WishlistEntryDto(e.Id, e.ProductId, ProductCard.From(products[e.ProductId])))
            .ToList()));
    }
}

public sealed record ToggleWishlistCommand(Guid UserId, Guid ProductId) : IRequest<Result<bool>>;

public sealed class ToggleWishlistHandler(CatalogDbContext db) : IRequestHandler<ToggleWishlistCommand, Result<bool>>
{
    public async Task<Result<bool>> Handle(ToggleWishlistCommand c, CancellationToken ct)
    {
        if (!await db.Products.AnyAsync(p => p.Id == c.ProductId && p.Status == ProductStatus.Active, ct))
            throw new NotFoundException("Product", c.ProductId);
        var existing = await db.WishlistItems.FirstOrDefaultAsync(w => w.UserId == c.UserId && w.ProductId == c.ProductId, ct);
        bool wishlisted;
        if (existing is null)
        {
            db.WishlistItems.Add(new WishlistItem { UserId = c.UserId, ProductId = c.ProductId });
            wishlisted = true;
        }
        else
        {
            db.WishlistItems.Remove(existing);
            wishlisted = false;
        }
        await db.SaveChangesAsync(ct);
        return Result.Ok(wishlisted);
    }
}
