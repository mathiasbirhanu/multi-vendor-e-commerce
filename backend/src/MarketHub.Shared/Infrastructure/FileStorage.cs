namespace MarketHub.Shared.Infrastructure;

/// <summary>Object-storage abstraction for vendor KYC documents and product media.</summary>
public interface IFileStorage
{
    /// <summary>Stores content and returns the storage key.</summary>
    Task<string> SaveAsync(string fileName, string contentType, byte[] content, CancellationToken ct = default);
    /// <summary>Reads back content by storage key (admin review only — keys are never public URLs).</summary>
    Task<byte[]> ReadAsync(string storageKey, CancellationToken ct = default);
}

/// <summary>Local-disk storage for development. Content is stored under a private root with GUID names.</summary>
public sealed class LocalFileStorage : IFileStorage
{
    private readonly string _root;
    public LocalFileStorage(Microsoft.Extensions.Configuration.IConfiguration config)
        => _root = config["FileStorage:LocalRoot"] ?? "filestorage";

    public async Task<string> SaveAsync(string fileName, string contentType, byte[] content, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root);
        var ext = Path.GetExtension(fileName);
        var key = $"{Guid.NewGuid():N}{ext}";
        await File.WriteAllBytesAsync(Path.Combine(_root, key), content, ct);
        return key;
    }

    public Task<byte[]> ReadAsync(string storageKey, CancellationToken ct = default)
    {
        var path = Path.Combine(_root, Path.GetFileName(storageKey)); // no traversal
        return File.ReadAllBytesAsync(path, ct);
    }
}

// PRODUCTION TODO: register MinioFileStorage (FileStorage__Provider=Minio) with Minio__Endpoint/AccessKey/SecretKey/Bucket.
// Use private buckets + short-lived pre-signed URLs for admin document review; never expose raw keys publicly.

/// <summary>Platform settings accessor — backed by the Finance module's settings table at runtime.</summary>
public interface ISettingsProvider
{
    Task<T> GetValueAsync<T>(string key, T defaultValue, CancellationToken ct = default);
    Task<string?> GetRawAsync(string key, CancellationToken ct = default);
}

/// <summary>Per-module seeder invoked by the API's DatabaseSeeder at startup (dev only). Must be idempotent.</summary>
public interface IModuleSeeder
{
    string ModuleName { get; }
    Task SeedAsync(IServiceProvider scopedProvider, CancellationToken ct);
}

/// <summary>Known settings keys.</summary>
public static class SettingKeys
{
    public const string VatRate = "VatRate";                         // 15
    public const string DefaultCommissionRate = "DefaultCommissionRate"; // 0.10
    public const string ReturnWindowDays = "ReturnWindowDays";       // 7
    public const string PaymentProvider = "PaymentProvider";         // Mock | Chapa | Telebirr | Santimpay
    public const string PayoutSlaHours = "PayoutSlaHours";           // 12
    public const string PlatformName = "PlatformName";               // MarketHub

    // ---- gap-fill v2 keys ----
    public const string ReservationMinutes = "ReservationMinutes";   // 20 — stock reservation TTL
    public const string AbandonedCartHours = "AbandonedCartHours";   // 2  — cart idle threshold before reminder
    public const string SmsProvider = "SmsProvider";                 // Mock | EthioTelecom | Safaricom
    public const string EmailProvider = "EmailProvider";             // Mock | Smtp
    public const string SearchProvider = "SearchProvider";           // Meilisearch | Null
    public const string FreeDeliveryAddisThreshold = "FreeDeliveryAddisThreshold"; // 1000

    /// <summary>Keys accepted by the admin settings endpoints (PUT validates against this set).</summary>
    public static readonly IReadOnlyList<string> KnownKeys = new[]
    {
        VatRate, DefaultCommissionRate, ReturnWindowDays, PaymentProvider, PayoutSlaHours, PlatformName,
        ReservationMinutes, AbandonedCartHours, SmsProvider, EmailProvider, SearchProvider, FreeDeliveryAddisThreshold,
    };
}
