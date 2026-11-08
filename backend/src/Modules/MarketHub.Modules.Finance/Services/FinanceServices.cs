using MarketHub.Shared;
using MarketHub.Modules.Finance.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketHub.Modules.Finance.Services;

/// <summary>Settings-backed provider registered by the Finance module (single source of truth for VAT, commission, SLA...).</summary>
public sealed class SettingsProvider(IServiceProvider sp) : MarketHub.Shared.ISettingsProvider
{
    public async Task<T> GetValueAsync<T>(string key, T defaultValue, CancellationToken ct = default)
    {
        var scope = sp.CreateScope();
        try
        {
            var raw = await scope.ServiceProvider.GetRequiredService<FinanceDbContext>().Settings.AsNoTracking()
                .Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
            if (raw is null) return defaultValue;
            var type = typeof(T);
            try { return (T)Convert.ChangeType(raw, Nullable.GetUnderlyingType(type) ?? type); }
            catch { return defaultValue; }
        }
        finally { scope.Dispose(); }
    }

    public async Task<string?> GetRawAsync(string key, CancellationToken ct = default)
    {
        var scope = sp.CreateScope();
        try
        {
            return await scope.ServiceProvider.GetRequiredService<FinanceDbContext>().Settings.AsNoTracking()
                .Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
        }
        finally { scope.Dispose(); }
    }
}

/// <summary>Commission precedence: vendor+category → vendor → category → global default (Settings).</summary>
public sealed class CommissionResolver(IServiceProvider sp) : MarketHub.Shared.Contracts.ICommissionResolver
{
    public async Task<decimal> ResolveAsync(Guid? vendorId, Guid? categoryId, CancellationToken ct = default)
    {
        var scope = sp.CreateScope();
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
            var now = DateTime.UtcNow;
            var overrides = await db.CommissionOverrides.AsNoTracking()
                .Where(o => o.EffectiveFromUtc <= now)
                .ToListAsync(ct);

            var vendorCategory = vendorId.HasValue && categoryId.HasValue
                ? overrides.FirstOrDefault(o => o.VendorId == vendorId && o.CategoryId == categoryId) : null;
            if (vendorCategory is not null) return vendorCategory.Rate;
            var vendor = vendorId.HasValue ? overrides.FirstOrDefault(o => o.VendorId == vendorId && o.CategoryId == null) : null;
            if (vendor is not null) return vendor.Rate;
            var category = categoryId.HasValue ? overrides.FirstOrDefault(o => o.CategoryId == categoryId && o.VendorId == null) : null;
            if (category is not null) return category.Rate;

            var global = await db.Settings.AsNoTracking().Where(s => s.Key == SettingKeys.DefaultCommissionRate).Select(s => s.Value).FirstOrDefaultAsync(ct);
            return decimal.TryParse(global, out var rate) ? rate : 0.10m;
        }
        finally { scope.Dispose(); }
    }
}

/// <summary>Pure refund-leg calculator (public static for testability).</summary>
public static class RefundLegsCalculator
{
    /// <summary>Computes proportional reversal legs for a refund of <paramref name="amount"/> against an order payment.</summary>
    public static (string AccountCode, decimal Debit, decimal Credit)[] ComputeRefundLegs(
        decimal grandTotal, decimal vat, decimal delivery, decimal commission, decimal vendorNet, decimal platformNet, decimal amount)
    {
        if (amount <= 0 || amount > grandTotal) throw new DomainException("Refund amount must be within the paid total.");
        var ratio = amount / grandTotal;
        var legs = new List<(string, decimal, decimal)>
        {
            (LedgerAccounts.CashPspReceivable, 0m, amount),                       // CR cash out to buyer
            (LedgerAccounts.VatPayable, Math.Round(vat * ratio, 2, MidpointRounding.AwayFromZero), 0m),
            (LedgerAccounts.DeliveryRevenue, Math.Round(delivery * ratio, 2, MidpointRounding.AwayFromZero), 0m),
            (LedgerAccounts.PlatformCommissionRevenue, Math.Round(commission * ratio, 2, MidpointRounding.AwayFromZero), 0m),
            (LedgerAccounts.VendorPayable, Math.Round(vendorNet * ratio, 2, MidpointRounding.AwayFromZero), 0m),
        };
        if (platformNet > 0.009m)
            legs.Add((LedgerAccounts.FirstPartySalesRevenue, Math.Round(platformNet * ratio, 2, MidpointRounding.AwayFromZero), 0m));
        legs.Add((LedgerAccounts.RefundExpense, amount, 0m));                     // DR refund expense
        legs.Add((LedgerAccounts.CashPspReceivable, 0m, amount));                 // CR PSP payout leg
        return legs.ToArray();
    }
}

/// <summary>Posts balanced double-entry entries for confirmed payments and refunds. Any unbalanced posting throws.</summary>
public sealed class LedgerPoster(IServiceProvider sp) : MarketHub.Shared.Contracts.ILedgerPoster
{
    /// <summary>Asserts Σdebits == Σcredits ≥ 0 and every leg non-negative. Throws DomainException otherwise.</summary>
    public static void AssertBalanced(IReadOnlyList<(Guid AccountId, Guid? VendorId, decimal Debit, decimal Credit)> legs)
    {
        var dr = Math.Round(legs.Sum(l => l.Debit), 2, MidpointRounding.AwayFromZero);
        var cr = Math.Round(legs.Sum(l => l.Credit), 2, MidpointRounding.AwayFromZero);
        if (dr != cr || legs.Any(l => l.Debit < 0 || l.Credit < 0))
            throw new DomainException($"Ledger entry unbalanced: DR {dr} vs CR {cr}");
    }

    /// <summary>
    /// Posts a full payment confirmation:
    /// DR 1000 Cash (grand total) | CR 2100 VAT, CR 3100 Delivery, CR 3000 Commission,
    /// CR 2000 VendorPayable (per vendor), CR 3200 FirstPartyRevenue. Idempotent per order.
    /// </summary>
    public async Task PostOrderPaymentAsync(Guid orderId, CancellationToken ct = default)
    {
        var scope = sp.CreateScope();
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
            var orderReader = scope.ServiceProvider.GetRequiredService<MarketHub.Shared.Contracts.IOrderReader>();
            if (await db.LedgerEntries.AnyAsync(e => e.OrderId == orderId && e.Description == "Order payment received", ct))
                return; // idempotent

            var financials = await orderReader.GetFinancialsAsync(orderId, ct) ?? throw new NotFoundException("Order", orderId);
            var commission = Math.Round(financials.Items.Sum(i => i.CommissionETB), 2, MidpointRounding.AwayFromZero);

            // per-vendor net = Σ(lineNet − commission); platform margin = Σ(platform lineNet − platform commission)
            var vendorNets = financials.Items.Where(i => i.VendorId != null)
                .GroupBy(i => i.VendorId!.Value)
                .ToDictionary(g => g.Key, g => Math.Round(g.Sum(i => i.LineNetETB - i.CommissionETB), 2, MidpointRounding.AwayFromZero));
            var platformNet = Math.Round(financials.Items.Where(i => i.VendorId == null).Sum(i => i.LineNetETB - i.CommissionETB), 2, MidpointRounding.AwayFromZero);

            var accounts = await db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(a => a.Code, ct);
            Guid Acc(string code) => accounts.TryGetValue(code, out var a) ? a.Id : throw new NotFoundException("LedgerAccount", code);

            var legs = new List<(Guid AccountId, Guid? VendorId, decimal Debit, decimal Credit)>
            {
                (Acc(LedgerAccounts.CashPspReceivable), null, financials.GrandTotalETB, 0m),
                (Acc(LedgerAccounts.VatPayable), null, 0m, financials.VatETB),
                (Acc(LedgerAccounts.DeliveryRevenue), null, 0m, financials.DeliveryFeeETB),
            };
            if (commission > 0m) legs.Add((Acc(LedgerAccounts.PlatformCommissionRevenue), null, 0m, commission));
            foreach (var (vendorId, net) in vendorNets) legs.Add((Acc(LedgerAccounts.VendorPayable), vendorId, 0m, net));
            if (platformNet > 0.009m) legs.Add((Acc(LedgerAccounts.FirstPartySalesRevenue), null, 0m, platformNet));

            // rounding absorb on the last credit leg so Σcredits == grandTotal exactly
            var credits = Math.Round(legs.Sum(l => l.Credit), 2, MidpointRounding.AwayFromZero);
            var diff = Math.Round(financials.GrandTotalETB - credits, 2, MidpointRounding.AwayFromZero);
            if (diff != 0m && legs.Count > 3)
            {
                var last = legs[^1];
                legs[^1] = (last.AccountId, last.VendorId, last.Debit, Math.Round(last.Credit + diff, 2, MidpointRounding.AwayFromZero));
            }

            AssertBalanced(legs);
            db.LedgerEntries.AddRange(legs.Select(l => new LedgerEntry
            {
                AccountId = l.AccountId,
                OrderId = orderId,
                VendorId = l.VendorId,
                AmountDebitETB = l.Debit,
                AmountCreditETB = l.Credit,
                Description = "Order payment received",
            }));
            await db.SaveChangesAsync(ct);
        }
        finally { scope.Dispose(); }
    }

    public async Task PostRefundAsync(Guid orderId, decimal amount, CancellationToken ct = default)
    {
        var scope = sp.CreateScope();
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
            var orderReader = scope.ServiceProvider.GetRequiredService<MarketHub.Shared.Contracts.IOrderReader>();
            var financials = await orderReader.GetFinancialsAsync(orderId, ct) ?? throw new NotFoundException("Order", orderId);
            var commission = Math.Round(financials.Items.Sum(i => i.CommissionETB), 2, MidpointRounding.AwayFromZero);
            var vendorNet = Math.Round(financials.Items.Where(i => i.VendorId != null).Sum(i => i.LineNetETB - i.CommissionETB), 2, MidpointRounding.AwayFromZero);
            var platformNet = Math.Round(financials.Items.Where(i => i.VendorId == null).Sum(i => i.LineNetETB - i.CommissionETB), 2, MidpointRounding.AwayFromZero);

            var legs = RefundLegsCalculator.ComputeRefundLegs(financials.GrandTotalETB, financials.VatETB, financials.DeliveryFeeETB, commission, vendorNet, platformNet, amount);
            var accounts = await db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(a => a.Code, ct);

            var mapped = legs.Select(l => (AccountId: accounts.TryGetValue(l.AccountCode, out var a) ? a.Id : throw new NotFoundException("LedgerAccount", l.AccountCode), VendorId: (Guid?)null, Debit: l.Debit, Credit: l.Credit)).ToList();
            AssertBalanced(mapped);

            db.LedgerEntries.AddRange(mapped.Select(l => new LedgerEntry
            {
                AccountId = l.AccountId,
                OrderId = orderId,
                AmountDebitETB = l.Debit,
                AmountCreditETB = l.Credit,
                Description = "Refund processed",
            }));
            await db.SaveChangesAsync(ct);
        }
        finally { scope.Dispose(); }
    }
}
