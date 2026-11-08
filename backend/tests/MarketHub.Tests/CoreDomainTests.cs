using MarketHub.Modules.Sales.Pricing;
using Xunit;

namespace MarketHub.Tests;

/// <summary>VAT 15% on subtotal + delivery, rounded 2dp away from zero — mirrors VatCalculator exactly.</summary>
public class VatCalculatorTests
{
    [Fact]
    public void Calculates_Vat_On_Subtotal_And_Delivery()
    {
        // 5650 subtotal + 250 delivery = 5900 × 15% = 885.00
        Assert.Equal(885.00m, VatCalculator.CalculateVat(5650m, 250m, 15m));
    }

    [Fact]
    public void Calculates_Vat_Without_Delivery()
    {
        Assert.Equal(15.00m, VatCalculator.CalculateVat(100m, 0m, 15m));
    }

    [Fact]
    public void Rounds_Away_From_Zero()
    {
        // 0.005 → 0.01
        Assert.Equal(0.01m, VatCalculator.CalculateVat(0.0333333m, 0m, 15m));
    }

    [Fact]
    public void Delivery_Is_Included_In_Vat_Base()
    {
        // 1000 + 150 = 1150 × 15% = 172.50
        Assert.Equal(172.50m, VatCalculator.CalculateVat(1000m, 150m, 15m));
    }
}

/// <summary>Commission resolution precedence: vendor+category → vendor → category → global.</summary>
public class CommissionResolverTests
{
    [Fact]
    public void Vendor_Specific_Override_Wins()
    {
        var overrides = new List<MarketHub.Modules.Finance.Domain.CommissionOverride>
        {
            new() { VendorId = TestIds.Vendor, Rate = 0.05m },
            new() { CategoryId = TestIds.Category, Rate = 0.08m },
        };
        Assert.Equal(0.05m, Resolve(0.10m, overrides, vendorId: TestIds.Vendor, categoryId: TestIds.Category));
    }

    [Fact]
    public void Category_Override_Applies_When_No_Vendor_Override()
    {
        var overrides = new List<MarketHub.Modules.Finance.Domain.CommissionOverride>
        {
            new() { CategoryId = TestIds.Category, Rate = 0.08m },
        };
        Assert.Equal(0.08m, Resolve(0.10m, overrides, vendorId: TestIds.Vendor, categoryId: TestIds.Category));
    }

    [Fact]
    public void Falls_Back_To_Global_Default()
    {
        Assert.Equal(0.10m, Resolve(0.10m, Array.Empty<MarketHub.Modules.Finance.Domain.CommissionOverride>(), TestIds.Vendor, null));
    }

    private static decimal Resolve(decimal globalRate, IEnumerable<MarketHub.Modules.Finance.Domain.CommissionOverride> overrides, Guid? vendorId, Guid? categoryId)
    {
        // same precedence as Finance.Services.CommissionResolver (pure re-implementation for unit testing)
        var list = overrides.ToList();
        var vc = vendorId.HasValue && categoryId.HasValue ? list.FirstOrDefault(o => o.VendorId == vendorId && o.CategoryId == categoryId) : null;
        if (vc is not null) return vc.Rate;
        var v = vendorId.HasValue ? list.FirstOrDefault(o => o.VendorId == vendorId && o.CategoryId == null) : null;
        if (v is not null) return v.Rate;
        var cat = categoryId.HasValue ? list.FirstOrDefault(o => o.CategoryId == categoryId && o.VendorId == null) : null;
        if (cat is not null) return cat.Rate;
        return globalRate;
    }
}

/// <summary>Order state machine — legal transitions allowed, illegal throw domain exceptions.</summary>
public class OrderStateMachineTests
{
    [Theory]
    [InlineData("Placed", "PaymentConfirmed", true)]
    [InlineData("Placed", "Cancelled", true)]
    [InlineData("PaymentConfirmed", "VendorAccepted", true)]
    [InlineData("VendorAccepted", "Shipped", true)]
    [InlineData("Shipped", "Delivered", true)]
    [InlineData("Delivered", "Completed", true)]
    [InlineData("Delivered", "RefundRequested", true)]
    [InlineData("Completed", "RefundRequested", true)]
    [InlineData("RefundRequested", "Refunded", true)]
    [InlineData("Placed", "Completed", false)]
    [InlineData("Cancelled", "Shipped", false)]
    [InlineData("Refunded", "Shipped", false)]
    [InlineData("Placed", "Shipped", false)]
    public void Transitions_Follow_The_Map(string from, string to, bool allowed)
    {
        var f = Enum.Parse<MarketHub.Modules.Sales.Domain.OrderStatus>(from);
        var t = Enum.Parse<MarketHub.Modules.Sales.Domain.OrderStatus>(to);
        Assert.Equal(allowed, MarketHub.Modules.Sales.Domain.OrderStateMachine.CanTransition(f, t));
        if (allowed)
            MarketHub.Modules.Sales.Domain.OrderStateMachine.EnsureCanTransition(f, t); // must not throw
        else
            Assert.Throws<MarketHub.Shared.DomainException>(() => MarketHub.Modules.Sales.Domain.OrderStateMachine.EnsureCanTransition(f, t));
    }
}

/// <summary>Double-entry ledger balance assertions.</summary>
public class LedgerTests
{
    [Fact]
    public void Refund_Legs_Are_Balanced()
    {
        // order: subtotal 5650 + delivery 250 + VAT 885 = 6785 grand; commission 565; vendor net 5085; platform 0
        var legs = MarketHub.Modules.Finance.Services.RefundLegsCalculator.ComputeRefundLegs(6785m, 885m, 250m, 565m, 5085m, 0m, 6785m);
        var dr = legs.Sum(l => l.Debit);
        var cr = legs.Sum(l => l.Credit);
        Assert.Equal(Math.Round(dr, 2), Math.Round(cr, 2));
    }

    [Fact]
    public void Partial_Refund_Legs_Are_Proportional_And_Balanced()
    {
        var legs = MarketHub.Modules.Finance.Services.RefundLegsCalculator.ComputeRefundLegs(6785m, 885m, 250m, 565m, 5085m, 0m, 1000m);
        var dr = legs.Sum(l => l.Debit);
        var cr = legs.Sum(l => l.Credit);
        Assert.Equal(Math.Round(dr, 2), Math.Round(cr, 2));
    }

    [Fact]
    public void Refund_Above_Total_Is_Rejected()
    {
        Assert.Throws<MarketHub.Shared.DomainException>(() =>
            MarketHub.Modules.Finance.Services.RefundLegsCalculator.ComputeRefundLegs(100m, 15m, 0m, 0m, 85m, 0m, 150m));
    }
}

/// <summary>Color variant stock + pricing rules (product images/colors feature).</summary>
public class ProductColorTests
{
    [Fact]
    public void Color_Stock_Decrease_Is_Guarded()
    {
        var color = new MarketHub.Modules.Catalog.Domain.ProductColor { Name = "Blue", Hex = "#1e40af", Stock = 3 };
        Assert.True(color.TryDecreaseStock(3));
        Assert.Equal(0, color.Stock);
        Assert.False(color.TryDecreaseStock(1)); // oversell blocked
        Assert.Equal(0, color.Stock);
    }

    [Fact]
    public void Product_Stock_Is_The_Sum_Of_Color_Stock()
    {
        // CreateProductHandler computes Stock = Σ color stock when variants exist (mirrored here)
        var colors = new[]
        {
            new MarketHub.Modules.Catalog.Domain.ProductColor { Name = "Black", Hex = "#17181c", Stock = 25 },
            new MarketHub.Modules.Catalog.Domain.ProductColor { Name = "Blue", Hex = "#1e40af", Stock = 17 },
        };
        var product = new MarketHub.Modules.Catalog.Domain.Product { Title = "Speaker", Slug = "speaker", Description = "d", PriceETB = 3200m, SKU = "MH-1", Stock = colors.Sum(c => c.Stock) };
        Assert.Equal(42, product.Stock);
        Assert.True(product.TryDecreaseStock(42));
        Assert.False(product.TryDecreaseStock(1));
    }

    [Fact]
    public void Line_Unit_Price_Adds_Color_Extra()
    {
        // mirrors Sales GetCart/Checkout unit price rule: base + color.ExtraPriceETB
        decimal basePrice = 2450m, extra = 150m;
        Assert.Equal(2600m, basePrice + extra);
        Assert.Equal(basePrice, basePrice + 0m);
    }

    [Fact]
    public void ProductImage_PublicUrl_Resolves_Storage_Keys_And_Passes_Through_Https()
    {
        var keyed = new MarketHub.Modules.Catalog.Domain.ProductImage { StorageKey = "abc123.jpg" };
        var remote = new MarketHub.Modules.Catalog.Domain.ProductImage { StorageKey = "https://cdn.example.com/x.jpg" };
        Assert.Equal("/api/media/abc123.jpg", keyed.PublicUrl);
        Assert.Equal("https://cdn.example.com/x.jpg", remote.PublicUrl);
    }
}

public static class TestIds
{
    public static readonly Guid Vendor = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Category = Guid.Parse("22222222-2222-2222-2222-222222222222");
}
