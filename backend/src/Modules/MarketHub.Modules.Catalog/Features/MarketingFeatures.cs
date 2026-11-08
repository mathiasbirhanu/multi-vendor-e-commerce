using FluentValidation;
using MarketHub.Shared;
using MarketHub.Modules.Catalog.Domain;
using MarketHub.Modules.Catalog.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quartz;

namespace MarketHub.Modules.Catalog.Features;

// ==================== FLASH SALE ====================
/// <summary>Pure flash-sale pricing (bounds + computation — unit-tested).</summary>
public static class FlashSaleCalculator
{
    public const int MinDiscountPct = 5;
    public const int MaxDiscountPct = 70;

    public static bool IsValidDiscountPct(int discountPct) => discountPct is >= MinDiscountPct and <= MaxDiscountPct;

    /// <summary>Sale price = base × (100 − pct) / 100, rounded 2dp away from zero. pct is clamped defensively.</summary>
    public static decimal ComputeSalePrice(decimal priceETB, int discountPct)
        => Math.Round(priceETB * (100m - Math.Clamp(discountPct, 0, 100)) / 100m, 2, MidpointRounding.AwayFromZero);
}

public sealed record SetFlashSaleCommand(IReadOnlyList<Guid> ProductIds, int DiscountPct, DateTime EndsAtUtc) : IRequest<Result<int>>;

public sealed class SetFlashSaleHandler(CatalogDbContext db, IServiceProvider sp) : IRequestHandler<SetFlashSaleCommand, Result<int>>
{
    public async Task<Result<int>> Handle(SetFlashSaleCommand c, CancellationToken ct)
    {
        if (!FlashSaleCalculator.IsValidDiscountPct(c.DiscountPct))
            return Result.Fail<int>("discount_pct", $"Discount must be between {FlashSaleCalculator.MinDiscountPct} and {FlashSaleCalculator.MaxDiscountPct} percent.");
        if (c.EndsAtUtc <= DateTime.UtcNow)
            return Result.Fail<int>("ends_at", "The flash sale end must be in the future.");
        if (c.ProductIds.Count == 0)
            return Result.Fail<int>("products", "Select at least one product for the flash sale.");

        var products = await db.Products.Where(p => c.ProductIds.Contains(p.Id) && p.Status == ProductStatus.Active).ToListAsync(ct);
        foreach (var p in products)
        {
            p.SalePriceETB = FlashSaleCalculator.ComputeSalePrice(p.PriceETB, c.DiscountPct);
            p.SaleEndsAtUtc = c.EndsAtUtc;
            p.ModifiedAtUtc = DateTime.UtcNow;
            SearchIndexerBackground.IndexProduct(sp, p.Id); // sale price changed → refresh the index entry
        }
        await db.SaveChangesAsync(ct);
        return Result.Ok(products.Count);
    }
}

public sealed record EndFlashSaleCommand : IRequest<Result<int>>;

public sealed class EndFlashSaleHandler(CatalogDbContext db, IServiceProvider sp) : IRequestHandler<EndFlashSaleCommand, Result<int>>
{
    public async Task<Result<int>> Handle(EndFlashSaleCommand c, CancellationToken ct)
    {
        var products = await db.Products.Where(p => p.SalePriceETB != null || p.SaleEndsAtUtc != null).ToListAsync(ct);
        foreach (var p in products)
        {
            p.SalePriceETB = null;
            p.SaleEndsAtUtc = null;
            p.ModifiedAtUtc = DateTime.UtcNow;
            SearchIndexerBackground.IndexProduct(sp, p.Id);
        }
        await db.SaveChangesAsync(ct);
        return Result.Ok(products.Count);
    }
}

/// <summary>Quartz job (every minute): clears expired sale prices so listings never show a stale discount.</summary>
public sealed class FlashSaleSchedulerJob(IServiceScopeFactory? scopeFactory = null) : IJob
{
    public static readonly JobKey Key = new("flash-sale-scheduler");

    public async Task Execute(IJobExecutionContext ctx)
    {
        var factory = scopeFactory
            ?? ctx.JobDetail.JobDataMap.Get("scopeFactory") as IServiceScopeFactory
            ?? throw new InvalidOperationException("scopeFactory unavailable for FlashSaleSchedulerJob");
        using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<FlashSaleSchedulerJob>>();
        try
        {
            var expired = await db.Products
                .Where(p => p.SaleEndsAtUtc != null && p.SaleEndsAtUtc < DateTime.UtcNow)
                .ToListAsync(ctx.CancellationToken);
            if (expired.Count == 0) return;
            foreach (var p in expired)
            {
                p.SalePriceETB = null;
                p.SaleEndsAtUtc = null;
                p.ModifiedAtUtc = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ctx.CancellationToken);
            logger.LogInformation("FlashSaleScheduler cleared {Count} expired sale prices", expired.Count);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "FlashSaleScheduler failed");
        }
    }
}

// ==================== BANNERS ====================
public sealed record ListBannersQuery : IRequest<Result<IReadOnlyList<BannerDto>>>;

public sealed class ListBannersHandler(CatalogDbContext db) : IRequestHandler<ListBannersQuery, Result<IReadOnlyList<BannerDto>>>
{
    public async Task<Result<IReadOnlyList<BannerDto>>> Handle(ListBannersQuery q, CancellationToken ct)
    {
        var banners = await db.Banners.AsNoTracking().OrderBy(b => b.CreatedAtUtc).ToListAsync(ct);
        return Result.Ok((IReadOnlyList<BannerDto>)banners.Select(b => new BannerDto(b.Id, b.Title, b.ImageUrl, b.Link, b.Position, b.IsActive)).ToList());
    }
}

public sealed record UpsertBannerCommand(Guid? Id, string Title, string ImageUrl, string? Link, string Position, bool IsActive, DateTime? StartsAtUtc, DateTime? EndsAtUtc) : IRequest<Result<BannerDto>>;

public sealed class UpsertBannerHandler(CatalogDbContext db) : IRequestHandler<UpsertBannerCommand, Result<BannerDto>>
{
    public async Task<Result<BannerDto>> Handle(UpsertBannerCommand c, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.Title)) return Result.Fail<BannerDto>("validation", "Title is required.");
        if (string.IsNullOrWhiteSpace(c.ImageUrl)) return Result.Fail<BannerDto>("validation", "ImageUrl is required.");

        Banner banner;
        if (c.Id.HasValue)
        {
            banner = await db.Banners.FirstOrDefaultAsync(b => b.Id == c.Id.Value, ct) ?? throw new NotFoundException("Banner", c.Id.Value);
            banner.Title = c.Title.Trim();
            banner.ImageUrl = c.ImageUrl.Trim();
            banner.Link = c.Link;
            banner.Position = string.IsNullOrWhiteSpace(c.Position) ? "hero" : c.Position.Trim();
            banner.IsActive = c.IsActive;
            banner.StartsAtUtc = c.StartsAtUtc;
            banner.EndsAtUtc = c.EndsAtUtc;
            banner.ModifiedAtUtc = DateTime.UtcNow;
        }
        else
        {
            banner = new Banner
            {
                Title = c.Title.Trim(),
                ImageUrl = c.ImageUrl.Trim(),
                Link = c.Link,
                Position = string.IsNullOrWhiteSpace(c.Position) ? "hero" : c.Position.Trim(),
                IsActive = c.IsActive,
                StartsAtUtc = c.StartsAtUtc,
                EndsAtUtc = c.EndsAtUtc,
            };
            db.Banners.Add(banner);
        }
        await db.SaveChangesAsync(ct);
        return Result.Ok(new BannerDto(banner.Id, banner.Title, banner.ImageUrl, banner.Link, banner.Position, banner.IsActive));
    }
}

public sealed record PatchBannerCommand(Guid Id, string? Title, string? ImageUrl, string? Link, string? Position, bool? IsActive, DateTime? StartsAtUtc, DateTime? EndsAtUtc) : IRequest<Result<BannerDto>>;

public sealed class PatchBannerHandler(CatalogDbContext db) : IRequestHandler<PatchBannerCommand, Result<BannerDto>>
{
    public async Task<Result<BannerDto>> Handle(PatchBannerCommand c, CancellationToken ct)
    {
        var banner = await db.Banners.FirstOrDefaultAsync(b => b.Id == c.Id, ct) ?? throw new NotFoundException("Banner", c.Id);
        if (!string.IsNullOrWhiteSpace(c.Title)) banner.Title = c.Title.Trim();
        if (!string.IsNullOrWhiteSpace(c.ImageUrl)) banner.ImageUrl = c.ImageUrl.Trim();
        if (c.Link != null) banner.Link = string.IsNullOrWhiteSpace(c.Link) ? null : c.Link.Trim();
        if (!string.IsNullOrWhiteSpace(c.Position)) banner.Position = c.Position.Trim();
        if (c.IsActive.HasValue) banner.IsActive = c.IsActive.Value;
        if (c.StartsAtUtc.HasValue) banner.StartsAtUtc = c.StartsAtUtc;
        if (c.EndsAtUtc.HasValue) banner.EndsAtUtc = c.EndsAtUtc;
        banner.ModifiedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Result.Ok(new BannerDto(banner.Id, banner.Title, banner.ImageUrl, banner.Link, banner.Position, banner.IsActive));
    }
}

public sealed record DeleteBannerCommand(Guid Id) : IRequest<Result>;

public sealed class DeleteBannerHandler(CatalogDbContext db) : IRequestHandler<DeleteBannerCommand, Result>
{
    public async Task<Result> Handle(DeleteBannerCommand c, CancellationToken ct)
    {
        var banner = await db.Banners.FirstOrDefaultAsync(b => b.Id == c.Id, ct) ?? throw new NotFoundException("Banner", c.Id);
        db.Banners.Remove(banner);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

// ==================== COUPONS (admin CRUD; validation math lives in Sales.Pricing.CouponCalculator) ====================
public sealed record CouponDto(Guid Id, string Code, string? Description, string Type, decimal Value, decimal MinSubtotal,
    decimal? MaxDiscount, int MaxUses, int UsedCount, DateTime StartsAtUtc, DateTime ExpiresAtUtc, bool IsActive);

public sealed record ListCouponsQuery : IRequest<Result<IReadOnlyList<CouponDto>>>;

public sealed class ListCouponsHandler(CatalogDbContext db) : IRequestHandler<ListCouponsQuery, Result<IReadOnlyList<CouponDto>>>
{
    public async Task<Result<IReadOnlyList<CouponDto>>> Handle(ListCouponsQuery q, CancellationToken ct)
    {
        var coupons = await db.Coupons.AsNoTracking().OrderByDescending(c => c.CreatedAtUtc).ToListAsync(ct);
        return Result.Ok((IReadOnlyList<CouponDto>)coupons.Select(ToDto).ToList());
    }

    internal static CouponDto ToDto(Coupon c) => new(c.Id, c.Code, c.Description, c.Type.ToString(), c.Value,
        c.MinSubtotal, c.MaxDiscount, c.MaxUses, c.UsedCount, c.StartsAtUtc, c.ExpiresAtUtc, c.IsActive);
}

public sealed record UpsertCouponCommand(Guid? Id, string Code, string? Description, string Type, decimal Value,
    decimal MinSubtotal, decimal? MaxDiscount, int MaxUses, DateTime StartsAtUtc, DateTime ExpiresAtUtc, bool IsActive) : IRequest<Result<CouponDto>>;

public sealed class UpsertCouponValidator : AbstractValidator<UpsertCouponCommand>
{
    public UpsertCouponValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(40).Matches("^[A-Za-z0-9_-]+$").WithMessage("Code may contain letters, digits, '-' and '_'.");
        RuleFor(x => x.Type).Must(t => t is "Percent" or "Fixed").WithMessage("Type must be Percent or Fixed.");
        RuleFor(x => x.Value).GreaterThan(0);
        RuleFor(x => x.Value).LessThanOrEqualTo(90m).When(x => x.Type == "Percent").WithMessage("Percent coupons cannot exceed 90%.");
        RuleFor(x => x.MinSubtotal).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MaxUses).GreaterThanOrEqualTo(1);
        RuleFor(x => x.ExpiresAtUtc).GreaterThan(x => x.StartsAtUtc).WithMessage("Expiry must be after the start date.");
    }
}

public sealed class UpsertCouponHandler(CatalogDbContext db, IValidator<UpsertCouponCommand> validator) : IRequestHandler<UpsertCouponCommand, Result<CouponDto>>
{
    public async Task<Result<CouponDto>> Handle(UpsertCouponCommand c, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(c, ct);
        if (!validation.IsValid) return Result.Fail<CouponDto>("validation", string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));

        var type = Enum.Parse<CouponType>(c.Type, true);
        var code = c.Code.Trim().ToUpperInvariant();
        if (c.Id.HasValue)
        {
            var coupon = await db.Coupons.FirstOrDefaultAsync(x => x.Id == c.Id.Value, ct) ?? throw new NotFoundException("Coupon", c.Id.Value);
            coupon.Code = code;
            coupon.Description = c.Description;
            coupon.Type = type;
            coupon.Value = c.Value;
            coupon.MinSubtotal = c.MinSubtotal;
            coupon.MaxDiscount = c.MaxDiscount;
            coupon.MaxUses = c.MaxUses;
            coupon.StartsAtUtc = c.StartsAtUtc;
            coupon.ExpiresAtUtc = c.ExpiresAtUtc;
            coupon.IsActive = c.IsActive;
            coupon.ModifiedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Result.Ok(ListCouponsHandler.ToDto(coupon));
        }

        if (await db.Coupons.AnyAsync(x => x.Code == code, ct))
            return Result.Fail<CouponDto>("duplicate_code", $"Coupon '{code}' already exists.");

        var created = new Coupon
        {
            Code = code,
            Description = c.Description,
            Type = type,
            Value = c.Value,
            MinSubtotal = c.MinSubtotal,
            MaxDiscount = c.MaxDiscount,
            MaxUses = c.MaxUses,
            StartsAtUtc = c.StartsAtUtc,
            ExpiresAtUtc = c.ExpiresAtUtc,
            IsActive = c.IsActive,
        };
        db.Coupons.Add(created);
        await db.SaveChangesAsync(ct);
        return Result.Ok(ListCouponsHandler.ToDto(created));
    }
}

public sealed record PatchCouponCommand(Guid Id, bool? IsActive) : IRequest<Result<CouponDto>>;

public sealed class PatchCouponHandler(CatalogDbContext db) : IRequestHandler<PatchCouponCommand, Result<CouponDto>>
{
    public async Task<Result<CouponDto>> Handle(PatchCouponCommand c, CancellationToken ct)
    {
        var coupon = await db.Coupons.FirstOrDefaultAsync(x => x.Id == c.Id, ct) ?? throw new NotFoundException("Coupon", c.Id);
        if (c.IsActive.HasValue) coupon.IsActive = c.IsActive.Value;
        coupon.ModifiedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Result.Ok(ListCouponsHandler.ToDto(coupon));
    }
}
