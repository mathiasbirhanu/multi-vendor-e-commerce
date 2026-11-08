using System.Text.Json;
using MarketHub.Shared.Contracts;
using MarketHub.Shared.Contracts.Events;
using MarketHub.Shared.Infrastructure;
using MarketHub.Modules.Notifications.Domain;
using MarketHub.Modules.Notifications.Persistence;
using MarketHub.Modules.Notifications.Services;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace MarketHub.Modules.Notifications.Features;

/// <summary>One dispatchable message. Critical templates also go out via SMS/Email when the user has a destination.</summary>
public sealed record NotificationMessage(Guid UserId, string Template, string Subject, string Body, bool Critical = true);

/// <summary>
/// Central dispatcher: ALWAYS writes the in-app notification; for critical templates it also sends
/// SMS + Email when the user has a destination on file and records an <see cref="OutboundMessage"/>
/// audit row for every channel attempt (Sent / Failed / Skipped) — nothing leaves silently.
/// </summary>
public interface INotificationDispatcher
{
    Task DispatchAsync(NotificationMessage message, CancellationToken ct = default);
}

public sealed class NotificationDispatcher(
    NotificationsDbContext db,
    INotificationSender inApp,
    ISmsSender sms,
    IEmailSender email,
    IUserDirectory users,
    ILogger<NotificationDispatcher> logger) : INotificationDispatcher
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task DispatchAsync(NotificationMessage message, CancellationToken ct = default)
    {
        // 1) in-app (always, even for unknown/anonymous ids the bell simply won't show it)
        await inApp.SendAsync(message.UserId, NotificationChannel.InApp, message.Template, message.Subject, message.Body, ct);

        if (!message.Critical || message.UserId == Guid.Empty) return;

        // 2) critical templates → SMS/Email when a destination exists
        var (emailAddress, phone, _) = await users.GetContactAsync(message.UserId, ct);
        var payload = JsonSerializer.Serialize(new { message.UserId, message.Template, message.Subject }, Json);

        if (!string.IsNullOrWhiteSpace(phone))
        {
            await SendAndRecordAsync(OutboundChannel.Sms, message, phone, payload,
                async token => await sms.SendSmsAsync(phone, message.Template, message.Body, token), ct);
        }
        else
        {
            Record(OutboundChannel.Sms, message, "(no phone on file)", payload, OutboundStatus.Skipped, provider: sms.ProviderName);
        }

        if (!string.IsNullOrWhiteSpace(emailAddress))
        {
            await SendAndRecordAsync(OutboundChannel.Email, message, emailAddress, payload,
                async token => await email.SendEmailAsync(emailAddress, message.Template, message.Subject, $"<p>{message.Body}</p>", token), ct);
        }
        else
        {
            Record(OutboundChannel.Email, message, "(no email on file)", payload, OutboundStatus.Skipped, provider: email.ProviderName);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task SendAndRecordAsync(OutboundChannel channel, NotificationMessage message, string destination, string payload,
        Func<CancellationToken, Task<OutboundSendResult>> send, CancellationToken ct)
    {
        OutboundSendResult result;
        try
        {
            result = await send(ct);
        }
        catch (GatewayNotConfiguredException ex)
        {
            logger.LogWarning("{Channel} provider not configured for template {Template}: {Message}", channel, message.Template, ex.Message);
            Record(channel, message, destination, payload, OutboundStatus.Skipped, provider: ProviderFor(channel), error: ex.Message);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{Channel} send failed for template {Template}", channel, message.Template);
            Record(channel, message, destination, payload, OutboundStatus.Failed, provider: ProviderFor(channel), error: ex.Message);
            return;
        }

        var status = result.Status.Equals("Sent", StringComparison.OrdinalIgnoreCase) ? OutboundStatus.Sent : OutboundStatus.Failed;
        Record(channel, message, destination, payload, status, provider: ProviderFor(channel), providerRef: result.ProviderRef, error: result.Error);
    }

    private string ProviderFor(OutboundChannel channel) => channel == OutboundChannel.Sms ? sms.ProviderName : email.ProviderName;

    private void Record(OutboundChannel channel, NotificationMessage message, string destination, string payload,
        OutboundStatus status, string provider, string? providerRef = null, string? error = null)
    {
        db.OutboundMessages.Add(new OutboundMessage
        {
            UserId = message.UserId == Guid.Empty ? null : message.UserId,
            Channel = channel,
            Template = message.Template,
            Destination = destination,
            PayloadJson = payload,
            Provider = provider,
            ProviderRef = providerRef,
            Status = status,
            Error = error,
            SentAtUtc = status == OutboundStatus.Sent ? DateTime.UtcNow : null,
        });
    }
}

// ---- in-app sender (unchanged dev behaviour: writes rows consumed by the storefront bell) ----
public sealed class MockNotificationSender(NotificationsDbContext db, ILogger<MockNotificationSender> logger) : INotificationSender
{
    public async Task SendAsync(Guid userId, NotificationChannel channel, string type, string subject, string body, CancellationToken ct = default)
    {
        if (userId == Guid.Empty) return; // best-effort: never write bell rows for anonymous ids
        db.NotificationLogs.Add(new NotificationLog { UserId = userId, Channel = channel, Type = type, Subject = subject, Body = body });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Notification [{Type}] → {UserId}: {Subject}", type, userId, subject);
    }
}

// ---- domain event handlers (published via the outbox) ----
public sealed class VendorApprovedHandler(INotificationDispatcher dispatcher) : INotificationHandler<VendorApprovedEvent>
{
    public Task Handle(VendorApprovedEvent e, CancellationToken ct)
        => dispatcher.DispatchAsync(new NotificationMessage(e.VendorId, NotificationTemplates.VendorApproved,
            "Application approved 🎉",
            $"Congratulations! {e.BusinessName} is now an approved MarketHub vendor. You can start listing products."), ct);
}

public sealed class VendorRejectedHandler(INotificationSender sender) : INotificationHandler<VendorRejectedEvent>
{
    public Task Handle(VendorRejectedEvent e, CancellationToken ct)
        => sender.SendAsync(e.VendorId, NotificationChannel.InApp, "VendorRejected", "Application rejected", $"Your vendor application was rejected: {e.Reason}. You may correct your documents and re-apply.", ct);
}

public sealed class VendorSuspendedHandler(INotificationSender sender) : INotificationHandler<VendorSuspendedEvent>
{
    public Task Handle(VendorSuspendedEvent e, CancellationToken ct)
        => sender.SendAsync(e.VendorId, NotificationChannel.InApp, "VendorSuspended", "Store suspended", $"Your store was suspended: {e.Reason}", ct);
}

public sealed class OrderPaymentConfirmedHandler(IOrderReader orders, INotificationDispatcher dispatcher)
    : INotificationHandler<OrderPaymentConfirmedEvent>
{
    public async Task Handle(OrderPaymentConfirmedEvent e, CancellationToken ct)
    {
        if (e.BuyerId != Guid.Empty)
        {
            await dispatcher.DispatchAsync(new NotificationMessage(e.BuyerId, NotificationTemplates.OrderConfirmed,
                "Payment confirmed", $"Payment of {e.GrandTotalETB:0.##} Br was confirmed and your order is being prepared."), ct);
        }

        // SMS every vendor with lines on the paid order (12h SLA applies from now)
        var financials = await orders.GetFinancialsAsync(e.OrderId, ct);
        if (financials is null) return;
        foreach (var vendorId in financials.Items.Where(i => i.VendorId.HasValue).Select(i => i.VendorId!.Value).Distinct())
        {
            await dispatcher.DispatchAsync(new NotificationMessage(vendorId, NotificationTemplates.OrderConfirmed,
                "New paid order", "You have a new paid order — accept it within 24 hours to keep your fulfilment score.", Critical: false), ct);
        }
    }
}

public sealed class OrderShippedHandler(INotificationDispatcher dispatcher) : INotificationHandler<OrderShippedEvent>
{
    public Task Handle(OrderShippedEvent e, CancellationToken ct)
        => dispatcher.DispatchAsync(new NotificationMessage(e.BuyerId, NotificationTemplates.OrderShipped,
            "Order shipped 🚚",
            string.IsNullOrWhiteSpace(e.TrackingCode)
                ? "Your order has shipped — track it in My Orders."
                : $"Your order has shipped. Tracking code: {e.TrackingCode}."), ct);
}

public sealed class OrderDeliveredHandler(INotificationDispatcher dispatcher) : INotificationHandler<OrderDeliveredEvent>
{
    public Task Handle(OrderDeliveredEvent e, CancellationToken ct)
        => dispatcher.DispatchAsync(new NotificationMessage(e.BuyerId, NotificationTemplates.OrderDelivered,
            "Order delivered ✓",
            "Your order was delivered (PIN verified). Enjoy — and tell others what you think with a review!"), ct);
}

public sealed class OrderCancelledHandler(INotificationSender sender, IOrderReader orders) : INotificationHandler<OrderCancelledEvent>
{
    public async Task Handle(OrderCancelledEvent e, CancellationToken ct)
    {
        var financials = await orders.GetFinancialsAsync(e.OrderId, ct);
        // vendor lines (if any) learn about the cancellation; the buyer cancelled it themselves
        var vendorIds = financials?.Items.Where(i => i.VendorId.HasValue).Select(i => i.VendorId!.Value).Distinct().ToList() ?? new List<Guid>();
        foreach (var vendorId in vendorIds)
        {
            await sender.SendAsync(vendorId, NotificationChannel.InApp, "OrderCancelled",
                $"Order {e.OrderNumber} cancelled", "The buyer cancelled this order before payment was captured.", ct);
        }
    }
}

public sealed class RefundDecidedHandler(IOrderReader orders, INotificationSender sender) : INotificationHandler<RefundDecidedEvent>
{
    public async Task Handle(RefundDecidedEvent e, CancellationToken ct)
    {
        var financials = await orders.GetFinancialsAsync(e.OrderId, ct);
        var buyerId = financials?.BuyerId ?? Guid.Empty;
        await sender.SendAsync(buyerId, NotificationChannel.InApp, "RefundDecided",
            e.Approved ? "Refund approved ✓" : "Refund request rejected",
            e.Approved ? $"Your refund of {e.Amount:0.##} Br was approved — it arrives within 5 business days." : "Your refund request was rejected after inspection. You may escalate within 7 days.", ct);
    }
}

public sealed class PayoutProcessedHandler(INotificationDispatcher dispatcher) : INotificationHandler<PayoutProcessedEvent>
{
    public Task Handle(PayoutProcessedEvent e, CancellationToken ct)
        => dispatcher.DispatchAsync(new NotificationMessage(e.VendorId, NotificationTemplates.PayoutProcessed,
            e.IsPaid ? "Payout sent ✓" : "Payout request rejected",
            e.IsPaid ? $"Your payout of {e.AmountETB:0.##} Br has been sent." : "Your payout request was rejected — the amount is back in your available balance."), ct);
}

public sealed class ReturnRequestedHandler(INotificationSender sender) : INotificationHandler<ReturnRequestedEvent>
{
    public async Task Handle(ReturnRequestedEvent e, CancellationToken ct)
    {
        // vendor queue
        if (e.VendorId.HasValue)
            await sender.SendAsync(e.VendorId.Value, NotificationChannel.InApp, "ReturnRequested",
                "New return request", $"A buyer opened a return ({e.Reason}). Respond within 48 hours.", ct);
        // admin awareness (in-app only; final arbitration is manual)
        // (admin users poll the admin returns queue — no per-admin fan-out here to stay lean)
    }
}

public sealed class ReturnDecidedHandler(INotificationDispatcher dispatcher) : INotificationHandler<ReturnDecidedEvent>
{
    public Task Handle(ReturnDecidedEvent e, CancellationToken ct)
        => dispatcher.DispatchAsync(new NotificationMessage(e.BuyerId, NotificationTemplates.ReturnDecided,
            e.Approved ? "Return approved ✓" : "Return update",
            e.Approved
                ? $"Your return was approved — a refund of {e.RefundETB:0.##} Br is on its way back to your payment method."
                : $"Your return request was rejected: {e.Note ?? "after inspection"}. You may escalate to MarketHub support within 7 days."), ct);
}

public sealed class ReviewCreatedHandler(INotificationSender sender) : INotificationHandler<ReviewCreatedEvent>
{
    public Task Handle(ReviewCreatedEvent e, CancellationToken ct)
        => e.VendorId is null || e.VendorId == Guid.Empty
            ? Task.CompletedTask
            : sender.SendAsync(e.VendorId.Value, NotificationChannel.InApp, "ReviewPending",
                "New review awaiting moderation", $"A buyer rated one of your listings {e.Rating}/5. It appears publicly once approved.", ct);
}

public sealed class ReviewModeratedHandler(INotificationSender sender) : INotificationHandler<ReviewModeratedEvent>
{
    public Task Handle(ReviewModeratedEvent e, CancellationToken ct)
        => sender.SendAsync(e.BuyerId, NotificationChannel.InApp, "ReviewModerated",
            e.Approved ? "Review published ✓" : "Review not published",
            e.Approved ? "Your review is now live on the product page. Thank you!" : "Your review was not approved by our moderation team.", ct);
}

public sealed class ReviewRepliedHandler(INotificationSender sender) : INotificationHandler<ReviewRepliedEvent>
{
    public Task Handle(ReviewRepliedEvent e, CancellationToken ct)
        => sender.SendAsync(e.BuyerId, NotificationChannel.InApp, "ReviewReplied",
            "The seller replied to your review", e.Reply, ct);
}

// ==================== ABANDONED-CART REMINDER ====================
/// <summary>
/// Shared sweep logic: SMS reminder for every cart idle past Settings.AbandonedCartHours.
/// Used by the Quartz job (30 min) and the admin trigger endpoint. Idempotent per cart (reminder stamp).
/// </summary>
public sealed class AbandonedCartSweeper(IServiceScopeFactory scopeFactory, ILogger<AbandonedCartSweeper> logger)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<MarketHub.Shared.Infrastructure.ISettingsProvider>();
        var carts = scope.ServiceProvider.GetRequiredService<IAbandonedCartReader>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<INotificationDispatcher>();

        var idleHours = await settings.GetValueAsync<int>(MarketHub.Shared.Infrastructure.SettingKeys.AbandonedCartHours, 2, ct);
        var stale = await carts.GetStaleCartsAsync(TimeSpan.FromHours(Math.Max(1, idleHours)), ct);
        foreach (var cart in stale)
        {
            var itemCount = CountItems(cart.ItemsJson);
            await dispatcher.DispatchAsync(new NotificationMessage(cart.UserId, NotificationTemplates.AbandonedCart,
                "You left items in your cart",
                itemCount == 0
                    ? "You left items in your MarketHub cart — complete your order before they sell out."
                    : $"You left {itemCount} item(s) in your MarketHub cart — complete your order before they sell out."), ct);
            await carts.MarkRemindedAsync(cart.UserId, ct);
        }
        if (stale.Count > 0) logger.LogInformation("AbandonedCartReminder notified {Count} buyers", stale.Count);
        return stale.Count;
    }

    private static int CountItems(string itemsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(itemsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        }
        catch (JsonException) { return 0; }
    }
}

/// <summary>Quartz job (every 30 minutes) wrapping <see cref="AbandonedCartSweeper"/>. Guarded, logged, idempotent.</summary>
public sealed class AbandonedCartReminderJob(AbandonedCartSweeper sweeper) : IJob
{
    public static readonly JobKey Key = new("abandoned-cart-reminder");

    public async Task Execute(IJobExecutionContext ctx)
    {
        try
        {
            await sweeper.RunAsync(ctx.CancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // logged by the sweeper; keep the scheduler alive
        }
    }
}
