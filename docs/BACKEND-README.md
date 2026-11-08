# MarketHub — Ethiopian Multivendor Marketplace (.NET 9 backend)

Production-grade modular-monolith backend for a marketplace that combines **independent vendors** with a **first-party store** (the platform is "vendor #1" with 0% commission). Ethiopia-specific domain: **ETB currency, 15% VAT, regional delivery zones, telebirr/Chapa/SantimPay payments, 3+ document vendor KYC, 12-hour payout SLA, versioned policy consent (Terms · Rules & Regulations · Return & Refund)**.

## Architecture

Modular monolith — every module owns its PostgreSQL schema and talks to other modules only through `MarketHub.Shared.Contracts` interfaces and the **transactional outbox** (domain events → MediatR). Clean/vertical-slice handlers (CQRS via MediatR), FluentValidation everywhere, Result pattern, RFC 7807 errors.

```
src/
├── MarketHub.Api                  composition root: auth, rate limiting, CORS, health, Swagger, seeder, middleware
├── MarketHub.Shared               Result/PagedResult, ICurrentUser, IFileStorage, ISettingsProvider,
│                                  contracts (ICatalogReader, IOrderReader, ICommissionResolver, ILedgerPoster,
│                                  IMarketplaceVendorChecker), integration events, OutboxProcessor
└── Modules/
    ├── Identity                   users, roles, vendor KYC lifecycle, document types, policies + consents, JWT
    ├── Catalog                    categories, products (platform + vendor), moderation, search/filter queries
    ├── Sales                      cart, checkout (VAT + delivery math), order state machine, refunds flow
    ├── Payments                   IPaymentGateway + Mock/Chapa/Telebirr/Santimpay, signed idempotent webhooks, refunds
    ├── Finance                    double-entry ledger, commission precedence, payouts (12h SLA), admin reports, settings
    └── Notifications              in-app notifications from domain events (email/SMS adapters later)
tests/MarketHub.Tests              VAT, commission precedence, order state machine, ledger balance (xUnit)
```

**Order state machine:** `Placed → PaymentConfirmed → VendorAccepted → Shipped → Delivered → Completed`, branches `Cancelled` (pre-shipment), `RefundRequested → Refunded | Disputed` — enforced by `OrderStateMachine` (illegal transitions throw).

**Money:** every order payment posts a balanced double-entry set — `DR 1000 Cash (grand total) | CR 2100 VAT, CR 3100 Delivery, CR 3000 Commission, CR 2000 VendorPayable (per vendor), CR 3200 FirstPartyRevenue`. Refunds post proportional reversal legs. Any unbalanced posting throws (`LedgerPoster.AssertBalanced`).

## Prerequisites

- .NET 9 SDK (`dotnet --version` → 9.x)
- Docker (for PostgreSQL 18 + Redis + MinIO via docker compose)

## Run locally

```bash
cd backend

# 1) infrastructure
docker compose up -d postgres redis minio

# 2) environment (edit credentials as needed)
cp .env.example .env

# 3) migrations — one per module context
dotnet ef migrations add InitialCreate --project src/Modules/MarketHub.Modules.Identity      --startup-project src/MarketHub.Api --context IdentityDbContext
dotnet ef migrations add InitialCreate --project src/Modules/MarketHub.Modules.Catalog       --startup-project src/MarketHub.Api --context CatalogDbContext
dotnet ef migrations add InitialCreate --project src/Modules/MarketHub.Modules.Sales         --startup-project src/MarketHub.Api --context SalesDbContext
dotnet ef migrations add InitialCreate --project src/Modules/MarketHub.Modules.Payments      --startup-project src/MarketHub.Api --context PaymentsDbContext
dotnet ef migrations add InitialCreate --project src/Modules/MarketHub.Modules.Finance       --startup-project src/MarketHub.Api --context FinanceDbContext
dotnet ef migrations add InitialCreate --project src/Modules/MarketHub.Modules.Notifications --startup-project src/MarketHub.Api --context NotificationsDbContext

# 4) run (dev: auto-migrates + idempotent seeding on startup)
dotnet run --project src/MarketHub.Api
```

- Swagger: http://localhost:8080/swagger
- Health: http://localhost:8080/health

**Seeded accounts** (dev):

| Role | Email | Password | State |
|---|---|---|---|
| Admin | admin@markethub.et | Admin@12345 | full platform access |
| Vendor | vendor1@markethub.et | Vendor@12345 | **Approved** (KYC verified) |
| Vendor | vendor2@markethub.et | Vendor@12345 | Approved |
| Buyer | buyer@markethub.et | Buyer@12345 | — |

**Tests:** `dotnet test tests/MarketHub.Tests`

## API surface (v1)

| Area | Endpoints |
|---|---|
| Auth | `POST /api/auth/register` (role Buyer/Vendor; vendor ⇒ 3+ documents covering all required types; consents to all 3 active policies required) · `POST /api/auth/login` · `POST /api/auth/refresh` · `GET /api/auth/me` |
| KYC | `GET /api/document-types?role=` · `GET /api/admin/vendors?status=` · `POST /api/admin/vendors/{id}/review` · `GET /api/admin/documents/{ownerId}/{id}` (private document stream) |
| Policies | `GET /api/policies` · `GET /api/policies/{key}` (TermsOfService · RulesAndRegulations · ReturnAndRefundPolicy) |
| Catalog | `GET /api/catalog/products` (search/filter/sort/paginate; DTOs include `colors` + `gallery`) · `GET /api/catalog/products/{slug}` · `GET /api/catalog/categories` · `POST /api/catalog/products` (vendor → PendingReview; admin → Active) · `POST /api/admin/catalog/products/{id}/moderate` · `GET /api/media/{key}` (product photo stream) |
| Sales | `GET /api/sales/delivery-zones` · cart GET/POST/PATCH `/api/sales/cart` (POST takes `colorId`) · `POST /api/sales/checkout` (server-computed delivery + 15% VAT; order items snapshot `colorName`/`colorHex`) · `GET /api/sales/orders` · `POST /api/sales/orders/{id}/refund-request` · vendor: `GET /api/vendor/sales/orders`, `POST /api/vendor/sales/orders/{id}/{accept\|reject\|ship\|deliver}` · admin: `GET /api/admin/sales/orders`, `POST /api/admin/sales/orders/{id}/refund-decision` |
| Payments | `POST /api/payments/initiate/{orderId}` · `POST /api/payments/webhook/{provider}` (HMAC-verified, idempotent) · `POST /api/payments/mock/confirm` (dev parity) · `GET /api/payments/{orderId}` |
| Finance | `GET /api/vendor/finance/earnings` · `GET/POST /api/vendor/finance/payouts` (**12h SLA** → `DueByUtc`) · admin: `GET /api/admin/payouts`, `POST /api/admin/payouts/{id}/process`, `GET /api/admin/reports/summary|revenue-over-time|top-vendors|top-products|vendor-income/{id}`, `GET/PUT /api/admin/commission` |
| Notifications | `GET /api/notifications` · `POST /api/notifications/read-all` |

## Product photos & color variants

Listings are photo-first and color-aware, mirroring the Next.js preview contract:

- **`POST /api/catalog/products`** accepts `images: [{ dataOrUrl, alt, colorIndex }]` (1–8 photos; `dataOrUrl` is a compressed `data:image/…;base64,…` payload or an https URL, `alt` is the angle label like "Front"/"Back") and `colors: [{ name, hex, stock, extraPriceETB }]` (≤6, unique names, hex `#1a1a1a`). With colors present, product `Stock` = Σ per-color stock (server-authoritative).
- Data-URL photos are decoded once and stored through **`IFileStorage`** (local disk in dev, MinIO/S3 in production via `FileStorage__Provider`); object-storage keys stream back through **`GET /api/media/{key}`**. External https URLs are stored as-is.
- **Cart & checkout are color-aware**: `POST /api/sales/cart` takes `colorId` (one cart line per product + color), the line price = base price + `extraPriceETB`, checkout validates/decrements **both** product and color stock, and order items snapshot `colorName` + `colorHex` for audit-proof invoices and returns.

## Payment gateway integration (mock → real)

`IPaymentGateway` (initiate/verify/ValidateWebhookSignature) is implemented by **MockGateway** (active by default) and shaped adapters for **Chapa**, **telebirr**, **SantimPay**. To go live:

1. Implement the provider-specific request/response in the adapter (marked `PRODUCTION TODO`), verify field names against the provider's current docs.
2. Set credentials **only** via environment config: `Chapa__ApiKey`, `Telebirr__AppId/AppKey/PublicKey`, `Santimpay__MerchantId/ApiKey` (see `.env.example`).
3. Switch provider: update the `PaymentProvider` setting (`Mock → Chapa | Telebirr | Santimpay`). **No checkout code changes.**

Webhook contract: raw body + `X-Signature` (HMAC-SHA256 hex of the raw body with the provider secret) + optional `X-Event-Id` for idempotency. Amount mismatches are rejected as tampering.

## Environment configuration

All secrets are injected via env vars / user-secrets — never hard-coded: `ConnectionStrings__Default`, `Redis__ConnectionString`, `Jwt__Key` (≥64 chars), `Cors__Origins`, `FileStorage__*` (Local dev / MinIO prod), `Payments__Provider`, gateway keys. Full list: `.env.example`.

## Security posture

JWT access (15 min) + rotating refresh tokens; BCrypt password hashing; role policies (`Admin`, `Vendor`, `Buyer`, `VendorOrAdmin`); rate limiting (100 req/min per IP globally, 10/min on auth); input validation on every command; private document storage (GUID keys, streamed only to admins); audit-grade consents (version + timestamp + IP); outbox-driven notifications; row-version concurrency on orders and payouts; per-module DB schemas; ProblemDetails error contract. Production TODOs flagged in code: MinIO adapter, real gateway field verification, email/SMS senders.

## Notes

- `net9.0` per product decision — code is structured so bumping `Directory.Build.props` to a later LTS is a one-line change.
- Dev seeding is idempotent and dev-only (`MigrateAndSeedAsync` runs when `ENV=Development`).

## Gap-fill v2

Industry-grade gap closure per `docs/GAP_PLAN.md` — same routes/JSON shapes as the Next.js preview mock.

### New endpoints

| Area | Endpoints |
|---|---|
| Discovery | `GET /api/catalog/home` (banners + flashSale + featured + topRated + newArrivals) · `GET /api/catalog/search/suggest?q=` (Meilisearch → SQL ILIKE fallback, 6 products + 3 categories) · product detail now returns `variants[]`, `reviewsSummary{avg,count,breakdown 5..1}`, `recentReviews[3]`, `related[6]` and increments `viewCount`; product list supports `sort=sold` |
| Reviews | `GET /api/catalog/products/{id}/reviews?page=` (paged, Approved-only, breakdown) · `POST /api/catalog/reviews` (auth; verified purchase = buyer owns a **Delivered/Completed** order containing the product; one per user+product+order; lands as Pending; vendor notified) · `GET /api/admin/reviews?status=` · `POST /api/admin/reviews/{id}/moderate` (approve ⇒ recomputes `Product.ratingAvg/ratingCount`) · `POST /api/vendor/reviews/{id}/reply` |
| Wishlist | `GET /api/sales/wishlist` · `POST /api/sales/wishlist/{productId}` (toggle ⇒ `{wishlisted}`) |
| Marketing | `GET/POST /api/admin/banners` · `PATCH/DELETE /api/admin/banners/{id}` · `POST /api/admin/flash-sale {productIds, discountPct 5..70, endsAt}` · `DELETE /api/admin/flash-sale` · `GET/POST /api/admin/coupons` · `PATCH /api/admin/coupons/{id} {isActive}` |
| Cart ops | `POST /api/sales/cart/reserve` (TTL = Settings.ReservationMinutes, supersedes previous Active batch, per-line conflicts reported) · `POST /api/sales/cart/release` · `POST /api/sales/cart/save` (abandoned-cart snapshot) · `POST /api/sales/cart/coupon {code, subtotal}` ⇒ `{ok, discountETB, message}` · cart add accepts `variantId` (validated stock + isActive) |
| Checkout/orders | `POST /api/sales/checkout` accepts `addressId?` or inline `address` (+`saveAddress`), `couponCode` — VAT on `(subtotal − discount + delivery)`, issues `deliveryPin` (4-digit) + `trackingCode` (`MH-TRK-…`), writes `OrderEvent(Placed)` and binds stock reservations; **stock decrements only on payment confirmation** (`OrderPaymentConfirmedHandler` → `IOrderFulfillment`) · `GET /api/sales/orders` includes `events[]`, `deliveryPin`, `trackingCode` · `POST /api/sales/orders/{id}/cancel` (Placed only ⇒ releases reservations) |
| Returns | `POST /api/sales/returns` (per-item; reason enum, description ≥10 chars, evidence URLs; window = ReturnWindowDays from delivery) · `GET /api/sales/returns` · vendor: `GET /api/vendor/returns`, `POST /api/vendor/returns/{id}/decision` · admin: `GET /api/admin/returns?status=`, `POST /api/admin/returns/{id}/arbitrate {approve, refundETB, note?}` (balanced refund ledger legs + restock + `Refunded`) |
| Vendor console | ship action **requires** `trackingCode`; deliver **requires** the buyer's 4-digit `pin` (400 on mismatch) · `PATCH /api/vendor/variants/{id} {priceETB?, stock?, isActive?}` (ownership + product stock resync) · `POST /api/vendor/products/bulk {csv}` (`title,description,priceETB,stock,categorySlug,sku` ⇒ PendingReview products, per-row errors) · `GET /api/vendor/performance` · `GET /api/vendor/dashboard` (14-day series + totals) |
| Staff / auth | `GET/POST /api/admin/staff` · `PATCH /api/admin/staff/{id}` (policy **SuperAdminOnly**) · register accepts optional `lang`; `/api/auth/me` returns `adminRole` + `lang` · `POST /api/admin/jobs/document-expiry`, `POST /api/admin/jobs/stock-sweeper` (manual Ops triggers) |
| Ops | `GET /api/admin/abandoned-carts` · `POST /api/admin/abandoned-carts/remind {userId}` · `GET /api/admin/vendors/{id}/performance` |

### Integrations & environment

| Concern | Abstraction | Impls | Config (env only) |
|---|---|---|---|
| SMS | `ISmsSender` | `EthioTelecomSmsSender`, `SafaricomSmsSender` (real HTTP via `IHttpClientFactory`), `MockSmsSender` | `Sms__Provider`, `EthioTelecom__Endpoint/ApiKey/SenderId`, `Safaricom__Endpoint/ApiKey/Shortcode` |
| Email | `IEmailSender` | `SmtpEmailSender` (MailKit), `MockEmailSender` | `Email__Provider`, `Smtp__Host/Port/User/Pass/From` |
| Search | `ISearchIndexer` | `MeilisearchIndexer` (PUT documents, typo-tolerance ON, Bearer `Meilisearch:ApiKey`), `NullSearchIndexer` | `Search__Provider`, `Meilisearch__Host/ApiKey/IndexPrefix` |
| CDN | `IFileStorage` | unchanged; public base via env | `Cdn__BaseUrl` |

`NotificationDispatcher` always writes the in-app notification, then sends SMS + Email for the critical templates (`OrderConfirmed`, `OrderShipped`, `OrderDelivered`, `PayoutProcessed`, `ReturnDecided`, `VendorApproved`, `AbandonedCart`) whenever the user has a destination on file — **every attempt is logged as an `OutboundMessage` row** (Sent / Failed / Skipped) visible in the admin Ops view. Dispatch triggers: payment confirmed (buyer + each vendor), ship, deliver, payout processed, return arbitration, vendor approval, abandoned cart (job + admin remind).

Full key list: `.env.example` (`docker compose` passes them to the `api` service). `docker compose up -d meilisearch mailpit` starts the local search engine (UI on :7700) and the SMTP catch-all (UI on :8025).

### Background jobs (Quartz)

| Job | Cadence | Behaviour |
|---|---|---|
| `ReservationSweeperJob` | 1 min | expires Active stock reservations past their TTL (release ⇒ available again) |
| `FlashSaleSchedulerJob` | 1 min | clears expired `salePriceETB/saleEndsAt` so listings never show stale discounts |
| `AbandonedCartReminderJob` | 30 min | SMS reminder for carts idle past `AbandonedCartHours` (idempotent via `reminderSentAt`) |
| `DocumentExpiryMonitorJob` | 1 h | flags documents past `expiresAt`; suspends vendors with expired **required** docs + notifies |
| `OverduePayoutJob` | 15 min | flags 12h payout-SLA breaches (existing, unchanged) |

All jobs are idempotent and log their own failures; each also has an admin `POST /api/admin/jobs/*` manual trigger.

### Staff roles

Admin accounts carry a back-office sub-role `adminRole`: **SuperAdmin | Support | Finance | Moderator** (`SuperAdmin` can PATCH staff accounts via the `SuperAdminOnly` policy; the claim is emitted in the JWT). Dev seed adds `support@markethub.et` / `Support@12345` (AdminRole=Support). Product decisions: reservation-aware availability (`available = stock − Σ active reservations`) with stock decremented exactly once at payment confirmation; reviews always moderated before publishing; refunds always arbitrated by admins with balanced double-entry legs.
