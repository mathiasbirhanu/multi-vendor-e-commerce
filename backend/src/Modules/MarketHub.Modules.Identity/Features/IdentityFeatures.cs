using FluentValidation;
using MarketHub.Shared;
using MarketHub.Shared.Contracts.Events;
using MarketHub.Shared.Infrastructure;
using MarketHub.Modules.Identity.Domain;
using MarketHub.Modules.Identity.Persistence;
using MarketHub.Modules.Identity.Services;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quartz;
using ValidationException = FluentValidation.ValidationException;

namespace MarketHub.Modules.Identity.Features;

// ==================== REGISTRATION ====================
public sealed record DocumentUploadDto(string DocumentTypeCode, string FileName, string ContentType, string Base64Content);
public sealed record PolicyAcceptanceDto(string PolicyKey, string PolicyVersion);

public sealed record RegisterCommand(
    string Email, string Phone, string Password, string FullName, UserRole Role,
    string? IpAddress,
    string? BusinessName, string? TIN, string? Region, string? City, string? StoreDescription,
    IReadOnlyList<DocumentUploadDto> Documents,
    IReadOnlyList<PolicyAcceptanceDto> Consents,
    string? Lang = null) : IRequest<Result<Guid>>;

public sealed class RegisterValidator : AbstractValidator<RegisterCommand>
{
    public RegisterValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8).MaximumLength(200);
        RuleFor(x => x.Phone).Matches(@"^\+251[79]\d{8}$").When(x => !string.IsNullOrEmpty(x.Phone))
            .WithMessage("Phone must be in +2519XXXXXXXX or +2517XXXXXXXX format.");
        RuleFor(x => x.Role).Must(r => r is UserRole.Buyer or UserRole.Vendor).WithMessage("Public registration is limited to Buyer and Vendor roles.");
        RuleFor(x => x.BusinessName).NotEmpty().MaximumLength(200).When(x => x.Role == UserRole.Vendor);
        RuleFor(x => x.TIN).Matches(@"^\d{10}$").When(x => x.Role == UserRole.Vendor).WithMessage("TIN must be exactly 10 digits.");
        RuleFor(x => x.Region).NotEmpty().When(x => x.Role == UserRole.Vendor);
        RuleFor(x => x.City).NotEmpty().When(x => x.Role == UserRole.Vendor);
    }
}

public sealed class RegisterHandler(IdentityDbContext db, IFileStorage storage, IValidator<RegisterCommand> validator) : IRequestHandler<RegisterCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(RegisterCommand c, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(c, ct);
        if (!validation.IsValid) return Result.Fail<Guid>("validation", string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));

        if (await db.Users.AnyAsync(u => u.Email == c.Email.ToLowerInvariant(), ct))
            return Result.Fail<Guid>("duplicate_email", "An account with this email already exists.");

        // 1) Mandatory consent to ALL THREE active policies (incl. Return & Refund Policy)
        var activePolicies = await db.Policies.Where(p => p.IsActive).ToListAsync(ct);
        foreach (var policy in activePolicies)
            if (!c.Consents.Any(x => x.PolicyKey == policy.Key.ToString() && x.PolicyVersion == policy.Version))
                return Result.Fail<Guid>("consent_missing", $"You must accept the {policy.Key} policy (version {policy.Version}) to register.");

        // 2) Vendor KYC: profile data + minimum 3 documents covering every required type
        var vendorRequiredTypes = await db.DocumentTypes.Where(t => t.AppliesTo == UserRole.Vendor && t.IsRequired).ToListAsync(ct);
        if (c.Role == UserRole.Vendor)
        {
            if (c.Documents.Count < 3)
                return Result.Fail<Guid>("documents_insufficient", "Vendor registration requires at least 3 legal documents (registration license, trade license, TIN certificate).");
            foreach (var type in vendorRequiredTypes)
                if (!c.Documents.Any(d => d.DocumentTypeCode == type.Code))
                    return Result.Fail<Guid>("document_missing", $"Missing required document: {type.Name}.");
            foreach (var d in c.Documents)
            {
                var size = (long)((d.Base64Content.Length - (d.Base64Content.IndexOf(',', Math.Min(100, d.Base64Content.Length)) + 1)) * 0.75);
                if (size > 10 * 1024 * 1024) return Result.Fail<Guid>("document_too_large", $"Document '{d.FileName}' exceeds the 10 MB limit.");
                if (d.ContentType is not ("application/pdf" or "image/jpeg" or "image/png"))
                    return Result.Fail<Guid>("document_type", $"Document '{d.FileName}' must be PDF, JPG or PNG.");
            }
        }

        var user = new User
        {
            Email = c.Email.ToLowerInvariant(),
            Phone = c.Phone,
            PasswordHash = PasswordHasher.Hash(c.Password),
            FullName = c.FullName,
            Role = c.Role,
            Status = UserStatus.Active,
            Lang = c.Lang is "am" or "am-ET" ? "am" : "en",
        };

        if (c.Role == UserRole.Vendor)
        {
            var vendor = new VendorProfile
            {
                Id = user.Id,
                BusinessName = c.BusinessName!,
                TIN = c.TIN!,
                Region = c.Region!,
                City = c.City!,
                StoreDescription = c.StoreDescription,
                Status = VendorStatus.PendingReview,
            };
            db.VendorProfiles.Add(vendor);
        }

        foreach (var d in c.Documents)
        {
            var type = await db.DocumentTypes.FirstOrDefaultAsync(t => t.Code == d.DocumentTypeCode, ct);
            if (type is null) return Result.Fail<Guid>("document_type_unknown", $"Unknown document type '{d.DocumentTypeCode}'.");
            var bytes = Convert.FromBase64String(d.Base64Content.Contains(',') ? d.Base64Content[(d.Base64Content.IndexOf(',') + 1)..] : d.Base64Content);
            var key = await storage.SaveAsync(d.FileName, d.ContentType, bytes, ct);
            db.Documents.Add(new Document
            {
                OwnerId = user.Id,
                DocumentTypeId = type.Id,
                FileName = d.FileName,
                ContentType = d.ContentType,
                StorageKey = key,
                SHA256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
                SizeBytes = bytes.LongLength,
                Status = DocumentStatus.Pending,
            });
        }

        foreach (var consent in c.Consents.Where(x => activePolicies.Any(p => p.Key.ToString() == x.PolicyKey && p.Version == x.PolicyVersion)))
        {
            db.Consents.Add(new Consent
            {
                UserId = user.Id,
                PolicyKey = Enum.Parse<PolicyKey>(consent.PolicyKey),
                PolicyVersion = consent.PolicyVersion,
                IpAddress = c.IpAddress,
            });
        }

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return Result.Ok(user.Id);
    }
}

// ==================== LOGIN / REFRESH / ME ====================
public sealed record LoginCommand(string Email, string Password, string? IpAddress) : IRequest<Result<AuthResponse>>;

public sealed class LoginHandler(IdentityDbContext db, IAuthTokenService tokens) : IRequestHandler<LoginCommand, Result<AuthResponse>>
{
    public async Task<Result<AuthResponse>> Handle(LoginCommand c, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == c.Email.ToLowerInvariant(), ct);
        if (user is null || !PasswordHasher.Verify(c.Password, user.PasswordHash))
            return Result.Fail<AuthResponse>("invalid_credentials", "Invalid email or password.");
        if (user.Status == UserStatus.Suspended)
            return Result.Fail<AuthResponse>("suspended", "This account has been suspended. Contact support.");

        var access = tokens.CreateAccessToken(user);
        var refresh = new RefreshToken
        {
            UserId = user.Id,
            Token = tokens.NewRefreshToken(),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
            CreatedByIp = c.IpAddress,
        };
        // revoke the previous active chain
        var actives = await db.RefreshTokens.Where(r => r.UserId == user.Id && r.RevokedAtUtc == null && DateTime.UtcNow < r.ExpiresAtUtc).ToListAsync(ct);
        foreach (var t in actives) t.RevokedAtUtc = DateTime.UtcNow;
        db.RefreshTokens.Add(refresh);
        await db.SaveChangesAsync(ct);

        return Result.Ok(new AuthResponse(user.Id, user.Email, user.FullName, user.Role.ToString(), access.Token, access.ExpiresAtUtc, refresh.Token));
    }
}

public sealed record RefreshTokenCommand(string RefreshToken, string? IpAddress) : IRequest<Result<AuthResponse>>;

public sealed class RefreshTokenHandler(IdentityDbContext db, IAuthTokenService tokens) : IRequestHandler<RefreshTokenCommand, Result<AuthResponse>>
{
    public async Task<Result<AuthResponse>> Handle(RefreshTokenCommand c, CancellationToken ct)
    {
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(r => r.Token == c.RefreshToken, ct);
        if (stored is null || !stored.IsActive) return Result.Fail<AuthResponse>("invalid_refresh_token", "Refresh token is invalid or expired.");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == stored.UserId, ct)
            ?? throw new NotFoundException("User", stored.UserId);
        if (user.Status == UserStatus.Suspended) return Result.Fail<AuthResponse>("suspended", "This account has been suspended.");

        stored.RevokedAtUtc = DateTime.UtcNow;
        var rotated = new RefreshToken
        {
            UserId = user.Id,
            Token = tokens.NewRefreshToken(),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
            CreatedByIp = c.IpAddress,
            ReplacedByToken = stored.Token,
        };
        db.RefreshTokens.Add(rotated);
        await db.SaveChangesAsync(ct);

        var access = tokens.CreateAccessToken(user);
        return Result.Ok(new AuthResponse(user.Id, user.Email, user.FullName, user.Role.ToString(), access.Token, access.ExpiresAtUtc, rotated.Token));
    }
}

public sealed record GetMeQuery(Guid UserId) : IRequest<Result<MeDto>>;
public sealed record MeDto(Guid Id, string Email, string Phone, string FullName, string Role, string Status, string? AdminRole, string Lang, VendorDto? Vendor);
public sealed record VendorDto(Guid Id, string BusinessName, string Status, string? Region, string? City, string? Description, string? RejectionReason);

public sealed class GetMeHandler(IdentityDbContext db) : IRequestHandler<GetMeQuery, Result<MeDto>>
{
    public async Task<Result<MeDto>> Handle(GetMeQuery q, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == q.UserId, ct)
            ?? throw new NotFoundException("User", q.UserId);
        var vendor = await db.VendorProfiles.AsNoTracking().FirstOrDefaultAsync(v => v.Id == q.UserId, ct);
        return Result.Ok(new MeDto(user.Id, user.Email, user.Phone, user.FullName, user.Role.ToString(), user.Status.ToString(),
            user.AdminRole?.ToString(), user.Lang,
            vendor is null ? null : new VendorDto(vendor.Id, vendor.BusinessName, vendor.Status.ToString(), vendor.Region, vendor.City, vendor.StoreDescription, vendor.RejectionReason)));
    }
}

// ==================== ADMIN VENDOR REVIEW ====================
public sealed record AdminReviewVendorCommand(Guid AdminId, Guid VendorId, bool Approve, string? RejectionReason) : IRequest<Result>;

public sealed class AdminReviewVendorHandler(IdentityDbContext db) : IRequestHandler<AdminReviewVendorCommand, Result>
{
    public async Task<Result> Handle(AdminReviewVendorCommand c, CancellationToken ct)
    {
        var vendor = await db.VendorProfiles.FirstOrDefaultAsync(v => v.Id == c.VendorId, ct)
            ?? throw new NotFoundException("VendorProfile", c.VendorId);

        if (c.Approve)
        {
            vendor.Approve(c.AdminId);
            // all pending docs become verified on approval (admin has reviewed them)
            var pending = await db.Documents.Where(d => d.OwnerId == vendor.Id && d.Status == DocumentStatus.Pending).ToListAsync(ct);
            foreach (var d in pending) { d.Status = DocumentStatus.Verified; d.VerifiedByUserId = c.AdminId; d.VerifiedAtUtc = DateTime.UtcNow; }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(c.RejectionReason) || c.RejectionReason.Trim().Length < 5)
                return Result.Failure(new Error("reason_required", "A rejection reason is required."));
            vendor.Reject(c.AdminId, c.RejectionReason.Trim());
        }

        db.AddToOutbox(c.Approve
            ? new VendorApprovedEvent(vendor.Id, vendor.BusinessName)
            : new VendorRejectedEvent(vendor.Id, vendor.RejectionReason!));
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed record AdminListVendorApplicationsQuery(VendorStatus? Status, int Page = 1, int PageSize = 20) : IRequest<Result<PagedResult<VendorApplicationDto>>>;
public sealed record VendorApplicationDto(Guid Id, string BusinessName, string TIN, string Status, string? RejectionReason, string Region, string City, string? StoreDescription, string OwnerEmail, string OwnerFullName, IReadOnlyList<DocumentDto> Documents);
public sealed record DocumentDto(Guid Id, string TypeCode, string TypeName, bool TypeRequired, string FileName, string ContentType, long SizeBytes, string Status, DateTime UploadedAt);

public sealed class AdminListVendorApplicationsHandler(IdentityDbContext db) : IRequestHandler<AdminListVendorApplicationsQuery, Result<PagedResult<VendorApplicationDto>>>
{
    public async Task<Result<PagedResult<VendorApplicationDto>>> Handle(AdminListVendorApplicationsQuery q, CancellationToken ct)
    {
        var vendors = await db.VendorProfiles.AsNoTracking()
            .Where(v => q.Status == null || v.Status == q.Status)
            .OrderBy(v => v.CreatedAtUtc)
            .ToListAsync(ct);
        var ownerIds = vendors.Select(v => v.Id).ToList();
        var owners = await db.Users.AsNoTracking().Where(u => ownerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        var docs = await db.Documents.AsNoTracking().Where(d => ownerIds.Contains(d.OwnerId)).ToListAsync(ct);
        var types = await db.DocumentTypes.AsNoTracking().ToListAsync(ct);
        var typeById = types.ToDictionary(t => t.Id);

        var items = vendors.Select(v => new VendorApplicationDto(
            v.Id, v.BusinessName, v.TIN, v.Status.ToString(), v.RejectionReason, v.Region, v.City, v.StoreDescription,
            owners.TryGetValue(v.Id, out var o) ? o.Email : "", owners.TryGetValue(v.Id, out var o2) ? o2.FullName : "",
            docs.Where(d => d.OwnerId == v.Id).Select(d => new DocumentDto(
                d.Id, typeById.TryGetValue(d.DocumentTypeId, out var t) ? t.Code : "", typeById.TryGetValue(d.DocumentTypeId, out var t2) ? t2.Name : "",
                typeById.TryGetValue(d.DocumentTypeId, out var t3) && t3.IsRequired, d.FileName, d.ContentType, d.SizeBytes,
                d.Status.ToString(), d.CreatedAtUtc)).ToList())).ToList();

        var page = items.Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).ToList();
        return Result.Ok(PagedResult<VendorApplicationDto>.Create(page, q.Page, q.PageSize, items.Count));
    }
}

public sealed record GetDocumentTypesQuery(UserRole? Role) : IRequest<Result<IReadOnlyList<DocumentTypeDto>>>;
public sealed record DocumentTypeDto(Guid Id, string Code, string Name, string AppliesTo, bool IsRequired, int MinFiles, int MaxFiles, string? Description);

public sealed class GetDocumentTypesHandler(IdentityDbContext db) : IRequestHandler<GetDocumentTypesQuery, Result<IReadOnlyList<DocumentTypeDto>>>
{
    public async Task<Result<IReadOnlyList<DocumentTypeDto>>> Handle(GetDocumentTypesQuery q, CancellationToken ct)
    {
        var types = await db.DocumentTypes.AsNoTracking()
            .Where(t => q.Role == null || t.AppliesTo == q.Role)
            .OrderBy(t => t.SortOrder).ToListAsync(ct);
        return Result.Ok(types.Select(t => new DocumentTypeDto(t.Id, t.Code, t.Name, t.AppliesTo.ToString(), t.IsRequired, t.MinFiles, t.MaxFiles, t.Description)).ToList());
    }
}

// ==================== POLICIES ====================
public sealed record GetActivePoliciesQuery : IRequest<Result<IReadOnlyList<PolicyDto>>>;
public sealed record PolicyDto(string Key, string Version, string Title, string ContentMarkdown, DateTime EffectiveFromUtc);

public sealed class GetActivePoliciesHandler(IdentityDbContext db) : IRequestHandler<GetActivePoliciesQuery, Result<IReadOnlyList<PolicyDto>>>
{
    public async Task<Result<IReadOnlyList<PolicyDto>>> Handle(GetActivePoliciesQuery q, CancellationToken ct)
    {
        var policies = await db.Policies.AsNoTracking().Where(p => p.IsActive).ToListAsync(ct);
        return Result.Ok(policies.Select(p => new PolicyDto(p.Key.ToString(), p.Version, FirstLine(p.ContentMarkdown), p.ContentMarkdown, p.EffectiveFromUtc)).ToList());
    }
    private static string FirstLine(string md) => md.Split('\n').FirstOrDefault()?.TrimStart('#', ' ') ?? "Policy";
}

public sealed record GetPolicyQuery(PolicyKey Key) : IRequest<Result<PolicyDto>>;

public sealed class GetPolicyHandler(IdentityDbContext db) : IRequestHandler<GetPolicyQuery, Result<PolicyDto>>
{
    public async Task<Result<PolicyDto>> Handle(GetPolicyQuery q, CancellationToken ct)
    {
        var p = await db.Policies.AsNoTracking().Where(x => x.Key == q.Key && x.IsActive).OrderByDescending(x => x.EffectiveFromUtc).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Policy", q.Key);
        return Result.Ok(new PolicyDto(p.Key.ToString(), p.Version, p.ContentMarkdown.Split('\n').First().TrimStart('#', ' '), p.ContentMarkdown, p.EffectiveFromUtc));
    }
}

// ==================== ADMIN STAFF (back-office accounts) ====================
public sealed record ListStaffQuery : IRequest<Result<IReadOnlyList<StaffDto>>>;
public sealed record StaffDto(Guid Id, string Email, string FullName, string? AdminRole, string Status, DateTime CreatedAtUtc);

public sealed class ListStaffHandler(IdentityDbContext db) : IRequestHandler<ListStaffQuery, Result<IReadOnlyList<StaffDto>>>
{
    public async Task<Result<IReadOnlyList<StaffDto>>> Handle(ListStaffQuery q, CancellationToken ct)
    {
        var staff = await db.Users.AsNoTracking()
            .Where(u => u.Role == UserRole.Admin)
            .OrderBy(u => u.CreatedAtUtc)
            .Select(u => new StaffDto(u.Id, u.Email, u.FullName, u.AdminRole.HasValue ? u.AdminRole.Value.ToString() : null, u.Status.ToString(), u.CreatedAtUtc))
            .ToListAsync(ct);
        return Result.Ok((IReadOnlyList<StaffDto>)staff);
    }
}

public sealed record CreateStaffCommand(string Email, string FullName, string Password, string AdminRole, string? Phone) : IRequest<Result<StaffDto>>;

public sealed class CreateStaffHandler(IdentityDbContext db) : IRequestHandler<CreateStaffCommand, Result<StaffDto>>
{
    public async Task<Result<StaffDto>> Handle(CreateStaffCommand c, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.Email) || !c.Email.Contains('@')) return Result.Fail<StaffDto>("validation", "A valid email is required.");
        if (string.IsNullOrWhiteSpace(c.FullName)) return Result.Fail<StaffDto>("validation", "Full name is required.");
        if (string.IsNullOrWhiteSpace(c.Password) || c.Password.Length < 8) return Result.Fail<StaffDto>("validation", "Password must be at least 8 characters.");
        if (!Enum.TryParse<AdminRole>(c.AdminRole, true, out var role)) return Result.Fail<StaffDto>("validation", $"AdminRole must be one of: {string.Join(", ", Enum.GetNames<AdminRole>())}.");

        var email = c.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            return Result.Fail<StaffDto>("duplicate_email", "An account with this email already exists.");

        var user = new User
        {
            Email = email,
            Phone = string.IsNullOrWhiteSpace(c.Phone) ? "+251900000000" : c.Phone.Trim(),
            PasswordHash = PasswordHasher.Hash(c.Password),
            FullName = c.FullName.Trim(),
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            AdminRole = role,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return Result.Ok(new StaffDto(user.Id, user.Email, user.FullName, role.ToString(), user.Status.ToString(), user.CreatedAtUtc));
    }
}

public sealed record PatchStaffCommand(Guid ActorId, Guid UserId, string? Email, string? FullName, string? AdminRole, string? Status) : IRequest<Result<StaffDto>>;

public sealed class PatchStaffHandler(IdentityDbContext db) : IRequestHandler<PatchStaffCommand, Result<StaffDto>>
{
    public async Task<Result<StaffDto>> Handle(PatchStaffCommand c, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == c.UserId && u.Role == UserRole.Admin, ct)
            ?? throw new NotFoundException("Staff", c.UserId);

        if (!string.IsNullOrWhiteSpace(c.FullName)) user.FullName = c.FullName.Trim();
        if (!string.IsNullOrWhiteSpace(c.Email))
        {
            var email = c.Email.Trim().ToLowerInvariant();
            if (await db.Users.AnyAsync(u => u.Email == email && u.Id != c.UserId, ct))
                return Result.Fail<StaffDto>("duplicate_email", "Another account already uses this email.");
            user.Email = email;
        }
        if (!string.IsNullOrWhiteSpace(c.AdminRole))
        {
            if (!Enum.TryParse<AdminRole>(c.AdminRole, true, out var role))
                return Result.Fail<StaffDto>("validation", $"AdminRole must be one of: {string.Join(", ", Enum.GetNames<AdminRole>())}.");
            user.AdminRole = role;
        }
        if (!string.IsNullOrWhiteSpace(c.Status))
        {
            if (!Enum.TryParse<UserStatus>(c.Status, true, out var status))
                return Result.Fail<StaffDto>("validation", "Status must be Active or Suspended.");
            user.Status = status;
        }
        user.ModifiedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Result.Ok(new StaffDto(user.Id, user.Email, user.FullName, user.AdminRole?.ToString(), user.Status.ToString(), user.CreatedAtUtc));
    }
}

/// <summary>Shared sweep logic: flags documents past <c>ExpiresAtUtc</c> and suspends vendors whose REQUIRED documents expired. Used by the Quartz job and the admin trigger endpoint.</summary>
public sealed class DocumentExpirySweeper(IdentityDbContext db, ILogger<DocumentExpirySweeper> logger)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var expired = await db.Documents
            .Where(d => d.ExpiresAtUtc != null && d.ExpiresAtUtc <= now && d.ExpiryFlaggedAtUtc == null && d.Status == DocumentStatus.Verified)
            .ToListAsync(ct);
        if (expired.Count == 0) return 0;

        var typeById = await db.DocumentTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        var flaggedOwners = new HashSet<Guid>();
        foreach (var doc in expired)
        {
            doc.ExpiryFlaggedAtUtc = now;
            doc.ModifiedAtUtc = now;
            if (typeById.TryGetValue(doc.DocumentTypeId, out var type) && type.IsRequired) flaggedOwners.Add(doc.OwnerId);
        }

        foreach (var vendorId in flaggedOwners)
        {
            var vendor = await db.VendorProfiles.FirstOrDefaultAsync(v => v.Id == vendorId, ct);
            if (vendor is null || vendor.Status != VendorStatus.Approved) continue;
            const string reason = "Required vendor documents expired — re-submit KYC documents to reactivate the store.";
            vendor.Suspend(Guid.Empty, reason);
            db.AddToOutbox(new MarketHub.Shared.Contracts.Events.VendorSuspendedEvent(vendorId, reason));
            logger.LogWarning("Vendor {Vendor} suspended: required documents expired", vendorId);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("DocumentExpiryMonitor flagged {Count} expired documents ({Vendors} vendors suspended)", expired.Count, flaggedOwners.Count);
        return expired.Count;
    }
}

/// <summary>Quartz job (hourly) wrapping <see cref="DocumentExpirySweeper"/>. Guarded, logged, idempotent.</summary>
public sealed class DocumentExpiryMonitorJob(IServiceScopeFactory? scopeFactory = null) : IJob
{
    public static readonly JobKey Key = new("document-expiry-monitor");

    public async Task Execute(IJobExecutionContext ctx)
    {
        var factory = scopeFactory
            ?? ctx.JobDetail.JobDataMap.Get("scopeFactory") as IServiceScopeFactory
            ?? throw new InvalidOperationException("scopeFactory unavailable for DocumentExpiryMonitorJob");
        using var scope = factory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DocumentExpirySweeper>().RunAsync(ctx.CancellationToken);
    }
}
