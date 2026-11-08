using MarketHub.Shared;
using MarketHub.Shared.Contracts;
using MarketHub.Shared.Infrastructure;
using MarketHub.Modules.Identity.Domain;
using MarketHub.Modules.Identity.Features;
using MarketHub.Modules.Identity.Persistence;
using MarketHub.Modules.Identity.Services;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MarketHub.Modules.Identity;

/// <summary>Identity module composition: auth, vendor KYC, policies, consents, staff accounts.</summary>
public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddDbContext<IdentityDbContext>(o =>
            o.UseNpgsql(cfg.GetConnectionString("Default")));
        services.AddScoped<IAuthTokenService, JwtTokenService>();
        services.AddValidatorsFromAssemblyContaining<RegisterValidator>();
        services.AddScoped<IMarketplaceVendorChecker, MarketplaceVendorChecker>();
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddScoped<IVendorStatsUpdater, VendorStatsUpdater>();
        services.AddScoped<DocumentExpirySweeper>();
        services.AddTransient<IModuleSeeder, IdentitySeeder>();
        return services;
    }

    /// <summary>Cross-module check used by Catalog: only approved vendors may list products.</summary>
    private sealed class MarketplaceVendorChecker(IServiceProvider sp) : IMarketplaceVendorChecker
    {
        public async Task<bool> IsApprovedVendorAsync(Guid userId, CancellationToken ct = default)
        {
            var scope = sp.CreateScope();
            try { return await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().VendorProfiles.AsNoTracking().AnyAsync(v => v.Id == userId && v.Status == VendorStatus.Approved, ct); }
            finally { scope.Dispose(); }
        }
        public async Task<string?> GetBusinessNameAsync(Guid userId, CancellationToken ct = default)
        {
            var scope = sp.CreateScope();
            try { return await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().VendorProfiles.AsNoTracking().Where(v => v.Id == userId).Select(v => v.BusinessName).FirstOrDefaultAsync(ct); }
            finally { scope.Dispose(); }
        }
    }

    /// <summary>Contact/locale projection consumed by the Notifications dispatcher (SMS/Email criticality).</summary>
    private sealed class UserDirectory(IServiceProvider sp) : IUserDirectory
    {
        public async Task<(string? Email, string? Phone, string Lang)> GetContactAsync(Guid userId, CancellationToken ct = default)
        {
            var scope = sp.CreateScope();
            try
            {
                var u = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Users.AsNoTracking()
                    .Where(x => x.Id == userId)
                    .Select(x => new { x.Email, x.Phone, x.Lang })
                    .FirstOrDefaultAsync(ct);
                return u is null ? (null, null, "en") : (u.Email, u.Phone, u.Lang);
            }
            finally { scope.Dispose(); }
        }

        public async Task<IReadOnlyList<Guid>> GetAdminUserIdsAsync(CancellationToken ct = default)
        {
            var scope = sp.CreateScope();
            try
            {
                var ids = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Users.AsNoTracking()
                    .Where(u => u.Role == UserRole.Admin && u.Status == UserStatus.Active)
                    .Select(u => u.Id)
                    .ToListAsync(ct);
                return ids;
            }
            finally { scope.Dispose(); }
        }
    }

    /// <summary>Receives recomputed review counters from the Catalog module (fire-and-forget friendly).</summary>
    private sealed class VendorStatsUpdater(IServiceProvider sp) : IVendorStatsUpdater
    {
        public async Task UpdateRatingCountAsync(Guid vendorId, int ratingCount, CancellationToken ct = default)
        {
            var scope = sp.CreateScope();
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
                var vendor = await db.VendorProfiles.FirstOrDefaultAsync(v => v.Id == vendorId, ct);
                if (vendor is null) return;
                vendor.RatingCount = ratingCount;
                vendor.ModifiedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            finally { scope.Dispose(); }
        }
    }
}

/// <summary>HTTP endpoints (minimal APIs) for the Identity module.</summary>
public static class IdentityEndpoints
{
    private const string Group = "/api";

    public static void MapEndpoints(IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup($"{Group}/auth").WithTags("Auth").RequireRateLimiting("auth");
        auth.MapPost("/register", async (RegisterRequest req, IMediator m, HttpContext ctx) =>
            {
                var ip = ctx.Connection.RemoteIpAddress?.ToString();
                var result = await m.Send(new RegisterCommand(req.Email, req.Phone ?? "", req.Password, req.FullName, Enum.Parse<UserRole>(req.Role, true), ip,
                    req.BusinessName, req.TIN, req.Region, req.City, req.StoreDescription,
                    req.Documents.Select(d => new DocumentUploadDto(d.DocumentTypeCode, d.FileName, d.ContentType, d.Base64Content)).ToList(),
                    req.Consents.Select(x => new PolicyAcceptanceDto(x.PolicyKey, x.PolicyVersion)).ToList(),
                    req.Lang));
                return result.ToHttp();
            }).AllowAnonymous();
        auth.MapPost("/login", async (LoginRequest req, IMediator m, HttpContext ctx) =>
            (await m.Send(new LoginCommand(req.Email, req.Password, ctx.Connection.RemoteIpAddress?.ToString()))).ToHttp()).AllowAnonymous();
        auth.MapPost("/refresh", async (RefreshRequest req, IMediator m, HttpContext ctx) =>
            (await m.Send(new RefreshTokenCommand(req.RefreshToken, ctx.Connection.RemoteIpAddress?.ToString()))).ToHttp()).AllowAnonymous();
        app.MapGet($"{Group}/auth/me", async (IMediator m, ICurrentUser user) =>
            user.UserId is null ? Results.Unauthorized() : (await m.Send(new GetMeQuery(user.UserId.Value))).ToHttp()).RequireAuthorization();

        app.MapGet($"{Group}/document-types", async (IMediator m, UserRole? role) =>
            (await m.Send(new GetDocumentTypesQuery(role))).ToHttp()).AllowAnonymous();

        app.MapGet($"{Group}/policies", async (IMediator m) => (await m.Send(new GetActivePoliciesQuery())).ToHttp()).AllowAnonymous();
        app.MapGet($"{Group}/policies/{{key}}", async (IMediator m, string key) =>
        {
            if (!Enum.TryParse<PolicyKey>(key, true, out var parsed)) return Results.NotFound(new { error = $"Unknown policy '{key}'" });
            return (await m.Send(new GetPolicyQuery(parsed))).ToHttp();
        }).AllowAnonymous();

        var admin = app.MapGroup($"{Group}/admin").WithTags("Admin").RequireAuthorization("Admin");
        admin.MapGet("/vendors", async (IMediator m, string? status, int page, int pageSize) =>
            (await m.Send(new AdminListVendorApplicationsQuery(string.IsNullOrEmpty(status) ? null : Enum.Parse<VendorStatus>(status, true), page <= 0 ? 1 : page, pageSize <= 0 ? 20 : pageSize))).ToHttp());
        admin.MapPost("/vendors/{id}/review", async (IMediator m, Guid id, ReviewRequest req, ICurrentUser user) =>
            (await m.Send(new AdminReviewVendorCommand(user.UserId!.Value, id, req.Approve, req.RejectionReason))).ToHttp());
        admin.MapGet("/documents/{ownerId}/{id}", async (IMediator m, IFileStorage storage, IdentityDbContext db, Guid ownerId, Guid id) =>
        {
            var doc = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id && d.OwnerId == ownerId)
                ?? throw new NotFoundException("Document", id);
            var bytes = await storage.ReadAsync(doc.StorageKey);
            return Results.File(bytes, doc.ContentType, doc.FileName);
        });

        // ---- staff management (PATCH restricted to SuperAdmin) ----
        admin.MapGet("/staff", async (IMediator m) => (await m.Send(new ListStaffQuery())).ToHttp());
        admin.MapPost("/staff", async (IMediator m, CreateStaffRequest req) =>
            (await m.Send(new CreateStaffCommand(req.Email, req.FullName, req.Password, req.AdminRole, req.Phone))).ToHttp());
        admin.MapPatch("/staff/{id}", async (IMediator m, Guid id, PatchStaffRequest req, ICurrentUser user) =>
            (await m.Send(new PatchStaffCommand(user.UserId!.Value, id, req.Email, req.FullName, req.AdminRole, req.Status))).ToHttp())
            .RequireAuthorization("SuperAdminOnly");

        // ---- manual trigger for the hourly DocumentExpiryMonitor job (Ops tab parity) ----
        admin.MapPost("/jobs/document-expiry", async (DocumentExpirySweeper sweeper) =>
            Results.Ok(new { ok = true, flagged = await sweeper.RunAsync(CancellationToken.None) }));
    }

    public sealed record RegisterRequest(string Email, string? Phone, string Password, string FullName, string Role,
        string? BusinessName, string? TIN, string? Region, string? City, string? StoreDescription,
        IReadOnlyList<DocumentUploadDto> Documents, IReadOnlyList<PolicyAcceptanceDto> Consents, string? Lang = null);
    public sealed record LoginRequest(string Email, string Password);
    public sealed record RefreshRequest(string RefreshToken);
    public sealed record ReviewRequest(bool Approve, string? RejectionReason);
    public sealed record CreateStaffRequest(string Email, string FullName, string Password, string AdminRole, string? Phone);
    public sealed record PatchStaffRequest(string? Email, string? FullName, string? AdminRole, string? Status);
}

/// <summary>Idempotent seeder: admin/vendor/buyer accounts, document types, policy v1.0.0 content.</summary>
public sealed class IdentitySeeder : IModuleSeeder
{
    public string ModuleName => "Identity";

    public async Task SeedAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<IdentityDbContext>();

        if (!await db.DocumentTypes.AnyAsync(ct))
        {
            db.DocumentTypes.AddRange(
                new DocumentType { Code = "business_license", Name = "Business Registration License", AppliesTo = UserRole.Vendor, IsRequired = true, MinFiles = 1, MaxFiles = 2, Description = "Valid business registration certificate from the Ministry of Trade.", SortOrder = 1 },
                new DocumentType { Code = "trade_license", Name = "Trade License", AppliesTo = UserRole.Vendor, IsRequired = true, MinFiles = 1, MaxFiles = 2, Description = "Current trade license for the current fiscal year.", SortOrder = 2 },
                new DocumentType { Code = "tin_certificate", Name = "TIN Certificate", AppliesTo = UserRole.Vendor, IsRequired = true, MinFiles = 1, MaxFiles = 2, Description = "Tax Identification Number certificate from the Ministry of Revenue.", SortOrder = 3 },
                new DocumentType { Code = "owner_id", Name = "Owner Identification", AppliesTo = UserRole.Vendor, IsRequired = false, MinFiles = 0, MaxFiles = 2, Description = "ID card or passport of the owner/general manager.", SortOrder = 4 },
                new DocumentType { Code = "sector_permit", Name = "Sector Permit", AppliesTo = UserRole.Vendor, IsRequired = false, MinFiles = 0, MaxFiles = 2, Description = "Additional sector-specific permit if applicable.", SortOrder = 5 },
                new DocumentType { Code = "government_id", Name = "Government ID", AppliesTo = UserRole.Buyer, IsRequired = false, MinFiles = 0, MaxFiles = 2, Description = "Fayda ID, passport or driving license (optional verification).", SortOrder = 6 });
        }

        if (!await db.Policies.AnyAsync(ct))
        {
            db.Policies.AddRange(
                new Policy { Key = PolicyKey.TermsOfService, Version = "1.0.0", ContentMarkdown = PolicyContent.TermsOfService, EffectiveFromUtc = DateTime.UtcNow },
                new Policy { Key = PolicyKey.RulesAndRegulations, Version = "1.0.0", ContentMarkdown = PolicyContent.RulesAndRegulations, EffectiveFromUtc = DateTime.UtcNow },
                new Policy { Key = PolicyKey.ReturnAndRefundPolicy, Version = "1.0.0", ContentMarkdown = PolicyContent.ReturnAndRefundPolicy, EffectiveFromUtc = DateTime.UtcNow });
        }

        if (!await db.Users.AnyAsync(ct))
        {
            db.Users.AddRange(
                new User { Id = SeedIds.AdminUserId, Email = "admin@markethub.et", Phone = "+251911000001", PasswordHash = PasswordHasher.Hash("Admin@12345"), FullName = "Platform Administrator", Role = UserRole.Admin, AdminRole = AdminRole.SuperAdmin },
                new User { Id = SeedIds.SupportAdminUserId, Email = "support@markethub.et", Phone = "+251911000006", PasswordHash = PasswordHasher.Hash("Support@12345"), FullName = "Support Desk", Role = UserRole.Admin, AdminRole = AdminRole.Support },
                new User { Id = SeedIds.Vendor1UserId, Email = "vendor1@markethub.et", Phone = "+251911000002", PasswordHash = PasswordHasher.Hash("Vendor@12345"), FullName = "Selam Girmay", Role = UserRole.Vendor },
                new User { Id = SeedIds.Vendor2UserId, Email = "vendor2@markethub.et", Phone = "+251911000003", PasswordHash = PasswordHasher.Hash("Vendor@12345"), FullName = "Daniel Mekonnen", Role = UserRole.Vendor },
                new User { Id = SeedIds.BuyerUserId, Email = "buyer@markethub.et", Phone = "+251921000005", PasswordHash = PasswordHasher.Hash("Buyer@12345"), FullName = "Abebe Kebede", Role = UserRole.Buyer });
            db.VendorProfiles.AddRange(
                new VendorProfile { Id = SeedIds.Vendor1UserId, BusinessName = "Sheba Electronics Trading PLC", TIN = "0012345678", Status = VendorStatus.Approved, Region = "Addis Ababa", City = "Addis Ababa", StoreDescription = "Authorized electronics and accessories importer.", ApprovedAtUtc = DateTime.UtcNow.AddDays(-40) },
                new VendorProfile { Id = SeedIds.Vendor2UserId, BusinessName = "Habesha Home & Kitchen", TIN = "0087654321", Status = VendorStatus.Approved, Region = "Oromia", City = "Adama", StoreDescription = "Handcrafted Ethiopian kitchenware and organic food products.", ApprovedAtUtc = DateTime.UtcNow.AddDays(-35) });
            foreach (var u in new[] { SeedIds.AdminUserId, SeedIds.SupportAdminUserId, SeedIds.Vendor1UserId, SeedIds.Vendor2UserId, SeedIds.BuyerUserId })
                foreach (PolicyKey k in Enum.GetValues<PolicyKey>())
                    db.Consents.Add(new Consent { UserId = u, PolicyKey = k, PolicyVersion = "1.0.0", IpAddress = "seed" });
        }
        else if (!await db.Users.AnyAsync(u => u.Email == "support@markethub.et", ct))
        {
            // gap-fill v2: back-office staff account (existing databases get it on the next dev boot)
            db.Users.Add(new User { Id = SeedIds.SupportAdminUserId, Email = "support@markethub.et", Phone = "+251911000006", PasswordHash = PasswordHasher.Hash("Support@12345"), FullName = "Support Desk", Role = UserRole.Admin, AdminRole = AdminRole.Support });
            foreach (PolicyKey k in Enum.GetValues<PolicyKey>())
                db.Consents.Add(new Consent { UserId = SeedIds.SupportAdminUserId, PolicyKey = k, PolicyVersion = "1.0.0", IpAddress = "seed" });
        }

        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Seed policy content (v1.0.0). Editing: publish a NEW version row instead of mutating this.</summary>
public static class PolicyContent
{
    public const string TermsOfService = """
# Terms of Service — MarketHub Ethiopia

**Version 1.0.0** · Effective from the date shown in platform settings.

## 1. About these terms
MarketHub Technologies PLC ("MarketHub", "we", "us") operates an electronic marketplace connecting buyers with independent vendors and with our own first-party store. By creating an account you agree to these Terms of Service, the Rules & Regulations, and the Return & Refund Policy.

## 2. Accounts and roles
You must provide accurate identification information when registering. Vendors must upload a valid business registration license, trade license and TIN certificate before their store can be activated. You are responsible for all activity under your account and must keep your password confidential.

## 3. The role of MarketHub
Unless we are the seller of record (first-party products marked "MarketHub Store"), MarketHub acts as an agent and technology platform only. Contracts of sale are formed between you and the vendor. We process payments in Ethiopian Birr (ETB), hold vendor settlements, charge commission on vendor sales, and operate the dispute and refund process described in the Return & Refund Policy.

## 4. Pricing and taxes
All prices are displayed and charged in ETB. Value Added Tax at the rate published in platform settings (currently 15%) is added at checkout on goods and delivery fees. Delivery fees are calculated per zone and shown before you confirm payment.

## 5. Prohibited uses
You may not use the platform for unlawful trade, payment fraud, or abuse of our systems. Attempting to interfere with platform security controls is grounds for immediate suspension and referral to Ethiopian authorities.

## 6. Liability
We provide the platform "as is". To the maximum extent permitted by Ethiopian law, our aggregate liability to you is limited to the value of the order giving rise to the claim. Nothing limits liability for fraud or willful misconduct.

## 7. Governing law
These terms are governed by the laws of the Federal Democratic Republic of Ethiopia. Disputes are subject to the jurisdiction of the Ethiopian courts.
""";

    public const string RulesAndRegulations = """
# Rules & Regulations — MarketHub Ethiopia

**Version 1.0.0** · Effective from the date shown in platform settings.

## 1. Vendor admission
Vendors must submit a business registration license, trade license and TIN certificate (at least three documents). All documents must be genuine and legible; forged documents result in permanent rejection and may be reported to the Ministry of Trade and Regional Integration. The MarketHub review team verifies every application before a store is activated.

## 2. Listing rules
- Listings must have truthful titles, descriptions, prices and stock levels.
- Prohibited items include counterfeit goods, weapons, narcotics, protected wildlife products, and any item whose sale is restricted under Ethiopian law.
- Prices must be in ETB; platform commission is charged transparently and reported in the vendor console.

## 3. Commission and payouts
MarketHub charges a commission on vendor sales. The default commission rate is shown in platform settings and may vary by vendor or category. Vendors may request withdrawal of their available balance at any time; **payout requests are processed within 12 hours of submission**. Vendor income, commission and payout history are always visible in the vendor console.

## 4. Order handling
Vendors must accept or reject new orders within 24 hours of payment confirmation, ship promptly, and mark orders delivered. Repeated late acceptance or cancellation may reduce store visibility or lead to suspension.

## 5. Conduct
Honest communication with buyers is required. Manipulating reviews, fake orders, or off-platform diversion of buyers is prohibited. Accounts that breach these rules may be suspended with notice and reason.

## 6. Changes
We may update these rules; material changes are announced on the platform and require re-acceptance by affected users.
""";

    public const string ReturnAndRefundPolicy = """
# Return & Refund Policy — MarketHub Ethiopia

**Version 1.0.0** · Effective from the date shown in platform settings.

## 1. Return window
You may request a return or refund within **7 days of delivery**. Items must be unused, complete with all accessories, and in their original packaging. Products with hygiene seals must have the seal intact.

## 2. How to request
Open "My Orders", select the order, and choose "Request Return/Refund", then state the reason. Pre-delivery cancellations are accepted while the order has not shipped.

## 3. Inspection and decision
MarketHub or the vendor inspects the returned item within 2 business days of receipt. If the item passes inspection the refund is approved; if the item is used or damaged by the buyer the refund may be partially or fully rejected with a written reason.

## 4. Refund method and timing
Approved refunds are returned to the **original payment method** (telebirr, card, or bank account) within **5 business days** of approval. Refunds are issued in ETB and include the item price and applicable VAT.

## 5. Delivery fees
Delivery fees are non-refundable unless the return is due to seller fault (wrong, damaged, or counterfeit item delivered), in which case both delivery legs are refunded.

## 6. Non-returnable items
Perishable groceries, custom-made products, and downloaded services cannot be returned unless faulty.

## 7. Disputes
If you disagree with a refund decision you may escalate to MarketHub support within 7 days; our dispute team reviews the full order record and issues a final decision.
""";
}
