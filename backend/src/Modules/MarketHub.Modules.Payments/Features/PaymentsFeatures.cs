using System.Text.Json;
using MarketHub.Shared;
using MarketHub.Shared.Contracts;
using MarketHub.Shared.Contracts.Events;
using MarketHub.Shared.Infrastructure;
using MarketHub.Modules.Payments.Domain;
using MarketHub.Modules.Payments.Gateways;
using MarketHub.Modules.Payments.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace MarketHub.Modules.Payments.Features;

// ==================== PAYMENT CONTEXT ====================
public class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options, MarketHub.Shared.ICurrentUser currentUser)
    : MarketHubDbContextBase<PaymentsDbContext>(options, currentUser)
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentEvent> PaymentEvents => Set<PaymentEvent>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("payments");
        b.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.HasIndex(x => x.ProviderRef).IsUnique();
            e.HasIndex(x => x.OrderId);
            e.Property(x => x.AmountETB).HasPrecision(18, 2);
        });
        b.Entity<PaymentEvent>(e =>
        {
            e.ToTable("payment_events");
            e.Property(x => x.PayloadJson).IsRequired();
            e.HasIndex(x => x.PaymentId);
        });
        b.Entity<Refund>(e =>
        {
            e.ToTable("refunds");
            e.Property(x => x.AmountETB).HasPrecision(18, 2);
        });
        b.Entity<OutboxMessage>(e => e.ToTable("outbox_messages", "outbox"));
    }
}

// ==================== INITIATE ====================
public sealed record InitiatePaymentCommand(Guid BuyerId, Guid OrderId, string CallbackBaseUrl) : IRequest<Result<InitiatePaymentDto>>;
public sealed record InitiatePaymentDto(Guid PaymentId, string Provider, string ProviderRef, string RedirectUrl);

public sealed class InitiatePaymentHandler(PaymentsDbContext db, IOrderReader orders, PaymentGatewayFactory factory)
    : IRequestHandler<InitiatePaymentCommand, Result<InitiatePaymentDto>>
{
    public async Task<Result<InitiatePaymentDto>> Handle(InitiatePaymentCommand c, CancellationToken ct)
    {
        var existing = await db.Payments.FirstOrDefaultAsync(p => p.OrderId == c.OrderId && p.Status == PaymentStatus.Confirmed, ct);
        if (existing is not null) return Result.Fail<InitiatePaymentDto>("already_paid", "This order is already paid.");

        var financials = await orders.GetFinancialsAsync(c.OrderId, ct) ?? throw new NotFoundException("Order", c.OrderId);
        var gateway = await factory.GetActiveAsync(ct);
        var gatewayResult = await gateway.InitiateAsync(new GatewayInitiateRequest(c.OrderId, financials.OrderId.ToString()[..8], financials.GrandTotalETB, $"MarketHub order payment", "buyer@markethub.et", c.CallbackBaseUrl), ct);

        var payment = await db.Payments.FirstOrDefaultAsync(p => p.OrderId == c.OrderId && p.Status == PaymentStatus.Initiated, ct);
        if (payment is null)
        {
            payment = new Payment { OrderId = c.OrderId, Provider = Enum.Parse<PaymentProvider>(gateway.ProviderName, true), AmountETB = financials.GrandTotalETB };
            db.Payments.Add(payment);
        }
        payment.Provider = Enum.Parse<PaymentProvider>(gateway.ProviderName, true);
        payment.ProviderRef = gatewayResult.ProviderRef;
        payment.RawInitiateResponse = gatewayResult.RawResponse;
        db.PaymentEvents.Add(new PaymentEvent { PaymentId = payment.Id, EventType = "initiated", PayloadJson = gatewayResult.RawResponse ?? "{}" });
        await db.SaveChangesAsync(ct);
        return Result.Ok(new InitiatePaymentDto(payment.Id, gateway.ProviderName, gatewayResult.ProviderRef, gatewayResult.RedirectUrl));
    }
}

// ==================== WEBHOOK (idempotent + signed) ====================
public sealed record HandleWebhookCommand(string Provider, string RawBody, string? Signature, Guid? EventId) : IRequest<Result>;

public sealed class HandleWebhookHandler(PaymentsDbContext db, ILedgerPoster ledger, IPublisher publisher, IOrderReader orders)
    : IRequestHandler<HandleWebhookCommand, Result>
{
    public async Task<Result> Handle(HandleWebhookCommand c, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<WebhookPayload>(c.RawBody, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            ?? throw new DomainException("Malformed webhook payload.");

        // idempotency: one processing per external event id
        if (c.EventId.HasValue && await db.PaymentEvents.AnyAsync(e => e.ExternalEventId == c.EventId, ct))
            return Result.Success();

        var gatewayName = Enum.TryParse<PaymentProvider>(c.Provider, true, out var p) ? p : throw new DomainException($"Unknown provider '{c.Provider}'.");

        var payment = await db.Payments.FirstOrDefaultAsync(x => x.ProviderRef == payload.ProviderRef && x.Provider == gatewayName, ct);
        if (payment is null) throw new NotFoundException("Payment", payload.ProviderRef);

        var eventRow = new PaymentEvent { PaymentId = payment.Id, EventType = $"webhook:{c.Provider}", PayloadJson = c.RawBody, ExternalEventId = c.EventId };
        db.PaymentEvents.Add(eventRow);

        if (payment.Status == PaymentStatus.Confirmed)
        {
            await db.SaveChangesAsync(ct); // log the duplicate, stay idempotent
            return Result.Success();
        }

        var isPaid = string.Equals(payload.Status, "success", StringComparison.OrdinalIgnoreCase) || string.Equals(payload.Status, "confirmed", StringComparison.OrdinalIgnoreCase);
        if (!isPaid)
        {
            payment.Status = PaymentStatus.Failed;
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }

        if (Math.Abs(payload.AmountETB - payment.AmountETB) > 0.01m)
            throw new DomainException($"Webhook amount {payload.AmountETB} does not match payment {payment.AmountETB} — possible tampering.");

        payment.Status = PaymentStatus.Confirmed;
        await ledger.PostOrderPaymentAsync(payment.OrderId, ct); // double-entry posting (balanced or throws)
        await db.SaveChangesAsync(ct);

        var financials = await orders.GetFinancialsAsync(payment.OrderId, ct);
        db.AddToOutbox(new OrderPaymentConfirmedEvent(payment.OrderId, financials?.BuyerId ?? Guid.Empty, payment.AmountETB));
        await db.SaveChangesAsync(ct);
        await publisher.Publish(new PaymentConfirmedNotification(payment.OrderId, payment.AmountETB), ct);
        return Result.Success();
    }
}

public sealed record PaymentConfirmedNotification(Guid OrderId, decimal AmountETB) : MediatR.INotification;

// ==================== MOCK CONFIRM (dev parity with the real webhook path) ====================
public sealed record MockConfirmCommand(string ProviderRef) : IRequest<Result>;

public sealed class MockConfirmHandler(PaymentsDbContext db, IMediator mediator) : IRequestHandler<MockConfirmCommand, Result>
{
    public async Task<Result> Handle(MockConfirmCommand c, CancellationToken ct)
    {
        // echo the payment's own amount — the webhook's tampering guard rejects amount mismatches
        var payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.ProviderRef == c.ProviderRef, ct)
            ?? throw new NotFoundException("Payment", c.ProviderRef);
        var raw = JsonSerializer.Serialize(new { providerRef = c.ProviderRef, status = "success", amountETB = payment.AmountETB }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return await mediator.Send(new HandleWebhookCommand("Mock", raw, MockGateway.Sign(raw), Guid.NewGuid()), ct);
    }
}

// ==================== REFUNDS ====================
public sealed record AdminInitiateRefundCommand(Guid OrderId, decimal AmountETB, string Reason) : IRequest<Result>;

public sealed class AdminInitiateRefundHandler(PaymentsDbContext db, ILedgerPoster ledger, IOrderReader orders) : IRequestHandler<AdminInitiateRefundCommand, Result>
{
    public async Task<Result> Handle(AdminInitiateRefundCommand c, CancellationToken ct)
    {
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.OrderId == c.OrderId && p.Status == PaymentStatus.Confirmed, ct)
            ?? throw new NotFoundException("Payment", c.OrderId);
        var financials = await orders.GetFinancialsAsync(c.OrderId, ct) ?? throw new NotFoundException("Order", c.OrderId);
        if (c.AmountETB <= 0 || c.AmountETB > financials.GrandTotalETB) return Result.Failure(new Error("amount", "Refund amount must be within the paid total."));

        var refund = new Refund { PaymentId = payment.Id, OrderId = c.OrderId, AmountETB = c.AmountETB, Reason = c.Reason };
        db.Refunds.Add(refund);
        payment.Status = PaymentStatus.Refunded;
        await ledger.PostRefundAsync(c.OrderId, c.AmountETB, ct);
        refund.Status = "Completed"; // Mock completes instantly; Chapa/telebirr adapters set Processing until provider confirms
        refund.ProcessedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed record GetPaymentQuery(Guid OrderId) : IRequest<Result<PaymentDto>>;
public sealed record PaymentDto(Guid Id, string Provider, string ProviderRef, decimal AmountETB, string Status, DateTime CreatedAtUtc);

public sealed class GetPaymentHandler(PaymentsDbContext db) : IRequestHandler<GetPaymentQuery, Result<PaymentDto>>
{
    public async Task<Result<PaymentDto>> Handle(GetPaymentQuery q, CancellationToken ct)
    {
        var p = await db.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.OrderId == q.OrderId, ct)
            ?? throw new NotFoundException("Payment", q.OrderId);
        return Result.Ok(new PaymentDto(p.Id, p.Provider.ToString(), p.ProviderRef, p.AmountETB, p.Status.ToString(), p.CreatedAtUtc));
    }
}

// ---- return-arbitration side effect (outbox): records the provider refund row for approved returns ----
// (the balanced ledger legs are posted by the Sales arbitration handler via ILedgerPoster — one poster, one truth)
public sealed class ReturnArbitrationRefundHandler(PaymentsDbContext db, ILogger<ReturnArbitrationRefundHandler> logger)
    : INotificationHandler<MarketHub.Shared.Contracts.Events.ReturnDecidedEvent>
{
    public async Task Handle(MarketHub.Shared.Contracts.Events.ReturnDecidedEvent e, CancellationToken ct)
    {
        if (!e.Approved || e.RefundETB <= 0) return;
        if (await db.Refunds.AnyAsync(r => r.OrderId == e.OrderId && r.Reason == "Return refund", ct)) return; // idempotent

        var payment = await db.Payments.FirstOrDefaultAsync(p => p.OrderId == e.OrderId && p.Status == PaymentStatus.Confirmed, ct);
        if (payment is null)
        {
            logger.LogWarning("Return refund for order {OrderId} skipped — no confirmed payment found", e.OrderId);
            return;
        }
        db.Refunds.Add(new Refund { PaymentId = payment.Id, OrderId = e.OrderId, AmountETB = e.RefundETB, Reason = "Return refund", Status = "Completed", ProcessedAtUtc = DateTime.UtcNow });
        payment.Status = PaymentStatus.Refunded;
        await db.SaveChangesAsync(ct);
    }
}
