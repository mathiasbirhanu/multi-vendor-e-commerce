using MarketHub.Shared;
using MarketHub.Shared.Contracts;
using MarketHub.Shared.Infrastructure;
using MarketHub.Modules.Notifications.Domain;
using MarketHub.Modules.Notifications.Features;
using MarketHub.Modules.Notifications.Persistence;
using MarketHub.Modules.Notifications.Services;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MarketHub.Modules.Notifications;

/// <summary>Notifications module composition: in-app bell + outbound SMS/Email adapters + dispatcher + jobs.</summary>
public static class NotificationsModule
{
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddDbContext<NotificationsDbContext>(o => o.UseNpgsql(cfg.GetConnectionString("Default")));
        services.AddScoped<INotificationSender, MockNotificationSender>();
        services.AddScoped<INotificationDispatcher, NotificationDispatcher>();
        services.AddScoped<AbandonedCartSweeper>();
        services.AddHttpClient("ethio-telecom-sms");
        services.AddHttpClient("safaricom-sms");

        // provider selection comes from configuration (real credentials live ONLY in env/user-secrets)
        var smsProvider = (cfg["Sms:Provider"] ?? "Mock").Trim();
        services.AddScoped<ISmsSender>(sp => smsProvider switch
        {
            "EthioTelecom" => new EthioTelecomSmsSender(sp.GetRequiredService<IHttpClientFactory>(), cfg),
            "Safaricom" => new SafaricomSmsSender(sp.GetRequiredService<IHttpClientFactory>(), cfg),
            _ => new MockSmsSender(sp.GetRequiredService<ILogger<MockSmsSender>>()),
        });
        var emailProvider = (cfg["Email:Provider"] ?? "Mock").Trim();
        services.AddScoped<IEmailSender>(sp => emailProvider switch
        {
            "Smtp" => new SmtpEmailSender(cfg, sp.GetRequiredService<ILogger<SmtpEmailSender>>()),
            _ => new MockEmailSender(sp.GetRequiredService<ILogger<MockEmailSender>>()),
        });

        services.AddTransient<IModuleSeeder, NotificationsSeeder>();
        return services;
    }
}

/// <summary>Notification endpoints (buyer bell + admin Ops: abandoned carts, outbound audit).</summary>
public static class NotificationsEndpoints
{
    public static void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notifications").WithTags("Notifications").RequireAuthorization();
        group.MapGet("/", async (NotificationsDbContext db, ICurrentUser user) =>
        {
            var items = await db.NotificationLogs.AsNoTracking()
                .Where(n => n.UserId == user.UserId)
                .OrderByDescending(n => n.CreatedAtUtc).Take(30)
                .Select(n => new { n.Id, n.Type, n.Subject, n.Body, n.ReadAtUtc, n.CreatedAtUtc })
                .ToListAsync();
            return Results.Ok(new { items });
        });
        group.MapPost("/read-all", async (NotificationsDbContext db, ICurrentUser user) =>
        {
            await db.NotificationLogs.Where(n => n.UserId == user.UserId && n.ReadAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, DateTime.UtcNow));
            return Results.Ok(new { ok = true });
        });

        var admin = app.MapGroup("/api/admin").WithTags("Admin").RequireAuthorization("Admin");
        admin.MapGet("/abandoned-carts", async (IAbandonedCartReader carts, IUserDirectory users) =>
        {
            var idleHours = 2; // listing threshold; reminders use the Settings value via the job
            var rows = await carts.GetStaleCartsAsync(TimeSpan.FromHours(idleHours));
            var items = new List<object>();
            foreach (var row in rows)
            {
                var (email, phone, _) = await users.GetContactAsync(row.UserId);
                items.Add(new { userId = row.UserId, email, phone, itemsJson = row.ItemsJson, updatedAt = row.UpdatedAtUtc, reminderSentAt = row.ReminderSentAtUtc });
            }
            return Results.Ok(new { items });
        });
        admin.MapPost("/abandoned-carts/remind", async (RemindRequest req, IAbandonedCartReader carts, INotificationDispatcher dispatcher) =>
        {
            if (req.UserId == Guid.Empty) return Results.BadRequest(new { error = "userId is required." });
            var rows = await carts.GetStaleCartsAsync(TimeSpan.Zero);
            var row = rows.FirstOrDefault(r => r.UserId == req.UserId);
            var itemCount = CountItems(row?.ItemsJson ?? "[]");
            await dispatcher.DispatchAsync(new NotificationMessage(req.UserId, NotificationTemplates.AbandonedCart,
                "You left items in your cart",
                itemCount == 0
                    ? "You left items in your MarketHub cart — complete your order before they sell out."
                    : $"You left {itemCount} item(s) in your MarketHub cart — complete your order before they sell out."));
            await carts.MarkRemindedAsync(req.UserId);
            return Results.Ok(new { ok = true });
        });
        // manual trigger for the 30-minute Quartz job (Ops tab parity)
        admin.MapPost("/jobs/abandoned-cart-reminder", async (AbandonedCartSweeper sweeper) =>
            Results.Ok(new { ok = true, reminded = await sweeper.RunAsync(CancellationToken.None) }));
    }

    private static int CountItems(string itemsJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(itemsJson);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        }
        catch (System.Text.Json.JsonException) { return 0; }
    }

    public sealed record RemindRequest(Guid UserId);
}

/// <summary>Idempotent notifications seeder.</summary>
public sealed class NotificationsSeeder : IModuleSeeder
{
    public string ModuleName => "Notifications";

    public async Task SeedAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<NotificationsDbContext>();
        if (await db.NotificationLogs.AnyAsync(ct)) return;
        db.NotificationLogs.AddRange(
            new NotificationLog { UserId = SeedIds.BuyerUserId, Type = "OrderConfirmed", Subject = "Order confirmed", Body = "Your payment was confirmed and your order is being prepared." },
            new NotificationLog { UserId = SeedIds.Vendor1UserId, Type = "NewOrder", Subject = "New order received", Body = "You have a new paid order. Accept it within 24 hours." },
            new NotificationLog { UserId = SeedIds.Vendor1UserId, Type = "PayoutReminder", Subject = "Payout request submitted", Body = "Your payout request will be processed within 12 hours." });
        await db.SaveChangesAsync(ct);
    }
}
