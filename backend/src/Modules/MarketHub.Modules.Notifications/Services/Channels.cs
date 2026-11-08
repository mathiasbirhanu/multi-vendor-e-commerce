using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MarketHub.Shared;
using MarketHub.Modules.Notifications.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MarketHub.Modules.Notifications.Services;

/// <summary>Result of one outbound send attempt (persisted on the OutboundMessage row by the dispatcher).</summary>
public sealed record OutboundSendResult(string Status, string? ProviderRef = null, string? Error = null)
{
    public static OutboundSendResult Sent(string? providerRef) => new("Sent", providerRef);
    public static OutboundSendResult Failed(string error) => new("Failed", null, error);
}

/// <summary>SMS channel. Production impls are real HTTP adapters; the mock returns a fake provider ref.</summary>
public interface ISmsSender
{
    string ProviderName { get; }
    Task<OutboundSendResult> SendSmsAsync(string phone, string template, string message, CancellationToken ct = default);
}

/// <summary>Email channel.</summary>
public interface IEmailSender
{
    string ProviderName { get; }
    Task<OutboundSendResult> SendEmailAsync(string to, string template, string subject, string htmlBody, CancellationToken ct = default);
}

// ==================== SMS PROVIDERS ====================

/// <summary>
/// Dev/default provider: no real HTTP — the dispatcher still writes an OutboundMessage row with
/// status Sent and a fake provider ref so the admin Ops view mirrors production behaviour.
/// </summary>
public sealed class MockSmsSender(ILogger<MockSmsSender> logger) : ISmsSender
{
    public string ProviderName => "Mock";

    public Task<OutboundSendResult> SendSmsAsync(string phone, string template, string message, CancellationToken ct = default)
    {
        var providerRef = $"MOCK-SMS-{Guid.NewGuid():N}"[..24];
        logger.LogInformation("[MOCK SMS] → {Phone} ({Template}): {Message}", phone, template, message);
        return Task.FromResult(OutboundSendResult.Sent(providerRef));
    }
}

/// <summary>
/// Ethio Telecom SMS bulk API. Credentials/endpoint are env-only
/// (EthioTelecom__Endpoint / ApiKey / SenderId). PRODUCTION TODO: verify the live API contract.
/// </summary>
public sealed class EthioTelecomSmsSender(IHttpClientFactory httpFactory, IConfiguration config) : ISmsSender
{
    public string ProviderName => "EthioTelecom";
    private string Endpoint => config["EthioTelecom:Endpoint"] ?? throw new GatewayNotConfiguredException(ProviderName);
    private string ApiKey => config["EthioTelecom:ApiKey"] ?? throw new GatewayNotConfiguredException(ProviderName);
    private string SenderId => config["EthioTelecom:SenderId"] ?? "MarketHub";

    public async Task<OutboundSendResult> SendSmsAsync(string phone, string template, string message, CancellationToken ct = default)
    {
        try
        {
            var client = httpFactory.CreateClient("ethio-telecom-sms");
            var payload = JsonSerializer.Serialize(new { apiKey = ApiKey, senderId = SenderId, to = phone, message, campaign = template });
            var res = await client.PostAsync(Endpoint, new StringContent(payload, Encoding.UTF8, "application/json"), ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                return OutboundSendResult.Failed($"EthioTelecom returned {(int)res.StatusCode}: {Truncate(raw)}");
            string? providerRef = null;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                providerRef = doc.RootElement.TryGetProperty("messageId", out var id) ? id.GetString()
                    : doc.RootElement.TryGetProperty("ref", out var rf) ? rf.GetString() : null;
            }
            catch (JsonException) { /* body shape is provider-specific; ref stays null */ }
            return OutboundSendResult.Sent(providerRef);
        }
        catch (GatewayNotConfiguredException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return OutboundSendResult.Failed($"EthioTelecom SMS failed: {ex.Message}");
        }
    }

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300];
}

/// <summary>
/// Safaricom Ethiopia SMS gateway. Credentials/endpoint are env-only
/// (Safaricom__Endpoint / ApiKey / Shortcode). PRODUCTION TODO: verify the live API contract.
/// </summary>
public sealed class SafaricomSmsSender(IHttpClientFactory httpFactory, IConfiguration config) : ISmsSender
{
    public string ProviderName => "Safaricom";
    private string Endpoint => config["Safaricom:Endpoint"] ?? throw new GatewayNotConfiguredException(ProviderName);
    private string ApiKey => config["Safaricom:ApiKey"] ?? throw new GatewayNotConfiguredException(ProviderName);
    private string Shortcode => config["Safaricom:Shortcode"] ?? "MARKETHUB";

    public async Task<OutboundSendResult> SendSmsAsync(string phone, string template, string message, CancellationToken ct = default)
    {
        try
        {
            var client = httpFactory.CreateClient("safaricom-sms");
            var payload = JsonSerializer.Serialize(new { apiKey = ApiKey, shortcode = Shortcode, recipient = phone, message, tag = template });
            var res = await client.PostAsync(Endpoint, new StringContent(payload, Encoding.UTF8, "application/json"), ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                return OutboundSendResult.Failed($"Safaricom returned {(int)res.StatusCode}: {Truncate(raw)}");
            string? providerRef = null;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                providerRef = doc.RootElement.TryGetProperty("messageId", out var id) ? id.GetString() : null;
            }
            catch (JsonException) { }
            return OutboundSendResult.Sent(providerRef);
        }
        catch (GatewayNotConfiguredException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return OutboundSendResult.Failed($"Safaricom SMS failed: {ex.Message}");
        }
    }

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300];
}

// ==================== EMAIL PROVIDERS ====================

/// <summary>Dev/default provider: no SMTP traffic — logs and returns a fake provider ref.</summary>
public sealed class MockEmailSender(ILogger<MockEmailSender> logger) : IEmailSender
{
    public string ProviderName => "Mock";

    public Task<OutboundSendResult> SendEmailAsync(string to, string template, string subject, string htmlBody, CancellationToken ct = default)
    {
        var providerRef = $"MOCK-EMAIL-{Guid.NewGuid():N}"[..26];
        logger.LogInformation("[MOCK EMAIL] → {To} ({Template}): {Subject}", to, template, subject);
        return Task.FromResult(OutboundSendResult.Sent(providerRef));
    }
}

/// <summary>Real SMTP sender built on MailKit (Smtp__Host/Port/User/Pass/From — credentials env-only).</summary>
public sealed class SmtpEmailSender(IConfiguration config, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public string ProviderName => "Smtp";
    private string Host => config["Smtp:Host"] ?? throw new GatewayNotConfiguredException(ProviderName);
    private int Port => int.TryParse(config["Smtp:Port"], out var p) ? p : 587;
    private string? User => config["Smtp:User"];
    private string? Pass => config["Smtp:Pass"];
    private string From => config["Smtp:From"] ?? "no-reply@markethub.et";

    public async Task<OutboundSendResult> SendEmailAsync(string to, string template, string subject, string htmlBody, CancellationToken ct = default)
    {
        try
        {
            var message = new MimeKit.MimeMessage();
            message.From.Add(MimeKit.MailboxAddress.Parse(From));
            message.To.Add(MimeKit.MailboxAddress.Parse(to));
            message.Subject = subject;
            message.Body = new MimeKit.BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

            using var client = new MailKit.Net.Smtp.SmtpClient();
            await client.ConnectAsync(Host, Port, MailKit.Security.SecureSocketOptions.StartTlsWhenAvailable, ct);
            if (!string.IsNullOrEmpty(User)) await client.AuthenticateAsync(User, Pass, ct);
            var response = await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
            logger.LogInformation("Email {Template} queued via SMTP {Host}:{Port}", template, Host, Port);
            return OutboundSendResult.Sent(response);
        }
        catch (GatewayNotConfiguredException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return OutboundSendResult.Failed($"SMTP send failed: {ex.Message}");
        }
    }
}
