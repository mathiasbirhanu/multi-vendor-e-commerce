using MarketHub.Shared;

namespace MarketHub.Modules.Payments.Domain;

public enum PaymentProvider { Mock, Chapa, Telebirr, Santimpay }
public enum PaymentStatus { Initiated, Processing, Confirmed, Failed, Refunded }

public class Payment : BaseEntity
{
    public Guid OrderId { get; set; }
    public PaymentProvider Provider { get; set; }
    public string ProviderRef { get; set; } = default!;
    public decimal AmountETB { get; set; }
    public PaymentStatus Status { get; set; } = PaymentStatus.Initiated;
    public string? RawInitiateResponse { get; set; }
}

/// <summary>Raw provider event log — supports idempotent webhook handling and audit.</summary>
public class PaymentEvent : BaseEntity
{
    public Guid PaymentId { get; set; }
    public string EventType { get; set; } = default!;
    public string PayloadJson { get; set; } = default!;
    public Guid? ExternalEventId { get; set; }
}

public class Refund : BaseEntity
{
    public Guid PaymentId { get; set; }
    public Guid OrderId { get; set; }
    public decimal AmountETB { get; set; }
    public string Reason { get; set; } = default!;
    public string Status { get; set; } = "Requested"; // Requested | Processing | Completed | Rejected
    public string? ProviderRef { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
}
