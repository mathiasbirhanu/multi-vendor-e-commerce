using MarketHub.Shared;
using MarketHub.Shared.Contracts;
using MarketHub.Shared.Infrastructure;
using MarketHub.Modules.Finance.Domain;
using MarketHub.Modules.Finance.Features;
using MarketHub.Modules.Finance.Persistence;
using MarketHub.Modules.Finance.Services;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace MarketHub.Modules.Finance;

/// <summary>Finance module composition: settings, commission, double-entry ledger, payouts, reports.</summary>
public static class FinanceModule
{
    public static IServiceCollection AddFinanceModule(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddDbContext<FinanceDbContext>(o => o.UseNpgsql(cfg.GetConnectionString("Default")));
        services.AddValidatorsFromAssemblyContaining<RequestPayoutValidator>();
        services.AddScoped<ISettingsProvider, SettingsProvider>();
        services.AddScoped<ICommissionResolver, CommissionResolver>();
        services.AddScoped<ILedgerPoster, LedgerPoster>();

        // Quartz scheduling is centralized in the composition root (Program.cs) — see AddMarketHubQuartz.

        services.AddTransient<IModuleSeeder, FinanceSeeder>();
        return services;
    }
}

/// <summary>HTTP endpoints for the Finance module.</summary>
public static class FinanceEndpoints
{
    public static void MapEndpoints(IEndpointRouteBuilder app)
    {
        var vendor = app.MapGroup("/api/vendor/finance").WithTags("Vendor").RequireAuthorization("Vendor");
        vendor.MapGet("/earnings", async (IMediator m, ICurrentUser user) => (await m.Send(new VendorEarningsQuery(user.UserId!.Value))).ToHttp());
        vendor.MapGet("/payouts", async (IMediator m, ICurrentUser user) => (await m.Send(new VendorPayoutHistoryQuery(user.UserId!.Value))).ToHttp());
        vendor.MapPost("/payouts", async (IMediator m, PayoutRequestDto req, ICurrentUser user) =>
            (await m.Send(new RequestPayoutCommand(user.UserId!.Value, req.AmountETB, Enum.TryParse<PayoutMethod>(req.PaymentMethod, true, out var pm) ? pm : PayoutMethod.Telebirr, req.DestinationAccount))).ToHttp());

        var admin = app.MapGroup("/api/admin").WithTags("Admin").RequireAuthorization("Admin");
        admin.MapGet("/reports/summary", async (IMediator m, string range) => (await m.Send(new ReportSummaryQuery(range ?? "30d"))).ToHttp());
        admin.MapGet("/reports/revenue-over-time", async (IMediator m, int days) => (await m.Send(new RevenueOverTimeQuery(days <= 0 ? 30 : days))).ToHttp());
        admin.MapGet("/reports/top-vendors", async (IMediator m, int limit) => (await m.Send(new TopVendorsQuery(limit <= 0 ? 5 : limit))).ToHttp());
        admin.MapGet("/reports/top-products", async (IMediator m, int limit) => (await m.Send(new TopProductsQuery(limit <= 0 ? 5 : limit))).ToHttp());
        admin.MapGet("/reports/vendor-income/{vendorId}", async (IMediator m, Guid vendorId) => (await m.Send(new VendorIncomeQuery(vendorId))).ToHttp());
        admin.MapGet("/payouts", async (IMediator m) => (await m.Send(new AdminListPayoutsQuery())).ToHttp());
        admin.MapPost("/payouts/{id}/process", async (IMediator m, Guid id, ProcessPayoutRequest req, ICurrentUser user) =>
            (await m.Send(new AdminProcessPayoutCommand(user.UserId!.Value, id, req.Approve, req.AdminNote))).ToHttp());
        admin.MapGet("/commission", async (IMediator m) => (await m.Send(new GetCommissionConfigQuery())).ToHttp());
        admin.MapPut("/commission", async (IMediator m, SetCommissionRequest req) => (await m.Send(new SetDefaultCommissionCommand(req.Rate))).ToHttp());
        admin.MapGet("/settings", async (IMediator m) => (await m.Send(new GetSettingsQuery())).ToHttp());
        admin.MapPut("/settings", async (IMediator m, PutSettingsRequest req) =>
            (await m.Send(new PutSettingsCommand(req.Entries.Select(e => new SettingEntryDto(e.Key, e.Value, null)).ToList()))).ToHttp());
    }

    public sealed record PayoutRequestDto(decimal AmountETB, string PaymentMethod, string? DestinationAccount);
    public sealed record ProcessPayoutRequest(bool Approve, string? AdminNote);
    public sealed record SetCommissionRequest(decimal Rate);
    public sealed record SettingEntryBody(string Key, string Value);
    public sealed record PutSettingsRequest(IReadOnlyList<SettingEntryBody> Entries);
}

/// <summary>Idempotent seeder: settings, ledger accounts, one commission override, sample payout requests.</summary>
public sealed class FinanceSeeder : IModuleSeeder
{
    public string ModuleName => "Finance";

    public async Task SeedAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<FinanceDbContext>();

        if (!await db.Settings.AnyAsync(ct))
        {
            db.Settings.AddRange(
                new Setting { Key = SettingKeys.VatRate, Value = "15", Description = "VAT percentage applied to subtotal + delivery" },
                new Setting { Key = SettingKeys.DefaultCommissionRate, Value = "0.10", Description = "Platform commission on vendor sales" },
                new Setting { Key = SettingKeys.ReturnWindowDays, Value = "7", Description = "Return & refund window after delivery" },
                new Setting { Key = SettingKeys.PaymentProvider, Value = "Mock", Description = "Active payment gateway (Mock | Chapa | Telebirr | Santimpay)" },
                new Setting { Key = SettingKeys.PayoutSlaHours, Value = "12", Description = "Payout request processing SLA in hours" },
                new Setting { Key = SettingKeys.PlatformName, Value = "MarketHub", Description = "Platform display name" });
        }

        // gap-fill v2 keys — added idempotently so existing databases pick them up on the next dev boot
        var existingKeys = await db.Settings.AsNoTracking().Select(s => s.Key).ToListAsync(ct);
        void AddIfMissing(string key, string value, string description)
        {
            if (!existingKeys.Contains(key)) db.Settings.Add(new Setting { Key = key, Value = value, Description = description });
        }
        AddIfMissing(SettingKeys.ReservationMinutes, "20", "Stock reservation TTL in minutes (checkout reserves, payment consumes)");
        AddIfMissing(SettingKeys.AbandonedCartHours, "2", "Cart idle hours before the abandoned-cart reminder fires");
        AddIfMissing(SettingKeys.SmsProvider, "Mock", "SMS adapter (Mock | EthioTelecom | Safaricom)");
        AddIfMissing(SettingKeys.EmailProvider, "Mock", "Email adapter (Mock | Smtp)");
        AddIfMissing(SettingKeys.SearchProvider, "Meilisearch", "Search backend (Meilisearch | Null → SQL ILIKE)");
        AddIfMissing(SettingKeys.FreeDeliveryAddisThreshold, "1000", "Free delivery subtotal threshold for Addis Ababa (ETB)");

        if (!await db.LedgerAccounts.AnyAsync(ct))
        {
            db.LedgerAccounts.AddRange(
                new LedgerAccount { Code = LedgerAccounts.CashPspReceivable, Name = "Cash / PSP Receivable", Type = LedgerAccountType.Asset },
                new LedgerAccount { Code = LedgerAccounts.VendorPayable, Name = "Vendor Payable", Type = LedgerAccountType.Liability },
                new LedgerAccount { Code = LedgerAccounts.VatPayable, Name = "VAT Payable (15%)", Type = LedgerAccountType.Liability },
                new LedgerAccount { Code = LedgerAccounts.PlatformCommissionRevenue, Name = "Platform Commission Revenue", Type = LedgerAccountType.Revenue },
                new LedgerAccount { Code = LedgerAccounts.DeliveryRevenue, Name = "Delivery Revenue", Type = LedgerAccountType.Revenue },
                new LedgerAccount { Code = LedgerAccounts.FirstPartySalesRevenue, Name = "First-Party Sales Revenue", Type = LedgerAccountType.Revenue },
                new LedgerAccount { Code = LedgerAccounts.RefundExpense, Name = "Refund Expense", Type = LedgerAccountType.Expense });
            db.CommissionOverrides.Add(new CommissionOverride { VendorId = SeedIds.Vendor2UserId, Rate = 0.08m, Note = "Founding vendor — reduced commission" });
        }

        if (!await db.PayoutRequests.AnyAsync(ct))
        {
            var slaHours = 12;
            db.PayoutRequests.AddRange(
                new PayoutRequest { VendorId = SeedIds.Vendor1UserId, AmountETB = 2000m, Status = PayoutStatus.Paid, RequestedAtUtc = DateTime.UtcNow.AddDays(-6), DueByUtc = DateTime.UtcNow.AddDays(-6).AddHours(slaHours), ProcessedAtUtc = DateTime.UtcNow.AddDays(-6).AddHours(4), ProcessedByUserId = SeedIds.AdminUserId, PaymentMethod = PayoutMethod.Telebirr, DestinationAccount = "+251911000002" },
                new PayoutRequest { VendorId = SeedIds.Vendor1UserId, AmountETB = 1500m, Status = PayoutStatus.Requested, RequestedAtUtc = DateTime.UtcNow.AddHours(-2), DueByUtc = DateTime.UtcNow.AddHours(slaHours - 2), PaymentMethod = PayoutMethod.Telebirr, DestinationAccount = "+251911000002" },
                new PayoutRequest { VendorId = SeedIds.Vendor2UserId, AmountETB = 3000m, Status = PayoutStatus.Paid, RequestedAtUtc = DateTime.UtcNow.AddDays(-9), DueByUtc = DateTime.UtcNow.AddDays(-9).AddHours(slaHours), ProcessedAtUtc = DateTime.UtcNow.AddDays(-9).AddHours(5), ProcessedByUserId = SeedIds.AdminUserId, PaymentMethod = PayoutMethod.BankTransfer, DestinationAccount = "CBE 1000123456789" });
        }

        await db.SaveChangesAsync(ct);
    }
}
