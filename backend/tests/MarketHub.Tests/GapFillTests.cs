using MarketHub.Modules.Catalog.Domain;
using MarketHub.Modules.Catalog.Features;
using MarketHub.Modules.Catalog.Infrastructure;
using MarketHub.Shared.Contracts;
using MarketHub.Modules.Sales.Pricing;
using Xunit;

namespace MarketHub.Tests;

/// <summary>
/// Gap-fill v2 pure-logic coverage: reservation availability math, coupon validation,
/// delivery-PIN check, flash-sale discount bounds, verified-purchase predicate, return window.
/// </summary>

// ==================== RESERVATION AVAILABILITY ====================
public class ReservationMathTests
{
    [Theory]
    [InlineData(10, 0, 10)]   // nothing reserved
    [InlineData(10, 4, 6)]    // partial reservation subtracts
    [InlineData(10, 10, 0)]   // fully reserved
    [InlineData(10, 15, 0)]   // over-reserved (stale rows) — clamped at zero, never negative
    [InlineData(0, 0, 0)]     // no stock at all
    public void Available_Is_Stock_Minus_Active_Reservations(int stock, int reserved, int expected)
        => Assert.Equal(expected, ReservationMath.Available(stock, reserved));

    [Fact]
    public void CanReserve_Fits_Within_Availability_Only()
    {
        Assert.True(ReservationMath.CanReserve(10, 3, 7));   // exactly the remaining stock
        Assert.False(ReservationMath.CanReserve(10, 3, 8));  // one more than available
        Assert.False(ReservationMath.CanReserve(10, 0, 0));  // zero/negative qty never allowed
        Assert.False(ReservationMath.CanReserve(10, 0, -1));
    }

    [Fact]
    public void LineKey_Distinguishes_Product_Variant_And_Color_Scopes()
    {
        var product = Guid.Parse("00000000-0000-0000-0000-00000000aa01");
        var variant = Guid.Parse("00000000-0000-0000-0000-00000000aa02");
        var color = Guid.Parse("00000000-0000-0000-0000-00000000aa03");

        Assert.Equal($"p:{product}", ReservationMath.LineKey(product, null, null));
        Assert.Equal($"p:{product}:v:{variant}", ReservationMath.LineKey(product, variant, null));
        Assert.Equal($"p:{product}:c:{color}", ReservationMath.LineKey(product, null, color));
    }
}

// ==================== COUPON VALIDATION ====================
public class CouponCalculatorTests
{
    private static CouponQuote Quote(string type = "Percent", decimal value = 10m, decimal minSubtotal = 0m,
        decimal? maxDiscount = null, int maxUses = 100, int usedCount = 0,
        DateTime? startsAt = null, DateTime? expiresAt = null, bool isActive = true)
        => new("TEST", type, value, minSubtotal, maxDiscount, maxUses, usedCount, startsAt, expiresAt, isActive);

    private static readonly DateTime Now = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Percent_Coupon_Takes_Percentage_Of_Subtotal()
        => Assert.Equal(100m, CouponCalculator.Validate(Quote(value: 10m), 1000m, Now).Discount);

    [Fact]
    public void Percent_Coupon_Is_Capped_By_MaxDiscount()
        => Assert.Equal(300m, CouponCalculator.Validate(Quote(value: 10m, maxDiscount: 300m), 5000m, Now).Discount);

    [Fact]
    public void Fixed_Coupon_Takes_Absolute_Amount()
        => Assert.Equal(200m, CouponCalculator.Validate(Quote(type: "Fixed", value: 200m), 1500m, Now).Discount);

    [Fact]
    public void Discount_Never_Exceeds_The_Subtotal()
        => Assert.Equal(50m, CouponCalculator.Validate(Quote(type: "Fixed", value: 200m), 50m, Now).Discount);

    [Fact]
    public void Min_Subtotal_Gates_The_Coupon()
    {
        var (ok, discount, message) = CouponCalculator.Validate(Quote(minSubtotal: 1500m), 1000m, Now);
        Assert.False(ok);
        Assert.Equal(0m, discount);
        Assert.Contains("minimum subtotal", message);
    }

    [Fact]
    public void Expired_Or_Not_Yet_Active_Coupons_Are_Rejected()
    {
        Assert.False(CouponCalculator.Validate(Quote(expiresAt: Now.AddDays(-1)), 1000m, Now).Ok);
        Assert.False(CouponCalculator.Validate(Quote(startsAt: Now.AddDays(1)), 1000m, Now).Ok);
    }

    [Fact]
    public void Usage_Limit_And_Inactive_Flag_Are_Respected()
    {
        Assert.False(CouponCalculator.Validate(Quote(usedCount: 100, maxUses: 100), 1000m, Now).Ok);
        Assert.False(CouponCalculator.Validate(Quote(isActive: false), 1000m, Now).Ok);
    }

    [Fact]
    public void Null_Quote_Is_Rejected()
        => Assert.False(CouponCalculator.Validate(null!, 1000m, Now).Ok);
}

// ==================== DELIVERY PIN / TRACKING ====================
public class OrderCodesTests
{
    [Fact]
    public void Delivery_Pin_Is_Four_Digits()
    {
        for (var i = 0; i < 50; i++)
        {
            var pin = OrderCodes.NewDeliveryPin();
            Assert.True(pin.Length == 4 && pin.All(char.IsDigit));
        }
    }

    [Fact]
    public void Pin_Check_Requires_Exact_Match()
    {
        Assert.True(OrderCodes.PinMatches("0421", "0421"));
        Assert.True(OrderCodes.PinMatches("0421", " 0421 ")); // trimmed input tolerated
        Assert.False(OrderCodes.PinMatches("0421", "421"));   // wrong length
        Assert.False(OrderCodes.PinMatches("0421", "0422"));  // wrong digit
        Assert.False(OrderCodes.PinMatches(null, "0421"));    // no pin issued yet
        Assert.False(OrderCodes.PinMatches("0421", null));
        Assert.False(OrderCodes.PinMatches("0421", ""));      // blank
    }

    [Fact]
    public void Tracking_Codes_Follow_The_MH_TRK_Format()
    {
        var code = OrderCodes.NewTrackingCode();
        Assert.StartsWith("MH-TRK-", code);
        Assert.Equal(13, code.Length); // MH-TRK- + 6 unambiguous chars
        Assert.True(OrderCodes.LooksLikeTrackingCode(code));
        Assert.False(OrderCodes.LooksLikeTrackingCode("TRK-123"));
        Assert.False(OrderCodes.LooksLikeTrackingCode(null));
    }
}

// ==================== FLASH-SALE BOUNDS ====================
public class FlashSaleCalculatorTests
{
    [Theory]
    [InlineData(5, true)]    // inclusive lower bound
    [InlineData(50, true)]
    [InlineData(70, true)]   // inclusive upper bound
    [InlineData(4, false)]
    [InlineData(71, false)]
    [InlineData(0, false)]
    [InlineData(100, false)]
    public void Discount_Percent_Must_Be_Within_5_To_70(int pct, bool valid)
        => Assert.Equal(valid, FlashSaleCalculator.IsValidDiscountPct(pct));

    [Theory]
    [InlineData("24999.00", 10, 22499.10)]
    [InlineData("2450.00", 20, 1960.00)]
    [InlineData("1150.00", 15, 977.50)]
    public void Sale_Price_Computes_From_The_Base_Price(string price, int pct, decimal expected)
        => Assert.Equal(expected, FlashSaleCalculator.ComputeSalePrice(decimal.Parse(price), pct));

    [Fact]
    public void Out_Of_Range_Pct_Is_Clamped_Defensively_For_Pricing()
        => Assert.Equal(0m, FlashSaleCalculator.ComputeSalePrice(1000m, 150)); // clamp(150→100) ⇒ free
}

// ==================== VERIFIED-PURCHASE PREDICATE ====================
public class ReviewPolicyTests
{
    [Theory]
    [InlineData("Delivered", true)]
    [InlineData("Completed", true)]
    [InlineData("Placed", false)]            // not yet delivered
    [InlineData("PaymentConfirmed", false)]  // not yet delivered
    [InlineData("Shipped", false)]           // not yet delivered
    [InlineData("Cancelled", false)]
    [InlineData("Refunded", false)]
    [InlineData(null, false)]                // order not found / not the buyer's
    public void Only_Delivered_Or_Completed_Orders_Verify(string? status, bool expected)
        => Assert.Equal(expected, ReviewPolicy.IsVerifiedPurchase(status));
}

// ==================== RETURN WINDOW ====================
public class ReturnPolicyTests
{
    private static readonly DateTime Delivered = new(2025, 6, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Window_Is_Open_Until_Delivered_Plus_WindowDays()
    {
        Assert.True(ReturnPolicy.IsWithinWindow(Delivered, 7, Delivered.AddDays(7)));  // last day
        Assert.False(ReturnPolicy.IsWithinWindow(Delivered, 7, Delivered.AddDays(7).AddSeconds(1)));
    }

    [Fact]
    public void Window_Requires_A_Delivery_Timestamp()
    {
        Assert.False(ReturnPolicy.IsWithinWindow(null, 7, Delivered));
        Assert.False(ReturnPolicy.IsWithinWindow(Delivered, -1, Delivered)); // disabled window
    }
}

// ==================== SEARCH INDEX FALLBACK ==================
public class SearchIndexerFallbackTests
{
    [Fact]
    public async Task Null_Indexer_Returns_Null_So_Caller_Falls_Back_To_SQL()
    {
        var indexer = new NullSearchIndexer();
        Assert.Null(await indexer.SearchAsync("coffee", 6));
        await indexer.IndexProductAsync(new ProductSearchDoc(Guid.NewGuid(), "t", "d", "c", 1m, null, 0m, 0, "t", null)); // no-op
        await indexer.RemoveAsync(Guid.NewGuid()); // no-op
    }
}
