using MarketHub.Shared;

namespace MarketHub.Modules.Finance.Domain;

public class Setting : BaseEntity
{
    public string Key { get; set; } = default!;
    public string Value { get; set; } = default!;
    public string? Description { get; set; }
}

/// <summary>Commission override. VendorId/CategoryId null combinations define precedence.</summary>
public class CommissionOverride : BaseEntity
{
    public Guid? VendorId { get; set; }
    public Guid? CategoryId { get; set; }
    public decimal Rate { get; set; }
    public DateTime EffectiveFromUtc { get; set; } = DateTime.UtcNow;
    public string? Note { get; set; }
}

public enum LedgerAccountType { Asset, Liability, Revenue, Expense }

public class LedgerAccount : BaseEntity
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public LedgerAccountType Type { get; set; }
}

/// <summary>Double-entry ledger line. Debits and credits always balance across a posting.</summary>
public class LedgerEntry : BaseEntity
{
    public Guid AccountId { get; set; }
    public Guid? OrderId { get; set; }
    public Guid? VendorId { get; set; }
    public decimal AmountDebitETB { get; set; }
    public decimal AmountCreditETB { get; set; }
    public string Description { get; set; } = default!;
}

public enum PayoutStatus { Requested, Processing, Paid, Rejected }
public enum PayoutMethod { Telebirr, BankTransfer }

/// <summary>Vendor payout request with the platform's 12-hour processing SLA (DueByUtc).</summary>
public class PayoutRequest : BaseEntity
{
    public Guid VendorId { get; set; }
    public decimal AmountETB { get; set; }
    public PayoutStatus Status { get; set; } = PayoutStatus.Requested;
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime DueByUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
    public Guid? ProcessedByUserId { get; set; }
    public PayoutMethod PaymentMethod { get; set; } = PayoutMethod.Telebirr;
    public string? DestinationAccount { get; set; }
    public string? AdminNote { get; set; }
    public uint RowVersion { get; set; }

    public void MarkPaid(Guid adminId)
    {
        if (Status is not (PayoutStatus.Requested or PayoutStatus.Processing)) throw new DomainException($"Payout is already {Status}.");
        Status = PayoutStatus.Paid;
        ProcessedAtUtc = DateTime.UtcNow;
        ProcessedByUserId = adminId;
    }

    public void Reject(Guid adminId, string? note)
    {
        if (Status is not (PayoutStatus.Requested or PayoutStatus.Processing)) throw new DomainException($"Payout is already {Status}.");
        Status = PayoutStatus.Rejected;
        ProcessedAtUtc = DateTime.UtcNow;
        ProcessedByUserId = adminId;
        AdminNote = note;
    }
}

/// <summary>Canonical account codes used by the ledger poster and reports.</summary>
public static class LedgerAccounts
{
    public const string CashPspReceivable = "1000";
    public const string VendorPayable = "2000";
    public const string VatPayable = "2100";
    public const string PlatformCommissionRevenue = "3000";
    public const string DeliveryRevenue = "3100";
    public const string FirstPartySalesRevenue = "3200";
    public const string RefundExpense = "4000";
}
