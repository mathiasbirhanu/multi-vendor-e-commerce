namespace MarketHub.Modules.Sales.Pricing;

/// <summary>VAT calculation — mirrors the preview mock exactly (rate% of subtotal+delivery, 2dp away from zero).</summary>
public static class VatCalculator
{
    public static decimal CalculateVat(decimal taxableBase, decimal deliveryFee, decimal vatRatePercent)
        => Math.Round((taxableBase + deliveryFee) * vatRatePercent / 100m, 2, MidpointRounding.AwayFromZero);
}

/// <summary>Delivery fee: base + per-kg surcharge above 5 kg, free above the zone threshold.</summary>
public static class DeliveryFeeCalculator
{
    public static decimal Calculate(decimal baseFeeETB, decimal perKgSurchargeETB, decimal freeThresholdETB, decimal totalWeightKg, decimal subtotalETB)
    {
        if (freeThresholdETB > 0 && subtotalETB >= freeThresholdETB) return 0m;
        return Math.Round(baseFeeETB + perKgSurchargeETB * Math.Max(0m, totalWeightKg - 5m), 2, MidpointRounding.AwayFromZero);
    }
}

/// <summary>
/// Pure coupon validation math (unit-tested). Percent coupons take <c>Value</c> as a percentage capped by
/// <c>MaxDiscount</c>; Fixed coupons take <c>Value</c> as an absolute ETB amount. A coupon never discounts
/// more than the subtotal.
/// </summary>
public static class CouponCalculator
{
    public static (bool Ok, decimal Discount, string Message) Validate(
        MarketHub.Shared.Contracts.CouponQuote coupon, decimal subtotal, DateTime nowUtc)
    {
        if (coupon is null) return (false, 0m, "Coupon code not found.");
        if (!coupon.IsActive) return (false, 0m, "This coupon is no longer active.");
        if (coupon.StartsAtUtc.HasValue && nowUtc < coupon.StartsAtUtc.Value)
            return (false, 0m, $"This coupon starts on {coupon.StartsAtUtc.Value:yyyy-MM-dd}.");
        if (coupon.ExpiresAtUtc.HasValue && nowUtc > coupon.ExpiresAtUtc.Value)
            return (false, 0m, "This coupon has expired.");
        if (coupon.UsedCount >= coupon.MaxUses)
            return (false, 0m, "This coupon has reached its usage limit.");
        if (subtotal < coupon.MinSubtotal)
            return (false, 0m, $"This coupon requires a minimum subtotal of {coupon.MinSubtotal:0.##} Br.");

        var raw = coupon.Type == "Percent"
            ? subtotal * coupon.Value / 100m
            : coupon.Value;
        if (coupon.MaxDiscount.HasValue) raw = Math.Min(raw, coupon.MaxDiscount.Value);
        var discount = Math.Round(Math.Min(raw, subtotal), 2, MidpointRounding.AwayFromZero);
        return (true, discount, $"Coupon {coupon.Code} applied.");
    }
}

/// <summary>Cryptographically random delivery PIN (4 digits) and MH-TRK tracking codes.</summary>
public static class OrderCodes
{
    public static string NewDeliveryPin() => System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 10_000).ToString("D4");

    /// <summary>PIN check is constant-independent (4-digit space, compare exact match).</summary>
    public static bool PinMatches(string? expectedPin, string? suppliedPin)
        => !string.IsNullOrWhiteSpace(expectedPin)
           && !string.IsNullOrWhiteSpace(suppliedPin)
           && string.Equals(expectedPin, suppliedPin.Trim(), StringComparison.Ordinal);

    public static string NewTrackingCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no ambiguous 0/O/1/I
        Span<char> suffix = stackalloc char[6];
        for (var i = 0; i < suffix.Length; i++)
            suffix[i] = alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, alphabet.Length)];
        return $"MH-TRK-{new string(suffix)}";
    }

    public static bool LooksLikeTrackingCode(string? code)
        => !string.IsNullOrWhiteSpace(code) && code.StartsWith("MH-TRK-", StringComparison.Ordinal) && code.Length is >= 13 and <= 24;
}

/// <summary>Per-item return window (unit-tested): from DeliveredAt + ReturnWindowDays.</summary>
public static class ReturnPolicy
{
    public static bool IsWithinWindow(DateTime? deliveredAtUtc, int windowDays, DateTime nowUtc)
        => deliveredAtUtc.HasValue
           && windowDays >= 0
           && nowUtc <= deliveredAtUtc.Value.AddDays(windowDays);
}
