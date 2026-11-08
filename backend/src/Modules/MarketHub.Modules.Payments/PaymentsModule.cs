using MarketHub.Shared;
using MarketHub.Modules.Payments.Domain;
using MarketHub.Modules.Payments.Features;
using MarketHub.Modules.Payments.Gateways;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MarketHub.Modules.Payments;

/// <summary>Payments module composition: gateway abstraction + Mock + Chapa/telebirr/Santimpay adapters.</summary>
public static class PaymentsModule
{
    public static IServiceCollection AddPaymentsModule(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddDbContext<PaymentsDbContext>(o => o.UseNpgsql(cfg.GetConnectionString("Default")));
        services.AddHttpClient("chapa");
        services.AddHttpClient("telebirr");
        services.AddHttpClient("santimpay");
        services.AddScoped<IPaymentGateway, MockGateway>();
        services.AddScoped<IPaymentGateway, ChapaGateway>();
        services.AddScoped<IPaymentGateway, TelebirrGateway>();
        services.AddScoped<IPaymentGateway, SantimpayGateway>();
        services.AddScoped<PaymentGatewayFactory>();
        return services;
    }
}

/// <summary>HTTP endpoints for the Payments module.</summary>
public static class PaymentsEndpoints
{
    public static void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/payments").WithTags("Payments");
        group.MapPost("/initiate/{orderId}", async (IMediator m, Guid orderId, ICurrentUser user, HttpContext ctx) =>
        {
            if (!user.IsAuthenticated) return Results.Unauthorized();
            var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            return (await m.Send(new InitiatePaymentCommand(user.UserId!.Value, orderId, baseUrl))).ToHttp();
        }).RequireAuthorization();

        // provider webhooks — signature verified BEFORE dispatch; raw body kept for audit
        group.MapPost("/webhook/{provider}", async (IMediator m, PaymentGatewayFactory factory, HttpRequest request, string provider) =>
        {
            request.EnableBuffering();
            using var reader = new StreamReader(request.Body, leaveOpen: true);
            var raw = await reader.ReadToEndAsync();
            var gateway = factory.GetByName(provider);
            var signature = request.Headers["X-Signature"].FirstOrDefault() ?? request.Headers["Chapa-Signature"].FirstOrDefault();
            if (!gateway.ValidateWebhookSignature(raw, signature ?? ""))
                return Results.Unauthorized();
            _ = Guid.TryParse(request.Headers["X-Event-Id"].FirstOrDefault(), out var eventId);
            return (await m.Send(new HandleWebhookCommand(provider, raw, signature, eventId == Guid.Empty ? null : eventId))).ToHttp();
        }).AllowAnonymous();

        // dev parity endpoint: same code path as the signed webhook (MockGateway only)
        group.MapPost("/mock/confirm", async (IMediator m, MockConfirmRequest req) =>
            (await m.Send(new MockConfirmCommand(req.ProviderRef))).ToHttp()).AllowAnonymous();

        group.MapGet("/{orderId}", async (IMediator m, Guid orderId, ICurrentUser user) =>
            !user.IsAuthenticated ? Results.Unauthorized() : (await m.Send(new GetPaymentQuery(orderId))).ToHttp()).RequireAuthorization();
    }

    public sealed record MockConfirmRequest(string ProviderRef);
}
