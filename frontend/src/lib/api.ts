// Client API layer — contract-first.
// NEXT_PUBLIC_API_MODE = "mock" (default) → same-origin preview API (this sandbox)
// NEXT_PUBLIC_API_MODE = "dotnet"        → real MarketHub .NET 9 backend at NEXT_PUBLIC_DOTNET_URL
const MODE = process.env.NEXT_PUBLIC_API_MODE ?? 'mock'
const DOTNET_URL = process.env.NEXT_PUBLIC_DOTNET_URL ?? 'http://localhost:8080'

export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message) }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const base = MODE === 'dotnet' ? DOTNET_URL : ''
  const res = await fetch(`${base}${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...(init?.headers ?? {}) },
    credentials: 'include',
    cache: 'no-store',
  })
  const ct = res.headers.get('content-type') ?? ''
  if (!ct.includes('application/json')) {
    if (!res.ok) throw new ApiError(res.status, `Request failed (${res.status})`)
    return undefined as T
  }
  const body = await res.json()
  if (!res.ok) throw new ApiError(res.status, body?.error ?? `Request failed (${res.status})`)
  return body as T
}

export const api = {
  get: <T,>(path: string) => request<T>(path),
  post: <T,>(path: string, body?: unknown) => request<T>(path, { method: 'POST', body: body ? JSON.stringify(body) : undefined }),
  patch: <T,>(path: string, body?: unknown) => request<T>(path, { method: 'PATCH', body: body ? JSON.stringify(body) : undefined }),
  put: <T,>(path: string, body?: unknown) => request<T>(path, { method: 'PUT', body: body ? JSON.stringify(body) : undefined }),
  delete: <T,>(path: string) => request<T>(path, { method: 'DELETE' }),
}

// ---------------- Domain types (mirror the .NET DTOs) ----------------
export type Vendor = {
  id: string; businessName: string; tin?: string; status: 'PendingReview' | 'UnderReview' | 'Approved' | 'Rejected' | 'Suspended'
  region?: string | null; city?: string | null; description?: string | null; rejectionReason?: string | null
  ratingAvg?: number; ratingCount?: number; createdAt?: string
}
export type AdminRole = 'SuperAdmin' | 'Support' | 'Finance' | 'Moderator'
export type SessionUser = {
  id: string; email: string; phone?: string | null; fullName: string
  role: 'Buyer' | 'Vendor' | 'Admin'; status?: string
  adminRole?: AdminRole | null; lang?: 'en' | 'am'
  vendor?: Vendor | null
}
export type ProductColor = {
  id: string; productId: string; name: string; hex: string
  stock: number; extraPriceETB: number; sortOrder: number
}
export type ProductImage = { id: string; productId: string; colorId: string | null; url: string; alt: string; sortOrder: number }
export type Product = {
  id: string; vendorId: string | null; vendorName: string | null; categoryId: string
  title: string; slug: string; description: string; priceETB: number; compareAtPriceETB?: number | null
  salePriceETB?: number | null; saleEndsAt?: string | null
  stock: number; soldCount?: number; viewCount?: number
  sku?: string | null; weightKg?: number; status: string
  moderationNote?: string | null
  isFeatured?: boolean; ratingAvg: number; ratingCount: number; images: string | null; createdAt?: string
  category?: { id: string; name: string; slug: string }
  colors?: ProductColor[]
  gallery?: ProductImage[]
  photoCount?: number
  variantsCount?: number
  variants?: ProductVariant[]
  _count?: { variants?: number }
}
export type Paged<T> = { items: T[]; page: number; pageSize: number; totalCount: number }
export type Category = { id: string; name: string; slug: string; productCount: number }
export type Zone = { id: string; name: string; baseFeeETB: number; perKgSurcharge: number; freeThreshold: number }
export type OrderItem = {
  id: string; productId: string; titleSnapshot: string; colorName?: string | null; colorHex?: string | null
  variantId?: string | null; variantSku?: string | null; variantLabel?: string | null
  vendorId: string | null; vendorName: string | null
  unitPriceETB: number; quantity: number; lineNetETB: number; vatETB: number; commissionRate: number
  commissionETB: number; deliveryAllocETB: number; refundStatus: string
}
export type Payment = { id: string; provider: string; providerRef: string; amountETB: number; status: string }
export type OrderEvent = {
  id: string; orderId: string; status: string; note?: string | null
  byRole: 'System' | 'Buyer' | 'Vendor' | 'Admin' | 'Courier'; createdAt: string
}
export type Order = {
  id: string; number: string; buyerId: string; status: string; previousStatus?: string | null
  addressId?: string | null; addressLine: string; city: string; zoneName: string; phone: string
  trackingCode?: string | null; deliveryPin?: string | null
  couponCode?: string | null; couponDiscountETB?: number
  subtotalETB: number; deliveryFeeETB: number; vatRate: number; vatETB: number; grandTotalETB: number
  refundReason?: string | null; deliveredAt?: string | null; createdAt: string
  items: OrderItem[]; payments?: Payment[]; events?: OrderEvent[]
}
export type Payout = {
  id: string; vendorId: string; amountETB: number; status: 'Requested' | 'Processing' | 'Paid' | 'Rejected'
  requestedAt: string; dueBy: string; processedAt?: string | null; paymentMethod: string
  destinationAccount?: string | null; adminNote?: string | null; vendorName?: string; slaHours?: number; overdue?: boolean
}
export type PolicySummary = { key: string; version: string; title: string; effectiveFrom: string }
export type Policy = { key: string; version: string; contentMarkdown: string; effectiveFrom: string }
export type Notification = { id: string; type: string; subject: string; body: string; readAt?: string | null; createdAt: string }
export type DocumentType = { id: string; code: string; name: string; appliesTo: string; isRequired: boolean; minFiles: number; maxFiles: number; description?: string | null }
export type ReportSummary = {
  grossSales: number; platformIncome: number; vendorIncome: number; commissionEarned: number
  deliveryRevenue: number; vatCollected: number; refundTotal: number; ordersCount: number
  pendingPayoutCount: number; pendingPayoutAmount: number
}
export type RevenueOverTime = { labels: string[]; series: { name: string; data: number[] }[] }
export type TopVendor = { vendorId: string; businessName: string; city: string; incomeETB: number }
export type TopProduct = { productId: string; title: string; soldQty: number; revenueETB: number }
export type VendorEarnings = {
  balanceETB: number; availableForPayout: number; pendingPayout: number; totalPaidOut: number
  slaHours: number; monthly: { month: string; amount: number }[]
}
export type VendorApplication = Vendor & {
  owner: { email: string; fullName: string; phone?: string; documents: { id: string; fileName: string; contentType: string; sizeBytes: number; status: string; type: DocumentType; uploadedAt: string }[] }
  productCount?: number
  products?: unknown[]
}

// ---------------- Gap-fill domain types (GAP_PLAN section B payloads) ----------------
export type ProductVariant = {
  id: string; productId: string; sku: string; colorId: string | null; size: string | null
  priceETB: number | null; stock: number; isActive: boolean; colorHex?: string
}
export type Review = {
  id: string; productId: string; userId: string; orderId?: string | null
  rating: number; title?: string | null; body: string; imageUrls?: string[] | null
  status: 'Pending' | 'Approved' | 'Rejected'
  vendorReply?: string | null; vendorRepliedAt?: string | null; createdAt: string
  userName?: string; productTitle?: string; verified?: boolean
  product?: { id: string; title: string; slug: string; vendorId: string | null }
  user?: unknown
}
export type ReviewBreakdown = { r5: number; r4: number; r3: number; r2: number; r1: number }
export type ReviewsPage = Paged<Review> & { breakdown: ReviewBreakdown }
export type StockReservation = {
  id: string; userId: string; productId: string; variantId?: string | null; colorId?: string | null
  qty: number; status: 'Active' | 'Consumed' | 'Released' | 'Expired'
  expiresAt: string; orderId?: string | null; createdAt: string
}
export type Address = {
  id: string; userId: string; label: string; fullName: string; phone: string
  region: string; city: string; subCity?: string | null; woreda: string
  kebele?: string | null; landmark?: string | null; isDefault: boolean; createdAt: string
}
export type ReturnRequest = {
  id: string; orderItemId: string; orderId: string; buyerId: string; vendorId?: string | null
  reason: 'Damaged' | 'WrongItem' | 'NotAsDescribed' | 'ChangedMind' | 'Other'
  description: string; evidenceUrls?: string[] | null
  status: 'Requested' | 'VendorApproved' | 'VendorRejected' | 'AdminReview' | 'Approved' | 'Rejected' | 'Refunded'
  vendorNote?: string | null; adminDecision?: string | null; adminId?: string | null
  refundETB?: number | null; createdAt: string; resolvedAt?: string | null
  // joined fields for lists (admin + vendor views)
  itemTitle?: string | null; variantSku?: string | null; variantLabel?: string | null
  itemUnitPriceETB?: number | null; itemQuantity?: number | null
  orderNumber?: string | null; buyerName?: string | null; vendorName?: string | null
}
export type Coupon = {
  id: string; code: string; description?: string | null; type: 'Percent' | 'Fixed'
  value: number; minSubtotal: number; maxDiscount?: number | null
  maxUses: number; usedCount: number; startsAt: string; expiresAt: string; isActive: boolean
  redemptionCount?: number
}
export type CouponRedemption = { id: string; couponId: string; orderId: string; userId: string; discountETB: number; createdAt: string }
export type CouponCheck = { ok: boolean; discountETB: number; message: string }
export type WishlistItem = { id: string; productId: string; product: ProductCard }
export type Banner = {
  id: string; title: string; imageUrl: string; link?: string | null; position: number
  isActive: boolean; startsAt?: string | null; endsAt?: string | null; createdAt?: string
}
export type CartSnapshotItem = { productId: string; variantId?: string | null; colorId?: string | null; qty: number }
export type CartSnapshot = { id: string; userId: string; items: CartSnapshotItem[]; updatedAt: string; reminderSentAt?: string | null }
export type OutboundMessage = {
  id: string; userId?: string | null; channel: 'Sms' | 'Email'; template: string; destination: string
  payload?: unknown; provider: string; providerRef?: string | null
  status: 'Queued' | 'Sent' | 'Failed' | 'Skipped'; error?: string | null
  createdAt: string; sentAt?: string | null; userEmail?: string | null; userName?: string | null
}
export type ProductCard = {
  id: string; slug: string; title: string; priceETB: number
  salePriceETB?: number; compareAtPriceETB?: number
  thumb: string | null; ratingAvg: number; ratingCount: number; soldCount: number
  vendorName: string | null; platform: boolean; stock: number
  colors?: { name: string; hex: string }[]
}
export type HomeData = {
  banners: Banner[]
  flashSale: { endsAt: string; products: ProductCard[] } | null
  featured: ProductCard[]
  topRated: ProductCard[]
  newArrivals: ProductCard[]
  categories: Category[]
}
export type SuggestData = {
  products: { slug: string; title: string; thumb: string | null; priceETB: number }[]
  categories: { slug: string; name: string }[]
}
export type ProductDetail = {
  product: Product
  variants: ProductVariant[]
  reviewsSummary: { avg: number; count: number; breakdown: ReviewBreakdown }
  recentReviews: Review[]
  related: ProductCard[]
}
export type ReserveResult = {
  expiresAt: string
  reservations: { key: string; ok: boolean; available: number }[]
  allOk: boolean
}
export type VendorPerformance = {
  ratingAvg: number; ratingCount: number; totalSold: number
  fulfillmentRate: number; cancelRate: number; openReturns: number; disputeRate: number
  businessName?: string; city?: string | null
}
export type VendorDashboard = {
  daily: { date: string; revenue: number; orders: number }[]
  totals: { products: number; activeOrders: number; pendingReturns: number; lowStock: number }
}
export type AdminSettingEntry = {
  key: string; value: string; description?: string | null; group: string
  type: 'number' | 'int' | 'text' | 'enum'; values?: string[] | null
}
export type AdminSettings = { entries: AdminSettingEntry[]; groups: { name: string; keys: string[] }[] }
export type StaffMember = {
  id: string; email: string; fullName: string
  adminRole: AdminRole | null; status: string; lang?: string; createdAt?: string
}
export type BulkImportResult = { created: number; errors: string[] }
export type AbandonedCart = {
  userId: string; email: string; fullName: string; phone?: string | null
  items: CartSnapshotItem[]; updatedAt: string; reminderSentAt?: string | null
}

// ---------------- Formatting ----------------
export const lineUnitPrice = (p: { priceETB: number }, color?: { extraPriceETB: number } | null) =>
  Math.round((p.priceETB + (color?.extraPriceETB ?? 0)) * 100) / 100
export const br = (n: number) => `${n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })} Br`
export const brShort = (n: number) => n >= 1e6 ? `${(n / 1e6).toFixed(1)}M Br` : n >= 1e3 ? `${(n / 1e3).toFixed(1)}k Br` : `${n} Br`
export const dateFmt = (iso: string | Date) => new Date(iso).toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' })
export const dateTimeFmt = (iso: string | Date) => new Date(iso).toLocaleString('en-GB', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' })
