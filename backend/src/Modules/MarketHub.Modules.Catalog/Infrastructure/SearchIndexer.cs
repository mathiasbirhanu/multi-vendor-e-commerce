using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MarketHub.Shared.Contracts;
using MarketHub.Modules.Catalog.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MarketHub.Modules.Catalog.Infrastructure;

/// <summary>
/// Real Meilisearch adapter. Index name = <c>{prefix}-products</c>; documents hold the searchable
/// product projection; typo tolerance is enabled explicitly. All calls are best-effort: failures are
/// logged and never break the calling use-case (search degrades to SQL ILIKE).
/// </summary>
public sealed class MeilisearchIndexer(HttpClient http, string host, string? apiKey, string indexPrefix) : ISearchIndexer
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private string IndexName => $"{indexPrefix}-products";
    private string Base => host.TrimEnd('/');

    private HttpRequestMessage Authenticated(HttpMethod method, string url, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, url) { Content = content };
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return req;
    }

    /// <summary>Creates/updates the index with typo-tolerant searchable attributes (safe to call repeatedly).</summary>
    public async Task EnsureIndexAsync(CancellationToken ct = default)
    {
        var settings = new
        {
            searchableAttributes = new[] { "title", "description", "category", "vendor" },
            filterableAttributes = new[] { "category", "vendor", "price" },
            sortableAttributes = new[] { "price", "rating", "sold" },
            typoTolerance = new { enabled = true, minWordSizeForTypos = new { oneTypo = 4, twoTypos = 8 } },
        };
        var res = await http.SendAsync(Authenticated(HttpMethod.Put, $"{Base}/indexes/{IndexName}/settings",
            new StringContent(JsonSerializer.Serialize(settings, Json), Encoding.UTF8, "application/json")), ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Meilisearch settings update returned {(int)res.StatusCode}");
    }

    public async Task IndexProductAsync(ProductSearchDoc doc, CancellationToken ct = default)
    {
        var res = await http.SendAsync(Authenticated(HttpMethod.Put, $"{Base}/indexes/{IndexName}/documents",
            new StringContent(JsonSerializer.Serialize(new[] { doc }, Json), Encoding.UTF8, "application/json")), ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Meilisearch document upsert returned {(int)res.StatusCode}");
    }

    public async Task RemoveAsync(Guid productId, CancellationToken ct = default)
    {
        using var req = Authenticated(HttpMethod.Delete, $"{Base}/indexes/{IndexName}/documents/{productId}");
        var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode && res.StatusCode != System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException($"Meilisearch document delete returned {(int)res.StatusCode}");
    }

    public async Task<IReadOnlyList<ProductSearchDoc>?> SearchAsync(string query, int limit, CancellationToken ct = default)
    {
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(new { q = query, limit }, Json), Encoding.UTF8, "application/json");
            var res = await http.PostAsync($"{Base}/indexes/{IndexName}/search", content, ct);
            if (!res.IsSuccessStatusCode) return null; // engine trouble → SQL fallback
            var payload = await res.Content.ReadFromJsonAsync<MeiliSearchResponse>(cancellationToken: ct);
            return payload?.Hits ?? Array.Empty<ProductSearchDoc>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // unreachable engine → SQL fallback
        }
    }

    private sealed record MeiliSearchResponse(IReadOnlyList<ProductSearchDoc>? Hits);
}

/// <summary>No-op indexer used when Search:Provider != Meilisearch — SearchAsync returns null so callers fall back to SQL.</summary>
public sealed class NullSearchIndexer : ISearchIndexer
{
    public Task IndexProductAsync(ProductSearchDoc doc, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveAsync(Guid productId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<ProductSearchDoc>?> SearchAsync(string query, int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ProductSearchDoc>?>(null);
}

/// <summary>
/// Fire-and-forget search sync from use-case code (product create/update/moderate/flash-sale).
/// Failures are logged, never thrown — the search engine is a read-path optimization, not a source of truth.
/// </summary>
public static class SearchIndexerBackground
{
    public static void IndexProduct(IServiceProvider sp, Guid productId)
        => Run(sp, "index", async (search, db, ct) =>
        {
            var p = await db.Products.AsNoTracking()
                .Include(x => x.Images)
                .FirstOrDefaultAsync(x => x.Id == productId, ct);
            if (p is null || p.Status != Domain.ProductStatus.Active) return; // archived/rejected products leave the index
            var category = await db.Categories.AsNoTracking().Where(c => c.Id == p.CategoryId).Select(c => c.Name).FirstOrDefaultAsync(ct);
            await search.IndexProductAsync(new ProductSearchDoc(
                p.Id, p.Title, p.Description, category ?? "", p.EffectivePriceUtc(DateTime.UtcNow),
                p.VendorNameSnapshot, p.RatingAvg, p.SoldCount, p.Slug,
                p.Images.OrderBy(i => i.SortOrder).Select(i => i.PublicUrl).FirstOrDefault()), ct);
        });

    public static void RemoveProduct(IServiceProvider sp, Guid productId)
        => Run(sp, "remove", async (search, _, ct) => await search.RemoveAsync(productId, ct));

    private static void Run(IServiceProvider sp, string operation, Func<ISearchIndexer, CatalogDbContext, CancellationToken, Task> action)
    {
        _ = Task.Run(async () =>
        {
            var scope = sp.CreateScope();
            try
            {
                var search = scope.ServiceProvider.GetRequiredService<ISearchIndexer>();
                var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
                await action(search, db, CancellationToken.None);
            }
            catch (Exception ex)
            {
                scope.ServiceProvider.GetRequiredService<ILogger<MeilisearchIndexer>>()
                    .LogWarning(ex, "Search index {Operation} failed (fire-and-forget)", operation);
            }
            finally { scope.Dispose(); }
        });
    }
}
