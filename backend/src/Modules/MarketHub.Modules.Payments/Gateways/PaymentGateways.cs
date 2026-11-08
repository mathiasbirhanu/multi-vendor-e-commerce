using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketHub.Shared;

namespace MarketHub.Modules.Payments.Gateways;

/// <summary>Request to start a payment at a provider.</summary>
public sealed record GatewayInitiateRequest(Guid OrderId, string OrderNumber, decimal AmountETB, string Description, string BuyerEmail, string CallbackBaseUrl);
public sealed record GatewayInitiateResult(string ProviderRef, string RedirectUrl, string? RawResponse);
public sealed record GatewayVerifyResult(bool Paid, string? RawResponse);

/// <summary>Uniform provider contract: initiate → redirect, async webhook, verification. All providers implement this.</summary>
public interface IPaymentGateway
{
    string ProviderName { get; }
    Task<GatewayInitiateResult> InitiateAsync(GatewayInitiateRequest request, CancellationToken ct = default);
    Task<GatewayVerifyResult> VerifyAsync(string providerRef, CancellationToken ct = default);
    bool ValidateWebhookSignature(string rawBody, string signatureHeader);
}

/// <summary>Standard webhook payload shape accepted by HandleWebhookCommand for every provider.</summary>
public sealed record WebhookPayload(string ProviderRef, string Status, decimal AmountETB, Guid? EventId);

/// <summary>Active-gateway factory — selection comes from the Settings table (key PaymentProvider).</summary>
public sealed class PaymentGatewayFactory(IServiceProvider sp, IEnumerable<IPaymentGateway> gateways, MarketHub.Shared.ISettingsProvider settings)
{
    public async Task<IPaymentGateway> GetActiveAsync(CancellationToken ct = default)
    {
        var name = await settings.GetRawAsync(MarketHub.Shared.SettingKeys.PaymentProvider, ct) ?? "Mock";
        return gateways.FirstOrDefault(g => string.Equals(g.ProviderName, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new GatewayNotConfiguredException($"No gateway registered for configured provider '{name}'.");
    }

    /// <summary>Resolves a gateway by provider name (used by the webhook endpoint to verify signatures).</summary>
    public IPaymentGateway GetByName(string providerName)
        => gateways.FirstOrDefault(g => string.Equals(g.ProviderName, providerName, StringComparison.OrdinalIgnoreCase))
           ?? throw new GatewayNotConfiguredException($"No gateway registered for provider '{providerName}'.");
}

/// <summary>
/// Development gateway implementing the FULL production lifecycle: initiate → hosted page redirect →
/// signed webhook → idempotent confirmation. Switch providers by changing Settings.PaymentProvider —
/// no checkout code changes required.
/// </summary>
public sealed class MockGateway : IPaymentGateway
{
    public const string WebhookSecret = "mock-secret-dev"; // dev-only secret, mirrored by the sandbox preview
    public string ProviderName => "Mock";

    public Task<GatewayInitiateResult> InitiateAsync(GatewayInitiateRequest request, CancellationToken ct = default)
    {
        var providerRef = $"MOCK-{request.OrderNumber}";
        return Task.FromResult(new GatewayInitiateResult(providerRef, $"{request.CallbackBaseUrl}/mock-pay/{providerRef}", null));
    }

    public Task<GatewayVerifyResult> VerifyAsync(string providerRef, CancellationToken ct = default)
        => Task.FromResult(new GatewayVerifyResult(providerRef.StartsWith("MOCK-", StringComparison.Ordinal), null));

    public bool ValidateWebhookSignature(string rawBody, string signatureHeader)
    {
        var expected = "mock-" + Convert.ToHexString(HMAC(rawBody, WebhookSecret)).ToLowerInvariant();
        return string.Equals(expected, signatureHeader, StringComparison.Ordinal);
    }

    public static string Sign(string rawBody) => "mock-" + Convert.ToHexString(HMAC(rawBody, WebhookSecret)).ToLowerInvariant();

    internal static byte[] HMAC(string body, string secret) => new HMACSHA256(Encoding.UTF8.GetBytes(secret)).ComputeHash(Encoding.UTF8.GetBytes(body));
}

/// <summary>
/// Chapa (https://chapa.co) — Ethiopian aggregator supporting telebirr, CBE Birr, cards and banks.
/// PRODUCTION TODO: verify live field names against current Chapa docs before go-live; keys from env only.
/// </summary>
public sealed class ChapaGateway(IHttpClientFactory httpFactory, IConfiguration config) : IPaymentGateway
{
    public string ProviderName => "Chapa";
    private string ApiKey => config["Chapa:ApiKey"] ?? throw new GatewayNotConfiguredException(ProviderName);
    private string WebhookSecret => config["Chapa:WebhookSecret"] ?? throw new GatewayNotConfiguredException(ProviderName);

    public async Task<GatewayInitiateResult> InitiateAsync(GatewayInitiateRequest request, CancellationToken ct = default)
    {
        var client = httpFactory.CreateClient("chapa");
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {ApiKey}");
        var body = JsonSerializer.Serialize(new
        {
            amount = request.AmountETB,
            currency = "ETB",
            tx_ref = $"CHAPA-{request.OrderNumber}",
            callback_url = $"{request.CallbackBaseUrl}/api/payments/webhook/chapa",
            customization = new { description = request.Description },
        });
        var res = await client.PostAsync("https://api.chapa.co/v1/transaction/initialize", new StringContent(body, Encoding.UTF8, "application/json"), ct);
        var raw = await res.Content.ReadAsStringAsync(ct);
        return new GatewayInitiateResult($"CHAPA-{request.OrderNumber}", raw, raw);
    }

    public async Task<GatewayVerifyResult> VerifyAsync(string providerRef, CancellationToken ct = default)
    {
        var client = httpFactory.CreateClient("chapa");
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {ApiKey}");
        var res = await client.GetAsync($"https://api.chapa.co/v1/transaction/verify/{providerRef}", ct);
        return new GatewayVerifyResult(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync(ct));
    }

    public bool ValidateWebhookSignature(string rawBody, string signatureHeader)
    {
        var expected = Convert.ToHexString(MockGateway.HMAC(rawBody, WebhookSecret)).ToLowerInvariant(); // HMAC-SHA256 hex of raw body
        return string.Equals(expected, signatureHeader, StringComparison.Ordinal);
    }
}

/// <summary>
/// telebirr (Ethio Telecom) wallet — H5/app payment via Ethio Telecom open API.
/// PRODUCTION TODO: register app keys with Ethio Telecom, implement USSD/URL push + notify callback per current docs.
/// </summary>
public sealed class TelebirrGateway(IHttpClientFactory httpFactory, IConfiguration config) : IPaymentGateway
{
    public string ProviderName => "Telebirr";
    private string AppId => config["Telebirr:AppId"] ?? throw new GatewayNotConfiguredException(ProviderName);
    private string AppKey => config["Telebirr:AppKey"] ?? throw new GatewayNotConfiguredException(ProviderName);
    private string PublicKey => config["Telebirr:PublicKey"] ?? throw new GatewayNotConfiguredException(ProviderName);

    public async Task<GatewayInitiateResult> InitiateAsync(GatewayInitiateRequest request, CancellationToken ct = default)
    {
        var client = httpFactory.CreateClient("telebirr");
        // PRODUCTION TODO: build the RSA-encrypted payment request payload per the telebirr openAPI spec and
        // return the returned receiveUrl to the client. Placeholder keeps the contract stable.
        var body = JsonSerializer.Serialize(new { appId = AppId, appKey = AppKey, outTradeNo = $"TB-{request.OrderNumber}", totalAmount = request.AmountETB, notifyUrl = $"{request.CallbackBaseUrl}/api/payments/webhook/telebirr" });
        await Task.Delay(0, ct);
        var raw = JsonSerializer.Serialize(new { todo = "implement per telebirr openAPI", request = body, publicKeyThumbprint = PublicKey[..8] });
        return new GatewayInitiateResult($"TB-{request.OrderNumber}", raw, raw);
    }

    public Task<GatewayVerifyResult> VerifyAsync(string providerRef, CancellationToken ct = default)
        => Task.FromResult(new GatewayVerifyResult(false, "PRODUCTION TODO: verify via telebirr transaction query API"));

    public bool ValidateWebhookSignature(string rawBody, string signatureHeader)
    {
        // PRODUCTION TODO: telebirr signs notifications with the registered public key — verify RSA signature here.
        var expected = Convert.ToHexString(MockGateway.HMAC(rawBody, AppKey)).ToLowerInvariant();
        return string.Equals(expected, signatureHeader, StringComparison.Ordinal);
    }
}

/// <summary>
/// SantimPay — Ethiopian aggregator (wallet + bank transfer + USSD push).
/// PRODUCTION TODO: confirm generate-payment-link / verify endpoints against current SantimPay docs.
/// </summary>
public sealed class SantimpayGateway(IHttpClientFactory httpFactory, IConfiguration config) : IPaymentGateway
{
    public string ProviderName => "Santimpay";
    private string MerchantId => config["Santimpay:MerchantId"] ?? throw new GatewayNotConfiguredException(ProviderName);
    private string ApiKey => config["Santimpay:ApiKey"] ?? throw new GatewayNotConfiguredException(ProviderName);

    public async Task<GatewayInitiateResult> InitiateAsync(GatewayInitiateRequest request, CancellationToken ct = default)
    {
        var client = httpFactory.CreateClient("santimpay");
        var body = JsonSerializer.Serialize(new
        {
            merchantId = MerchantId,
            amount = request.AmountETB,
            referenceNumber = $"SP-{request.OrderNumber}",
            description = request.Description,
            notifyUrl = $"{request.CallbackBaseUrl}/api/payments/webhook/santimpay",
        });
        // PRODUCTION TODO: hit the real SantimPay generate-payment-link endpoint and return its URL.
        var raw = JsonSerializer.Serialize(new { todo = "implement per SantimPay docs", request = body });
        await Task.Delay(0, ct);
        return new GatewayInitiateResult($"SP-{request.OrderNumber}", raw, raw);
    }

    public Task<GatewayVerifyResult> VerifyAsync(string providerRef, CancellationToken ct = default)
        => Task.FromResult(new GatewayVerifyResult(false, "PRODUCTION TODO: verify via SantimPay transaction status API"));

    public bool ValidateWebhookSignature(string rawBody, string signatureHeader)
    {
        var expected = Convert.ToHexString(MockGateway.HMAC(rawBody, ApiKey)).ToLowerInvariant();
        return string.Equals(expected, signatureHeader, StringComparison.Ordinal);
    }
}
