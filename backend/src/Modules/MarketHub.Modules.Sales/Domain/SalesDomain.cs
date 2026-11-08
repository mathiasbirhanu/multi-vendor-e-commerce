using MarketHub.Shared;

namespace MarketHub.Modules.Sales.Domain;

public enum OrderStatus { Placed, PaymentConfirmed, VendorAccepted, Shipped, Delivered, Completed, Cancelled, RefundRequested, Refunded, Disputed }

/// <summary>Explicit legal transitions of the order state machine. Illegal transitions throw domain exceptions.</summary>
public static class OrderStateMachine
{
    public static readonly IReadOnlyDictionary<OrderStatus, OrderStatus[]> AllowedTransitions =
        new Dictionary<OrderStatus, OrderStatus[]>
        {
            [OrderStatus.Placed] = [OrderStatus.PaymentConfirmed, OrderStatus.Cancelled],
            [OrderStatus.PaymentConfirmed] = [OrderStatus.VendorAccepted, OrderStatus.RefundRequested, OrderStatus.Disputed],
            [OrderStatus.VendorAccepted] = [OrderStatus.Shipped, OrderStatus.RefundRequested, OrderStatus.Disputed],
            [OrderStatus.Shipped] = [OrderStatus.Delivered, OrderStatus.Disputed],
            [OrderStatus.Delivered] = [OrderStatus.Completed, OrderStatus.RefundRequested, OrderStatus.Disputed],
            [OrderStatus.Completed] = [OrderStatus.RefundRequested],
            [OrderStatus.RefundRequested] = [OrderStatus.Refunded, OrderStatus.Disputed],
            [OrderStatus.Cancelled] = [],
            [OrderStatus.Refunded] = [],
            [OrderStatus.Disputed] = [],
        };

    public static bool CanTransition(OrderStatus from, OrderStatus to) => AllowedTransitions[from].Contains(to);

    public static void EnsureCanTransition(OrderStatus from, OrderStatus to)
    {
        if (!CanTransition(from, to)) throw new DomainException($"Illegal order transition: {from} → {to}.");
    }
}

/// <summary>Buyer order with an immutable financial snapshot. Money is decimal(18,2), ETB.</summary>
public class Order : BaseEntity
{
    public string OrderNumber { get; set; } = default!;
    public Guid BuyerId { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Placed;
    public string? PreviousStatus { get; set; }
    public string AddressLine { get; set; } = default!;
    public string City { get; set; } = default!;
    public Guid ZoneId { get; set; }
    public string ZoneNameSnapshot { get; set; } = default!;
    public string Phone { get; set; } = default!;
    public decimal SubtotalETB { get; set; }
    public decimal DeliveryFeeETB { get; set; }
    public decimal VatRatePercent { get; set; }
    public decimal VatETB { get; set; }
    public decimal GrandTotalETB { get; set; }
    /// <summary>Code shared with the buyer for PIN-verified hand-over (set at checkout).</summary>
    public string? DeliveryPin { get; set; }
    /// <summary>Courier tracking code "MH-TRK-xxxxxx" (set by the vendor's ship action).</summary>
    public string? TrackingCode { get; set; }
    /// <summary>Saved address book entry used at checkout (null when the buyer typed an inline address).</summary>
    public Guid? AddressId { get; set; }
    public string? CouponCode { get; set; }
    public decimal CouponDiscountETB { get; set; }
    public string? RefundReason { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }
    public uint RowVersion { get; set; }

    public IReadOnlyList<OrderItem> Items { get; set; } = Array.Empty<OrderItem>();
    public IReadOnlyList<OrderEvent> Events { get; set; } = Array.Empty<OrderEvent>();

    public void Transition(OrderStatus to)
    {
        EnsureTransitionAllowed(Status, to);
        PreviousStatus = Status.ToString();
        Status = to;
        if (to == OrderStatus.Delivered) DeliveredAtUtc = DateTime.UtcNow;
        ModifiedAtUtc = DateTime.UtcNow;
    }

    private static void EnsureTransitionAllowed(OrderStatus from, OrderStatus to)
    {
        if (!OrderStateMachine.CanTransition(from, to)) throw new DomainException($"Illegal order transition: {from} → {to}.");
    }
}

public class OrderItem : BaseEntity
{
    public Guid OrderId { get; set; }
    public Guid ProductId { get; set; }
    public string TitleSnapshot { get; set; } = default!;
    /// <summary>Immutable snapshot of the chosen color variant (null = single-color product).</summary>
    public string? ColorNameSnapshot { get; set; }
    public string? ColorHexSnapshot { get; set; }
    /// <summary>SKU-level variant (takes precedence over the color-only snapshot when present).</summary>
    public Guid? VariantId { get; set; }
    public string? VariantSku { get; set; }
    /// <summary>"Black / 42"-style label snapshot for invoices and returns.</summary>
    public string? VariantLabel { get; set; }
    public Guid? VendorId { get; set; }
    public string? VendorNameSnapshot { get; set; }
    public decimal UnitPriceETB { get; set; }
    public int Quantity { get; set; }
    public decimal LineNetETB { get; set; }
    public decimal VatETB { get; set; }
    public decimal CommissionRateSnapshot { get; set; }
    public decimal CommissionETB { get; set; }
    public decimal DeliveryFeeAllocationETB { get; set; }
    public string RefundStatus { get; set; } = "None"; // None | Requested | Approved | Rejected
}

/// <summary>Immutable status-timeline entry rendered in the buyer's order tracking view.</summary>
public class OrderEvent : BaseEntity
{
    public Guid OrderId { get; set; }
    public string Status { get; set; } = default!;
    public string? Note { get; set; }
    public string ByRole { get; set; } = "System"; // System | Buyer | Vendor | Admin | Courier
}

/// <summary>Ethiopian address-book entry (region → sub-city → woreda → kebele → landmark).</summary>
public class Address : BaseEntity
{
    public Guid UserId { get; set; }
    public string Label { get; set; } = "Home";
    public string FullName { get; set; } = default!;
    public string Phone { get; set; } = default!; // +2519XXXXXXXX
    public string Region { get; set; } = default!;
    public string City { get; set; } = default!;
    public string SubCity { get; set; } = default!;
    public string Woreda { get; set; } = default!;
    public string? Kebele { get; set; }
    public string? Landmark { get; set; }
    public bool IsDefault { get; set; }

    /// <summary>One-line snapshot stored on the order (keeps historical deliveries stable).</summary>
    public string ToAddressLine()
    {
        var parts = new[] { FullName, SubCity, Woreda, Kebele, Landmark }.Where(s => !string.IsNullOrWhiteSpace(s));
        return string.Join(", ", parts);
    }
}

public enum ReturnReason { Damaged, WrongItem, NotAsDescribed, ChangedMind, Other }

public enum ReturnStatus { Requested, VendorApproved, VendorRejected, AdminReview, Approved, Rejected, Refunded }

/// <summary>Per-item return request. Vendor decides first; admin arbitrates and issues the final refund.</summary>
public class ReturnRequest : BaseEntity
{
    public Guid OrderItemId { get; set; }
    public Guid OrderId { get; set; }
    public Guid BuyerId { get; set; }
    public Guid? VendorId { get; set; }
    public ReturnReason Reason { get; set; }
    public string Description { get; set; } = default!;
    /// <summary>JSON string[] of evidence photo URLs (null when none attached).</summary>
    public string? EvidenceUrlsJson { get; set; }
    public ReturnStatus Status { get; set; } = ReturnStatus.Requested;
    public string? VendorNote { get; set; }
    public string? AdminDecision { get; set; }
    public Guid? AdminId { get; set; }
    public decimal? RefundETB { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }

    public static IReadOnlyList<string>? ParseUrls(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
}

/// <summary>Cart snapshot persisted for abandoned-cart recovery (client debounces POST /api/sales/cart/save).</summary>
public class Cart : BaseEntity
{
    public Guid UserId { get; set; }
    /// <summary>JSON [{variantId?,colorId?,productId,qty}] — the exact client-side cart state.</summary>
    public string ItemsJson { get; set; } = "[]";
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ReminderSentAtUtc { get; set; }
}

public class CartItem : BaseEntity
{
    public Guid UserId { get; set; }
    public Guid ProductId { get; set; }
    /// <summary>SKU-level variant (takes precedence over the color-only path when present).</summary>
    public Guid? VariantId { get; set; }
    /// <summary>Chosen color variant (null = product without color variants).</summary>
    public Guid? ColorId { get; set; }
    public int Quantity { get; set; } = 1;
}

public class DeliveryZone : BaseEntity
{
    public string Name { get; set; } = default!;
    public bool IsActive { get; set; } = true;
}

public class DeliveryRate : BaseEntity
{
    public Guid ZoneId { get; set; }
    public decimal BaseFeeETB { get; set; }
    public decimal PerKgSurchargeETB { get; set; }
    /// <summary>Subtotal above which delivery is free (0 = never free).</summary>
    public decimal FreeThresholdETB { get; set; }
}
