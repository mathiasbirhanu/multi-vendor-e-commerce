namespace MarketHub.Shared.Contracts.Events;

/// <summary>Vendor application approved by an admin.</summary>
public sealed record VendorApprovedEvent(Guid VendorId, string BusinessName) : DomainEvent;
/// <summary>Vendor application rejected with a reason.</summary>
public sealed record VendorRejectedEvent(Guid VendorId, string Reason) : DomainEvent;
/// <summary>Vendor suspended (e.g. required KYC documents expired).</summary>
public sealed record VendorSuspendedEvent(Guid VendorId, string Reason) : DomainEvent;
/// <summary>Checkout completed and order persisted (one event per vendor group is emitted by Sales).</summary>
public sealed record OrderPlacedEvent(Guid OrderId, Guid VendorId, decimal GrossTotal) : DomainEvent;
/// <summary>Payment confirmed via provider webhook — triggers ledger posting.</summary>
public sealed record OrderPaymentConfirmedEvent(Guid OrderId, Guid BuyerId, decimal GrandTotal) : DomainEvent;
/// <summary>Admin decided a buyer refund request.</summary>
public sealed record RefundDecidedEvent(Guid OrderId, bool Approved, decimal Amount) : DomainEvent;
/// <summary>Vendor payout processed (paid or rejected).</summary>
public sealed record PayoutProcessedEvent(Guid VendorId, decimal AmountETB, bool IsPaid) : DomainEvent;
/// <summary>Buyer cancelled an order that was still in Placed state.</summary>
public sealed record OrderCancelledEvent(Guid OrderId, Guid BuyerId, string OrderNumber) : DomainEvent;
/// <summary>Vendor shipped the order (tracking code attached).</summary>
public sealed record OrderShippedEvent(Guid OrderId, Guid BuyerId, string? TrackingCode) : DomainEvent;
/// <summary>Order handed over to the buyer (delivery PIN verified).</summary>
public sealed record OrderDeliveredEvent(Guid OrderId, Guid BuyerId) : DomainEvent;
/// <summary>Buyer opened a per-item return request.</summary>
public sealed record ReturnRequestedEvent(Guid ReturnId, Guid OrderId, Guid BuyerId, Guid? VendorId, string Reason) : DomainEvent;
/// <summary>Return request decided (vendor stage or admin arbitration).</summary>
public sealed record ReturnDecidedEvent(Guid ReturnId, Guid OrderId, Guid BuyerId, Guid? VendorId, bool Approved, decimal RefundETB, string? Note) : DomainEvent;
/// <summary>Buyer wrote a review (vendor gets notified after moderation approval).</summary>
public sealed record ReviewCreatedEvent(Guid ReviewId, Guid ProductId, Guid? VendorId, Guid BuyerId, int Rating) : DomainEvent;
/// <summary>Admin moderated a review (reviewer gets notified).</summary>
public sealed record ReviewModeratedEvent(Guid ReviewId, Guid BuyerId, Guid ProductId, bool Approved) : DomainEvent;
/// <summary>Vendor replied to a review on their product.</summary>
public sealed record ReviewRepliedEvent(Guid ReviewId, Guid ProductId, Guid VendorId, Guid BuyerId, string Reply) : DomainEvent;
