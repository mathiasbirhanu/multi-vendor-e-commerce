# MarketHub — Gap-Fill Implementation Contract (v2)

Single source of truth for closing the industry-grade gaps. Three consumers:
1. `.NET 9 backend` (`/backend`) — production code
2. `Next.js preview data layer` (`/prisma`, `/src/server`) — mock API mirroring the contract 1:1
3. `Next.js UI` (`/src/components/marketplace`) — consumes exactly these shapes

Rule: **same routes, same JSON shapes** in mock and .NET. Payments stay as-is (mock gateway + env-ready adapters).

---

## A. New data models

```
Review            id, productId, userId, orderId?, rating(1..5), title?, body, imageUrls(JSON str[] | null),
                  status: Pending|Approved|Rejected, vendorReply?, vendorRepliedAt?, createdAt
ProductVariant    id, productId, sku(unique), colorId? -> ProductColor, size?, priceETB(null=base price),
                  stock, isActive, sortOrder   |  relations: orderItems, stockReservations
StockReservation  id, userId, productId, variantId?, colorId?, qty, status: Active|Consumed|Released|Expired,
                  expiresAt, orderId?, createdAt
Address           id, userId, label("Home"), fullName, phone(+251…), region, city, subCity, woreda,
                  kebele?, landmark, isDefault, createdAt
OrderEvent        id, orderId, status, note?, byRole: System|Buyer|Vendor|Admin|Courier, createdAt
ReturnRequest     id, orderItemId, orderId, buyerId, vendorId?, reason(Damaged|WrongItem|NotAsDescribed|ChangedMind|Other),
                  description, evidenceUrls(JSON str[] | null), status: Requested|VendorApproved|VendorRejected|
                  AdminReview|Approved|Rejected|Refunded, vendorNote?, adminDecision?, adminId?, refundETB?, createdAt, resolvedAt?
Coupon            id, code(unique), description?, type: Percent|Fixed, value, minSubtotal, maxDiscount?,
                  maxUses, usedCount, startsAt, expiresAt, isActive  |  redemptions
CouponRedemption  id, couponId, orderId, userId, discountETB, createdAt
WishlistItem      id, userId, productId, createdAt   @@unique([userId,productId])
Banner            id, title, imageUrl, link?, position, isActive, startsAt?, endsAt?
Cart              id, userId(unique), itemsJson([{variantId?,colorId?,productId,qty}]), updatedAt, reminderSentAt?
OutboundMessage   id, userId?, channel: Sms|Email, template, destination, payloadJson?, provider, providerRef?,
                  status: Queued|Sent|Failed|Skipped, error?, createdAt, sentAt?
```

Field additions:
```
User          + adminRole: SuperAdmin|Support|Finance|Moderator (null for Buyer/Vendor), + lang: "en"|"am" (default en)
VendorProfile + ratingCount (default 0)
Document      + expiresAt?, + verifiedAt?
Product       + salePriceETB?, + saleEndsAt?, + soldCount(0), + viewCount(0)
Order         + trackingCode?, + deliveryPin?(4 digits), + addressId?, + couponCode?, + couponDiscountETB(0), + events
OrderItem     + variantId?, + variantSku?, + variantLabel? ("Black / 42")
CartItem      + variantId? (variant takes precedence over color when present)
Settings      + keys: ReservationMinutes=20, AbandonedCartHours=2, SmsProvider=Mock, EmailProvider=Mock,
              SearchProvider=Meilisearch, FreeDeliveryAddisThreshold=1000 (existing keys unchanged)
```

Stock rule: `available = stock − Σ active reservations`. Checkout reserves; payment confirm decrements stock and
consumes reservations; expiry/cancel releases. Product.stock stays the denormalized total.

---

## B. Endpoint contract (mock + .NET 1:1)

### Discovery / catalog
| Route | Shape |
|---|---|
| `GET /api/catalog/home` | `{ banners[], flashSale: { endsAt, products: Card[] } | null, featured: Card[], topRated: Card[], newArrivals: Card[], categories[] }` |
| `GET /api/catalog/products` | existing + `sort=sold` (soldCount desc); items include `salePriceETB`, `saleEndsAt`, `soldCount`, `variants?` count |
| `GET /api/catalog/search/suggest?q=` | `{ products: [{slug,title,thumb,priceETB}], categories: [{slug,name}] }` (max 6 + 3) |
| `GET /api/catalog/products/{slug}` | product + `variants[]` (active, with colorHex) + `reviewsSummary {avg,count,breakdown:{5..1}}` + `recentReviews[3]` (Approved) + `related[6]` (same category, top rated, excl. self) + increments viewCount |
| `GET /api/catalog/products/{id}/reviews?page=` | paged Approved reviews `{items, page, pageSize, totalCount, breakdown}` |
| `POST /api/catalog/reviews` | body `{productId, orderId, rating 1..5, title?, body(≥10 chars), imageUrls?[]}`; auth; verified = buyer has a Delivered/Completed order containing the product; one review per (user, product, order); status Pending; notify vendor |
| `GET /api/sales/wishlist` | `{ items: [{id, productId, product: Card}] }` |
| `POST /api/sales/wishlist/{productId}` | toggle → `{ wishlisted }` |

`Card` (product card) = `{ id, slug, title, priceETB, salePriceETB?, compareAtPriceETB?, thumb, ratingAvg, ratingCount, soldCount, vendorName, platform(boolean), stock, colors?: [{name,hex}] }`

### Cart / reservation / coupon
| Route | Shape |
|---|---|
| `POST /api/sales/cart/reserve` | reserves for current cart → `{ expiresAt, reservations: [{key, ok, available}], allOk }`; releases previous Active ones first |
| `POST /api/sales/cart/release` | `{ ok }` |
| `POST /api/sales/cart/save` | upsert Cart snapshot (abandoned-cart recovery), debounced from client |
| `POST /api/sales/cart/coupon` | `{ code, subtotal }` → `{ ok, discountETB, message }` (validates window/uses/min) |
| cart add/update | accept `variantId`; validate variant stock & isActive |

### Checkout / orders
| Route | Shape |
|---|---|
| `POST /api/sales/checkout` | body `{ addressId?, address?: {fullName,phone,region,city,subCity,woreda,kebele,landmark}, saveAddress?, zoneId, phone, itemIds?, couponCode? }` → order + `deliveryPin` + `trackingCode = "MH-TRK-…"` + OrderEvent(Placed) + consumes/creates reservations; VAT on `(subtotal − discount + delivery)`; `couponDiscountETB` recorded; coupon redemption row; ledger unchanged until payment |
| `GET /api/sales/orders` | + `events[]`, `deliveryPin`, `trackingCode` |
| `POST /api/sales/orders/{id}/cancel` | only while `Placed` → Cancelled + release reservations + OrderEvent + notify |
| `POST /api/sales/returns` | `{ orderItemId, reason, description(≥10), evidenceUrls?[] }` → ReturnRequest(Requested), item.refundStatus=Requested, notify vendor+admin; window = ReturnWindowDays from Delivered |
| `GET /api/sales/returns` | `{ items }` (mine) |

### Vendor console additions
| Route | Shape |
|---|---|
| `POST /api/vendor/orders/{id}/action` | existing + `packed` (VendorAccepted→Shipped requires `trackingCode` in body; sets order.trackingCode) and `deliver` (Shipped→Delivered requires `pin` matching order.deliveryPin) |
| `PATCH /api/vendor/variants/{id}` | `{ priceETB?, stock?, isActive? }` ownership-enforced; keeps Product.stock = Σ |
| `POST /api/vendor/products/bulk` | `{ csv }` → header `title,description,priceETB,stock,categorySlug,sku`; creates PendingReview products (1 default variant each); `{ created, errors[] }` |
| `GET /api/vendor/returns` | `{ items }` for my products |
| `POST /api/vendor/returns/{id}/decision` | `{ approve, note? }` → VendorApproved/VendorRejected; notify buyer + admin (admin issues final refund) |
| `POST /api/vendor/reviews/{id}/reply` | `{ reply }` (owner of product) |
| `GET /api/vendor/performance` | `{ ratingAvg, ratingCount, totalSold, fulfillmentRate, cancelRate, openReturns, disputeRate }` computed from orders/reviews/returns |
| `GET /api/vendor/dashboard` | `{ daily: [{date, revenue, orders}] (14d), totals: {products, activeOrders, pendingReturns, lowStock} }` |

### Admin console additions
| Route | Shape |
|---|---|
| `GET /api/admin/reviews?status=` · `POST /api/admin/reviews/{id}/moderate {approve,note?}` | approve → recompute Product.ratingAvg/ratingCount; notify reviewer |
| `GET /api/admin/returns?status=` · `POST /api/admin/returns/{id}/arbitrate {approve, refundETB, note?}` | approve → balanced refund ledger legs (reuse refund pattern, amount=refundETB), item.refundStatus=Approved, restock variant/color/product, ReturnRequest.Refunded, notify buyer+vendor |
| `GET/POST /api/admin/staff` · `PATCH /api/admin/staff/{id}` | list/create/patch admin users `{email,fullName,adminRole,status}`; PATCH restricted to SuperAdmin; password required on create |
| `GET /api/admin/settings` · `PUT /api/admin/settings` | grouped known keys w/ validation; PUT `{entries:[{key,value}]}` |
| `GET/POST /api/admin/banners` · `PATCH/DELETE /api/admin/banners/{id}` | marketing |
| `POST /api/admin/flash-sale` | `{productIds, discountPct 5..70, endsAt ISO}` sets salePriceETB/saleEndsAt; `DELETE /api/admin/flash-sale` ends now |
| `GET/POST /api/admin/coupons · PATCH /api/admin/coupons/{id}` | `{isActive}` |
| `GET /api/admin/vendors/{id}/performance` | same shape as vendor self-view |
| `GET /api/admin/abandoned-carts` | `{items:[{userId,email,phone,itemsJson,updatedAt,reminderSentAt}]}` |
| `POST /api/admin/abandoned-carts/remind` | `{userId}` sends SMS via provider adapter → OutboundMessage |
| `POST /api/admin/jobs/document-expiry` | expire docs past expiresAt → vendor Suspended + notify (Quartz in .NET) |
| `POST /api/admin/jobs/stock-sweeper` | release expired reservations (Quartz in .NET) |
| `GET /api/admin/reports/summary` etc. | existing, unchanged |

### Auth
`register` unchanged + optional `lang`; `me` returns `adminRole`, `lang`. Staff login = normal login with role Admin.

---

## C. External integrations — real adapter code, keys in env only

| Concern | Abstraction | Production impl (real code) | Env keys |
|---|---|---|---|
| SMS | `ISmsSender` | `EthioTelecomSmsSender` (HTTP), `SafaricomSmsSender` (HTTP), `MockSmsSender` (logs OutboundMessage) | `Sms__Provider`, `EthioTelecom__Endpoint`, `EthioTelecom__ApiKey`, `EthioTelecom__SenderId`, `Safaricom__Endpoint`, `Safaricom__ApiKey`, `Safaricom__Shortcode` |
| Email | `IEmailSender` | `SmtpEmailSender` (MailKit), `MockEmailSender` | `Email__Provider`, `Smtp__Host`, `Smtp__Port`, `Smtp__User`, `Smtp__Pass`, `Smtp__From` |
| Search | `ISearchIndexer` | `MeilisearchIndexer` (HttpClient: index products `{id,title,description,category,price,vendor,rating,sold}`, typo-tolerance ON), `NullSearchIndexer` | `Search__Provider`, `Meilisearch__Host`, `Meilisearch__ApiKey`, `Meilisearch__IndexPrefix` |
| Images/CDN | `IFileStorage` (exists) | unchanged; CDN base via env | `Cdn__BaseUrl` |
| Payments | existing adapters | unchanged | existing |

Trigger points: payment confirmed → SMS+Email buyer, SMS vendors; shipped/delivered → SMS buyer; payout processed → SMS vendor; return decided → SMS buyer; abandoned cart → SMS. Preview mock writes `OutboundMessage` rows (viewable in admin) instead of real HTTP.

Background jobs (.NET Quartz; preview = admin-triggered endpoints + lazy sweeps):
`ReservationSweeper` (min), `AbandonedCartReminder` (30m), `DocumentExpiryMonitor` (hourly), `FlashSaleScheduler` (min).

---

## D. UI additions (Next.js)

- Header: search box with live suggestions; wishlist icon+count; language toggle EN/አማርኛ; data-saver toggle (exists)
- Home: banner carousel, Flash Sale strip (countdown + sale prices), Featured, Categories, Top Rated, New Arrivals
- Product detail: gallery (color swatches → per-color angle shots), variant selector (color × size, per-variant stock/price), reviews block (breakdown bars, list, write-review dialog w/ photos, verified badge), vendor reply display, seller card, related products, JSON-LD, sticky buy box
- Cart/checkout: reservation countdown timer, address book (select/add Ethiopian address: region→sub-city→woreda→kebele→landmark), coupon field, PIN notice after payment
- Orders: status timeline from OrderEvents, tracking code, delivery PIN card, per-item return request dialog (reason + evidence upload), cancel button
- Vendor console tabs: Dashboard(chart), Orders(queue + accept/pack+tracking/deliver w/ PIN), Products(inline stock/price editor, variants, CSV bulk upload), Returns, Reviews(reply), Payouts(existing), Performance
- Admin console tabs: Overview(existing+new), Vendors, Products, Reviews, Returns, Orders, Payouts, Marketing(banners/flash/coupons), Staff, Settings, Ops(abandoned carts + job triggers)
- SEO: metadata + OG in layout, JSON-LD Product, sitemap.ts, robots.txt (exists)
- PWA: manifest.webmanifest + sw.js (cache-first images, network-first API w/ timeout) — registered only in production build
- i18n: `src/lib/i18n.ts` dictionary en/am, header toggle, persisted to localStorage + user.lang
