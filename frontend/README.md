# MarketHub.et — Storefront (Next.js 16)

Ethiopian multi-vendor marketplace + first-party store. Production stack: **.NET 9 + PostgreSQL + Next.js 16**.
This sandbox runs a pixel-faithful preview of the storefront on Next.js + Prisma/SQLite with a
mock API (`src/server/`) that mirrors the .NET contract — see `src/lib/api.ts`.

## Performance: built for slow networks & low-end devices

Ethiopian reality: 3G metered data, high-latency international routes, budget Android phones.
The storefront is engineered around two budgets — **bytes** and **CPU**.

### 1. Image pipeline (the heaviest resource on any commerce site)

| Measure | Where | Effect |
| --- | --- | --- |
| AVIF → WebP → optimized JPEG negotiation | `next.config.ts` `images.formats` | 30–50% smaller than JPEG at equal quality |
| Responsive `srcset` + explicit `sizes` per slot | every `ProductPhoto` usage | a phone downloads a ~15–40 KB thumb, never the original |
| Fixed aspect-ratio boxes (`aspect-square`/`aspect-[4/5]`) | cards, gallery, hero | zero layout shift (CLS ≈ 0) → no reflow CPU waste |
| Shimmer placeholder + fade-in, gradient fallback tile | `shared.tsx` `ProductPhoto` | perceived speed; a dead URL never shows a broken-image glyph |
| `priority` only for above-the-fold LCP images | first 2 cards, hero | LCP without mass-preloading the grid |
| Lazy loading below the fold | next/image default | off-screen photos never touch the radio |
| Quality tiers 40/60/75/80/90 | `next.config.ts` `qualities` | 40 = data saver, 60 = thumbs, 75/80 = cards/gallery, 90 = zoom |
| 30-day immutable cache on optimized output | `minimumCacheTTL` | repeat visits are nearly free; CDN-friendly |
| Strict `remotePatterns` allow-list | `next.config.ts` | only the product-image CDN may be optimized (blocks `/_next/image` SSRF abuse) |
| `preconnect` + `dns-prefetch` to the image CDN | `layout.tsx` | saves a DNS+TLS round trip (~300–600 ms on high-latency routes) |
| Upload-time rules (production `IFileStorage`) | .NET backend | JPEG/PNG/WebP only, size caps, server-side multi-size derivative generation, EXIF stripping |

### 2. Data saver mode (low-bandwidth adaptation)

`src/lib/lowdata.ts` + header toggle (cycle **auto → on → off**):

- **Auto-detect**: Android "Data saver" (`navigator.connection.saveData`) or `effectiveType` 2g/3g.
- When active: every image drops to **quality 40**, the second (hover) product photo per card is
  **never fetched** (halves grid traffic), and all looping animations stop.
- Preference persists in `localStorage`; `html[data-saver]` lets CSS stop animations without a re-render.

### 3. CPU discipline (budget phones)

- **Route-level code splitting**: Admin, Vendor, Checkout, Auth load via `next/dynamic` — their JS
  is fetched only when opened (`src/app/page.tsx`). Smaller bundle = less download **and** less parse/compile on weak CPUs.
- **`content-visibility: auto`** on below-fold sections (`.cv-auto`): the browser skips layout/paint
  of far-off-screen content entirely.
- **CSS-only animations** — transform/opacity only (GPU-composited); no Framer Motion on the storefront;
  `prefers-reduced-motion` fully honored.
- **Zustand selector subscriptions** — components re-render only on the slice they use; no context-wide cascades.
- **Skeletons everywhere** instead of spinners; pagination (12/page) instead of unbounded lists.
- Search fires **on submit**, not per keystroke — zero extra requests while typing.

### 4. Server-side measures (production .NET 9 + CDN)

- gzip/brotli on all text assets (Next standalone compresses; nginx/brotli + CDN in production).
- ISR/stale-while-revalidate for product & category HTML; Redis response-cache for hot read APIs
  (categories, zones, settings, home feed).
- Pagination + indexed queries on every endpoint; DTO shaping so no over-fetching.
- HTTP/2 + `Cache-Control: immutable` for hashed static assets via CDN edge in Addis Ababa/Frankfurt PoPs.

### 5. Budgets (CI-enforced in production)

- LCP < 2.5 s, CLS < 0.1, INP < 200 ms — measured with Lighthouse CI under **Slow 4G + 4× CPU throttle**.
- Initial JS < 200 KB gzip per route; first-visit page weight < 1 MB; card image < 40 KB.

## Local commands

```bash
bun run dev        # dev server (managed by the sandbox)
bun run lint       # ESLint
bun run db:push    # push Prisma schema to SQLite
bun run db:reset   # re-run seed
```

## Where things live

- `src/components/marketplace/` — storefront, checkout, auth + policy consent, vendor console, admin console, product form (multi-image upload with color + angle tags)
- `src/server/` — mock API router/handlers mirroring the .NET 9 contract
- `prisma/` — preview data model + Ethiopian seed (zones, ETB products, VAT 15%, policies)
- `../backend/` — the production .NET 9 solution (modular monolith, CQRS, outbox, ledger)

---

## Gap-fill v2 — what's new (frontend + preview data layer)

All industry-grade gaps closed, mirrored 1:1 by the .NET 9 backend (see `backend/README.md` and `docs/GAP_PLAN.md`):

| Area | What shipped |
|---|---|
| Reviews & ratings | Verified-purchase reviews (1–5★, photos, moderation queue, seller replies, rating breakdown; product ratings recompute on approval) |
| SKU variants | Color × size matrix with per-SKU price/stock/SKU; buyer picks exact variant; vendors edit inline; CSV bulk import |
| Stock reservation | 20-min holds at checkout (configurable), oversell-proof availability, admin sweeper job |
| Ethiopian addresses | Address book (region → sub-city → woreda → kebele → landmark), saved at checkout, auto zone selection |
| Tracking + delivery PIN | Per-order tracking code, courier must enter buyer's 4-digit PIN to mark Delivered |
| Returns center | Per-item return requests with photo evidence → vendor response (48h) → admin arbitration → balanced ledger refund + restock |
| Coupons & marketing | Coupon codes (percent/fixed, caps, windows), flash sales with live countdown, homepage banner CMS |
| Wishlist | Save/unsave products, header badge |
| Notifications | In-app + SMS/Email dispatch log (OutboundMessage) — provider adapters (Ethio Telecom / Safaricom / SMTP) read keys from env only |
| Staff roles | SuperAdmin / Support / Finance / Moderator, least-privilege admin console |
| Settings UI | VAT, commission, SLA, reservation TTL, providers — all editable in-platform |
| Search | Live suggest dropdown; production backend ships a real Meilisearch adapter (typo-tolerant, Amharic-friendly) |
| SEO & PWA | Metadata + OG, JSON-LD Product schema, sitemap.xml, manifest + service worker (image cache-first, API network-first) |
| i18n | EN / አማርኛ toggle across the storefront shell |

### Demo accounts (preview)

| Role | Email | Password |
|---|---|---|
| Admin (SuperAdmin) | admin@markethub.et | Admin@12345 |
| Admin staff (Support) | support@markethub.et | Admin@12345 |
| Vendor (approved) | vendor1@markethub.et | Vendor@12345 |
| Buyer | buyer@markethub.et | Buyer@12345 |
