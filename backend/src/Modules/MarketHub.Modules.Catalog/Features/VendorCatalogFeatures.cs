using System.Globalization;
using MarketHub.Shared;
using MarketHub.Modules.Catalog.Domain;
using MarketHub.Modules.Catalog.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace MarketHub.Modules.Catalog.Features;

// ==================== VENDOR VARIANT EDITOR ====================
public sealed record PatchVariantCommand(Guid VendorId, Guid VariantId, decimal? PriceETB, int? Stock, bool? IsActive) : IRequest<Result<ProductVariantDto>>;

public sealed class PatchVariantHandler(CatalogDbContext db) : IRequestHandler<PatchVariantCommand, Result<ProductVariantDto>>
{
    public async Task<Result<ProductVariantDto>> Handle(PatchVariantCommand c, CancellationToken ct)
    {
        var variant = await db.ProductVariants.FirstOrDefaultAsync(v => v.Id == c.VariantId, ct)
            ?? throw new NotFoundException("ProductVariant", c.VariantId);
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == variant.ProductId, ct)
            ?? throw new NotFoundException("Product", variant.ProductId);
        if (product.VendorId != c.VendorId) throw new NotFoundException("ProductVariant", c.VariantId); // ownership enforced

        if (c.PriceETB.HasValue)
        {
            if (c.PriceETB.Value < 0) return Result.Fail<ProductVariantDto>("validation", "Price cannot be negative.");
            variant.PriceETB = c.PriceETB.Value == 0m ? null : c.PriceETB.Value; // 0 → fall back to base price
        }
        if (c.Stock.HasValue)
        {
            if (c.Stock.Value < 0) return Result.Fail<ProductVariantDto>("validation", "Stock cannot be negative.");
            variant.Stock = c.Stock.Value;
        }
        if (c.IsActive.HasValue) variant.IsActive = c.IsActive.Value;
        variant.ModifiedAtUtc = DateTime.UtcNow;

        // product stock stays the denormalized Σ variant stock whenever the product has variants
        if (await db.ProductVariants.AnyAsync(v => v.ProductId == product.Id, ct))
        {
            product.Stock = await db.ProductVariants.Where(v => v.ProductId == product.Id).SumAsync(v => v.Stock, ct);
            product.ModifiedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);

        ProductColor? color = variant.ColorId.HasValue
            ? await db.ProductColors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == variant.ColorId.Value, ct)
            : null;
        return Result.Ok(new ProductVariantDto(variant.Id, variant.SKU, variant.ColorId, color?.Hex, variant.Size,
            variant.PriceETB, variant.Stock, variant.IsActive, variant.SortOrder, variant.Label(color?.Name)));
    }
}

// ==================== VENDOR BULK CSV UPLOAD ====================
public sealed record BulkUploadCommand(Guid VendorId, string VendorName, string Csv) : IRequest<Result<BulkUploadResultDto>>;
public sealed record BulkUploadResultDto(int Created, IReadOnlyList<string> Errors);

/// <summary>
/// CSV bulk listing upload. Header: <c>title,description,priceETB,stock,categorySlug,sku</c>.
/// Each row creates a PendingReview product with one default variant; row-level problems are reported (never fatal).
/// </summary>
public sealed class BulkUploadHandler(CatalogDbContext db, IMarketplaceVendorChecker vendorChecker) : IRequestHandler<BulkUploadCommand, Result<BulkUploadResultDto>>
{
    public async Task<Result<BulkUploadResultDto>> Handle(BulkUploadCommand c, CancellationToken ct)
    {
        if (!await vendorChecker.IsApprovedVendorAsync(c.VendorId, ct))
            return Result.Fail<BulkUploadResultDto>("vendor_not_approved", "Only approved vendors can upload listings.");

        var categories = await db.Categories.ToDictionaryAsync(x => x.Slug, x => x.Id, ct);
        var lines = (c.Csv ?? string.Empty).Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return Result.Fail<BulkUploadResultDto>("csv", "The CSV payload is empty.");
        if (lines.Length > 200) return Result.Fail<BulkUploadResultDto>("csv", "At most 200 rows per upload.");

        var header = ParseRow(lines[0]);
        var normalized = header.Select(h => h.Trim().ToLowerInvariant().Replace("_", "")).ToArray();
        var required = new[] { "title", "description", "priceetb", "stock", "categoryslug", "sku" };
        if (!required.All(required_ => normalized.Contains(required_)))
            return Result.Fail<BulkUploadResultDto>("csv", "Header must be: title,description,priceETB,stock,categorySlug,sku");

        var created = 0;
        var errors = new List<string>();
        var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++) idx[header[i].Trim()] = i;

        for (var row = 1; row < lines.Length; row++)
        {
            var cells = ParseRow(lines[row]);
            string Col(string name) => idx.TryGetValue(name, out var i) && i < cells.Length ? cells[i].Trim() : "";
            try
            {
                var title = Col("title");
                var description = Col("description");
                var price = decimal.Parse(Col("priceetb"), CultureInfo.InvariantCulture);
                var stock = int.Parse(Col("stock"), CultureInfo.InvariantCulture);
                var categorySlug = Col("categoryslug");
                var sku = Col("sku");
                if (!string.IsNullOrWhiteSpace(sku) && await db.ProductVariants.AnyAsync(v => v.SKU == sku, ct))
                    sku = $"MHV-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}"; // unique index guard

                if (title.Length == 0 || title.Length > 220) throw new DomainException("title is required (max 220 chars).");
                if (description.Length == 0) throw new DomainException("description is required.");
                if (price <= 0) throw new DomainException("priceETB must be greater than zero.");
                if (stock < 0) throw new DomainException("stock cannot be negative.");
                if (!categories.TryGetValue(categorySlug, out var categoryId)) throw new DomainException($"unknown categorySlug '{categorySlug}'.");

                var product = new Product
                {
                    VendorId = c.VendorId,
                    VendorNameSnapshot = string.IsNullOrWhiteSpace(c.VendorName) ? null : c.VendorName,
                    CategoryId = categoryId,
                    Title = title,
                    Slug = BuildSlug(title),
                    Description = description,
                    PriceETB = price,
                    Stock = stock,
                    WeightKg = 1m,
                    SKU = $"MH-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                    Status = ProductStatus.PendingReview, // bulk uploads go through the same moderation queue
                    IsFeatured = false,
                };
                db.Products.Add(product);
                db.ProductVariants.Add(new ProductVariant
                {
                    ProductId = product.Id,
                    SKU = string.IsNullOrWhiteSpace(sku) ? $"MHV-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}" : sku,
                    ColorId = null,
                    Size = null,
                    PriceETB = null, // default variant sells at the base price
                    Stock = stock,
                    IsActive = true,
                    SortOrder = 0,
                });
                created++;
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or DomainException)
            {
                errors.Add($"row {row + 1}: {ex.Message}");
            }
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok(new BulkUploadResultDto(created, errors));
    }

    private static string[] ParseRow(string line)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else inQuotes = !inQuotes;
            }
            else if (ch == ',' && !inQuotes) { cells.Add(current.ToString()); current.Clear(); }
            else current.Append(ch);
        }
        cells.Add(current.ToString());
        return cells.ToArray();
    }

    private static string BuildSlug(string title)
    {
        var slug = System.Text.RegularExpressions.Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 60) slug = slug[..60];
        return $"{slug}-{Guid.NewGuid().ToString("N")[..6]}";
    }
}
