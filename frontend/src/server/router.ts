// API router — maps /api/<path> to handlers. This mock server mirrors the .NET API contract 1:1,
// so the frontend can switch to the real MarketHub backend by setting NEXT_PUBLIC_API_MODE=dotnet.
import { NextRequest, NextResponse } from 'next/server'
import * as store from './handlers/store'
import * as back from './handlers/backoffice'
import * as ext from './handlers/extensions'
import { HttpError } from './session'

type Ctx = { params: Promise<{ path: string[] }> }

async function dispatch(req: NextRequest, ctx: Ctx) {
  const { path } = await ctx.params
  const [a, b, c, d] = path
  const method = req.method
  const seg = `${method} /${path.join('/')}`

  // -- auth
  if (a === 'auth' && b === 'register' && method === 'POST') return store.register(req)
  if (a === 'auth' && b === 'login' && method === 'POST') return store.login(req)
  if (a === 'auth' && b === 'logout' && method === 'POST') return store.logout()
  if (a === 'auth' && b === 'me') return store.me()

  // -- catalog
  if (a === 'catalog' && b === 'home' && !c) return ext.catalogHome()
  if (a === 'catalog' && b === 'search' && c === 'suggest') return ext.catalogSuggest(req)
  if (a === 'catalog' && b === 'reviews' && !c && method === 'POST') return ext.createReview(req)
  if (a === 'catalog' && b === 'products' && c && d === 'reviews' && method === 'GET') return ext.catalogProductReviews(req, c)
  if (a === 'catalog' && b === 'products' && c && !d && method === 'GET') return ext.catalogProductDetail(c)
  if (a === 'catalog' && b === 'products' && !c && method === 'GET') return store.listProducts(req)
  if (a === 'catalog' && b === 'products' && method === 'POST' && !c) return store.createProduct(req)
  if (a === 'catalog' && b === 'categories') return store.listCategories()

  // -- sales & cart
  if (a === 'sales' && b === 'zones') return store.listZones()
  if (a === 'sales' && b === 'wishlist' && !c && method === 'GET') return ext.listWishlist()
  if (a === 'sales' && b === 'wishlist' && c && method === 'POST') return ext.toggleWishlist(req, c)
  if (a === 'sales' && b === 'cart' && c === 'reserve' && method === 'POST') return ext.cartReserve()
  if (a === 'sales' && b === 'cart' && c === 'release' && method === 'POST') return ext.cartRelease()
  if (a === 'sales' && b === 'cart' && c === 'save' && method === 'POST') return ext.cartSave(req)
  if (a === 'sales' && b === 'cart' && c === 'coupon' && method === 'POST') return ext.cartCouponCheck(req)
  if (a === 'sales' && b === 'cart' && method === 'GET') return store.getCart()
  if (a === 'sales' && b === 'cart' && method === 'POST' && !c) return store.addToCart(req)
  if (a === 'sales' && b === 'cart' && method === 'PATCH') return store.updateCartItem(req)
  if (a === 'sales' && b === 'addresses' && !c && method === 'GET') return ext.listAddresses()
  if (a === 'sales' && b === 'addresses' && !c && method === 'POST') return ext.createAddress(req)
  if (a === 'sales' && b === 'checkout' && method === 'POST') return store.checkout(req)
  if (a === 'sales' && b === 'orders' && !c) return store.myOrders(req)
  if (a === 'sales' && b === 'orders' && c === 'refund-request' && d && method === 'POST') return store.requestRefund(req, d)
  if (a === 'sales' && b === 'orders' && c && d === 'cancel' && method === 'POST') return ext.cancelOrder(req, c)
  if (a === 'sales' && b === 'returns' && !c && method === 'GET') return ext.myReturns()
  if (a === 'sales' && b === 'returns' && !c && method === 'POST') return ext.createReturn(req)

  // -- payments
  if (a === 'payments' && b === 'mock' && c === 'confirm') return store.mockConfirmPayment(req)

  // -- policies
  if (a === 'policies' && !b) return store.listPolicies()
  if (a === 'policies' && b) return store.getPolicy(b)

  // -- notifications
  if (a === 'notifications' && !b) return store.myNotifications()
  if (a === 'notifications' && b === 'read-all') return store.readAllNotifications()

  // -- admin
  if (a === 'admin' && b === 'reports' && c === 'summary') return back.reportSummary(req)
  if (a === 'admin' && b === 'reports' && c === 'revenue-over-time') return back.reportRevenueOverTime(req)
  if (a === 'admin' && b === 'reports' && c === 'top-vendors') return back.reportTopVendors(req)
  if (a === 'admin' && b === 'reports' && c === 'top-products') return back.reportTopProducts(req)
  if (a === 'admin' && b === 'reports' && c === 'vendor-income' && d) return back.reportVendorIncome(req, d)
  if (a === 'admin' && b === 'vendors' && c && d === 'performance' && method === 'GET') return ext.adminVendorPerformance(req, c)
  if (a === 'admin' && b === 'vendors' && !c) return back.adminListVendors(req)
  if (a === 'admin' && b === 'vendors' && c === 'review' && d && method === 'POST') return back.adminReviewVendor(req, d)
  if (a === 'admin' && b === 'products' && !c) return back.adminListProducts(req)
  if (a === 'admin' && b === 'products' && c === 'moderate' && d && method === 'POST') return back.adminModerateProduct(req, d)
  if (a === 'admin' && b === 'orders' && !c) return back.adminListOrders(req)
  if (a === 'admin' && b === 'orders' && c === 'refund-decision' && d && method === 'POST') return back.adminRefundDecision(req, d)
  if (a === 'admin' && b === 'payouts' && !c) return back.adminListPayouts()
  if (a === 'admin' && b === 'payouts' && c === 'process' && d && method === 'POST') return back.adminProcessPayout(req, d)
  if (a === 'admin' && b === 'commission' && method === 'GET') return back.getCommission()
  if (a === 'admin' && b === 'commission' && method === 'PUT') return back.putCommission(req)
  // -- admin · gap-fill (reviews, returns, staff, settings, marketing, ops)
  if (a === 'admin' && b === 'reviews' && !c) return ext.adminListReviews(req)
  if (a === 'admin' && b === 'reviews' && c && d === 'moderate' && method === 'POST') return ext.adminModerateReview(req, c)
  if (a === 'admin' && b === 'returns' && !c) return ext.adminListReturns(req)
  if (a === 'admin' && b === 'returns' && c && d === 'arbitrate' && method === 'POST') return ext.adminArbitrateReturn(req, c)
  if (a === 'admin' && b === 'staff' && !c && method === 'GET') return ext.adminListStaff()
  if (a === 'admin' && b === 'staff' && !c && method === 'POST') return ext.adminCreateStaff(req)
  if (a === 'admin' && b === 'staff' && c && method === 'PATCH') return ext.adminPatchStaff(req, c)
  if (a === 'admin' && b === 'settings' && method === 'GET') return ext.adminGetSettings()
  if (a === 'admin' && b === 'settings' && method === 'PUT') return ext.adminPutSettings(req)
  if (a === 'admin' && b === 'banners' && !c && method === 'GET') return ext.adminListBanners()
  if (a === 'admin' && b === 'banners' && !c && method === 'POST') return ext.adminCreateBanner(req)
  if (a === 'admin' && b === 'banners' && c && method === 'PATCH') return ext.adminPatchBanner(req, c)
  if (a === 'admin' && b === 'banners' && c && method === 'DELETE') return ext.adminDeleteBanner(req, c)
  if (a === 'admin' && b === 'flash-sale' && method === 'POST') return ext.adminSetFlashSale(req)
  if (a === 'admin' && b === 'flash-sale' && method === 'DELETE') return ext.adminClearFlashSale()
  if (a === 'admin' && b === 'coupons' && !c && method === 'GET') return ext.adminListCoupons()
  if (a === 'admin' && b === 'coupons' && !c && method === 'POST') return ext.adminCreateCoupon(req)
  if (a === 'admin' && b === 'coupons' && c && method === 'PATCH') return ext.adminPatchCoupon(req, c)
  if (a === 'admin' && b === 'abandoned-carts' && !c && method === 'GET') return ext.adminListAbandonedCarts()
  if (a === 'admin' && b === 'abandoned-carts' && c === 'remind' && method === 'POST') return ext.adminRemindAbandonedCart(req)
  if (a === 'admin' && b === 'outbound' && method === 'GET') return ext.adminListOutbound(req)
  if (a === 'admin' && b === 'jobs' && c === 'document-expiry' && method === 'POST') return ext.jobDocumentExpiry()
  if (a === 'admin' && b === 'jobs' && c === 'stock-sweeper' && method === 'POST') return ext.jobStockSweeper()

  // -- vendor console
  if (a === 'vendor' && b === 'earnings') return back.vendorEarnings()
  if (a === 'vendor' && b === 'payouts' && method === 'GET') return back.vendorPayouts()
  if (a === 'vendor' && b === 'payouts' && method === 'POST') return back.vendorRequestPayout(req)
  if (a === 'vendor' && b === 'orders' && !c) return back.vendorOrders()
  if (a === 'vendor' && b === 'orders' && c && d === 'action' && method === 'POST') return back.vendorOrderAction(req, c)
  if (a === 'vendor' && b === 'orders' && c === 'action' && d && method === 'POST') return back.vendorOrderAction(req, d)
  if (a === 'vendor' && b === 'products' && c === 'bulk' && method === 'POST') return ext.vendorBulkImport(req)
  if (a === 'vendor' && b === 'products' && !c) return back.vendorProducts()
  if (a === 'vendor' && b === 'variants' && c && method === 'PATCH') return ext.vendorPatchVariant(req, c)
  if (a === 'vendor' && b === 'returns' && !c && method === 'GET') return ext.vendorReturnsList()
  if (a === 'vendor' && b === 'returns' && c && d === 'decision' && method === 'POST') return ext.vendorReturnDecision(req, c)
  if (a === 'vendor' && b === 'reviews' && !c && method === 'GET') return ext.vendorReviewsList()
  if (a === 'vendor' && b === 'reviews' && c && d === 'reply' && method === 'POST') return ext.vendorReviewReply(req, c)
  if (a === 'vendor' && b === 'performance' && !c) return ext.vendorPerformance()
  if (a === 'vendor' && b === 'dashboard' && !c) return ext.vendorDashboard()

  return NextResponse.json({ error: `No route: ${seg}` }, { status: 404 })
}

export async function GET(req: NextRequest, ctx: Ctx) {
  try { return await dispatch(req, ctx) }
  catch (e) { return handleError(e) }
}
export async function POST(req: NextRequest, ctx: Ctx) {
  try { return await dispatch(req, ctx) }
  catch (e) { return handleError(e) }
}
export async function PATCH(req: NextRequest, ctx: Ctx) {
  try { return await dispatch(req, ctx) }
  catch (e) { return handleError(e) }
}
export async function PUT(req: NextRequest, ctx: Ctx) {
  try { return await dispatch(req, ctx) }
  catch (e) { return handleError(e) }
}
export async function DELETE(req: NextRequest, ctx: Ctx) {
  try { return await dispatch(req, ctx) }
  catch (e) { return handleError(e) }
}

function handleError(e: unknown) {
  if (e instanceof HttpError) return NextResponse.json({ error: e.message }, { status: e.status })
  console.error('[api]', e)
  return NextResponse.json({ error: 'Internal server error' }, { status: 500 })
}
