using MarketHub.Shared;
using MarketHub.Shared.Contracts.Events;
using MarketHub.Shared.Infrastructure;
using MarketHub.Modules.Finance.Domain;
using MarketHub.Modules.Finance.Persistence;
using MarketHub.Shared.Contracts;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace MarketHub.Modules.Finance.Features;

// ==================== PAYOUTS (12h SLA) ====================
public sealed record RequestPayoutCommand(Guid VendorId, decimal AmountETB, PayoutMethod PaymentMethod, string? DestinationAccount) : IRequest<Result<PayoutDto>>;

public sealed class RequestPayoutValidator : AbstractValidator<RequestPayoutCommand>
{
    public RequestPayoutValidator()
    {
        RuleFor(x => x.AmountETB).GreaterThanOrEqualTo(100m).WithMessage("Minimum payout amount is 100 Br.");
        RuleFor(x => x.PaymentMethod).IsInEnum();
    }
}

public sealed record PayoutDto(Guid Id, Guid VendorId, decimal AmountETB, string Status, DateTime RequestedAtUtc, DateTime DueByUtc, DateTime? ProcessedAtUtc, string PaymentMethod, string? DestinationAccount, string? AdminNote, int SlaHours, bool Overdue);

public sealed class RequestPayoutHandler(FinanceDbContext db, ISettingsProvider settings, IValidator<RequestPayoutCommand> validator)
    : IRequestHandler<RequestPayoutCommand, Result<PayoutDto>>
{
    public async Task<Result<PayoutDto>> Handle(RequestPayoutCommand c, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(c, ct);
        if (!validation.IsValid) return Result.Fail<PayoutDto>("validation", string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));

        var balance = await db.LedgerEntries.AsNoTracking()
            .Where(e => e.VendorId == c.VendorId && e.Description != "Refund processed")
            .Join(db.LedgerAccounts.Where(a => a.Code == LedgerAccounts.VendorPayable), e => e.AccountId, a => a.Id, (e, _) => e)
            .ToListAsync(ct);
        var currentBalance = Math.Round(balance.Sum(e => e.AmountCreditETB - e.AmountDebitETB), 2, MidpointRounding.AwayFromZero);

        var pending = await db.PayoutRequests.AsNoTracking()
            .Where(p => p.VendorId == c.VendorId && (p.Status == PayoutStatus.Requested || p.Status == PayoutStatus.Processing))
            .SumAsync(p => p.AmountETB, ct);
        var available = Math.Round(currentBalance - pending, 2, MidpointRounding.AwayFromZero);
        if (c.AmountETB > available)
            return Result.Fail<PayoutDto>("insufficient", $"Amount exceeds your available balance ({available} Br).");

        var slaHours = await settings.GetValueAsync<int>(SettingKeys.PayoutSlaHours, 12, ct);
        var payout = new PayoutRequest
        {
            VendorId = c.VendorId,
            AmountETB = Math.Round(c.AmountETB, 2, MidpointRounding.AwayFromZero),
            DueByUtc = DateTime.UtcNow.AddHours(slaHours), // ← 12-hour platform SLA
            PaymentMethod = c.PaymentMethod,
            DestinationAccount = c.DestinationAccount,
        };
        db.PayoutRequests.Add(payout);
        await db.SaveChangesAsync(ct);
        return Result.Ok(ToDto(payout, slaHours));
    }

    internal static PayoutDto ToDto(PayoutRequest p, int slaHours) => new(p.Id, p.VendorId, p.AmountETB, p.Status.ToString(),
        p.RequestedAtUtc, p.DueByUtc, p.ProcessedAtUtc, p.PaymentMethod.ToString(), p.DestinationAccount, p.AdminNote, slaHours,
        (p.Status == PayoutStatus.Requested || p.Status == PayoutStatus.Processing) && p.DueByUtc < DateTime.UtcNow);
}

public sealed record AdminProcessPayoutCommand(Guid AdminId, Guid PayoutId, bool Approve, string? AdminNote) : IRequest<Result>;

public sealed class AdminProcessPayoutHandler(FinanceDbContext db, IPublisher publisher) : IRequestHandler<AdminProcessPayoutCommand, Result>
{
    public async Task<Result> Handle(AdminProcessPayoutCommand c, CancellationToken ct)
    {
        var payout = await db.PayoutRequests.FirstOrDefaultAsync(p => p.Id == c.PayoutId, ct)
            ?? throw new NotFoundException("PayoutRequest", c.PayoutId);
        if (c.Approve)
        {
            payout.MarkPaid(c.AdminId);
            db.AddToOutbox(new PayoutProcessedEvent(payout.VendorId, payout.AmountETB, true));
        }
        else
        {
            payout.Reject(c.AdminId, c.AdminNote);
            db.AddToOutbox(new PayoutProcessedEvent(payout.VendorId, payout.AmountETB, false));
        }
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed record AdminListPayoutsQuery : IRequest<Result<IReadOnlyList<PayoutDto>>>;

public sealed class AdminListPayoutsHandler(FinanceDbContext db, ISettingsProvider settings) : IRequestHandler<AdminListPayoutsQuery, Result<IReadOnlyList<PayoutDto>>>
{
    public async Task<Result<IReadOnlyList<PayoutDto>>> Handle(AdminListPayoutsQuery q, CancellationToken ct)
    {
        var slaHours = await settings.GetValueAsync<int>(SettingKeys.PayoutSlaHours, 12, ct);
        var payouts = await db.PayoutRequests.AsNoTracking().OrderBy(p => p.RequestedAtUtc).ToListAsync(ct);
        return Result.Ok(payouts.Select(p => RequestPayoutHandler.ToDto(p, slaHours)).ToList());
    }
}

public sealed record VendorPayoutHistoryQuery(Guid VendorId) : IRequest<Result<IReadOnlyList<PayoutDto>>>;

public sealed class VendorPayoutHistoryHandler(FinanceDbContext db, ISettingsProvider settings) : IRequestHandler<VendorPayoutHistoryQuery, Result<IReadOnlyList<PayoutDto>>>
{
    public async Task<Result<IReadOnlyList<PayoutDto>>> Handle(VendorPayoutHistoryQuery q, CancellationToken ct)
    {
        var slaHours = await settings.GetValueAsync<int>(SettingKeys.PayoutSlaHours, 12, ct);
        var payouts = await db.PayoutRequests.AsNoTracking().Where(p => p.VendorId == q.VendorId).OrderByDescending(p => p.RequestedAtUtc).ToListAsync(ct);
        return Result.Ok(payouts.Select(p => RequestPayoutHandler.ToDto(p, slaHours)).ToList());
    }
}

public sealed record VendorEarningsQuery(Guid VendorId) : IRequest<Result<VendorEarningsDto>>;
public sealed record VendorEarningsDto(decimal BalanceETB, decimal AvailableForPayoutETB, decimal PendingPayoutETB, decimal TotalPaidOutETB, int SlaHours);

public sealed class VendorEarningsHandler(FinanceDbContext db, ISettingsProvider settings) : IRequestHandler<VendorEarningsQuery, Result<VendorEarningsDto>>
{
    public async Task<Result<VendorEarningsDto>> Handle(VendorEarningsQuery q, CancellationToken ct)
    {
        var account = await db.LedgerAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Code == LedgerAccounts.VendorPayable, ct)
            ?? throw new NotFoundException("LedgerAccount", LedgerAccounts.VendorPayable);
        var balance = await db.LedgerEntries.AsNoTracking()
            .Where(e => e.AccountId == account.Id && e.VendorId == q.VendorId)
            .ToListAsync(ct);
        var current = Math.Round(balance.Sum(e => e.AmountCreditETB - e.AmountDebitETB), 2, MidpointRounding.AwayFromZero);
        var pending = await db.PayoutRequests.AsNoTracking()
            .Where(p => p.VendorId == q.VendorId && (p.Status == PayoutStatus.Requested || p.Status == PayoutStatus.Processing))
            .SumAsync(p => p.AmountETB, ct);
        var paid = await db.PayoutRequests.AsNoTracking()
            .Where(p => p.VendorId == q.VendorId && p.Status == PayoutStatus.Paid)
            .SumAsync(p => p.AmountETB, ct);
        var slaHours = await settings.GetValueAsync<int>(SettingKeys.PayoutSlaHours, 12, ct);
        return Result.Ok(new VendorEarningsDto(current, Math.Round(current - pending, 2, MidpointRounding.AwayFromZero), Math.Round(pending, 2), Math.Round(paid, 2), slaHours));
    }
}

/// <summary>Quartz job: flags payout requests that breached the 12h SLA (never auto-pays).</summary>
public sealed class OverduePayoutJob(IServiceScopeFactory? scopeFactory = null) : IJob
{
    public static readonly JobKey Key = new("overdue-payouts");

    public async Task Execute(IJobExecutionContext ctx)
    {
        var factory = scopeFactory
            ?? ctx.JobDetail.JobDataMap.Get("scopeFactory") as IServiceScopeFactory
            ?? throw new InvalidOperationException("scopeFactory unavailable for OverduePayoutJob");
        using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<OverduePayoutJob>>();
        var overdue = await db.PayoutRequests
            .Where(p => (p.Status == PayoutStatus.Requested || p.Status == PayoutStatus.Processing) && p.DueByUtc < DateTime.UtcNow)
            .ToListAsync(ctx.CancellationToken);
        foreach (var p in overdue)
            logger.LogWarning("PAYOUT SLA BREACH: payout {Id} for vendor {Vendor} (due {Due}) is still {Status}", p.Id, p.VendorId, p.DueByUtc, p.Status);
        await Task.CompletedTask;
    }
}

// ==================== ADMIN REPORTS ====================
public sealed record ReportSummaryQuery(string Range) : IRequest<Result<ReportSummaryDto>>;
public sealed record ReportSummaryDto(decimal GrossSales, decimal PlatformIncome, decimal VendorIncome, decimal CommissionEarned,
    decimal DeliveryRevenue, decimal VatCollected, decimal RefundTotal, long OrdersCount, int PendingPayoutCount, decimal PendingPayoutAmount);

public sealed class ReportSummaryHandler(FinanceDbContext db, IOrderReader orders) : IRequestHandler<ReportSummaryQuery, Result<ReportSummaryDto>>
{
    public async Task<Result<ReportSummaryDto>> Handle(ReportSummaryQuery q, CancellationToken ct)
    {
        var from = q.Range switch
        {
            "today" => DateTime.UtcNow.Date,
            "7d" => DateTime.UtcNow.AddDays(-7),
            "30d" => DateTime.UtcNow.AddDays(-30),
            _ => null,
        };
        var entries = await db.LedgerEntries.AsNoTracking()
            .Where(e => from == null || e.CreatedAtUtc >= from)
            .Join(db.LedgerAccounts, e => e.AccountId, a => a.Id, (e, a) => new { e, a.Code })
            .ToListAsync(ct);

        static decimal Net(IEnumerable<(LedgerEntry e, string code)> rows, string code) => rows.Where(r => r.code == code).Sum(r => r.e.AmountCreditETB - r.e.AmountDebitETB);
        static decimal Debits(IEnumerable<(LedgerEntry e, string code)> rows, string code) => rows.Where(r => r.code == code).Sum(r => r.e.AmountDebitETB - r.e.AmountCreditETB);

        var gross = Math.Round(Debits(entries, LedgerAccounts.CashPspReceivable), 2, MidpointRounding.AwayFromZero); // cash account: debits are inflows
        var commission = Math.Round(Net(entries, LedgerAccounts.PlatformCommissionRevenue), 2, MidpointRounding.AwayFromZero);
        var delivery = Math.Round(Net(entries, LedgerAccounts.DeliveryRevenue), 2, MidpointRounding.AwayFromZero);
        var firstParty = Math.Round(Net(entries, LedgerAccounts.FirstPartySalesRevenue), 2, MidpointRounding.AwayFromZero);
        var vendor = Math.Round(Net(entries, LedgerAccounts.VendorPayable), 2, MidpointRounding.AwayFromZero);
        var vat = Math.Round(Net(entries, LedgerAccounts.VatPayable), 2, MidpointRounding.AwayFromZero);
        var refunds = Math.Round(Debits(entries, LedgerAccounts.RefundExpense), 2, MidpointRounding.AwayFromZero);

        var ordersCount = from == null
            ? await orders.CountOrdersAsync(null, null, ct)
            : await db.LedgerEntries.AsNoTracking().Where(e => e.Description == "Order payment received" && e.CreatedAtUtc >= from).Select(e => e.OrderId).Distinct().LongCountAsync(ct);

        var pendingPayouts = await db.PayoutRequests.AsNoTracking()
            .Where(p => p.Status == PayoutStatus.Requested || p.Status == PayoutStatus.Processing)
            .ToListAsync(ct);

        return Result.Ok(new ReportSummaryDto(gross, Math.Round(commission + delivery + firstParty, 2, MidpointRounding.AwayFromZero), vendor, commission, delivery, vat, refunds,
            ordersCount, pendingPayouts.Count, Math.Round(pendingPayouts.Sum(p => p.AmountETB), 2, MidpointRounding.AwayFromZero)));
    }
}

public sealed record RevenueOverTimeQuery(int Days) : IRequest<Result<RevenueOverTimeDto>>;
public sealed record RevenueOverTimeDto(IReadOnlyList<string> Labels, IReadOnlyList<SeriesDto> Series);
public sealed record SeriesDto(string Name, IReadOnlyList<decimal> Data);

public sealed class RevenueOverTimeHandler(FinanceDbContext db) : IRequestHandler<RevenueOverTimeQuery, Result<RevenueOverTimeDto>>
{
    public async Task<Result<RevenueOverTimeDto>> Handle(RevenueOverTimeQuery q, CancellationToken ct)
    {
        var days = Math.Clamp(q.Days, 7, 90);
        var from = DateTime.UtcNow.AddDays(-days).Date;
        var entries = await db.LedgerEntries.AsNoTracking()
            .Where(e => e.CreatedAtUtc >= from && (e.Description == "Order payment received" || e.Description == "Refund processed"))
            .Join(db.LedgerAccounts, e => e.AccountId, a => a.Id, (e, a) => new { e, a.Code })
            .ToListAsync(ct);

        var labels = new List<string>();
        var gross = new List<decimal>();
        var platform = new List<decimal>();
        var vendor = new List<decimal>();
        for (var d = days - 1; d >= 0; d--)
        {
            var day = DateTime.UtcNow.Date.AddDays(-d);
            var dayRows = entries.Where(r => r.e.CreatedAtUtc.Date == day).ToList();
            labels.Add(day.ToString("MM-dd"));
            gross.Add(Math.Round(dayRows.Where(r => r.Code == LedgerAccounts.CashPspReceivable).Sum(r => r.e.AmountDebitETB), 2));
            platform.Add(Math.Round(dayRows.Where(r => r.Code is LedgerAccounts.PlatformCommissionRevenue or LedgerAccounts.DeliveryRevenue or LedgerAccounts.FirstPartySalesRevenue).Sum(r => r.e.AmountCreditETB - r.e.AmountDebitETB), 2));
            vendor.Add(Math.Round(dayRows.Where(r => r.Code == LedgerAccounts.VendorPayable).Sum(r => r.e.AmountCreditETB - r.e.AmountDebitETB), 2));
        }
        return Result.Ok(new RevenueOverTimeDto(labels, new[] { new SeriesDto("Gross Sales", gross), new SeriesDto("Platform Income", platform), new SeriesDto("Vendor Income", vendor) }));
    }
}

public sealed record TopVendorsQuery(int Limit) : IRequest<Result<IReadOnlyList<TopVendorDto>>>;
public sealed record TopVendorDto(Guid VendorId, decimal IncomeETB);

public sealed class TopVendorsHandler(FinanceDbContext db) : IRequestHandler<TopVendorsQuery, Result<IReadOnlyList<TopVendorDto>>>
{
    public async Task<Result<IReadOnlyList<TopVendorDto>>> Handle(TopVendorsQuery q, CancellationToken ct)
    {
        var account = await db.LedgerAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Code == LedgerAccounts.VendorPayable, ct)
            ?? throw new NotFoundException("LedgerAccount", LedgerAccounts.VendorPayable);
        var rows = await db.LedgerEntries.AsNoTracking()
            .Where(e => e.AccountId == account.Id && e.VendorId != null && e.Description == "Order payment received")
            .GroupBy(e => e.VendorId!.Value)
            .Select(g => new { VendorId = g.Key, Income = g.Sum(e => e.AmountCreditETB - e.AmountDebitETB) })
            .OrderByDescending(x => x.Income).Take(Math.Clamp(q.Limit, 1, 20)).ToListAsync(ct);
        return Result.Ok(rows.Select(r => new TopVendorDto(r.VendorId, r.Income)).ToList());
    }
}

public sealed record TopProductsQuery(int Limit) : IRequest<Result<IReadOnlyList<TopProductRow>>>;

public sealed class TopProductsHandler(IOrderReader orders, ICatalogReader catalog) : IRequestHandler<TopProductsQuery, Result<IReadOnlyList<TopProductRow>>>
{
    public async Task<Result<IReadOnlyList<TopProductRow>>> Handle(TopProductsQuery q, CancellationToken ct)
    {
        var aggregates = await orders.GetTopProductAggregatesAsync(Math.Clamp(q.Limit, 1, 20), null, ct);
        var titles = await catalog.GetProductTitlesAsync(aggregates.Select(a => a.ProductId).ToList(), ct);
        return Result.Ok(aggregates.Select(a => new TopProductRow(a.ProductId, titles.GetValueOrDefault(a.ProductId, "Removed product"), a.SoldQty, a.RevenueETB)).ToList());
    }
}

public sealed record VendorIncomeQuery(Guid VendorId) : IRequest<Result<VendorIncomeDto>>;
public sealed record VendorIncomeDto(Guid VendorId, decimal BalanceETB, decimal AvailableForPayoutETB, decimal TotalPaidOutETB, decimal PendingPayoutETB);

public sealed class VendorIncomeHandler(FinanceDbContext db, ISettingsProvider settings) : IRequestHandler<VendorIncomeQuery, Result<VendorIncomeDto>>
{
    public async Task<Result<VendorIncomeDto>> Handle(VendorIncomeQuery q, CancellationToken ct)
    {
        var earnings = await new VendorEarningsHandler(db, settings).Handle(new VendorEarningsQuery(q.VendorId), ct);
        return Result.Ok(new VendorIncomeDto(q.VendorId, earnings.Value.BalanceETB, earnings.Value.AvailableForPayoutETB, earnings.Value.TotalPaidOutETB, earnings.Value.PendingPayoutETB));
    }
}

// ==================== PLATFORM SETTINGS (admin, grouped known keys) ====================
public sealed record GetSettingsQuery : IRequest<Result<IReadOnlyList<SettingEntryDto>>>;
public sealed record SettingEntryDto(string Key, string Value, string? Description);

public sealed class GetSettingsHandler(FinanceDbContext db) : IRequestHandler<GetSettingsQuery, Result<IReadOnlyList<SettingEntryDto>>>
{
    public async Task<Result<IReadOnlyList<SettingEntryDto>>> Handle(GetSettingsQuery q, CancellationToken ct)
    {
        // grouped known keys (SettingKeys.KnownKeys), in declaration order — unknown rows are hidden
        var stored = await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => (Value: s.Value, Description: s.Description), ct);
        var items = new List<SettingEntryDto>();
        foreach (var key in SettingKeys.KnownKeys)
        {
            if (stored.TryGetValue(key, out var row))
                items.Add(new SettingEntryDto(key, row.Value, row.Description));
            else
                items.Add(new SettingEntryDto(key, DefaultValueFor(key), null));
        }
        return Result.Ok((IReadOnlyList<SettingEntryDto>)items);
    }

    internal static string DefaultValueFor(string key) => key switch
    {
        SettingKeys.VatRate => "15",
        SettingKeys.DefaultCommissionRate => "0.10",
        SettingKeys.ReturnWindowDays => "7",
        SettingKeys.PaymentProvider => "Mock",
        SettingKeys.PayoutSlaHours => "12",
        SettingKeys.PlatformName => "MarketHub",
        SettingKeys.ReservationMinutes => "20",
        SettingKeys.AbandonedCartHours => "2",
        SettingKeys.SmsProvider => "Mock",
        SettingKeys.EmailProvider => "Mock",
        SettingKeys.SearchProvider => "Meilisearch",
        SettingKeys.FreeDeliveryAddisThreshold => "1000",
        _ => "",
    };
}

public sealed record PutSettingsCommand(IReadOnlyList<SettingEntryDto> Entries) : IRequest<Result<IReadOnlyList<SettingEntryDto>>>;

public sealed class PutSettingsHandler(FinanceDbContext db) : IRequestHandler<PutSettingsCommand, Result<IReadOnlyList<SettingEntryDto>>>
{
    public async Task<Result<IReadOnlyList<SettingEntryDto>>> Handle(PutSettingsCommand c, CancellationToken ct)
    {
        foreach (var entry in c.Entries)
        {
            if (!SettingKeys.KnownKeys.Contains(entry.Key))
                return Result.Fail<IReadOnlyList<SettingEntryDto>>("unknown_key", $"Unknown setting key '{entry.Key}'.");
            if (!IsValid(entry.Key, entry.Value))
                return Result.Fail<IReadOnlyList<SettingEntryDto>>("invalid_value", $"Value '{entry.Value}' is not valid for '{entry.Key}'.");
        }

        foreach (var entry in c.Entries)
        {
            var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == entry.Key, ct);
            if (setting is null)
                db.Settings.Add(new Setting { Key = entry.Key, Value = entry.Value, Description = null });
            else if (setting.Value != entry.Value)
            {
                setting.Value = entry.Value;
                setting.ModifiedAtUtc = DateTime.UtcNow;
            }
        }
        await db.SaveChangesAsync(ct);
        return await new GetSettingsHandler(db).Handle(new GetSettingsQuery(), ct);
    }

    private static bool IsValid(string key, string value)
    {
        try
        {
            switch (key)
            {
                case SettingKeys.VatRate or SettingKeys.DefaultCommissionRate:
                    return decimal.TryParse(value, out var d) && d >= 0 && d <= 100;
                case SettingKeys.ReturnWindowDays or SettingKeys.PayoutSlaHours or SettingKeys.ReservationMinutes
                    or SettingKeys.AbandonedCartHours or SettingKeys.FreeDeliveryAddisThreshold:
                    return int.TryParse(value, out var i) && i >= 0;
                case SettingKeys.PaymentProvider:
                    return value is "Mock" or "Chapa" or "Telebirr" or "Santimpay";
                case SettingKeys.SmsProvider:
                    return value is "Mock" or "EthioTelecom" or "Safaricom";
                case SettingKeys.EmailProvider:
                    return value is "Mock" or "Smtp";
                case SettingKeys.SearchProvider:
                    return value is "Meilisearch" or "Null";
                default:
                    return !string.IsNullOrWhiteSpace(value); // PlatformName
            }
        }
        catch { return false; }
    }
}

// ==================== COMMISSION ADMIN ====================
public sealed record GetCommissionConfigQuery : IRequest<Result<CommissionConfigDto>>;
public sealed record CommissionConfigDto(decimal DefaultRate, IReadOnlyList<OverrideDto> Overrides);
public sealed record OverrideDto(Guid? VendorId, Guid? CategoryId, decimal Rate, DateTime EffectiveFromUtc, string? Note);

public sealed class GetCommissionConfigHandler(FinanceDbContext db, ISettingsProvider settings) : IRequestHandler<GetCommissionConfigQuery, Result<CommissionConfigDto>>
{
    public async Task<Result<CommissionConfigDto>> Handle(GetCommissionConfigQuery q, CancellationToken ct)
    {
        var rate = await settings.GetValueAsync<decimal>(SettingKeys.DefaultCommissionRate, 0.10m, ct);
        var overrides = await db.CommissionOverrides.AsNoTracking().OrderByDescending(o => o.EffectiveFromUtc).ToListAsync(ct);
        return Result.Ok(new CommissionConfigDto(rate, overrides.Select(o => new OverrideDto(o.VendorId, o.CategoryId, o.Rate, o.EffectiveFromUtc, o.Note)).ToList()));
    }
}

public sealed record SetDefaultCommissionCommand(decimal Rate) : IRequest<Result>;

public sealed class SetDefaultCommissionHandler(FinanceDbContext db) : IRequestHandler<SetDefaultCommissionCommand, Result>
{
    public async Task<Result> Handle(SetDefaultCommissionCommand c, CancellationToken ct)
    {
        if (c.Rate is < 0m or > 0.5m) return Result.Failure(new Error("rate", "Commission rate must be between 0 and 0.5."));
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DefaultCommissionRate, ct);
        if (setting is null) db.Settings.Add(new Setting { Key = SettingKeys.DefaultCommissionRate, Value = c.Rate.ToString("0.00##"), Description = "Platform commission on vendor sales" });
        else setting.Value = c.Rate.ToString("0.00##");
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}
