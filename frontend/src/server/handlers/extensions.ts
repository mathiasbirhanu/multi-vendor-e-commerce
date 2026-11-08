// Gap-fill handlers (GAP_PLAN section B): discovery/home, reviews, wishlist, cart reservation &
// coupons, buyer returns, vendor console additions (variants, bulk CSV, returns, reviews, performance,
// dashboard) and admin console additions (reviews, returns arbitration, staff, settings, marketing,
// abandoned carts, job triggers). Mirrors the .NET API contract 1:1.
import { NextRequest } from 'next/server'
import { db } from '@/lib/db'
import { dispatch, sendSms } from '@/lib/providers'
import { r2 } from '../money'
import { requireRole, requireUser } from '../session'
import { addOrderEvent, availableQty, bad, getSetting, json, notify, validateCoupon } from './store'

// ---------------- shared helpers ----------------
const parseJsonArr = (s: string | null | undefined): string[] | null => {
  try {
    const v = s ? JSON.parse(s) : null
    return Array.isArray(v) ? v.map(String) : null
  } catch {
    return null
  }
}
const dayKey = (d: Date) => d.toISOString().slice(0, 10)
const startOfDay = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate())
const EMAIL_RE = /^\S+@\S+\.\S+$/

type CardProduct = {
  id: string; slug: string; title: string; priceETB: number; compareAtPriceETB: number | null
  salePriceETB: number | null; saleEndsAt: Date | null; stock: number; ratingAvg: number; ratingCount: number
  soldCount: number; vendorName: string | null; vendorId: string | null
  gallery?: { url: string; colorId: string | null }[]
  colors?: { name: string; hex: string }[]
}

/** `Card` shape from GAP_PLAN B — the product card the UI renders everywhere. */
function toCard(p: CardProduct) {
  const thumb = p.gallery?.find(g => !g.colorId)?.url ?? p.gallery?.[0]?.url ?? null
  return {
    id: p.id, slug: p.slug, title: p.title, priceETB: p.priceETB,
    salePriceETB: p.salePriceETB ?? undefined, compareAtPriceETB: p.compareAtPriceETB ?? undefined,
    thumb, ratingAvg: p.ratingAvg, ratingCount: p.ratingCount, soldCount: p.soldCount,
    vendorName: p.vendorName, platform: p.vendorId == null, stock: p.stock,
    colors: p.colors?.length ? p.colors.map(c => ({ name: c.name, hex: c.hex })) : undefined,
  }
}

const cardInclude = {
  gallery: { orderBy: { sortOrder: 'asc' as const } },
  colors: { orderBy: { sortOrder: 'asc' as const } },
}

/** Recompute Product.ratingAvg / ratingCount from Approved reviews (mirrors .NET ReviewProjection) */
async function recomputeProductRating(productId: string) {
  const agg = await db.review.aggregate({
    where: { productId, status: 'Approved' }, _avg: { rating: true }, _count: { _all: true },
  })
  await db.product.update({
    where: { id: productId },
    data: { ratingAvg: r2(agg._avg.rating ?? 0), ratingCount: agg._count._all },
  })
}

const REVIEW_REASONS = ['Damaged', 'WrongItem', 'NotAsDescribed', 'ChangedMind', 'Other']

// =================================================================
// DISCOVERY / CATALOG
// =================================================================
export async function catalogHome() {
  const now = new Date()
  const [banners, saleRows, featured, topRated, newArrivals, cats] = await Promise.all([
    db.banner.findMany({
      where: { isActive: true, OR: [{ startsAt: null }, { startsAt: { lte: now } }], AND: [{ OR: [{ endsAt: null }, { endsAt: { gt: now } }] }] },
      orderBy: { position: 'asc' },
    }),
    db.product.findMany({ where: { status: 'Active', salePriceETB: { not: null }, saleEndsAt: { gt: now } }, orderBy: { saleEndsAt: 'asc' }, take: 10, include: cardInclude }),
    db.product.findMany({ where: { status: 'Active', isFeatured: true }, orderBy: { ratingAvg: 'desc' }, take: 8, include: cardInclude }),
    db.product.findMany({ where: { status: 'Active', ratingCount: { gt: 0 } }, orderBy: { ratingAvg: 'desc' }, take: 8, include: cardInclude }),
    db.product.findMany({ where: { status: 'Active' }, orderBy: { createdAt: 'desc' }, take: 8, include: cardInclude }),
    db.category.findMany({ orderBy: { sortOrder: 'asc' }, include: { _count: { select: { products: { where: { status: 'Active' } } } } } }),
  ])
  const endsAt = saleRows.length ? saleRows.reduce((min, p) => (p.saleEndsAt && p.saleEndsAt < min ? p.saleEndsAt : min), saleRows[0].saleEndsAt!) : null
  return json({
    banners: banners.map(b => ({ id: b.id, title: b.title, imageUrl: b.imageUrl, link: b.link, position: b.position })),
    flashSale: saleRows.length ? { endsAt, products: saleRows.map(toCard) } : null,
    featured: featured.map(toCard),
    topRated: topRated.map(toCard),
    newArrivals: newArrivals.map(toCard),
    categories: cats.map(c => ({ id: c.id, name: c.name, slug: c.slug, productCount: c._count.products })),
  })
}

export async function catalogSuggest(req: NextRequest) {
  const q = (req.nextUrl.searchParams.get('q') ?? '').trim()
  if (!q) return json({ products: [], categories: [] })
  const [products, categories] = await Promise.all([
    db.product.findMany({
      where: { status: 'Active', OR: [{ title: { contains: q } }, { description: { contains: q } }] },
      orderBy: { soldCount: 'desc' }, take: 6,
      include: { gallery: { orderBy: { sortOrder: 'asc' }, take: 1 } },
    }),
    db.category.findMany({ where: { isActive: true, name: { contains: q } }, orderBy: { sortOrder: 'asc' }, take: 3 }),
  ])
  return json({
    products: products.map(p => ({ slug: p.slug, title: p.title, thumb: p.gallery[0]?.url ?? null, priceETB: p.priceETB })),
    categories: categories.map(c => ({ slug: c.slug, name: c.name })),
  })
}

export async function catalogProductDetail(slug: string) {
  const product = await db.product.findUnique({
    where: { slug },
    include: {
      category: true,
      vendor: { select: { id: true, businessName: true, city: true, region: true, ratingAvg: true, ratingCount: true, status: true } },
      colors: { orderBy: { sortOrder: 'asc' } },
      gallery: { orderBy: { sortOrder: 'asc' } },
    },
  })
  if (!product) return bad('Product not found', 404)
  const [variants, avgAgg, breakdownRows, recent, related] = await Promise.all([
    db.productVariant.findMany({
      where: { productId: product.id, isActive: true },
      orderBy: { sortOrder: 'asc' },
      include: { color: true },
    }),
    db.review.aggregate({ where: { productId: product.id, status: 'Approved' }, _avg: { rating: true }, _count: { _all: true } }),
    db.review.groupBy({ by: ['rating'], where: { productId: product.id, status: 'Approved' }, _count: { _all: true } }),
    db.review.findMany({
      where: { productId: product.id, status: 'Approved' },
      orderBy: { createdAt: 'desc' }, take: 3,
      include: { user: { select: { id: true, fullName: true } } },
    }),
    db.product.findMany({
      where: { categoryId: product.categoryId, status: 'Active', id: { not: product.id } },
      orderBy: { ratingAvg: 'desc' }, take: 6, include: cardInclude,
    }),
  ])
  await db.product.update({ where: { id: product.id }, data: { viewCount: { increment: 1 } } })
  const bc = (r: number) => breakdownRows.find(b => b.rating === r)?._count._all ?? 0
  return json({
    product,
    variants: variants.map(v => ({
      id: v.id, productId: v.productId, sku: v.sku, colorId: v.colorId, size: v.size,
      priceETB: v.priceETB, stock: v.stock, isActive: v.isActive, colorHex: v.color?.hex ?? undefined,
    })),
    reviewsSummary: {
      avg: r2(avgAgg._avg.rating ?? 0), count: avgAgg._count._all,
      breakdown: { r5: bc(5), r4: bc(4), r3: bc(3), r2: bc(2), r1: bc(1) },
    },
    recentReviews: recent.map(r => ({ ...r, userName: r.user.fullName, imageUrls: parseJsonArr(r.imageUrls), user: undefined })),
    related: related.map(toCard),
  })
}

export async function catalogProductReviews(req: NextRequest, productId: string) {
  const page = Math.max(1, parseInt(req.nextUrl.searchParams.get('page') ?? '1', 10))
  const pageSize = Math.min(50, Math.max(1, parseInt(req.nextUrl.searchParams.get('pageSize') ?? '10', 10)))
  const product = await db.product.findUnique({ where: { id: productId }, select: { id: true } })
  if (!product) return bad('Product not found', 404)
  const [rows, total, breakdownRows] = await Promise.all([
    db.review.findMany({
      where: { productId, status: 'Approved' },
      orderBy: { createdAt: 'desc' }, skip: (page - 1) * pageSize, take: pageSize,
      include: { user: { select: { id: true, fullName: true } } },
    }),
    db.review.count({ where: { productId, status: 'Approved' } }),
    db.review.groupBy({ by: ['rating'], where: { productId, status: 'Approved' }, _count: { _all: true } }),
  ])
  const bc = (r: number) => breakdownRows.find(b => b.rating === r)?._count._all ?? 0
  return json({
    items: rows.map(r => ({ ...r, userName: r.user.fullName, imageUrls: parseJsonArr(r.imageUrls), user: undefined })),
    page, pageSize, totalCount: total,
    breakdown: { r5: bc(5), r4: bc(4), r3: bc(3), r2: bc(2), r1: bc(1) },
  })
}

export async function createReview(req: NextRequest) {
  const user = await requireUser()
  const b = await req.json()
  const { productId, orderId, rating, title, body, imageUrls } = b ?? {}
  if (!productId || !Number.isInteger(rating) || rating < 1 || rating > 5) return bad('productId and rating (1..5) are required')
  if (!body || String(body).trim().length < 10) return bad('Review body must be at least 10 characters')
  const imgs = Array.isArray(imageUrls) ? imageUrls.map(String).slice(0, 5) : null
  const product = await db.product.findUnique({ where: { id: productId } })
  if (!product || product.status !== 'Active') return bad('Product not available', 404)

  // verified purchase = buyer has a Delivered/Completed order containing this product
  const verifiedOrder = await db.order.findFirst({
    where: { buyerId: user.id, status: { in: ['Delivered', 'Completed'] }, items: { some: { productId } } },
    orderBy: { createdAt: 'desc' },
  })
  let linkedOrderId: string | null = null
  if (orderId) {
    const o = await db.order.findFirst({ where: { id: orderId, buyerId: user.id, items: { some: { productId } } } })
    if (!o) return bad('orderId does not reference one of your orders containing this product', 400)
    linkedOrderId = o.id
  } else if (verifiedOrder) {
    linkedOrderId = verifiedOrder.id
  }
  // one review per (user, product, order)
  const dupe = await db.review.findFirst({
    where: { userId: user.id, productId, ...(linkedOrderId ? { orderId: linkedOrderId } : { orderId: null }) },
  })
  if (dupe) return bad('You already reviewed this product' + (linkedOrderId ? ' for this order' : ''), 409)

  const review = await db.review.create({
    data: {
      productId, userId: user.id, orderId: linkedOrderId, rating,
      title: title ? String(title).slice(0, 120) : null, body: String(body).trim(),
      imageUrls: imgs ? JSON.stringify(imgs) : null, status: 'Pending',
    },
  })
  if (product.vendorId) await notify(product.vendorId, 'NewReview', 'New review awaiting moderation', `"${product.title}" received a ${rating}-star review. It will appear after moderation.`)
  return json({ review: { ...review, verified: !!linkedOrderId }, status: 'Pending' }, 201)
}

// =================================================================
// WISHLIST
// =================================================================
export async function listWishlist() {
  const user = await requireUser()
  const items = await db.wishlistItem.findMany({
    where: { userId: user.id }, orderBy: { createdAt: 'desc' },
    include: { product: { include: cardInclude } },
  })
  return json({ items: items.map(w => ({ id: w.id, productId: w.productId, product: toCard(w.product) })) })
}

export async function toggleWishlist(req: NextRequest, productId: string) {
  const user = await requireUser()
  const product = await db.product.findUnique({ where: { id: productId }, select: { id: true, status: true } })
  if (!product || product.status !== 'Active') return bad('Product not available', 404)
  const existing = await db.wishlistItem.findUnique({ where: { userId_productId: { userId: user.id, productId } } })
  if (existing) {
    await db.wishlistItem.delete({ where: { id: existing.id } })
    return json({ wishlisted: false })
  }
  await db.wishlistItem.create({ data: { userId: user.id, productId } })
  return json({ wishlisted: true })
}

// =================================================================
// CART: RESERVE / RELEASE / SAVE / COUPON
// =================================================================
export async function cartReserve() {
  const user = await requireUser()
  const now = new Date()
  // lazy expiry sweep + release previous holds for this user (contract: release previous Active first)
  await db.stockReservation.updateMany({ where: { userId: user.id, status: 'Active', expiresAt: { lte: now } }, data: { status: 'Expired' } })
  await db.stockReservation.updateMany({ where: { userId: user.id, status: 'Active', orderId: null }, data: { status: 'Released' } })
  const items = await db.cartItem.findMany({ where: { userId: user.id } })
  const minutes = Math.max(1, parseInt(await getSetting('ReservationMinutes', '20'), 10))
  const expiresAt = new Date(Date.now() + minutes * 60000)
  const reservations: { key: string; ok: boolean; available: number }[] = []
  for (const i of items) {
    const key = `${i.productId}:${i.variantId ?? i.colorId ?? 'base'}`
    const available = await availableQty({ productId: i.productId, variantId: i.variantId, colorId: i.colorId })
    const ok = available >= i.quantity
    if (ok) {
      await db.stockReservation.create({
        data: {
          userId: user.id, productId: i.productId, variantId: i.variantId ?? null, colorId: i.colorId ?? null,
          qty: i.quantity, status: 'Active', expiresAt,
        },
      })
    }
    reservations.push({ key, ok, available })
  }
  return json({ expiresAt, reservations, allOk: reservations.every(r => r.ok) })
}

export async function cartRelease() {
  const user = await requireUser()
  const res = await db.stockReservation.updateMany({ where: { userId: user.id, status: 'Active', orderId: null }, data: { status: 'Released' } })
  return json({ ok: true, released: res.count })
}

export async function cartSave(req: NextRequest) {
  const user = await requireUser()
  const b = await req.json().catch(() => ({}))
  let snapshot: unknown[]
  if (Array.isArray(b.items)) {
    snapshot = b.items.map((i: { productId?: string; variantId?: string | null; colorId?: string | null; qty?: number; quantity?: number }) => ({
      productId: i.productId, variantId: i.variantId ?? null, colorId: i.colorId ?? null, qty: i.qty ?? i.quantity ?? 1,
    }))
  } else {
    const items = await db.cartItem.findMany({ where: { userId: user.id } })
    snapshot = items.map(i => ({ productId: i.productId, variantId: i.variantId ?? null, colorId: i.colorId ?? null, qty: i.quantity }))
  }
  await db.cart.upsert({
    where: { userId: user.id },
    update: { itemsJson: JSON.stringify(snapshot) },
    create: { userId: user.id, itemsJson: JSON.stringify(snapshot) },
  })
  return json({ ok: true, savedAt: new Date() })
}

export async function cartCouponCheck(req: NextRequest) {
  await requireUser()
  const { code, subtotal } = await req.json()
  const st = Number(subtotal)
  if (!Number.isFinite(st) || st < 0) return bad('subtotal must be a non-negative number')
  const check = await validateCoupon(String(code ?? ''), st)
  return json({ ok: check.ok, discountETB: check.discountETB, message: check.message })
}

// =================================================================
// ADDRESSES
// =================================================================
export async function listAddresses() {
  const user = await requireUser()
  const items = await db.address.findMany({ where: { userId: user.id }, orderBy: [{ isDefault: 'desc' }, { createdAt: 'desc' }] })
  return json({ items })
}

export async function createAddress(req: NextRequest) {
  const user = await requireUser()
  const b = await req.json()
  const { label, fullName, phone, region, city, subCity, woreda, kebele, landmark } = b ?? {}
  if (!fullName || !region || !city || !woreda) return bad('fullName, region, city and woreda are required')
  if (!phone || !/^\+251[79]\d{8}$/.test(phone)) return bad('Phone must be in +2519XXXXXXXX or +2517XXXXXXXX format')
  const isFirst = (await db.address.count({ where: { userId: user.id } })) === 0
  const address = await db.address.create({
    data: {
      userId: user.id, label: label ?? 'Home', fullName, phone, region, city,
      subCity: subCity ?? null, woreda, kebele: kebele ?? null, landmark: landmark ?? null,
      isDefault: b.isDefault ?? isFirst,
    },
  })
  if (address.isDefault) {
    await db.address.updateMany({ where: { userId: user.id, id: { not: address.id } }, data: { isDefault: false } })
  }
  return json({ address }, 201)
}

// =================================================================
// ORDERS: CANCEL / RETURNS
// =================================================================
export async function cancelOrder(req: NextRequest, orderId: string) {
  const user = await requireUser()
  const order = await db.order.findUnique({ where: { id: orderId }, include: { items: true } })
  if (!order || order.buyerId !== user.id) return bad('Order not found', 404)
  if (order.status !== 'Placed') return bad(`Only unpaid orders in Placed status can be cancelled — this order is ${order.status}`)
  await db.order.update({ where: { id: orderId }, data: { status: 'Cancelled', previousStatus: order.status } })
  await db.stockReservation.updateMany({ where: { orderId, status: 'Active' }, data: { status: 'Released' } })
  await addOrderEvent(orderId, 'Cancelled', 'Buyer', 'Cancelled by buyer before payment')
  await notify(user.id, 'OrderCancelled', 'Order cancelled', `Order ${order.number} was cancelled and the reserved stock was released.`)
  return json({ ok: true, status: 'Cancelled' })
}

export async function createReturn(req: NextRequest) {
  const user = await requireUser()
  const b = await req.json()
  const { orderItemId, reason, description, evidenceUrls } = b ?? {}
  if (!orderItemId) return bad('orderItemId is required')
  if (!REVIEW_REASONS.includes(reason)) return bad(`reason must be one of: ${REVIEW_REASONS.join(', ')}`)
  if (!description || String(description).trim().length < 10) return bad('Please describe the issue (at least 10 characters)')
  const urls = Array.isArray(evidenceUrls) ? evidenceUrls.map(String).slice(0, 5) : null
  const item = await db.orderItem.findUnique({ where: { id: orderItemId }, include: { order: true, product: true } })
  if (!item || item.order.buyerId !== user.id) return bad('Order item not found', 404)
  if (!['Delivered', 'Completed'].includes(item.order.status)) return bad('Returns can be requested once the order is delivered')
  const windowDays = parseInt(await getSetting('ReturnWindowDays', '7'), 10)
  if (item.order.deliveredAt && (Date.now() - item.order.deliveredAt.getTime()) > windowDays * 864e5)
    return bad(`The ${windowDays}-day return window has passed for this order. See the Return & Refund Policy.`)
  const existing = await db.returnRequest.findFirst({
    where: { orderItemId, status: { in: ['Requested', 'VendorApproved', 'AdminReview', 'Approved'] } },
  })
  if (existing) return bad('A return was already requested for this item', 409)
  const ret = await db.returnRequest.create({
    data: {
      orderItemId, orderId: item.orderId, buyerId: user.id, vendorId: item.vendorId,
      reason, description: String(description).trim(), evidenceUrls: urls ? JSON.stringify(urls) : null,
      status: 'Requested',
    },
  })
  await db.orderItem.update({ where: { id: orderItemId }, data: { refundStatus: 'Requested' } })
  if (item.vendorId) await notify(item.vendorId, 'ReturnRequested', 'Return request received', `Order ${item.order.number}: "${reason}" — respond within 48h.`)
  await notify('u_admin', 'ReturnRequested', 'Return request filed', `Order ${item.order.number}: "${reason}"`)
  return json({ return: ret }, 201)
}

const mapReturn = (r: {
  id: string; orderItemId: string; orderId: string; buyerId: string; vendorId: string | null
  reason: string; description: string; evidenceUrls: string | null; status: string; vendorNote: string | null
  adminDecision: string | null; adminId: string | null; refundETB: number | null
  createdAt: Date; resolvedAt: Date | null
  orderItem?: { titleSnapshot: string; variantSku: string | null; variantLabel: string | null; unitPriceETB: number; quantity: number } | null
  order?: { number: string } | null
  buyer?: { fullName: string } | null
  vendor?: { fullName: string } | null
  vendorProfileName?: string | null
}) => ({
  ...r,
  evidenceUrls: parseJsonArr(r.evidenceUrls),
  itemTitle: r.orderItem?.titleSnapshot ?? null,
  variantSku: r.orderItem?.variantSku ?? null,
  variantLabel: r.orderItem?.variantLabel ?? null,
  itemUnitPriceETB: r.orderItem?.unitPriceETB ?? null,
  itemQuantity: r.orderItem?.quantity ?? null,
  orderNumber: r.order?.number ?? null,
  buyerName: r.buyer?.fullName ?? null,
  vendorName: r.vendorProfileName ?? r.vendor?.fullName ?? null,
})

export async function myReturns() {
  const user = await requireUser()
  const rows = await db.returnRequest.findMany({
    where: { buyerId: user.id }, orderBy: { createdAt: 'desc' },
    include: {
      orderItem: true,
      order: { select: { number: true } },
    },
  })
  return json({ items: rows.map(r => mapReturn({ ...r, buyer: { fullName: user.fullName } })) })
}

// =================================================================
// VENDOR CONSOLE ADDITIONS
// =================================================================
export async function vendorPatchVariant(req: NextRequest, variantId: string) {
  const vendor = await requireRole('Vendor')
  const b = await req.json()
  const { priceETB, stock, isActive } = b ?? {}
  const variant = await db.productVariant.findUnique({ where: { id: variantId }, include: { product: true } })
  if (!variant || variant.product.vendorId !== vendor.id) return bad('Variant not found', 404)
  const data: { priceETB?: number | null; stock?: number; isActive?: boolean } = {}
  if (priceETB !== undefined) {
    if (priceETB !== null && (isNaN(Number(priceETB)) || Number(priceETB) < 0)) return bad('priceETB must be a non-negative number or null (base price)')
    data.priceETB = priceETB === null ? null : r2(Number(priceETB))
  }
  if (stock !== undefined) {
    if (!Number.isInteger(Number(stock)) || Number(stock) < 0) return bad('stock must be a whole number ≥ 0')
    data.stock = Number(stock)
  }
  if (isActive !== undefined) data.isActive = !!isActive
  const updated = await db.productVariant.update({ where: { id: variantId }, data })
  // keep Product.stock = Σ variant stocks
  const sum = await db.productVariant.aggregate({ where: { productId: variant.productId }, _sum: { stock: true } })
  await db.product.update({ where: { id: variant.productId }, data: { stock: sum._sum.stock ?? 0 } })
  return json({ variant: updated })
}

function parseCsvLine(line: string): string[] {
  const out: string[] = []
  let cur = ''
  let inQ = false
  for (let i = 0; i < line.length; i++) {
    const ch = line[i]
    if (inQ) {
      if (ch === '"' && line[i + 1] === '"') { cur += '"'; i++ }
      else if (ch === '"') inQ = false
      else cur += ch
    } else if (ch === '"') inQ = true
    else if (ch === ',') { out.push(cur); cur = '' }
    else cur += ch
  }
  out.push(cur)
  return out
}

export async function vendorBulkImport(req: NextRequest) {
  const vendor = await requireRole('Vendor')
  if (vendor.vendorProfile?.status !== 'Approved') return bad('Only approved vendors can import products', 403)
  const { csv } = await req.json()
  if (!csv || typeof csv !== 'string') return bad('csv string is required')
  const lines = csv.split(/\r?\n/).map(l => l.trim()).filter(Boolean)
  if (!lines.length) return bad('CSV is empty')
  const header = lines.shift()!.split(',').map(h => h.trim().toLowerCase())
  for (const col of ['title', 'description', 'priceetb', 'stock', 'categoryslug', 'sku']) {
    if (!header.includes(col)) return bad(`CSV header must include: title,description,priceETB,stock,categorySlug,sku`)
  }
  const col = (name: string) => header.indexOf(name)
  const errors: string[] = []
  let created = 0
  const seenSkus = new Set<string>()
  const pendingSkus: string[] = []
  for (const [idx, line] of lines.entries()) {
    const rowNo = idx + 2
    const cells = parseCsvLine(line)
    const title = (cells[col('title')] ?? '').trim()
    const description = (cells[col('description')] ?? '').trim()
    const price = parseFloat((cells[col('priceetb')] ?? '').trim())
    const stock = parseInt((cells[col('stock')] ?? '').trim(), 10)
    const categorySlug = (cells[col('categoryslug')] ?? '').trim()
    const sku = (cells[col('sku')] ?? '').trim()
    if (!title) { errors.push(`Row ${rowNo}: title is required`); continue }
    if (title.length > 140) { errors.push(`Row ${rowNo}: title must be 140 characters or less`); continue }
    if (!description) { errors.push(`Row ${rowNo}: description is required`); continue }
    if (!Number.isFinite(price) || price <= 0) { errors.push(`Row ${rowNo}: priceETB must be > 0`); continue }
    if (!Number.isInteger(stock) || stock < 0) { errors.push(`Row ${rowNo}: stock must be a whole number ≥ 0`); continue }
    if (!sku) { errors.push(`Row ${rowNo}: sku is required`); continue }
    if (seenSkus.has(sku)) { errors.push(`Row ${rowNo}: duplicate SKU "${sku}" in file`); continue }
    if (pendingSkus.includes(sku)) { errors.push(`Row ${rowNo}: duplicate SKU "${sku}" in file`); continue }
    const cat = await db.category.findUnique({ where: { slug: categorySlug } })
    if (!cat) { errors.push(`Row ${rowNo}: unknown categorySlug "${categorySlug}"`); continue }
    const skuTaken = (await db.product.findFirst({ where: { sku } })) || (await db.productVariant.findUnique({ where: { sku } }))
    if (skuTaken) { errors.push(`Row ${rowNo}: SKU "${sku}" already exists`); continue }
    seenSkus.add(sku)
    pendingSkus.push(sku)
    const product = await db.product.create({
      data: {
        vendorId: vendor.id, vendorName: vendor.vendorProfile?.businessName ?? null,
        categoryId: cat.id, title, slug: `${title.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, '')}-${Date.now().toString(36)}-${idx}`,
        description, priceETB: r2(price), stock, sku, weightKg: 1, status: 'PendingReview',
        images: JSON.stringify({ from: '#1e293b', to: '#0f766e' }),
      },
    })
    await db.productVariant.create({
      data: { productId: product.id, sku, colorId: null, size: null, priceETB: null, stock, isActive: true, sortOrder: 0 },
    })
    created++
  }
  return json({ created, errors })
}

export async function vendorReturnsList() {
  const vendor = await requireRole('Vendor')
  const rows = await db.returnRequest.findMany({
    where: { vendorId: vendor.id }, orderBy: { createdAt: 'desc' },
    include: {
      orderItem: true,
      order: { select: { number: true } },
      buyer: { select: { fullName: true } },
    },
  })
  return json({ items: rows.map(r => mapReturn(r)) })
}

export async function vendorReturnDecision(req: NextRequest, id: string) {
  const vendor = await requireRole('Vendor')
  const { approve, note } = await req.json()
  const ret = await db.returnRequest.findUnique({ where: { id }, include: { buyer: true, orderItem: true, order: true } })
  if (!ret || ret.vendorId !== vendor.id) return bad('Return request not found', 404)
  if (ret.status !== 'Requested') return bad(`Return is already ${ret.status}`)
  if (approve) {
    const updated = await db.returnRequest.update({ where: { id }, data: { status: 'VendorApproved', vendorNote: note ?? null } })
    await notify('u_admin', 'ReturnApproved', 'Vendor approved a return — refund due', `Return for order ${ret.order.number} was approved by the vendor. Issue the refund of ${ret.orderItem.lineNetETB} Br.`)
    await dispatch('ReturnDecided', ret.buyer, {
      subject: 'Return approved by vendor',
      body: `Your return for order ${ret.order.number} was approved by the vendor. The final refund will be issued by MarketHub shortly.`,
    })
    return json({ return: updated })
  }
  const updated = await db.returnRequest.update({ where: { id }, data: { status: 'VendorRejected', vendorNote: note ?? null, resolvedAt: new Date() } })
  await notify('u_admin', 'ReturnRejected', 'Vendor rejected a return', `Return for order ${ret.order.number} was rejected by the vendor. Buyer may escalate to admin review.`)
  await dispatch('ReturnDecided', ret.buyer, {
    subject: 'Return request rejected',
    body: `Your return for order ${ret.order.number} was rejected by the vendor.${note ? ` Reason: ${note}` : ''} You may escalate to MarketHub support within 7 days.`,
  })
  return json({ return: updated })
}

export async function vendorReviewsList() {
  const vendor = await requireRole('Vendor')
  const rows = await db.review.findMany({
    where: { product: { vendorId: vendor.id }, status: { in: ['Approved', 'Pending'] } },
    orderBy: { createdAt: 'desc' },
    include: { user: { select: { fullName: true } }, product: { select: { id: true, title: true, slug: true, vendorId: true } } },
    take: 100,
  })
  return json({
    items: rows.map(r => ({
      ...r, userName: r.user.fullName, verified: !!r.orderId,
    })),
  })
}

export async function vendorReviewReply(req: NextRequest, id: string) {
  const vendor = await requireRole('Vendor')
  const { reply } = await req.json()
  if (!reply || String(reply).trim().length < 2) return bad('Reply text is required')
  const review = await db.review.findUnique({ where: { id }, include: { product: true, user: true } })
  if (!review || review.product.vendorId !== vendor.id) return bad('Review not found', 404)
  const updated = await db.review.update({
    where: { id },
    data: { vendorReply: String(reply).trim(), vendorRepliedAt: new Date() },
  })
  await notify(review.userId, 'VendorReplied', 'The seller replied to your review', `Your review on "${review.product.title}" received a reply from the seller.`)
  return json({ review: updated })
}

async function computeVendorPerformance(vendorId: string) {
  const [profile, orders, openReturns] = await Promise.all([
    db.vendorProfile.findUnique({ where: { id: vendorId } }),
    db.order.findMany({ where: { items: { some: { vendorId } } }, include: { items: true } }),
    db.returnRequest.count({ where: { vendorId, status: { in: ['Requested', 'VendorApproved', 'AdminReview'] } } }),
  ])
  const paid = orders.filter(o => !['Placed', 'Cancelled'].includes(o.status))
  const totalSold = paid.flatMap(o => o.items).filter(i => i.vendorId === vendorId).reduce((a, i) => a + i.quantity, 0)
  const considered = orders.filter(o => o.status !== 'Placed')
  const fulfilled = considered.filter(o => ['Delivered', 'Completed', 'Refunded'].includes(o.status)).length
  const pct = (n: number) => (considered.length ? Math.round((n / considered.length) * 1000) / 10 : 0)
  return {
    ratingAvg: profile?.ratingAvg ?? 0,
    ratingCount: profile?.ratingCount ?? 0,
    totalSold,
    fulfillmentRate: pct(fulfilled),
    cancelRate: pct(considered.filter(o => o.status === 'Cancelled').length),
    openReturns,
    disputeRate: pct(considered.filter(o => o.status === 'Disputed').length),
  }
}

export async function vendorPerformance() {
  const vendor = await requireRole('Vendor')
  return json(await computeVendorPerformance(vendor.id))
}

export async function vendorDashboard() {
  const vendor = await requireRole('Vendor')
  const now = new Date()
  const from = startOfDay(new Date(now.getTime() - 13 * 864e5))
  const [orders, totals] = await Promise.all([
    db.order.findMany({
      where: { createdAt: { gte: from }, status: { notIn: ['Placed', 'Cancelled'] }, items: { some: { vendorId: vendor.id } } },
      include: { items: true },
    }),
    Promise.all([
      db.product.count({ where: { vendorId: vendor.id } }),
      db.order.count({ where: { status: { in: ['PaymentConfirmed', 'VendorAccepted', 'Shipped'] }, items: { some: { vendorId: vendor.id } } } }),
      db.returnRequest.count({ where: { vendorId: vendor.id, status: { in: ['Requested', 'VendorApproved', 'AdminReview'] } } }),
      db.product.count({ where: { vendorId: vendor.id, stock: { lte: 5 } } }),
    ]),
  ])
  const daily: { date: string; revenue: number; orders: number }[] = []
  for (let d = 13; d >= 0; d--) {
    const day = startOfDay(new Date(now.getTime() - d * 864e5))
    const key = dayKey(day)
    const next = new Date(day.getTime() + 864e5)
    const dayOrders = orders.filter(o => o.createdAt >= day && o.createdAt < next)
    const revenue = r2(dayOrders.flatMap(o => o.items).filter(i => i.vendorId === vendor.id).reduce((a, i) => a + i.lineNetETB, 0))
    daily.push({ date: key, revenue, orders: dayOrders.length })
  }
  return json({
    daily,
    totals: { products: totals[0], activeOrders: totals[1], pendingReturns: totals[2], lowStock: totals[3] },
  })
}

// =================================================================
// ADMIN — REVIEWS
// =================================================================
export async function adminListReviews(req: NextRequest) {
  await requireRole('Admin')
  const status = req.nextUrl.searchParams.get('status') ?? 'Pending'
  const where = status === 'all' ? {} : { status }
  const [rows, counts] = await Promise.all([
    db.review.findMany({
      where, orderBy: { createdAt: 'desc' }, take: 200,
      include: { product: { select: { id: true, title: true, slug: true, vendorId: true } }, user: { select: { id: true, fullName: true, email: true } } },
    }),
    db.review.groupBy({ by: ['status'], _count: { _all: true } }),
  ])
  const byStatus = (s: string) => counts.find(c => c.status === s)?._count._all ?? 0
  return json({
    items: rows.map(r => ({
      ...r,
      imageUrls: parseJsonArr(r.imageUrls),
      productTitle: r.product.title,
      userName: r.user.fullName,
      verified: !!r.orderId,
    })),
    counts: { Pending: byStatus('Pending'), Approved: byStatus('Approved'), Rejected: byStatus('Rejected') },
  })
}

export async function adminModerateReview(req: NextRequest, id: string) {
  await requireRole('Admin')
  const { approve, note } = await req.json()
  const review = await db.review.findUnique({ where: { id }, include: { product: true } })
  if (!review) return bad('Review not found', 404)
  if (review.status !== 'Pending') return bad(`Review is already ${review.status}`)
  const updated = await db.review.update({ where: { id }, data: { status: approve ? 'Approved' : 'Rejected' } })
  if (approve) await recomputeProductRating(review.productId)
  await notify(review.userId, 'ReviewModerated', approve ? 'Your review is live ✓' : 'Your review was not approved',
    approve ? `Your review on "${review.product.title}" is now published. Thank you!` : `Your review on "${review.product.title}" did not pass moderation.`)
  return json({ review: updated })
}

// =================================================================
// ADMIN — RETURNS ARBITRATION
// =================================================================
export async function adminListReturns(req: NextRequest) {
  await requireRole('Admin')
  const status = req.nextUrl.searchParams.get('status') ?? undefined
  const rows = await db.returnRequest.findMany({
    where: status ? { status } : undefined,
    orderBy: { createdAt: 'desc' }, take: 200,
    include: {
      orderItem: true,
      order: { select: { number: true } },
      buyer: { select: { fullName: true, email: true } },
      vendor: { select: { fullName: true } },
    },
  })
  const vendorNames = await db.vendorProfile.findMany({ select: { id: true, businessName: true } })
  return json({
    items: rows.map(r => mapReturn({ ...r, vendorProfileName: vendorNames.find(v => v.id === r.vendorId)?.businessName ?? r.vendor?.fullName ?? null })),
  })
}

export async function adminArbitrateReturn(req: NextRequest, id: string) {
  const admin = await requireRole('Admin')
  const { approve, refundETB, note } = await req.json()
  const ret = await db.returnRequest.findUnique({ where: { id }, include: { orderItem: true, order: true, buyer: true } })
  if (!ret) return bad('Return request not found', 404)
  if (!['Requested', 'VendorApproved', 'AdminReview'].includes(ret.status)) return bad(`Return is already ${ret.status}`)
  if (!approve) {
    const updated = await db.returnRequest.update({
      where: { id },
      data: { status: 'Rejected', adminDecision: note ?? 'Rejected after inspection', adminId: admin.id, resolvedAt: new Date() },
    })
    await db.orderItem.update({ where: { id: ret.orderItemId }, data: { refundStatus: 'Rejected' } })
    await addOrderEvent(ret.orderId, ret.order.status, 'Admin', `Return rejected — ${note ?? 'failed inspection'}`)
    await dispatch('ReturnDecided', ret.buyer, {
      subject: 'Return request rejected',
      body: `Your return for order ${ret.order.number} was rejected after inspection.${note ? ` Reason: ${note}` : ''}`,
    })
    await notify(ret.vendorId ?? 'u_admin', 'ReturnDecided', 'Return rejected by admin', `Return for order ${ret.order.number} was rejected after inspection.`)
    return json({ return: updated })
  }
  const amount = refundETB != null ? r2(Number(refundETB)) : ret.orderItem.lineNetETB
  if (isNaN(amount) || amount <= 0) return bad('refundETB must be a positive number')
  if (amount > ret.order.grandTotalETB) return bad(`refundETB cannot exceed the order total (${ret.order.grandTotalETB} Br)`)
  const order = ret.order
  const rows = await db.ledgerEntry.findMany({ where: { orderId: order.id, accountCode: { in: ['2000', '3200', '3000'] } } })
  const net = (code: string) => r2(rows.filter(r => r.accountCode === code).reduce((a, r) => a + r.creditETB - r.debitETB, 0))
  const ratio = amount / order.grandTotalETB
  const legs = [
    { accountCode: '1000', orderId: order.id, creditETB: amount, description: 'Refund to buyer (item return)' },
    { accountCode: '2100', orderId: order.id, debitETB: r2(order.vatETB * ratio), description: 'Reverse VAT' },
    { accountCode: '3100', orderId: order.id, debitETB: r2(order.deliveryFeeETB * ratio), description: 'Reverse delivery revenue' },
    { accountCode: '3000', orderId: order.id, debitETB: r2(net('3000') * ratio), description: 'Reverse commission' },
    { accountCode: '2000', orderId: order.id, debitETB: r2(net('2000') * ratio), description: 'Reverse vendor payable' },
    ...(net('3200') > 0.009 ? [{ accountCode: '3200', orderId: order.id, debitETB: r2(net('3200') * ratio), description: 'Reverse first-party revenue' }] : []),
    { accountCode: '4000', orderId: order.id, debitETB: amount, description: 'Refund expense (item return)' },
    { accountCode: '1000', orderId: order.id, creditETB: amount, description: 'Refund payout from PSP' },
  ]
  const dr = r2(legs.reduce((a, l) => a + (l.debitETB ?? 0), 0))
  const cr = r2(legs.reduce((a, l) => a + (l.creditETB ?? 0), 0))
  if (Math.abs(dr - cr) > 0.01) return bad(`Refund ledger would be unbalanced (DR ${dr} vs CR ${cr}) — operation aborted`, 500)
  await db.ledgerEntry.createMany({ data: legs })
  // restock: variant → color → product (stock was decremented at payment confirm)
  const item = ret.orderItem
  if (item.variantId) await db.productVariant.update({ where: { id: item.variantId }, data: { stock: { increment: item.quantity } } })
  if (item.colorId) await db.productColor.update({ where: { id: item.colorId }, data: { stock: { increment: item.quantity } } })
  await db.product.update({ where: { id: item.productId }, data: { stock: { increment: item.quantity }, soldCount: { decrement: Math.min(item.quantity, Math.max(0, item.quantity)) } } })
  const updated = await db.returnRequest.update({
    where: { id },
    data: {
      status: 'Refunded', adminDecision: note ?? 'Refund approved by admin', adminId: admin.id,
      refundETB: amount, resolvedAt: new Date(),
    },
  })
  await db.orderItem.update({ where: { id: ret.orderItemId }, data: { refundStatus: 'Approved' } })
  if (amount >= order.grandTotalETB - 0.01 && order.status !== 'Refunded') {
    await db.order.update({ where: { id: order.id }, data: { status: 'Refunded', previousStatus: order.status, refundReason: ret.reason } })
    await addOrderEvent(order.id, 'Refunded', 'Admin', `Item return refunded (${amount} Br) — full order amount`)
  } else {
    await addOrderEvent(order.id, order.status, 'Admin', `Item return refunded (${amount} Br)`)
  }
  await dispatch('ReturnDecided', ret.buyer, {
    subject: 'Refund approved ✓',
    body: `Your return for order ${ret.order.number} was approved. A refund of ${amount} Br will arrive in your original payment method within 5 business days.`,
  })
  if (ret.vendorId) await notify(ret.vendorId, 'ReturnDecided', 'Return refunded by admin', `The return for order ${ret.order.number} was refunded (${amount} Br) and the stock was restored.`)
  return json({ return: updated, refundETB: amount })
}

// =================================================================
// ADMIN — STAFF
// =================================================================
const ADMIN_ROLES = ['SuperAdmin', 'Support', 'Finance', 'Moderator']

export async function adminListStaff() {
  await requireRole('Admin')
  const users = await db.user.findMany({
    where: { role: 'Admin' }, orderBy: { createdAt: 'asc' },
    select: { id: true, email: true, fullName: true, adminRole: true, status: true, lang: true, createdAt: true },
  })
  return json({ items: users })
}

export async function adminCreateStaff(req: NextRequest) {
  await requireRole('Admin')
  const { email, fullName, password, adminRole, phone, lang } = await req.json()
  if (!email || !fullName || !password) return bad('email, fullName and password are required')
  if (!EMAIL_RE.test(email)) return bad('Invalid email format')
  if (String(password).length < 8) return bad('Password must be at least 8 characters')
  if (!adminRole || !ADMIN_ROLES.includes(adminRole)) return bad(`adminRole must be one of: ${ADMIN_ROLES.join(', ')}`)
  if (await db.user.findUnique({ where: { email } })) return bad('An account with this email already exists', 409)
  const user = await db.user.create({
    data: {
      email, fullName, password, phone: phone ?? null, role: 'Admin', adminRole,
      lang: lang === 'am' ? 'am' : 'en', status: 'Active',
    },
    select: { id: true, email: true, fullName: true, adminRole: true, status: true, createdAt: true },
  })
  await notify(user.id, 'StaffCreated', 'Welcome to the MarketHub team', `Your ${adminRole} account was created. Please change your password after first login.`)
  return json({ staff: user }, 201)
}

export async function adminPatchStaff(req: NextRequest, id: string) {
  const admin = await requireRole('Admin')
  if (admin.adminRole !== 'SuperAdmin') return bad('Only SuperAdmin can modify staff accounts', 403)
  const staff = await db.user.findUnique({ where: { id } })
  if (!staff || staff.role !== 'Admin') return bad('Staff account not found', 404)
  const { fullName, adminRole, status } = await req.json()
  const data: { fullName?: string; adminRole?: string; status?: string } = {}
  if (fullName !== undefined) data.fullName = String(fullName)
  if (adminRole !== undefined) {
    if (!ADMIN_ROLES.includes(adminRole)) return bad(`adminRole must be one of: ${ADMIN_ROLES.join(', ')}`)
    if (staff.adminRole === 'SuperAdmin' && adminRole !== 'SuperAdmin') {
      const superAdmins = await db.user.count({ where: { role: 'Admin', adminRole: 'SuperAdmin', status: 'Active' } })
      if (superAdmins <= 1) return bad('Cannot demote the last active SuperAdmin')
    }
    data.adminRole = adminRole
  }
  if (status !== undefined) {
    if (!['Active', 'Suspended'].includes(status)) return bad('status must be Active or Suspended')
    if (staff.id === admin.id && status === 'Suspended') return bad('You cannot suspend your own account')
    data.status = status
  }
  const updated = await db.user.update({ where: { id }, data, select: { id: true, email: true, fullName: true, adminRole: true, status: true } })
  return json({ staff: updated })
}

// =================================================================
// ADMIN — SETTINGS (known keys, grouped, validated)
// =================================================================
type SettingDef = { type: 'number' | 'int' | 'text' | 'enum'; values?: string[]; min?: number; max?: number; def: string; group: string }
export const SETTING_DEFS: Record<string, SettingDef> = {
  VatRate: { type: 'number', min: 0, max: 30, def: '15', group: 'Tax & Commission' },
  DefaultCommissionRate: { type: 'number', min: 0, max: 0.5, def: '0.10', group: 'Tax & Commission' },
  ReturnWindowDays: { type: 'int', min: 0, max: 90, def: '7', group: 'Returns' },
  PaymentProvider: { type: 'enum', values: ['Mock', 'Chapa', 'Telebirr', 'Santimpay'], def: 'Mock', group: 'Payments' },
  PayoutSlaHours: { type: 'int', min: 1, max: 168, def: '12', group: 'Payments' },
  PlatformName: { type: 'text', def: 'MarketHub', group: 'General' },
  ReservationMinutes: { type: 'int', min: 5, max: 120, def: '20', group: 'Checkout' },
  AbandonedCartHours: { type: 'int', min: 1, max: 72, def: '2', group: 'Notifications' },
  SmsProvider: { type: 'enum', values: ['Mock', 'EthioTelecom', 'Safaricom'], def: 'Mock', group: 'Notifications' },
  EmailProvider: { type: 'enum', values: ['Mock', 'Smtp'], def: 'Mock', group: 'Notifications' },
  SearchProvider: { type: 'enum', values: ['Meilisearch', 'Null'], def: 'Meilisearch', group: 'Search' },
  FreeDeliveryAddisThreshold: { type: 'number', min: 0, max: 100000, def: '1000', group: 'Delivery' },
}

function validateSettingValue(key: string, value: string): string | null {
  const def = SETTING_DEFS[key]
  if (!def) return `Unknown setting key "${key}"`
  if (def.type === 'enum') {
    if (!def.values!.includes(value)) return `${key} must be one of: ${def.values!.join(', ')}`
    return null
  }
  const n = Number(value)
  if (def.type === 'int') {
    if (!Number.isInteger(n)) return `${key} must be a whole number`
  } else if (def.type === 'number') {
    if (!Number.isFinite(n)) return `${key} must be a number`
  } else {
    if (!String(value).trim()) return `${key} must not be empty`
    return null
  }
  if (def.min !== undefined && n < def.min) return `${key} must be ≥ ${def.min}`
  if (def.max !== undefined && n > def.max) return `${key} must be ≤ ${def.max}`
  return null
}

export async function adminGetSettings() {
  await requireRole('Admin')
  const rows = await db.settings.findMany()
  const entries = Object.entries(SETTING_DEFS).map(([key, def]) => {
    const row = rows.find(r => r.key === key)
    return { key, value: row?.value ?? def.def, description: row?.description ?? `${def.group} setting`, group: def.group, type: def.type, values: def.values ?? null }
  })
  const groups = [...new Set(entries.map(e => e.group))].map(name => ({ name, keys: entries.filter(e => e.group === name).map(e => e.key) }))
  return json({ entries, groups })
}

export async function adminPutSettings(req: NextRequest) {
  await requireRole('Admin')
  const { entries } = await req.json()
  if (!Array.isArray(entries) || !entries.length) return bad('entries: [{key, value}] is required')
  for (const e of entries) {
    const err = validateSettingValue(String(e.key ?? ''), String(e.value ?? ''))
    if (err) return bad(err)
  }
  for (const e of entries) {
    const key = String(e.key)
    await db.settings.upsert({
      where: { key },
      update: { value: String(e.value) },
      create: { key, value: String(e.value), description: SETTING_DEFS[key] ? `${SETTING_DEFS[key].group} setting` : null },
    })
  }
  return json({ ok: true, updated: entries.length })
}

// =================================================================
// ADMIN — MARKETING (banners, flash sale, coupons)
// =================================================================
export async function adminListBanners() {
  await requireRole('Admin')
  const items = await db.banner.findMany({ orderBy: { position: 'asc' } })
  return json({ items })
}

export async function adminCreateBanner(req: NextRequest) {
  await requireRole('Admin')
  const { title, imageUrl, link, position, isActive, startsAt, endsAt } = await req.json()
  if (!title || !imageUrl) return bad('title and imageUrl are required')
  if (!/^https:\/\//.test(String(imageUrl))) return bad('imageUrl must be an https URL')
  const banner = await db.banner.create({
    data: {
      title, imageUrl, link: link ?? null,
      position: Number.isInteger(position) ? position : 0,
      isActive: isActive ?? true,
      startsAt: startsAt ? new Date(startsAt) : null,
      endsAt: endsAt ? new Date(endsAt) : null,
    },
  })
  return json({ banner }, 201)
}

export async function adminPatchBanner(req: NextRequest, id: string) {
  await requireRole('Admin')
  const banner = await db.banner.findUnique({ where: { id } })
  if (!banner) return bad('Banner not found', 404)
  const b = await req.json()
  const data: Record<string, unknown> = {}
  for (const k of ['title', 'link', 'isActive', 'position'] as const) if (b[k] !== undefined) data[k] = b[k]
  if (b.imageUrl !== undefined) {
    if (!/^https:\/\//.test(String(b.imageUrl))) return bad('imageUrl must be an https URL')
    data.imageUrl = b.imageUrl
  }
  if (b.startsAt !== undefined) data.startsAt = b.startsAt ? new Date(b.startsAt) : null
  if (b.endsAt !== undefined) data.endsAt = b.endsAt ? new Date(b.endsAt) : null
  const updated = await db.banner.update({ where: { id }, data })
  return json({ banner: updated })
}

export async function adminDeleteBanner(req: NextRequest, id: string) {
  await requireRole('Admin')
  const banner = await db.banner.findUnique({ where: { id } })
  if (!banner) return bad('Banner not found', 404)
  await db.banner.delete({ where: { id } })
  return json({ ok: true })
}

export async function adminSetFlashSale(req: NextRequest) {
  await requireRole('Admin')
  const { productIds, discountPct, endsAt } = await req.json()
  if (!Array.isArray(productIds) || !productIds.length) return bad('productIds is required')
  const pct = Number(discountPct)
  if (!Number.isFinite(pct) || pct < 5 || pct > 70) return bad('discountPct must be between 5 and 70')
  const end = endsAt ? new Date(endsAt) : new Date(Date.now() + 48 * 36e5)
  if (isNaN(end.getTime()) || end <= new Date()) return bad('endsAt must be a future ISO date')
  let count = 0
  for (const id of productIds) {
    const p = await db.product.findUnique({ where: { id: String(id) } })
    if (!p) continue
    await db.product.update({ where: { id: p.id }, data: { salePriceETB: r2(p.priceETB * (1 - pct / 100)), saleEndsAt: end } })
    count++
  }
  return json({ ok: true, count, endsAt: end })
}

export async function adminClearFlashSale() {
  await requireRole('Admin')
  const res = await db.product.updateMany({ where: { saleEndsAt: { not: null } }, data: { salePriceETB: null, saleEndsAt: null } })
  return json({ ok: true, cleared: res.count })
}

export async function adminListCoupons() {
  await requireRole('Admin')
  const items = await db.coupon.findMany({ orderBy: { createdAt: 'desc' }, include: { _count: { select: { redemptions: true } } } })
  return json({ items: items.map(c => ({ ...c, redemptionCount: c._count.redemptions, _count: undefined })) })
}

export async function adminCreateCoupon(req: NextRequest) {
  await requireRole('Admin')
  const { code, description, type, value, minSubtotal, maxDiscount, maxUses, startsAt, expiresAt } = await req.json()
  const normalized = String(code ?? '').trim().toUpperCase()
  if (!/^[A-Z0-9_-]{3,30}$/.test(normalized)) return bad('code must be 3-30 chars (A-Z, 0-9, dash, underscore)')
  if (!['Percent', 'Fixed'].includes(type)) return bad('type must be Percent or Fixed')
  const val = Number(value)
  if (!Number.isFinite(val) || val <= 0) return bad('value must be > 0')
  if (type === 'Percent' && val > 90) return bad('Percent coupons cannot exceed 90%')
  const end = expiresAt ? new Date(expiresAt) : null
  if (!end || isNaN(end.getTime()) || end <= new Date()) return bad('expiresAt must be a future ISO date')
  if (await db.coupon.findUnique({ where: { code: normalized } })) return bad('A coupon with this code already exists', 409)
  const coupon = await db.coupon.create({
    data: {
      code: normalized, description: description ?? null, type, value: r2(val),
      minSubtotal: r2(Number(minSubtotal ?? 0)), maxDiscount: maxDiscount != null ? r2(Number(maxDiscount)) : null,
      maxUses: Number.isInteger(maxUses) ? maxUses : 0,
      startsAt: startsAt ? new Date(startsAt) : new Date(), expiresAt: end, isActive: true,
    },
  })
  return json({ coupon }, 201)
}

export async function adminPatchCoupon(req: NextRequest, id: string) {
  await requireRole('Admin')
  const coupon = await db.coupon.findUnique({ where: { id } })
  if (!coupon) return bad('Coupon not found', 404)
  const b = await req.json()
  const data: Record<string, unknown> = {}
  if (b.isActive !== undefined) data.isActive = !!b.isActive
  if (b.expiresAt !== undefined) {
    const end = new Date(b.expiresAt)
    if (isNaN(end.getTime())) return bad('expiresAt must be a valid ISO date')
    data.expiresAt = end
  }
  if (b.maxUses !== undefined) {
    if (!Number.isInteger(Number(b.maxUses)) || Number(b.maxUses) < 0) return bad('maxUses must be a whole number ≥ 0')
    data.maxUses = Number(b.maxUses)
  }
  if (b.description !== undefined) data.description = b.description
  const updated = await db.coupon.update({ where: { id }, data })
  return json({ coupon: updated })
}

export async function adminVendorPerformance(req: NextRequest, vendorId: string) {
  await requireRole('Admin')
  const profile = await db.vendorProfile.findUnique({ where: { id: vendorId } })
  if (!profile) return bad('Vendor not found', 404)
  const perf = await computeVendorPerformance(vendorId)
  return json({ ...perf, businessName: profile.businessName, city: profile.city })
}

// =================================================================
// ADMIN — OPS (abandoned carts, outbound, jobs)
// =================================================================
export async function adminListAbandonedCarts() {
  await requireRole('Admin')
  const hours = Math.max(1, parseInt(await getSetting('AbandonedCartHours', '2'), 10))
  const cutoff = new Date(Date.now() - hours * 36e5)
  const carts = await db.cart.findMany({
    where: { updatedAt: { lt: cutoff } },
    orderBy: { updatedAt: 'desc' },
    include: { user: { select: { id: true, email: true, fullName: true, phone: true } } },
  })
  return json({
    items: carts.map(c => ({
      userId: c.userId, email: c.user.email, fullName: c.user.fullName, phone: c.user.phone,
      items: parseJsonArr(c.itemsJson) ?? [], updatedAt: c.updatedAt, reminderSentAt: c.reminderSentAt,
    })),
    abandonedAfterHours: hours,
  })
}

export async function adminRemindAbandonedCart(req: NextRequest) {
  await requireRole('Admin')
  const { userId } = await req.json()
  if (!userId) return bad('userId is required')
  const user = await db.user.findUnique({ where: { id: String(userId) } })
  if (!user) return bad('User not found', 404)
  const cart = await db.cart.findUnique({ where: { userId: user.id } })
  const cartItems = parseJsonArr(cart?.itemsJson ?? null) ?? []
  const message = await sendSms(user.phone, 'AbandonedCart', {
    subject: 'Your cart is waiting',
    body: `Selam ${user.fullName}! You left ${cartItems.length} item(s) in your MarketHub cart. Complete your order before stock runs out.`,
    items: cartItems,
  })
  if (cart) await db.cart.update({ where: { id: cart.id }, data: { reminderSentAt: new Date() } })
  await notify(user.id, 'AbandonedCart', 'Your cart is waiting', 'You left items in your cart — complete your order before the flash sale ends.')
  return json({ ok: true, provider: message.provider, status: message.status, providerRef: message.providerRef })
}

const parsePayload = (s: string | null | undefined): unknown => {
  if (!s) return null
  try { return JSON.parse(s) } catch { return null }
}

export async function adminListOutbound(req: NextRequest) {
  await requireRole('Admin')
  const channel = req.nextUrl.searchParams.get('channel') ?? undefined
  const items = await db.outboundMessage.findMany({
    where: channel ? { channel } : undefined,
    orderBy: { createdAt: 'desc' }, take: 100,
    include: { user: { select: { email: true, fullName: true } } },
  })
  return json({
    items: items.map(m => {
      const { payloadJson, user, ...rest } = m
      return { ...rest, payload: parsePayload(payloadJson), userEmail: user?.email ?? null, userName: user?.fullName ?? null }
    }),
  })
}

/** Quartz equivalent: DocumentExpiryMonitor — expires docs past expiresAt and suspends affected vendors. */
export async function jobDocumentExpiry() {
  await requireRole('Admin')
  const now = new Date()
  const expired = await db.document.findMany({
    where: { expiresAt: { lt: now }, status: { in: ['Verified', 'Pending'] } },
    include: { owner: true },
  })
  if (expired.length) {
    await db.document.updateMany({ where: { id: { in: expired.map(d => d.id) } }, data: { status: 'Expired' } })
    const vendorIds = [...new Set(expired.map(d => d.ownerId))]
    const suspended: string[] = []
    for (const vid of vendorIds) {
      const profile = await db.vendorProfile.findUnique({ where: { id: vid } })
      if (!profile || profile.status === 'Suspended') continue
      await db.vendorProfile.update({ where: { id: vid }, data: { status: 'Suspended', rejectionReason: 'Legal documents expired — renew to reactivate your store' } })
      await notify(vid, 'VendorSuspended', 'Store suspended — documents expired', 'One or more of your legal documents expired. Your store is suspended until you upload renewed documents.')
      suspended.push(vid)
    }
    return json({ ok: true, expiredDocuments: expired.length, suspendedVendors: suspended })
  }
  return json({ ok: true, expiredDocuments: 0, suspendedVendors: [] })
}

/** Quartz equivalent: ReservationSweeper — releases expired stock reservations. */
export async function jobStockSweeper() {
  await requireRole('Admin')
  const res = await db.stockReservation.updateMany({
    where: { status: 'Active', expiresAt: { lte: new Date() } },
    data: { status: 'Expired' },
  })
  return json({ ok: true, expired: res.count })
}
