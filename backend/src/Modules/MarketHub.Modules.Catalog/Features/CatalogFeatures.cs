using FluentValidation;
using MarketHub.Shared;
using MarketHub.Shared.Contracts;
using MarketHub.Modules.Catalog.Domain;
using MarketHub.Modules.Catalog.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace MarketHub.Modules.Catalog.Features;

// ==================== CREATE PRODUCT ====================
/// <summary>One photo of a new listing. DataOrUrl is either a base64 data URL (data:image/…)
/// which the backend stores via IFileStorage, or an absolute https URL.</summary>
public sealed record ProductImageInput(string DataOrUrl, string? Alt, int? ColorIndex);

/// <summary>A color variant of a new listing — swatch hex, per-color stock and optional price delta.</summary>
public sealed record ProductColorInput(string Name, string Hex, int Stock, decimal ExtraPriceETB = 0m);

public sealed record CreateProductCommand(Guid? VendorId, Guid? CategoryId, string Title, string Description,
    decimal PriceETB, decimal? CompareAtPriceETB, int Stock, decimal WeightKg, bool IsPlatformProduct,
    IReadOnlyList<ProductImageInput>? Images = null, IReadOnlyList<ProductColorInput>? Colors = null) : IRequest<Result<ProductDto>>;

public sealed class CreateProductValidator : AbstractValidator<CreateProductCommand>
{
    public const int MaxImages = 8;
    public const int MaxImageChars = 480_000; // ≈350KB payload per photo
    public const int MaxColors = 6;
    private static readonly System.Text.RegularExpressions.Regex HexRe = new("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public CreateProductValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(220);
        RuleFor(x => x.Description).NotEmpty().MaximumLength(4000);
        RuleFor(x => x.PriceETB).GreaterThan(0).PrecisionScale(18, 2, true);
        RuleFor(x => x.CompareAtPriceETB).GreaterThan(0).PrecisionScale(18, 2, true).When(x => x.CompareAtPriceETB.HasValue);
        RuleFor(x => x.Stock).GreaterThanOrEqualTo(0);
        RuleFor(x => x.WeightKg).GreaterThan(0);
        RuleFor(x => x.CategoryId).NotNull();

        // Media rules — a listing must ship with at least one photo, at most MaxImages.
        RuleFor(x => x.Images).NotNull().NotEmpty().WithMessage("At least 1 product photo is required.")
            .Must(list => list.Count <= MaxImages).WithMessage($"A listing can hold at most {MaxImages} photos.");
        RuleForEach(x => x.Images).ChildRules(img =>
        {
            img.RuleFor(i => i.DataOrUrl).NotEmpty().Must(u =>
                    (u.StartsWith("data:image/", StringComparison.Ordinal) && u.Contains("base64,"))
                    || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                .WithMessage("Each photo must be an image data URL or an https URL.")
                .MaximumLength(MaxImageChars).WithMessage("Each photo must stay under ~350KB — compress larger images.");
            img.RuleFor(i => i.Alt).MaximumLength(60);
        });

        RuleFor(x => x.Colors).Must(c => c is null || c.Count <= MaxColors).WithMessage($"A listing can have at most {MaxColors} color variants.");
        RuleForEach(x => x.Colors).ChildRules(col =>
        {
            col.RuleFor(c => c.Name).NotEmpty().MaximumLength(40);
            col.RuleFor(c => c.Hex).Matches(HexRe).WithMessage("Color hex must look like #1a1a1a.");
            col.RuleFor(c => c.Stock).GreaterThanOrEqualTo(0);
            col.RuleFor(c => c.ExtraPriceETB).GreaterThanOrEqualTo(0).PrecisionScale(18, 2, true);
        });
        RuleFor(x => x)
            .Must(x => x.Colors is null || x.Colors.Select(c => c.Name.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).Count() == x.Colors.Count)
            .WithMessage("Color names must be unique.")
            .Must(x => x.Colors is null || x.Images is null || x.Images.All(i => !i.ColorIndex.HasValue || i.ColorIndex.Value < x.Colors.Count))
            .WithMessage("A photo references a color that does not exist.");
    }
}

public sealed class CreateProductHandler(CatalogDbContext db, IMarketplaceVendorChecker vendorChecker,
    MarketHub.Shared.Infrastructure.IFileStorage fileStorage, IValidator<CreateProductCommand> validator,
    IServiceProvider sp)
    : IRequestHandler<CreateProductCommand, Result<ProductDto>>
{
    public async Task<Result<ProductDto>> Handle(CreateProductCommand c, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(c, ct);
        if (!validation.IsValid) return Result.Fail<ProductDto>("validation", string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));

        var category = await db.Categories.FirstOrDefaultAsync(x => x.Id == c.CategoryId && x.IsActive, ct);
        if (category is null) throw new NotFoundException("Category", c.CategoryId);

        Guid? vendorId = null;
        string? vendorName = null;
        ProductStatus initialStatus;

        if (c.IsPlatformProduct)
        {
            initialStatus = ProductStatus.Active; // first-party products go live immediately
        }
        else
        {
            vendorId = c.VendorId;
            if (!await vendorChecker.IsApprovedVendorAsync(c.VendorId!.Value, ct))
                return Result.Fail<ProductDto>("vendor_not_approved", "Only approved vendors can list products. Your application must be approved by the admin team.");
            vendorName = await vendorChecker.GetBusinessNameAsync(c.VendorId.Value, ct);
            initialStatus = ProductStatus.PendingReview; // vendor listings go through moderation
        }

        var slugBase = System.Text.RegularExpressions.Regex.Replace(c.Title.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        var slug = $"{slugBase}-{Guid.NewGuid().ToString("N")[..6]}";

        // With color variants the vendor tracks stock per color — product stock is the authoritative sum.
        var colors = c.Colors is { Count: > 0 }
            ? c.Colors.Select((col, i) => new ProductColor
            {
                Name = col.Name.Trim(),
                Hex = col.Hex.ToLowerInvariant(),
                Stock = col.Stock,
                ExtraPriceETB = col.ExtraPriceETB,
                SortOrder = i,
            }).ToList()
            : new List<ProductColor>();
        var totalStock = colors.Count > 0 ? colors.Sum(x => x.Stock) : c.Stock;

        var product = new Product
        {
            VendorId = vendorId,
            VendorNameSnapshot = vendorName,
            CategoryId = c.CategoryId!.Value,
            Title = c.Title,
            Slug = slug,
            Description = c.Description,
            PriceETB = c.PriceETB,
            CompareAtPriceETB = c.CompareAtPriceETB,
            Stock = totalStock,
            WeightKg = c.WeightKg,
            SKU = $"MH-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            Status = initialStatus,
            IsFeatured = c.IsPlatformProduct,
            Colors = colors,
        };
        db.Products.Add(product);
        await db.SaveChangesAsync(ct); // assigns Product.Id and each ProductColor.Id

        // Persist photos: data URLs are decoded and stored via IFileStorage; https URLs pass through.
        var sortOrder = 0;
        foreach (var input in c.Images ?? Array.Empty<ProductImageInput>())
        {
            string storageRef;
            if (input.DataOrUrl.StartsWith("data:image/", StringComparison.Ordinal))
            {
                var comma = input.DataOrUrl.IndexOf("base64,", StringComparison.Ordinal) + "base64,".Length;
                var semi = input.DataOrUrl.IndexOf(';');
                var mime = semi > 0 ? input.DataOrUrl[5..semi] : "jpeg"; // "image/png" → ext below
                var ext = mime switch
                {
                    "image/png" => ".png",
                    "image/webp" => ".webp",
                    "image/gif" => ".gif",
                    _ => ".jpg",
                };
                var bytes = Convert.FromBase64String(input.DataOrUrl[comma..]);
                storageRef = await fileStorage.SaveAsync($"product{ext}", mime, bytes, ct);
            }
            else
            {
                storageRef = input.DataOrUrl; // externally hosted https URL
            }

            db.ProductImages.Add(new ProductImage
            {
                ProductId = product.Id,
                ColorId = input.ColorIndex.HasValue ? colors[input.ColorIndex.Value].Id : null,
                StorageKey = storageRef,
                Alt = (input.Alt ?? string.Empty).Trim(),
                SortOrder = sortOrder++,
            });
        }
        await db.SaveChangesAsync(ct);

        // keep the search engine in sync (fire-and-forget; platform products are live immediately)
        if (product.Status == ProductStatus.Active) SearchIndexerBackground.IndexProduct(sp, product.Id);

        return Result.Ok(ProductDto.From(product, category.Name));
    }
}

// ==================== MODERATION ====================
public sealed record ModerateProductCommand(Guid AdminId, Guid ProductId, bool Approve, string? Note) : IRequest<Result>;

public sealed class ModerateProductHandler(CatalogDbContext db, IServiceProvider sp) : IRequestHandler<ModerateProductCommand, Result>
{
    public async Task<Result> Handle(ModerateProductCommand c, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == c.ProductId, ct)
            ?? throw new NotFoundException("Product", c.ProductId);
        if (product.VendorId is not null && product.Status != ProductStatus.PendingReview)
            return Result.Failure(new Error("not_moderatable", "Only pending-review vendor products require moderation."));
        product.Status = c.Approve ? ProductStatus.Active : ProductStatus.Rejected;
        product.ModerationNote = c.Note;
        product.ModifiedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        // search index follows moderation (fire-and-forget with error logging)
        if (c.Approve && product.Status == ProductStatus.Active) SearchIndexerBackground.IndexProduct(sp, product.Id);
        else if (!c.Approve) SearchIndexerBackground.RemoveProduct(sp, product.Id);
        return Result.Success();
    }
}

// ==================== QUERIES ====================
public sealed record ListProductsQuery(string? Search, Guid? CategoryId, Guid? VendorId, bool PlatformOnly,
    decimal? MinPrice, decimal? MaxPrice, decimal? MinRating, string Sort, int Page = 1, int PageSize = 12) : IRequest<Result<PagedResult<ProductDto>>>;

public sealed class ListProductsHandler(CatalogDbContext db) : IRequestHandler<ListProductsQuery, Result<PagedResult<ProductDto>>>
{
    public async Task<Result<PagedResult<ProductDto>>> Handle(ListProductsQuery q, CancellationToken ct)
    {
        var query = db.Products.AsNoTracking()
            .Include(p => p.Colors)
            .Include(p => p.Images)
            .Where(p => p.Status == ProductStatus.Active);
        if (!string.IsNullOrWhiteSpace(q.Search))
            query = query.Where(p => EF.Functions.ILike(p.Title, $"%{q.Search}%") || EF.Functions.ILike(p.Description, $"%{q.Search}%"));
        if (q.CategoryId.HasValue) query = query.Where(p => p.CategoryId == q.CategoryId);
        if (q.VendorId.HasValue) query = query.Where(p => p.VendorId == q.VendorId);
        if (q.PlatformOnly) query = query.Where(p => p.VendorId == null);
        if (q.MinPrice.HasValue) query = query.Where(p => p.PriceETB >= q.MinPrice);
        if (q.MaxPrice.HasValue) query = query.Where(p => p.PriceETB <= q.MaxPrice);
        if (q.MinRating.HasValue) query = query.Where(p => p.RatingAvg >= q.MinRating);

        query = (q.Sort?.ToLowerInvariant()) switch
        {
            "newest" => query.OrderByDescending(p => p.CreatedAtUtc),
            "price_asc" => query.OrderBy(p => p.PriceETB),
            "price_desc" => query.OrderByDescending(p => p.PriceETB),
            "rating" => query.OrderByDescending(p => p.RatingAvg),
            "sold" => query.OrderByDescending(p => p.SoldCount),
            _ => query.OrderByDescending(p => p.IsFeatured).ThenByDescending(p => p.RatingAvg),
        };

        var total = await query.LongCountAsync(ct);
        var items = await query.Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).ToListAsync(ct);
        var categories = await db.Categories.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var variantCounts = await db.ProductVariants.AsNoTracking()
            .Where(v => items.Select(i => i.Id).Contains(v.ProductId) && v.IsActive)
            .GroupBy(v => v.ProductId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return Result.Ok(PagedResult<ProductDto>.Create(items.Select(p => ProductDto.From(p, categories.GetValueOrDefault(p.CategoryId),
            variantCounts.FirstOrDefault(v => v.Key == p.Id)?.Count ?? 0)).ToList(), q.Page, q.PageSize, total));
    }
}

public sealed record ListCategoriesQuery : IRequest<Result<IReadOnlyList<CategoryDto>>>;
public sealed record CategoryDto(Guid Id, string Name, string Slug, int ProductCount);

public sealed class ListCategoriesHandler(CatalogDbContext db) : IRequestHandler<ListCategoriesQuery, Result<IReadOnlyList<CategoryDto>>>
{
    public async Task<Result<IReadOnlyList<CategoryDto>>> Handle(ListCategoriesQuery q, CancellationToken ct)
    {
        var cats = await db.Categories.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.SortOrder).ToListAsync(ct);
        var counts = await db.Products.AsNoTracking().Where(p => p.Status == ProductStatus.Active)
            .GroupBy(p => p.CategoryId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return Result.Ok(cats.Select(c => new CategoryDto(c.Id, c.Name, c.Slug, counts.FirstOrDefault(x => x.Key == c.Id)?.Count ?? 0)).ToList());
    }
}

public sealed record ProductColorDto(Guid Id, string Name, string Hex, int Stock, decimal ExtraPriceETB, int SortOrder);
public sealed record ProductImageDto(Guid Id, Guid? ColorId, string Url, string Alt, int SortOrder);

public sealed record ProductDto(Guid Id, Guid? VendorId, string? VendorName, Guid CategoryId, string CategoryName,
    string Title, string Slug, string Description, decimal PriceETB, decimal? CompareAtPriceETB,
    decimal? SalePriceETB, DateTime? SaleEndsAtUtc, int SoldCount, int ViewCount, int VariantsCount, int Stock,
    string SKU, decimal WeightKg, string Status, bool IsFeatured, decimal RatingAvg, int RatingCount, DateTime CreatedAtUtc,
    IReadOnlyList<ProductColorDto> Colors, IReadOnlyList<ProductImageDto> Gallery)
{
    public static ProductDto From(Product p, string? categoryName, int variantsCount = 0) => new(
        p.Id, p.VendorId, p.VendorNameSnapshot, p.CategoryId, categoryName ?? "", p.Title, p.Slug, p.Description,
        p.PriceETB, p.CompareAtPriceETB, p.SalePriceETB, p.SaleEndsAtUtc, p.SoldCount, p.ViewCount, variantsCount, p.Stock,
        p.SKU, p.WeightKg, p.Status.ToString(), p.IsFeatured, p.RatingAvg, p.RatingCount, p.CreatedAtUtc,
        p.Colors.OrderBy(c => c.SortOrder).Select(c => new ProductColorDto(c.Id, c.Name, c.Hex, c.Stock, c.ExtraPriceETB, c.SortOrder)).ToList(),
        p.Images.OrderBy(i => i.SortOrder).Select(i => new ProductImageDto(i.Id, i.ColorId, i.PublicUrl, i.Alt, i.SortOrder)).ToList());
}
