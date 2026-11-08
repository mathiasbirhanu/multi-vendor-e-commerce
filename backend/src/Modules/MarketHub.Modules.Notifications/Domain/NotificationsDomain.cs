using MarketHub.Shared;

namespace MarketHub.Modules.Notifications.Domain;

public enum NotificationChannel { InApp, Email, Sms }

public class NotificationLog : BaseEntity
{
    public Guid UserId { get; set; }
    public NotificationChannel Channel { get; set; } = NotificationChannel.InApp;
    public string Type { get; set; } = default!;
    public string Subject { get; set; } = default!;
    public string Body { get; set; } = default!;
    public DateTime? ReadAtUtc { get; set; }
}

/// <summary>Outbound channel abstraction. Implementations: in-app (default), email, SMS providers.</summary>
public interface INotificationSender
{
    Task SendAsync(Guid userId, NotificationChannel channel, string type, string subject, string body, CancellationToken ct = default);
}

public enum OutboundChannel { Sms, Email }

public enum OutboundStatus { Queued, Sent, Failed, Skipped }

/// <summary>
/// Audit row for every external message the platform attempts (SMS/email). Written by the dispatcher
/// for every critical send — including Skipped rows when no destination exists — so the admin console
/// can inspect exactly what left the platform.
/// </summary>
public class OutboundMessage : BaseEntity
{
    public Guid? UserId { get; set; }
    public OutboundChannel Channel { get; set; }
    /// <summary>Template name (OrderConfirmed, OrderShipped, OrderDelivered, PayoutProcessed, ReturnDecided, VendorApproved, AbandonedCart).</summary>
    public string Template { get; set; } = default!;
    public string Destination { get; set; } = default!;
    public string? PayloadJson { get; set; }
    public string Provider { get; set; } = default!;
    public string? ProviderRef { get; set; }
    public OutboundStatus Status { get; set; } = OutboundStatus.Queued;
    public string? Error { get; set; }
    public DateTime? SentAtUtc { get; set; }
}

/// <summary>Canonical template names used by the dispatcher + jobs (critical = SMS/Email when a destination exists).</summary>
public static class NotificationTemplates
{
    public const string OrderConfirmed = "OrderConfirmed";
    public const string OrderShipped = "OrderShipped";
    public const string OrderDelivered = "OrderDelivered";
    public const string PayoutProcessed = "PayoutProcessed";
    public const string ReturnDecided = "ReturnDecided";
    public const string VendorApproved = "VendorApproved";
    public const string AbandonedCart = "AbandonedCart";

    public static readonly IReadOnlyList<string> All = new[]
    {
        OrderConfirmed, OrderShipped, OrderDelivered, PayoutProcessed, ReturnDecided, VendorApproved, AbandonedCart,
    };

    /// <summary>Critical templates trigger SMS/Email in addition to the always-on in-app notification.</summary>
    public static bool IsCritical(string template) => All.Contains(template, StringComparer.OrdinalIgnoreCase);
}
