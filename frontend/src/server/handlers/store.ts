// Public + buyer flow handlers: auth, catalog, cart, checkout, payments, orders, policies, notifications.
// Mirrors the MarketHub .NET API contract 1:1 (see /backend README for the production endpoints).
import { NextRequest, NextResponse } from 'next/server'
import { db } from '@/lib/db'
import { dispatch, sendSms } from '@/lib/providers'
import { calculateVat, canTransition, paymentLegs, r2 } from '../money'
import { HttpError, currentUser, requireUser, withSession } from '../session'

const json = (data: unknown, status = 200) => NextResponse.json(data as object, { status })
const bad = (msg: string, status = 400) => json({ error: msg }, status)

export async function getSetting(key: string, def: string) {
  const s = await db.settings.findUnique({ where: { key } })
  return s?.value ?? def
}
export const vatRate = async () => parseFloat(await getSetting('VatRate', '15'))
export const commissionRate = async () => parseFloat(await getSetting('DefaultCommissionRate', '0.10'))
export const payoutSlaHours = async () => parseInt(await getSetting('PayoutSlaHours', '12'), 10)
const orderNumber = () => {
  const d = new Date()
  const stamp = `${d.getFullYear()}${String(d.getMonth() + 1).padStart(2, '0')}${String(d.getDate()).padStart(2, '0')}`
  return `MH-${stamp}-${String(Math.floor(1000 + Math.random() * 9000)).padStart(6, '0')}`
}
const notify = (userId: string, type: string, subject: string, body: string) =>
  db.notification.create({ data: { userId, type, subject, body } })

// ---------------- STOCK / RESERVATIONS / COUPONS (shared helpers) ----------------
export type StockRef = { productId: string; variantId?: string | null; colorId?: string | null }

/** Σ of active, unexpired reservations held against one stock reference */
export async function reservedQty(ref: StockRef): Promise<number> {
  const agg = await db.stockReservation.aggregate({
    where: {
      status: 'Active', expiresAt: { gt: new Date() },
      productId: ref.productId, variantId: ref.variantId ?? null, colorId: ref.colorId ?? null,
    },
    _sum: { qty: true },
  })
  return agg._sum.qty ?? 0
}

/** Reservation-aware availability: available = stock − Σ active reservations (GAP_PLAN stock rule) */
export async function availableQty(ref: StockRef): Promise<number> {
  if (ref.variantId) {
    const v = await db.productVariant.findUnique({ where: { id: ref.variantId } })
    if (!v || !v.isActive || v.productId !== ref.productId) return 0
    return Math.max(0, v.stock - (await reservedQty(ref)))
  }
  if (ref.colorId) {
    const c = await db.productColor.findUnique({ where: { id: ref.colorId } })
    if (!c || c.productId !== ref.productId) return 0
    return Math.max(0, c.stock - (await reservedQty(ref)))
  }
  const p = await db.product.findUnique({ where: { id: ref.productId }, select: { stock: true } })
  if (!p) return 0
  return Math.max(0, p.stock - (await reservedQty(ref)))
}

export type ValidCoupon = NonNullable<Awaited<ReturnType<typeof db.coupon.findUnique>>>

/** Validate a coupon against a subtotal — window, usage count, minimum spend, cap (mirrors .NET CouponValidator) */
export async function validateCoupon(rawCode: string, subtotal: number): Promise<{ ok: false; discountETB: 0; message: string } | { ok: true; discountETB: number; message: string; coupon: ValidCoupon }> {
  const code = String(rawCode ?? '').trim().toUpperCase()
  if (!code) return { ok: false, discountETB: 0, message: 'Enter a coupon code' }
  const coupon = await db.coupon.findUnique({ where: { code } })
  if (!coupon || !coupon.isActive) return { ok: false, discountETB: 0, message: `Coupon "${code}" is not valid` }
  const now = new Date()
  if (now < coupon.startsAt) return { ok: false, discountETB: 0, message: `Coupon "${code}" is not active yet` }
  if (now > coupon.expiresAt) return { ok: false, discountETB: 0, message: `Coupon "${code}" has expired` }
  if (coupon.maxUses > 0 && coupon.usedCount >= coupon.maxUses) return { ok: false, discountETB: 0, message: `Coupon "${code}" has reached its usage limit` }
  if (subtotal < coupon.minSubtotal) return { ok: false, discountETB: 0, message: `Coupon "${code}" requires a minimum subtotal of ${coupon.minSubtotal} Br` }
  let discount = coupon.type === 'Percent' ? r2((subtotal * coupon.value) / 100) : Math.min(coupon.value, subtotal)
  if (coupon.maxDiscount != null) discount = Math.min(discount, coupon.maxDiscount)
  discount = r2(Math.max(0, discount))
  return { ok: true, discountETB: discount, message: `Coupon ${code} applied — ${discount} Br off`, coupon }
}

const newTrackingCode = () =>
  `MH-TRK-${Array.from({ length: 6 }, () => 'ABCDEFGHJKLMNPQRSTUVWXYZ0123456789'[Math.floor(Math.random() * 34)]).join('')}`
const newDeliveryPin = () => String(Math.floor(1000 + Math.random() * 9000))

/** Append an order timeline event (OrderEvent) */
export const addOrderEvent = (orderId: string, status: string, byRole: string, note?: string) =>
  db.orderEvent.create({ data: { orderId, status, byRole, note: note ?? null } })

// ---------------- AUTH ----------------
export async function register(req: NextRequest) {
  const b = await req.json()
  const { email, phone, password, fullName, role } = b ?? {}
  if (!email || !password || !fullName || !['Buyer', 'Vendor'].includes(role)) return bad('email, password, fullName and role (Buyer|Vendor) are required')
  if (!/^\S+@\S+\.\S+$/.test(email)) return bad('Invalid email format')
  if (password.length < 8) return bad('Password must be at least 8 characters')
  if (phone && !/^\+251[79]\d{8}$/.test(phone)) return bad('Phone must be in +2519XXXXXXXX or +2517XXXXXXXX format')
  if (await db.user.findUnique({ where: { email } })) return bad('An account with this email already exists', 409)

  // Policy consent is MANDATORY for all three active policies (incl. Return & Refund)
  const activePolicies = await db.policy.findMany({ where: { isActive: true } })
  const consents: { policyKey: string; policyVersion: string }[] = b.consents ?? []
  for (const p of activePolicies) {
    const ok = consents.some(c => c.policyKey === p.key && c.policyVersion === p.version)
    if (!ok) return bad(`You must accept the ${p.key.replace(/([A-Z])/g, ' $1').trim()} policy to register`)
  }

  // Vendor application: profile + minimum 3 documents covering all required types
  const docs: { typeCode: string; fileName: string; contentType?: string; sizeBytes?: number }[] = b.documents ?? []
  if (role === 'Vendor') {
    const { businessName, tin, region, city } = b
    if (!businessName || !tin || !region || !city) return bad('Business name, TIN, region and city are required for vendor registration')
    if (!/^\d{10}$/.test(tin)) return bad('TIN must be exactly 10 digits')
    if (docs.length < 3) return bad('Vendor registration requires at least 3 legal documents (registration license, trade license, TIN certificate)')
    const requiredTypes = await db.documentType.findMany({ where: { appliesTo: 'Vendor', isRequired: true } })
    for (const t of requiredTypes) if (!docs.some(d => d.typeCode === t.code)) return bad(`Missing required document: ${t.name}`)
  }

  const user = await db.user.create({
    data: {
      email, phone: phone ?? null, password, fullName, role,
      lang: b.lang === 'am' ? 'am' : 'en',
      vendorProfile: role === 'Vendor' ? {
        create: { businessName: b.businessName, tin: b.tin, region: b.region, city: b.city, description: b.storeDescription ?? null, status: 'PendingReview' },
      } : undefined,
    },
    include: { vendorProfile: true },
  })
  for (const c of consents.filter(c => activePolicies.some(p => p.key === c.policyKey && p.version === c.policyVersion)))
    await db.consent.create({ data: { userId: user.id, policyKey: c.policyKey, policyVersion: c.policyVersion, ipAddress: req.headers.get('x-forwarded-for') ?? 'preview' } })
  for (const d of docs) {
    const t = await db.documentType.findUnique({ where: { code: d.typeCode } })
    if (t) await db.document.create({ data: { ownerId: user.id, typeId: t.id, fileName: d.fileName, contentType: d.contentType ?? 'application/pdf', sizeBytes: d.sizeBytes ?? 0, status: 'Pending' } })
  }
  if (role === 'Vendor') await notify(user.id, 'DocumentsReceived', 'Application received', 'Your vendor application and legal documents are now under review by the MarketHub team.')
  return withSession(json({ id: user.id, email: user.email, fullName: user.fullName, role: user.role, adminRole: user.adminRole, lang: user.lang, vendor: user.vendorProfile }, 201), user.id)
}

export async function login(req: NextRequest) {
  const { email, password } = await req.json()
  const user = await db.user.findUnique({ where: { email }, include: { vendorProfile: true } })
  if (!user || user.password !== password) return bad('Invalid email or password', 401)
  if (user.status === 'Suspended') return bad('This account has been suspended. Contact support.', 403)
  return withSession(json({ id: user.id, email: user.email, fullName: user.fullName, role: user.role, adminRole: user.adminRole, lang: user.lang, vendor: user.vendorProfile }), user.id)
}

export async function logout() {
  const res = json({ ok: true })
  res.cookies.delete('mh_uid')
  return res
}

export async function me() {
  const user = await currentUser()
  if (!user) return json({ user: null })
  return json({
    user: {
      id: user.id, email: user.email, phone: user.phone, fullName: user.fullName, role: user.role, status: user.status,
      adminRole: user.adminRole, lang: user.lang,
      vendor: user.vendorProfile ? {
        id: user.vendorProfile.id, businessName: user.vendorProfile.businessName, status: user.vendorProfile.status,
        region: user.vendorProfile.region, city: user.vendorProfile.city, ratingAvg: user.vendorProfile.ratingAvg,
      } : null,
    },
  })
}

// ---------------- CATALOG ----------------
export async function listProducts(req: NextRequest) {
  const p = req.nextUrl.searchParams
  const page = Math.max(1, parseInt(p.get('page') ?? '1', 10))
  const pageSize = Math.min(48, Math.max(1, parseInt(p.get('pageSize') ?? '12', 10)))
  const where: Record<string, unknown> = { status: 'Active' }
  if (p.get('q')) where.OR = [{ title: { contains: p.get('q')! } }, { description: { contains: p.get('q')! } }]
  if (p.get('cat')) where.categoryId = p.get('cat')
  if (p.get('platform') === 'true') where.vendorId = null
  if (p.get('vendorId')) where.vendorId = p.get('vendorId')
  if (p.get('min')) where.priceETB = { ...(where.priceETB as object ?? {}), gte: parseFloat(p.get('min')!) }
  if (p.get('max')) where.priceETB = { ...(where.priceETB as object ?? {}), lte: parseFloat(p.get('max')!) }
  if (p.get('rating')) where.ratingAvg = { gte: parseFloat(p.get('rating')!) }
  const sortMap: Record<string, object> = {
    newest: { createdAt: 'desc' }, price_asc: { priceETB: 'asc' }, price_desc: { priceETB: 'desc' },
    rating: { ratingAvg: 'desc' }, featured: [{ isFeatured: 'desc' }, { ratingAvg: 'desc' }], sold: { soldCount: 'desc' },
  }
  const orderBy = sortMap[p.get('sort') ?? 'featured'] ?? sortMap.featured
  const incl = {
    gallery: { orderBy: { sortOrder: 'asc' as const } },
    colors: { orderBy: { sortOrder: 'asc' as const } },
    _count: { select: { variants: { where: { isActive: true } } } },
  }
  const [items, total] = await Promise.all([
    db.product.findMany({ where, orderBy: orderBy as never, skip: (page - 1) * pageSize, take: pageSize, include: incl }),
    db.product.count({ where }),
  ])
  return json({ items, page, pageSize, totalCount: total })
}

export async function getProduct(slug: string) {
  const product = await db.product.findUnique({
    where: { slug },
    include: {
      category: true, vendor: true,
      colors: { orderBy: { sortOrder: 'asc' } },
      gallery: { orderBy: { sortOrder: 'asc' } },
    },
  })
  if (!product) return bad('Product not found', 404)
  return json({ product })
}

export async function listCategories() {
  const cats = await db.category.findMany({ orderBy: { sortOrder: 'asc' }, include: { _count: { select: { products: { where: { status: 'Active' } } } } } })
  return json({ items: cats.map(c => ({ id: c.id, name: c.name, slug: c.slug, productCount: c._count.products })) })
}

const MAX_IMAGES = 8
const MAX_IMAGE_CHARS = 480_000 // ≈350KB data URL
const HEX_RE = /^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$/

type IncomingImage = { url?: string; alt?: string; colorIndex?: number | null }
type IncomingColor = { name?: string; hex?: string; stock?: number; extraPriceETB?: number }
type ParsedColor = { name: string; hex: string; stock: number; extraPriceETB: number; sortOrder: number }
type ParsedImage = { url: string; alt: string; colorIndex: number | null; sortOrder: number }
type GalleryParse = { ok: false; error: string } | { ok: true; colors: ParsedColor[]; images: ParsedImage[] }

function parseGalleryInput(b: { images?: unknown; colors?: unknown }): GalleryParse {
  const rawImages: IncomingImage[] = Array.isArray(b.images) ? b.images as IncomingImage[] : []
  const rawColors: IncomingColor[] = Array.isArray(b.colors) ? b.colors as IncomingColor[] : []
  if (rawImages.length === 0) return { ok: false, error: 'At least 1 product photo is required' }
  if (rawImages.length > MAX_IMAGES) return { ok: false, error: `A listing can hold at most ${MAX_IMAGES} photos` }
  if (rawColors.length > 6) return { ok: false, error: 'A listing can have at most 6 color variants' }
  const colors: ParsedColor[] = []
  for (const [i, c] of rawColors.entries()) {
    const name = (c.name ?? '').trim()
    if (!name || name.length > 40) return { ok: false, error: `Color #${i + 1}: a name (max 40 chars) is required` }
    if (!c.hex || !HEX_RE.test(c.hex)) return { ok: false, error: `Color "${name}": hex must look like #1a1a1a` }
    const stock = Number(c.stock ?? 0)
    if (!Number.isInteger(stock) || stock < 0) return { ok: false, error: `Color "${name}": stock must be a whole number ≥ 0` }
    const extra = Number(c.extraPriceETB ?? 0)
    if (isNaN(extra) || extra < 0) return { ok: false, error: `Color "${name}": extra price must be ≥ 0` }
    colors.push({ name, hex: c.hex.toLowerCase(), stock, extraPriceETB: r2(extra), sortOrder: i })
  }
  const names = colors.map(c => c.name.toLowerCase())
  if (new Set(names).size !== names.length) return { ok: false, error: 'Color names must be unique' }
  const images: ParsedImage[] = []
  for (const [i, im] of rawImages.entries()) {
    const url = (im.url ?? '').trim()
    const isData = url.startsWith('data:image/')
    const isHttps = /^https:\/\/\S+$/.test(url)
    if (!isData && !isHttps) return { ok: false, error: `Photo #${i + 1}: provide an image file or https URL` }
    if (url.length > MAX_IMAGE_CHARS) return { ok: false, error: `Photo #${i + 1} is too large — keep each photo under ~350KB` }
    const ci = im.colorIndex ?? null
    if (ci !== null && (!Number.isInteger(ci) || ci < 0 || ci >= colors.length))
      return { ok: false, error: `Photo #${i + 1} references a color that does not exist` }
    images.push({ url, alt: (im.alt ?? '').trim().slice(0, 60), colorIndex: ci, sortOrder: i })
  }
  return { ok: true, colors, images }
}

export async function createProduct(req: NextRequest) {
  const user = await requireUser()
  const b = await req.json()
  const { title, description, priceETB, stock, categoryId, weightKg } = b
  if (!title || !description || !priceETB || !categoryId || stock === undefined) return bad('title, description, priceETB, stock, categoryId are required')
  if (priceETB <= 0) return bad('Price must be greater than 0')
  if (stock < 0) return bad('Stock cannot be negative')
  if (String(title).length > 140) return bad('Title must be 140 characters or less')
  const cat = await db.category.findUnique({ where: { id: categoryId } })
  if (!cat) return bad('Category not found', 404)
  const gallery = parseGalleryInput(b)
  if (!gallery.ok) return bad(gallery.error)
  const isPlatform = user.role === 'Admin'
  const vendorId = isPlatform ? null : user.id
  if (!isPlatform && user.role === 'Vendor' && user.vendorProfile?.status !== 'Approved') return bad('Only approved vendors can list products', 403)
  const slug = `${title.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, '')}-${Date.now().toString(36)}`
  // SKU-level variants: color × size matrix (industry standard). Every product gets at least
  // one default variant so stock is tracked per purchasable SKU.
  const rawSizes: unknown[] = Array.isArray(b.sizes) ? b.sizes as unknown[] : []
  const sizes = rawSizes.map(s => String(s).trim()).filter(s => s.length > 0 && s.length <= 20).slice(0, 10)
  const colorList = gallery.colors
  const stockOf = (c?: { stock: number }) => c?.stock ?? Math.trunc(stock)
  const unitStock = colorList.length > 0
    ? colorList.reduce((a, c) => a + c.stock, 0)
    : Math.trunc(stock)
  const totalStock = unitStock
  const variantRows: { sku: string; colorIndex: number | null; size: string | null; priceETB: number; stock: number; sortOrder: number }[] = []
  let vSeq = 0
  const mkSku = () => `MH-V-${(vSeq++).toString(36).toUpperCase()}${Date.now().toString(36).slice(-3).toUpperCase()}`
  if (colorList.length > 0 && sizes.length > 0) {
    colorList.forEach((c, ci) => {
      const per = Math.floor(c.stock / sizes.length)
      sizes.forEach((s, idx) => variantRows.push({ sku: mkSku(), colorIndex: ci, size: s, priceETB: r2(priceETB + c.extraPriceETB), stock: idx === 0 ? c.stock - per * (sizes.length - 1) : per, sortOrder: variantRows.length }))
    })
  } else if (colorList.length > 0) {
    colorList.forEach((c, ci) => variantRows.push({ sku: mkSku(), colorIndex: ci, size: c.name, priceETB: r2(priceETB + c.extraPriceETB), stock: c.stock, sortOrder: variantRows.length }))
  } else {
    variantRows.push({ sku: mkSku(), colorIndex: null, size: null, priceETB: r2(priceETB), stock: totalStock, sortOrder: 0 })
  }
  void stockOf
  const product = await db.product.create({
    data: {
      vendorId, vendorName: isPlatform ? null : user.vendorProfile?.businessName ?? null,
      categoryId, title, slug, description, priceETB: r2(priceETB), stock: totalStock, weightKg: weightKg ?? 1,
      status: isPlatform ? 'Active' : 'PendingReview', sku: `MH-${Date.now().toString(36).toUpperCase()}`,
      images: JSON.stringify({ from: '#1e293b', to: '#0f766e' }),
      colors: { create: gallery.colors },
    },
  })
  // attach variants to the created color rows (colorId FK)
  if (variantRows.length) {
    const createdColors = gallery.colors.length
      ? await db.productColor.findMany({ where: { productId: product.id }, orderBy: { sortOrder: 'asc' } })
      : []
    await db.productVariant.createMany({
      data: variantRows.map((v, i) => ({
        productId: product.id, sku: v.sku, size: v.size, priceETB: v.priceETB, stock: v.stock, sortOrder: i,
        colorId: v.colorIndex === null ? null : createdColors[v.colorIndex]?.id ?? null,
      })),
    })
  }
  if (gallery.colors.length) {
    const created = await db.productColor.findMany({ where: { productId: product.id }, orderBy: { sortOrder: 'asc' } })
    await db.productImage.createMany({
      data: gallery.images.map(im => ({
        productId: product.id, url: im.url, alt: im.alt, sortOrder: im.sortOrder,
        colorId: im.colorIndex === null ? null : created[im.colorIndex].id,
      })),
    })
  } else {
    await db.productImage.createMany({
      data: gallery.images.map(im => ({ productId: product.id, url: im.url, alt: im.alt, sortOrder: im.sortOrder, colorId: null })),
    })
  }
  return json({ product }, 201)
}

// ---------------- CART ----------------
export async function getCart() {
  const user = await requireUser()
  const items = await db.cartItem.findMany({
    where: { userId: user.id },
    include: { product: { include: { category: true, gallery: { orderBy: { sortOrder: 'asc' }, take: 1 } } }, color: true, variant: true },
    orderBy: { id: 'asc' },
  })
  const linePrice = (i: typeof items[number]) => r2(i.variant?.priceETB ?? i.product.priceETB + (i.color?.extraPriceETB ?? 0))
  const subtotal = r2(items.reduce((a, i) => a + linePrice(i) * i.quantity, 0))
  const availability = await Promise.all(items.map(i => availableQty({ productId: i.productId, variantId: i.variantId, colorId: i.colorId })))
  return json({
    items: items.map((i, idx) => ({
      id: i.id, productId: i.productId, quantity: i.quantity, product: i.product, color: i.color,
      variantId: i.variantId, variant: i.variant, availableQty: availability[idx],
    })),
    subtotal,
  })
}

export async function addToCart(req: NextRequest) {
  const user = await requireUser()
  const { productId, quantity, colorId, variantId } = await req.json()
  const product = await db.product.findUnique({ where: { id: productId } })
  if (!product || product.status !== 'Active') return bad('Product not available', 404)
  if (quantity < 1) return bad('Quantity must be at least 1')
  if (variantId) {
    // SKU-level variant — takes precedence over color
    const v = await db.productVariant.findUnique({ where: { id: variantId }, include: { color: true } })
    if (!v || v.productId !== productId) return bad('Selected variant is not available for this product', 400)
    if (!v.isActive) return bad('This variant is currently unavailable', 400)
    const existing = await db.cartItem.findFirst({ where: { userId: user.id, productId, variantId } })
    const inCart = existing?.quantity ?? 0
    const available = await availableQty({ productId, variantId })
    if (available < quantity + inCart) return bad(`Only ${available} left for ${v.sku}`)
    const item = existing
      ? await db.cartItem.update({ where: { id: existing.id }, data: { quantity: inCart + quantity } })
      : await db.cartItem.create({ data: { userId: user.id, productId, variantId, colorId: v.colorId, quantity } })
    return json({ item }, 201)
  }
  if (colorId) {
    const c = await db.productColor.findUnique({ where: { id: colorId } })
    if (!c || c.productId !== productId) return bad('Selected color is not available for this product', 400)
    const existing = await db.cartItem.findFirst({ where: { userId: user.id, productId, colorId, variantId: null } })
    const inCart = existing?.quantity ?? 0
    const available = await availableQty({ productId, colorId })
    if (available < quantity + inCart) return bad(`Only ${available} left in ${c.name}`)
  } else if (await db.productVariant.count({ where: { productId } })) {
    return bad('Please choose a variant first')
  } else if (await db.productColor.count({ where: { productId } })) {
    return bad('Please choose a color first')
  }
  const existing = await db.cartItem.findFirst({ where: { userId: user.id, productId, colorId: colorId ?? null, variantId: null } })
  const item = existing
    ? await db.cartItem.update({ where: { id: existing.id }, data: { quantity: existing.quantity + quantity } })
    : await db.cartItem.create({ data: { userId: user.id, productId, colorId: colorId ?? null, quantity } })
  return json({ item }, 201)
}

export async function updateCartItem(req: NextRequest) {
  const user = await requireUser()
  const { itemId, quantity } = await req.json()
  const item = await db.cartItem.findUnique({ where: { id: itemId } })
  if (!item || item.userId !== user.id) return bad('Cart item not found', 404)
  if (quantity <= 0) {
    await db.cartItem.delete({ where: { id: itemId } })
  } else {
    const available = await availableQty({ productId: item.productId, variantId: item.variantId, colorId: item.colorId })
    if (quantity > available) return bad(`Only ${available} left in stock`)
    await db.cartItem.update({ where: { id: itemId }, data: { quantity } })
  }
  return json({ ok: true })
}

// ---------------- CHECKOUT & PAYMENTS ----------------
export async function listZones() {
  const zones = await db.deliveryZone.findMany()
  return json({ items: zones })
}

export async function checkout(req: NextRequest) {
  const user = await requireUser()
  const b = await req.json()
  const { zoneId, phone, itemIds, couponCode, addressId, saveAddress } = b ?? {}
  if (!zoneId || !phone) return bad('zoneId and phone are required')
  if (!/^\+251[79]\d{8}$/.test(phone)) return bad('Phone must be in +2519XXXXXXXX or +2517XXXXXXXX format')
  const zone = await db.deliveryZone.findUnique({ where: { id: zoneId } })
  if (!zone) return bad('Delivery zone not found', 404)

  // ---- Address resolution: saved addressId → inline address → legacy addressLine shape ----
  let addressLine: string
  let city: string
  let savedAddressId: string | null = null
  if (addressId) {
    const a = await db.address.findUnique({ where: { id: addressId } })
    if (!a || a.userId !== user.id) return bad('Address not found', 404)
    addressLine = [a.subCity ? `${a.subCity} Sub-City` : null, `Woreda ${a.woreda}`, a.kebele ? `Kebele ${a.kebele}` : null, a.landmark, a.city].filter(Boolean).join(', ')
    city = a.city
    savedAddressId = a.id
  } else if (b.address?.fullName && b.address?.region) {
    const a = b.address
    if (!a.region || !a.city || !a.woreda) return bad('address.region, address.city and address.woreda are required')
    if (!/^\+251[79]\d{8}$/.test(a.phone ?? phone)) return bad('Address phone must be in +2519XXXXXXXX or +2517XXXXXXXX format')
    addressLine = [a.subCity ? `${a.subCity} Sub-City` : null, `Woreda ${a.woreda}`, a.kebele ? `Kebele ${a.kebele}` : null, a.landmark].filter(Boolean).join(', ') || a.city
    city = a.city
    if (saveAddress) {
      const isFirst = (await db.address.count({ where: { userId: user.id } })) === 0
      const created = await db.address.create({
        data: {
          userId: user.id, label: a.label ?? 'Home', fullName: a.fullName, phone: a.phone ?? phone,
          region: a.region, city: a.city, subCity: a.subCity ?? null, woreda: a.woreda, kebele: a.kebele ?? null,
          landmark: a.landmark ?? null, isDefault: isFirst,
        },
      })
      savedAddressId = created.id
    }
  } else if (b.addressLine && b.city) {
    // legacy checkout shape (kept for compatibility)
    addressLine = b.addressLine
    city = b.city
  } else {
    return bad('Provide addressId, an inline address, or addressLine + city')
  }

  const cartItems = await db.cartItem.findMany({
    where: { userId: user.id, ...(itemIds?.length ? { id: { in: itemIds } } : {}) },
    include: { product: true, color: true, variant: true },
  })
  if (!cartItems.length) return bad('Your cart is empty')
  const linePrice = (i: typeof cartItems[number]) => r2(i.variant?.priceETB ?? i.product.priceETB + (i.color?.extraPriceETB ?? 0))
  // reservation-aware availability check
  for (const i of cartItems) {
    const available = await availableQty({ productId: i.productId, variantId: i.variantId, colorId: i.colorId })
    if (available < i.quantity) return bad(`Insufficient stock for "${i.variant?.sku ?? i.product.title}" — only ${available} left`)

  }

  const subtotal = r2(cartItems.reduce((a, i) => a + linePrice(i) * i.quantity, 0))

  // ---- Coupon (validated: window / uses / minimum / cap) ----
  let discount = 0
  let appliedCoupon: ValidCoupon | null = null
  if (couponCode) {
    const check = await validateCoupon(couponCode, subtotal)
    if (!check.ok) return bad(check.message)
    discount = check.discountETB
    appliedCoupon = check.coupon
  }

  const totalWeight = r2(cartItems.reduce((a, i) => a + i.product.weightKg * i.quantity, 0))
  let deliveryFee = zone.baseFeeETB + zone.perKgSurcharge * Math.max(0, totalWeight - 5)
  const freeThreshold = zone.id === 'z_addis' && zone.freeThreshold === 0
    ? parseFloat(await getSetting('FreeDeliveryAddisThreshold', '1000'))
    : zone.freeThreshold
  if (freeThreshold > 0 && subtotal - discount >= freeThreshold) deliveryFee = 0
  deliveryFee = r2(deliveryFee)
  const rate = await vatRate()
  const vat = calculateVat(r2(subtotal - discount), deliveryFee, rate) // VAT on (subtotal − discount + delivery)
  const grand = r2(subtotal - discount + deliveryFee + vat)
  const cRate = parseFloat(await getSetting('DefaultCommissionRate', '0.10'))
  const discRatio = subtotal > 0 ? discount / subtotal : 0 // discount allocated proportionally across lines
  const deliveryPin = newDeliveryPin()
  const trackingCode = newTrackingCode()

  const order = await db.order.create({
    data: {
      number: orderNumber(), buyerId: user.id, status: 'Placed',
      addressId: savedAddressId, addressLine, city, zoneId, zoneName: zone.name, phone,
      trackingCode, deliveryPin, couponCode: appliedCoupon?.code ?? null, couponDiscountETB: discount,
      subtotalETB: subtotal, deliveryFeeETB: deliveryFee, vatRate: rate, vatETB: vat, grandTotalETB: grand,
      items: {
        create: cartItems.map(i => {
          const unit = linePrice(i)
          const lineNet = r2(unit * i.quantity * (1 - discRatio))
          const variantLabel = i.variant ? [i.color?.name, i.variant.size].filter(Boolean).join(' / ') : null
          return {
            productId: i.productId, variantId: i.variantId, variantSku: i.variant?.sku ?? null, variantLabel,
            colorId: i.colorId, titleSnapshot: i.product.title, colorName: i.color?.name ?? null, colorHex: i.color?.hex ?? null,
            vendorId: i.product.vendorId, vendorName: i.product.vendorName,
            unitPriceETB: unit, quantity: i.quantity, lineNetETB: lineNet,
            vatETB: r2((lineNet / Math.max(subtotal - discount, 0.01)) * vat), commissionRate: i.product.vendorId ? cRate : 0,
            commissionETB: i.product.vendorId ? r2(lineNet * cRate) : 0,
            deliveryAllocETB: r2((lineNet / Math.max(subtotal - discount, 0.01)) * deliveryFee),
          }
        }),
      },
      payments: { create: { provider: 'Mock', providerRef: `MOCK-${orderNumber()}`, amountETB: grand, status: 'Initiated' } },
      events: { create: { status: 'Placed', byRole: 'Buyer', note: appliedCoupon ? `Order placed with coupon ${appliedCoupon.code} (−${discount} Br)` : 'Order placed' } },
    },
    include: { items: true, payments: true, events: { orderBy: { createdAt: 'asc' as const } } },
  })

  // ---- Reservations: attach matching holds, create the rest (stock decrements at payment confirm) ----
  const minutes = Math.max(1, parseInt(await getSetting('ReservationMinutes', '20'), 10))
  const attached = new Set<string>()
  for (const i of cartItems) {
    const hold = await db.stockReservation.findFirst({
      where: {
        userId: user.id, productId: i.productId, variantId: i.variantId ?? null, colorId: i.colorId ?? null,
        status: 'Active', orderId: null, expiresAt: { gt: new Date() },
      },
    })
    if (hold && !attached.has(hold.id)) {
      await db.stockReservation.update({ where: { id: hold.id }, data: { orderId: order.id } })
      attached.add(hold.id)
    } else {
      await db.stockReservation.create({
        data: {
          userId: user.id, productId: i.productId, variantId: i.variantId ?? null, colorId: i.colorId ?? null,
          qty: i.quantity, status: 'Active', expiresAt: new Date(Date.now() + minutes * 60000), orderId: order.id,
        },
      })
    }
  }
  // leftover unattached holds are released so they don't ghost-lock stock
  await db.stockReservation.updateMany({ where: { userId: user.id, status: 'Active', orderId: null }, data: { status: 'Released' } })
  // coupon redemption
  if (appliedCoupon) {
    await db.couponRedemption.create({ data: { couponId: appliedCoupon.id, orderId: order.id, userId: user.id, discountETB: discount } })
    await db.coupon.update({ where: { id: appliedCoupon.id }, data: { usedCount: { increment: 1 } } })
  }
  await db.cartItem.deleteMany({ where: { id: { in: cartItems.map(i => i.id) } } })
  await notify(user.id, 'OrderPlaced', 'Order placed', `Order ${order.number} was placed. Complete the payment to confirm — your items are reserved for ${minutes} minutes.`)
  return json({ order }, 201)
}

export async function mockConfirmPayment(req: NextRequest) {
  const { providerRef } = await req.json()
  const payment = await db.payment.findUnique({ where: { providerRef }, include: { order: { include: { items: true } } } })
  if (!payment) return bad('Payment reference not found', 404)
  if (payment.status === 'Confirmed') return json({ ok: true, idempotent: true, payment }) // idempotent webhook behavior
  const legs = paymentLegs({
    id: payment.order.id, vatETB: payment.order.vatETB, deliveryFeeETB: payment.order.deliveryFeeETB,
    items: payment.order.items.map(i => ({ vendorId: i.vendorId, commissionETB: i.commissionETB, lineNetETB: i.lineNetETB })),
  })
  await db.payment.update({ where: { id: payment.id }, data: { status: 'Confirmed' } })
  await db.order.update({ where: { id: payment.order.id }, data: { status: 'PaymentConfirmed' } })
  await db.orderEvent.create({ data: { orderId: payment.order.id, status: 'PaymentConfirmed', byRole: 'System', note: `Payment ${payment.providerRef} confirmed via ${payment.provider}` } })
  await db.ledgerEntry.createMany({ data: legs.map(l => ({ accountCode: l.accountCode, orderId: l.orderId, vendorId: l.vendorId, debitETB: l.debitETB ?? 0, creditETB: l.creditETB ?? 0, description: l.description })) })
  // Stock decrements happen at payment confirmation (GAP_PLAN stock rule); reservations are consumed.
  for (const i of payment.order.items) {
    await db.product.update({ where: { id: i.productId }, data: { stock: { decrement: i.quantity }, soldCount: { increment: i.quantity } } })
    if (i.colorId) await db.productColor.update({ where: { id: i.colorId }, data: { stock: { decrement: i.quantity } } })
    if (i.variantId) await db.productVariant.update({ where: { id: i.variantId }, data: { stock: { decrement: i.quantity } } })
  }
  await db.stockReservation.updateMany({ where: { orderId: payment.order.id, status: 'Active' }, data: { status: 'Consumed' } })
  const buyer = await db.user.findUnique({ where: { id: payment.order.buyerId } })
  await dispatch('OrderConfirmed', buyer, {
    subject: 'Payment confirmed',
    body: `Payment of ${payment.amountETB} Br for order ${payment.order.number} was confirmed. Vendors will prepare your items.${payment.order.deliveryPin ? ` Delivery PIN: ${payment.order.deliveryPin} — share it only with the courier.` : ''}`,
  })
  const vendorIds = [...new Set(payment.order.items.map(i => i.vendorId).filter(Boolean))] as string[]
  for (const v of vendorIds) {
    const vendor = await db.user.findUnique({ where: { id: v } })
    await notify(v, 'NewOrder', 'New order received', `Order ${payment.order.number} is paid and waiting for your acceptance (within 24h).`)
    await sendSms(vendor?.phone, 'OrderConfirmed', { orderNumber: payment.order.number, role: 'vendor' })
  }
  return json({ ok: true, payment: { ...payment, status: 'Confirmed' }, order: { ...payment.order, status: 'PaymentConfirmed' } })
}

// ---------------- BUYER ORDERS ----------------
export async function myOrders(req: NextRequest) {
  const user = await requireUser()
  const status = req.nextUrl.searchParams.get('status') ?? undefined
  const orders = await db.order.findMany({
    where: { buyerId: user.id, ...(status ? { status } : {}) },
    orderBy: { createdAt: 'desc' },
    include: { items: true, payments: true, events: { orderBy: { createdAt: 'asc' as const } } },
  })
  return json({ items: orders })
}

export async function requestRefund(req: NextRequest, orderId: string) {
  const user = await requireUser()
  const { reason } = await req.json()
  if (!reason || reason.trim().length < 10) return bad('Please describe the reason (at least 10 characters)')
  const order = await db.order.findUnique({ where: { id: orderId } })
  if (!order || order.buyerId !== user.id) return bad('Order not found', 404)
  if (!canTransition(order.status, 'RefundRequested')) return bad(`A refund cannot be requested while the order is ${order.status}. See the Return & Refund Policy.`)
  const windowDays = parseInt(await getSetting('ReturnWindowDays', '7'), 10)
  if (order.deliveredAt && (Date.now() - order.deliveredAt.getTime()) > windowDays * 864e5)
    return bad(`The ${windowDays}-day return window has passed for this order. See the Return & Refund Policy.`)
  const updated = await db.order.update({
    where: { id: orderId }, data: { status: 'RefundRequested', previousStatus: order.status, refundReason: reason },
    include: { items: true },
  })
  await db.orderItem.updateMany({ where: { orderId }, data: { refundStatus: 'Requested' } })
  await db.orderEvent.create({ data: { orderId, status: 'RefundRequested', byRole: 'Buyer', note: reason } })
  await notify('u_admin', 'RefundRequested', 'Refund requested', `Order ${order.number}: "${reason}"`)
  return json({ order: updated })
}

// ---------------- POLICIES ----------------
export async function listPolicies() {
  const policies = await db.policy.findMany({ where: { isActive: true } })
  return json({ items: policies.map(p => ({ key: p.key, version: p.version, title: p.contentMarkdown.split('\n')[0].replace(/^#\s*/, ''), effectiveFrom: p.effectiveFrom })) })
}

export async function getPolicy(key: string) {
  const policy = await db.policy.findFirst({ where: { key, isActive: true }, orderBy: { effectiveFrom: 'desc' } })
  if (!policy) return bad('Policy not found', 404)
  return json({ policy })
}

// ---------------- NOTIFICATIONS ----------------
export async function myNotifications() {
  const user = await requireUser()
  const items = await db.notification.findMany({ where: { userId: user.id }, orderBy: { createdAt: 'desc' }, take: 30 })
  return json({ items })
}

export async function readAllNotifications() {
  const user = await requireUser()
  await db.notification.updateMany({ where: { userId: user.id, readAt: null }, data: { readAt: new Date() } })
  return json({ ok: true })
}

void HttpError
export { json, bad, notify }
