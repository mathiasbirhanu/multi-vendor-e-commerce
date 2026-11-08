// Back-office handlers: admin console + vendor console.
// Admin: reports (charts+tables), vendor approvals, product moderation, payouts (12h SLA), commission.
// Vendor: earnings, payout requests, order lifecycle, product listing.
import { NextRequest } from 'next/server'
import { db } from '@/lib/db'
import { dispatch } from '@/lib/providers'
import { canTransition, r2 } from '../money'
import { requireRole } from '../session'
import { bad, getSetting, json, notify } from './store'

const LEDGER = { CASH: '1000', VENDOR_PAYABLE: '2000', VAT_PAYABLE: '2100', COMMISSION: '3000', DELIVERY: '3100', FIRST_PARTY: '3200', REFUND_EXPENSE: '4000' } as const

// ---------------- ADMIN · REPORTS ----------------
function rangeStart(range: string): Date | null {
  const now = Date.now()
  if (range === 'today') return new Date(new Date().setHours(0, 0, 0, 0))
  if (range === '7d') return new Date(now - 7 * 864e5)
  if (range === '30d') return new Date(now - 30 * 864e5)
  return null
}
const sumLedger = (rows: { debitETB: number; creditETB: number }[]) =>
  r2(rows.reduce((a, e) => a + e.creditETB - e.debitETB, 0))

export async function reportSummary(req: NextRequest) {
  await requireRole('Admin')
  const from = rangeStart(req.nextUrl.searchParams.get('range') ?? '30d')
  const where = { createdAt: from ? { gte: from } : undefined }
  const [cash, commission, delivery, vat, refunds, orders, payouts] = await Promise.all([
    db.ledgerEntry.findMany({ where: { accountCode: LEDGER.CASH, orderId: { not: null }, ...where } }),
    db.ledgerEntry.findMany({ where: { accountCode: LEDGER.COMMISSION, ...where } }),
    db.ledgerEntry.findMany({ where: { accountCode: LEDGER.DELIVERY, ...where } }),
    db.ledgerEntry.findMany({ where: { accountCode: LEDGER.VAT_PAYABLE, ...where } }),
    db.ledgerEntry.findMany({ where: { accountCode: LEDGER.REFUND_EXPENSE, ...where } }),
    db.order.count({ where }),
    db.payoutRequest.aggregate({ where: { status: { in: ['Requested', 'Processing'] } }, _count: { _all: true }, _sum: { amountETB: true } }),
  ])
  const firstParty = await db.ledgerEntry.findMany({ where: { accountCode: LEDGER.FIRST_PARTY, ...where } })
  return json({
    grossSales: r2(cash.reduce((a, e) => a + e.debitETB - e.creditETB, 0)), // cash is a debit account
    platformIncome: r2(sumLedger(commission) + sumLedger(delivery) + sumLedger(firstParty)),
    vendorIncome: r2(sumLedger(await db.ledgerEntry.findMany({ where: { accountCode: LEDGER.VENDOR_PAYABLE, ...where } }))),
    commissionEarned: sumLedger(commission),
    deliveryRevenue: sumLedger(delivery),
    vatCollected: sumLedger(vat),
    refundTotal: r2(refunds.reduce((a, e) => a + e.debitETB, 0)),
    ordersCount: orders,
    pendingPayoutCount: payouts._count._all,
    pendingPayoutAmount: r2(payouts._sum.amountETB ?? 0),
  })
}

export async function reportRevenueOverTime(req: NextRequest) {
  await requireRole('Admin')
  const days = Math.min(90, Math.max(7, parseInt(req.nextUrl.searchParams.get('days') ?? '30', 10)))
  const from = new Date(Date.now() - days * 864e5)
  const entries = await db.ledgerEntry.findMany({
    where: { createdAt: { gte: from }, accountCode: { in: [LEDGER.CASH, LEDGER.VENDOR_PAYABLE, LEDGER.COMMISSION, LEDGER.DELIVERY, LEDGER.FIRST_PARTY] }, orderId: { not: null } },
  })
  const labels: string[] = []
  const gross: number[] = [], platform: number[] = [], vendor: number[] = []
  for (let d = days - 1; d >= 0; d--) {
    const day = new Date(Date.now() - d * 864e5)
    const key = day.toISOString().slice(5, 10)
    labels.push(key)
    const dayEntries = entries.filter(e => e.createdAt.toISOString().slice(5, 10) === key)
    gross.push(r2(dayEntries.filter(e => e.accountCode === LEDGER.CASH).reduce((a, e) => a + e.debitETB, 0)))
    platform.push(r2(dayEntries.filter(e => [LEDGER.COMMISSION, LEDGER.DELIVERY, LEDGER.FIRST_PARTY].includes(e.accountCode as never)).reduce((a, e) => a + e.creditETB, 0)))
    vendor.push(r2(dayEntries.filter(e => e.accountCode === LEDGER.VENDOR_PAYABLE).reduce((a, e) => a + e.creditETB, 0)))
  }
  return json({ labels, series: [{ name: 'Gross Sales', data: gross }, { name: 'Platform Income', data: platform }, { name: 'Vendor Income', data: vendor }] })
}

export async function reportTopVendors(req: NextRequest) {
  await requireRole('Admin')
  const limit = Math.min(20, parseInt(req.nextUrl.searchParams.get('limit') ?? '5', 10))
  const entries = await db.ledgerEntry.findMany({ where: { accountCode: LEDGER.VENDOR_PAYABLE, vendorId: { not: null } } })
  const byVendor = new Map<string, number>()
  for (const e of entries) if (e.vendorId) byVendor.set(e.vendorId, r2((byVendor.get(e.vendorId) ?? 0) + e.creditETB - e.debitETB))
  const vendors = await db.vendorProfile.findMany()
  const items = [...byVendor.entries()]
    .sort((a, b) => b[1] - a[1]).slice(0, limit)
    .map(([vendorId, incomeETB]) => ({
      vendorId, incomeETB,
      businessName: vendors.find(v => v.id === vendorId)?.businessName ?? 'Unknown Vendor',
      city: vendors.find(v => v.id === vendorId)?.city ?? '',
    }))
  return json({ items })
}

export async function reportTopProducts(req: NextRequest) {
  await requireRole('Admin')
  const limit = Math.min(20, parseInt(req.nextUrl.searchParams.get('limit') ?? '5', 10))
  const paidOrders = await db.order.findMany({ where: { status: { notIn: ['Placed', 'Cancelled'] } }, select: { id: true } })
  const items = await db.orderItem.findMany({ where: { orderId: { in: paidOrders.map(o => o.id) } } })
  const byProduct = new Map<string, { qty: number; revenue: number }>()
  for (const i of items) {
    const cur = byProduct.get(i.productId) ?? { qty: 0, revenue: 0 }
    cur.qty += i.quantity
    cur.revenue = r2(cur.revenue + i.lineNetETB)
    byProduct.set(i.productId, cur)
  }
  const products = await db.product.findMany({ where: { id: { in: [...byProduct.keys()] } } })
  const rows = [...byProduct.entries()]
    .sort((a, b) => b[1].revenue - a[1].revenue).slice(0, limit)
    .map(([productId, v]) => ({ productId, title: products.find(p => p.id === productId)?.title ?? 'Removed product', soldQty: v.qty, revenueETB: v.revenue }))
  return json({ items: rows })
}

export async function reportVendorIncome(req: NextRequest, vendorId: string) {
  await requireRole('Admin')
  const [balance, paidOut, pending] = await Promise.all([
    db.ledgerEntry.aggregate({ where: { accountCode: LEDGER.VENDOR_PAYABLE, vendorId }, _sum: { creditETB: true, debitETB: true } }),
    db.payoutRequest.aggregate({ where: { vendorId, status: 'Paid' }, _sum: { amountETB: true } }),
    db.payoutRequest.aggregate({ where: { vendorId, status: { in: ['Requested', 'Processing'] } }, _sum: { amountETB: true } }),
  ])
  const balanceETB = r2((balance._sum.creditETB ?? 0) - (balance._sum.debitETB ?? 0))
  return json({
    vendorId, balanceETB,
    availableForPayout: r2(balanceETB - (pending._sum.amountETB ?? 0)),
    totalPaidOut: r2(paidOut._sum.amountETB ?? 0),
    pendingPayout: r2(pending._sum.amountETB ?? 0),
  })
}

// ---------------- ADMIN · REFUND DECISION ----------------
export async function adminRefundDecision(req: NextRequest, orderId: string) {
  await requireRole('Admin')
  const { approve, note } = await req.json()
  const order = await db.order.findUnique({ where: { id: orderId }, include: { items: true } })
  if (!order) return bad('Order not found', 404)
  if (order.status !== 'RefundRequested') return bad(`Order is ${order.status} — no pending refund request`)
  if (approve) {
    // balanced reversal legs (mirrors Finance.LedgerPoster.PostRefundAsync)
    const rows = await db.ledgerEntry.findMany({ where: { orderId, accountCode: { in: ['2000', '3200', '3000'] } } })
    const net = (code: string) => r2(rows.filter(r => r.accountCode === code).reduce((a, r) => a + r.creditETB - r.debitETB, 0))
    const amount = order.grandTotalETB
    const ratio = amount / order.grandTotalETB
    const legs = [
      { accountCode: '1000', orderId, creditETB: amount, description: 'Refund to buyer' },
      { accountCode: '2100', orderId, debitETB: r2(order.vatETB * ratio), description: 'Reverse VAT' },
      { accountCode: '3100', orderId, debitETB: r2(order.deliveryFeeETB * ratio), description: 'Reverse delivery revenue' },
      { accountCode: '3000', orderId, debitETB: r2(net('3000') * ratio), description: 'Reverse commission' },
      { accountCode: '2000', orderId, debitETB: r2(net('2000') * ratio), description: 'Reverse vendor payable' },
      ...(net('3200') > 0.009 ? [{ accountCode: '3200', orderId, debitETB: r2(net('3200') * ratio), description: 'Reverse first-party revenue' }] : []),
      { accountCode: '4000', orderId, debitETB: amount, description: 'Refund expense (buyer refund)' },
      { accountCode: '1000', orderId, creditETB: amount, description: 'Refund payout from PSP' },
    ]
    const dr = r2(legs.reduce((a, l) => a + (l.debitETB ?? 0), 0))
    const cr = r2(legs.reduce((a, l) => a + (l.creditETB ?? 0), 0))
    if (Math.abs(dr - cr) > 0.01) return bad(`Refund ledger would be unbalanced (DR ${dr} vs CR ${cr}) — operation aborted`, 500)
    await db.ledgerEntry.createMany({ data: legs })
    const updated = await db.order.update({
      where: { id: orderId }, data: { status: 'Refunded', refundReason: order.refundReason },
      include: { items: true },
    })
    await db.orderItem.updateMany({ where: { orderId }, data: { refundStatus: 'Approved' } })
    await db.orderEvent.create({ data: { orderId, status: 'Refunded', byRole: 'Admin', note: note ?? 'Refund approved after inspection' } })
    const buyerUser = await db.user.findUnique({ where: { id: order.buyerId } })
    await dispatch('ReturnDecided', buyerUser, {
      subject: 'Refund approved ✓',
      body: `Your refund of ${amount} Br for order ${order.number} was approved. It will arrive in your original payment method within 5 business days.${note ? ` Note: ${note}` : ''}`,
    })
    return json({ ok: true, status: 'Refunded', order: updated })
  }
  const restored = order.previousStatus && canTransition(order.previousStatus, order.status) === false ? order.previousStatus : 'Completed'
  const updated = await db.order.update({ where: { id: orderId }, data: { status: restored, refundReason: null }, include: { items: true } })
  await db.orderItem.updateMany({ where: { orderId }, data: { refundStatus: 'Rejected' } })
  await db.orderEvent.create({ data: { orderId, status: restored, byRole: 'Admin', note: `Refund request rejected${note ? ` — ${note}` : ''}` } })
  await notify(order.buyerId, 'RefundDecided', 'Refund request rejected', `Your refund request for order ${order.number} was rejected after inspection.${note ? ` Reason: ${note}` : ''} You may escalate within 7 days per the Return & Refund Policy.`)
  return json({ ok: true, status: restored, order: updated })
}

// ---------------- ADMIN · VENDOR APPROVAL ----------------
export async function adminListVendors(req: NextRequest) {
  await requireRole('Admin')
  const status = req.nextUrl.searchParams.get('status') ?? undefined
  const vendors = await db.vendorProfile.findMany({
    where: status ? { status } : undefined,
    include: { owner: { include: { documents: { include: { type: true } } } }, _count: { select: { products: true } } },
    orderBy: { createdAt: 'asc' },
  })
  return json({ items: vendors })
}

export async function adminReviewVendor(req: NextRequest, vendorId: string) {
  await requireRole('Admin')
  const { approve, rejectionReason } = await req.json()
  const profile = await db.vendorProfile.findUnique({ where: { id: vendorId }, include: { owner: true } })
  if (!profile) return bad('Vendor not found', 404)
  if (approve) {
    if (profile.status === 'Approved') return bad('Vendor is already approved')
    await db.vendorProfile.update({ where: { id: vendorId }, data: { status: 'Approved', reviewedAt: new Date() } })
    await db.document.updateMany({ where: { ownerId: vendorId, status: 'Pending' }, data: { status: 'Verified', verifiedAt: new Date() } })
    await dispatch('VendorApproved', { id: vendorId, phone: profile.owner?.phone, email: profile.owner?.email, fullName: profile.owner?.fullName }, {
      subject: 'Application approved 🎉',
      body: `Congratulations! ${profile.businessName} is now an approved MarketHub vendor. You can start listing products.`,
    })
    return json({ ok: true, status: 'Approved' })
  }
  if (!rejectionReason || rejectionReason.trim().length < 5) return bad('A rejection reason is required')
  await db.vendorProfile.update({ where: { id: vendorId }, data: { status: 'Rejected', rejectionReason, reviewedAt: new Date() } })
  await notify(vendorId, 'VendorRejected', 'Application rejected', `Your vendor application was rejected: ${rejectionReason}. You may correct your documents and re-apply.`)
  return json({ ok: true, status: 'Rejected' })
}

// ---------------- ADMIN · PRODUCT MODERATION ----------------
export async function adminListProducts(req: NextRequest) {
  await requireRole('Admin')
  const status = req.nextUrl.searchParams.get('status') ?? undefined
  const products = await db.product.findMany({
    where: status ? { status } : undefined,
    include: {
      category: true, vendor: { select: { businessName: true } },
      gallery: { orderBy: { sortOrder: 'asc' }, take: 1 },
      colors: { orderBy: { sortOrder: 'asc' } },
      _count: { select: { gallery: true } },
    },
    orderBy: { createdAt: 'desc' },
  })
  return json({ items: products.map(p => ({ ...p, photoCount: p._count.gallery })) })
}

export async function adminModerateProduct(req: NextRequest, productId: string) {
  await requireRole('Admin')
  const { approve, note } = await req.json()
  const product = await db.product.findUnique({ where: { id: productId } })
  if (!product) return bad('Product not found', 404)
  if (product.vendorId && product.status !== 'PendingReview') return bad('Only pending products require moderation')
  const updated = await db.product.update({
    where: { id: productId },
    data: { status: approve ? 'Active' : 'Rejected', moderationNote: note ?? null },
  })
  if (product.vendorId) await notify(product.vendorId, approve ? 'ProductApproved' : 'ProductRejected', approve ? 'Product approved' : 'Product rejected', approve ? `"${product.title}" is now live on the marketplace.` : `"${product.title}" was rejected. ${note ?? ''}`)
  return json({ product: updated })
}

// ---------------- ADMIN · ORDERS / PAYOUTS / SETTINGS ----------------
export async function adminListOrders(req: NextRequest) {
  await requireRole('Admin')
  const status = req.nextUrl.searchParams.get('status') ?? undefined
  const orders = await db.order.findMany({
    where: status ? { status } : undefined,
    orderBy: { createdAt: 'desc' }, include: { items: true, payments: true },
    take: 100,
  })
  return json({ items: orders })
}

export async function adminListPayouts() {
  await requireRole('Admin')
  const slaHours = parseInt(await getSetting('PayoutSlaHours', '12'), 10)
  const [payouts, vendors] = await Promise.all([
    db.payoutRequest.findMany({ orderBy: { requestedAt: 'asc' } }),
    db.vendorProfile.findMany(),
  ])
  return json({
    items: payouts.map(p => ({
      ...p,
      vendorName: vendors.find(v => v.id === p.vendorId)?.businessName ?? 'Unknown',
      slaHours,
      overdue: ['Requested', 'Processing'].includes(p.status) && p.dueBy.getTime() < Date.now(),
    })),
  })
}

export async function adminProcessPayout(req: NextRequest, payoutId: string) {
  const admin = await requireRole('Admin')
  const { approve, adminNote } = await req.json()
  const payout = await db.payoutRequest.findUnique({ where: { id: payoutId } })
  if (!payout) return bad('Payout request not found', 404)
  if (!['Requested', 'Processing'].includes(payout.status)) return bad(`Payout is already ${payout.status}`)
  if (approve) {
    await db.payoutRequest.update({ where: { id: payoutId }, data: { status: 'Paid', processedAt: new Date(), processedBy: admin.id, adminNote: adminNote ?? null } })
    const vendorUser = await db.user.findUnique({ where: { id: payout.vendorId } })
    await dispatch('PayoutProcessed', vendorUser, {
      subject: 'Payout sent ✓',
      body: `Your payout of ${payout.amountETB} Br has been sent to ${payout.paymentMethod} ${payout.destinationAccount}.`,
    })
    return json({ ok: true, status: 'Paid' })
  }
  await db.payoutRequest.update({ where: { id: payoutId }, data: { status: 'Rejected', processedAt: new Date(), processedBy: admin.id, adminNote: adminNote ?? null } })
  await notify(payout.vendorId, 'PayoutProcessed', 'Payout request rejected', adminNote ?? 'Contact support for details. The amount is back in your available balance.')
  return json({ ok: true, status: 'Rejected' })
}

export async function getCommission() {
  await requireRole('Admin')
  const rate = await getSetting('DefaultCommissionRate', '0.10')
  const vendors = await db.vendorProfile.findMany({ where: { status: 'Approved' }, select: { id: true, businessName: true } })
  const overrides = await db.settings.findUnique({ where: { key: 'CommissionOverrides' } })
  return json({ rate: parseFloat(rate), slaHours: await getSetting('PayoutSlaHours', '12'), vendors, overrides: overrides ? JSON.parse(overrides.value) : [] })
}

export async function putCommission(req: NextRequest) {
  await requireRole('Admin')
  const { rate } = await req.json()
  const r = parseFloat(rate)
  if (isNaN(r) || r < 0 || r > 0.5) return bad('Commission rate must be between 0 and 0.5')
  await db.settings.upsert({ where: { key: 'DefaultCommissionRate' }, update: { value: String(r) }, create: { key: 'DefaultCommissionRate', value: String(r), description: 'Platform commission on vendor sales' } })
  return json({ ok: true, rate: r })
}

// ---------------- VENDOR CONSOLE ----------------
export async function vendorEarnings() {
  const vendor = await requireRole('Vendor')
  const [ledger, pending, paidOut] = await Promise.all([
    db.ledgerEntry.findMany({ where: { accountCode: LEDGER.VENDOR_PAYABLE, vendorId: vendor.id } }),
    db.payoutRequest.aggregate({ where: { vendorId: vendor.id, status: { in: ['Requested', 'Processing'] } }, _sum: { amountETB: true } }),
    db.payoutRequest.aggregate({ where: { vendorId: vendor.id, status: 'Paid' }, _sum: { amountETB: true } }),
  ])
  const balance = r2(ledger.reduce((a, e) => a + e.creditETB - e.debitETB, 0))
  const pendingAmt = r2(pending._sum.amountETB ?? 0)
  const monthly = new Map<string, number>()
  for (const e of ledger) {
    const key = e.createdAt.toISOString().slice(0, 7)
    monthly.set(key, r2((monthly.get(key) ?? 0) + e.creditETB - e.debitETB))
  }
  return json({
    balanceETB: balance,
    availableForPayout: r2(balance - pendingAmt),
    pendingPayout: pendingAmt,
    totalPaidOut: r2(paidOut._sum.amountETB ?? 0),
    slaHours: parseInt(await getSetting('PayoutSlaHours', '12'), 10),
    monthly: [...monthly.entries()].sort().map(([month, amount]) => ({ month, amount })),
  })
}

export async function vendorRequestPayout(req: NextRequest) {
  const vendor = await requireRole('Vendor')
  const { amount, paymentMethod, destinationAccount } = await req.json()
  const amt = parseFloat(amount)
  if (isNaN(amt) || amt < 100) return bad('Minimum payout amount is 100 Br')
  const ledger = await db.ledgerEntry.findMany({ where: { accountCode: LEDGER.VENDOR_PAYABLE, vendorId: vendor.id } })
  const balance = r2(ledger.reduce((a, e) => a + e.creditETB - e.debitETB, 0))
  const pending = await db.payoutRequest.aggregate({ where: { vendorId: vendor.id, status: { in: ['Requested', 'Processing'] } }, _sum: { amountETB: true } })
  const available = r2(balance - (pending._sum.amountETB ?? 0))
  if (amt > available) return bad(`Amount exceeds your available balance (${available} Br)`)
  const slaHours = parseInt(await getSetting('PayoutSlaHours', '12'), 10)
  const payout = await db.payoutRequest.create({
    data: {
      vendorId: vendor.id, amountETB: r2(amt), status: 'Requested',
      dueBy: new Date(Date.now() + slaHours * 36e5),
      paymentMethod: paymentMethod === 'BankTransfer' ? 'BankTransfer' : 'Telebirr',
      destinationAccount: destinationAccount ?? vendor.id,
    },
  })
  await notify('u_admin', 'PayoutRequested', 'Vendor payout request', `${vendor.vendorProfile?.businessName} requested ${amt} Br — process within ${slaHours}h.`)
  return json({ payout }, 201)
}

export async function vendorPayouts() {
  const vendor = await requireRole('Vendor')
  const slaHours = parseInt(await getSetting('PayoutSlaHours', '12'), 10)
  const payouts = await db.payoutRequest.findMany({ where: { vendorId: vendor.id }, orderBy: { requestedAt: 'desc' } })
  return json({ items: payouts.map(p => ({ ...p, slaHours, overdue: ['Requested', 'Processing'].includes(p.status) && p.dueBy.getTime() < Date.now() })) })
}

export async function vendorOrders() {
  const vendor = await requireRole('Vendor')
  const orders = await db.order.findMany({
    where: { items: { some: { vendorId: vendor.id } } },
    orderBy: { createdAt: 'desc' },
    include: { items: true, payments: true, events: { orderBy: { createdAt: 'asc' as const } } },
  })
  return json({ items: orders.map(o => ({ ...o, items: o.items.filter(i => i.vendorId === vendor.id) })) })
}

export async function vendorOrderAction(req: NextRequest, orderId: string) {
  const vendor = await requireRole('Vendor')
  const { action, trackingCode, pin } = await req.json()
  const order = await db.order.findUnique({ where: { id: orderId }, include: { items: true } })
  if (!order || !order.items.some(i => i.vendorId === vendor.id)) return bad('Order not found', 404)
  const map: Record<string, { to: string; from: string }> = {
    accept: { to: 'VendorAccepted', from: 'PaymentConfirmed' },
    reject: { to: 'RefundRequested', from: 'PaymentConfirmed' },
    ship: { to: 'Shipped', from: 'VendorAccepted' },
    packed: { to: 'Shipped', from: 'VendorAccepted' }, // packed == hand-off with tracking
    deliver: { to: 'Delivered', from: 'Shipped' },
  }
  const t = map[action]
  if (!t) return bad('Unknown action')
  if (order.status !== t.from) return bad(`Order is ${order.status} — cannot ${action} now`)
  if (action === 'ship' || action === 'packed') {
    if (!trackingCode || !String(trackingCode).trim()) return bad('A trackingCode is required to ship the order (MH-TRK-…)')
  }
  if (action === 'deliver') {
    if (!pin || String(pin) !== order.deliveryPin) return bad('Invalid delivery PIN — ask the buyer for the 4-digit PIN before marking delivered')
  }
  const updated = await db.order.update({
    where: { id: orderId },
    data: {
      status: t.to,
      ...(t.to === 'Delivered' ? { deliveredAt: new Date() } : {}),
      ...(action === 'ship' || action === 'packed' ? { trackingCode: String(trackingCode).trim().toUpperCase() } : {}),
      ...(action === 'reject' ? { refundReason: 'Vendor rejected the order — full refund initiated' } : {}),
    },
    include: { items: true },
  })
  if (action === 'reject') {
    await db.orderItem.updateMany({ where: { orderId }, data: { refundStatus: 'Requested' } })
    await db.orderEvent.create({ data: { orderId, status: t.to, byRole: 'Vendor', note: 'Vendor rejected the order — refund initiated' } })
    await notify(order.buyerId, 'OrderRejected', 'Vendor cannot fulfil your order', `Order ${order.number} was rejected by the vendor. A full refund will be processed.`)
    return json({ order: updated })
  }
  await db.orderEvent.create({
    data: {
      orderId, status: t.to, byRole: 'Vendor',
      note: action === 'accept' ? 'Vendor accepted the order'
        : action === 'deliver' ? 'Delivered — PIN verified'
        : `Shipped — tracking ${updated.trackingCode}`,
    },
  })
  const buyerUser = await db.user.findUnique({ where: { id: order.buyerId } })
  if (t.to === 'Shipped') {
    await dispatch('OrderShipped', buyerUser, {
      subject: 'Order shipped',
      body: `Order ${order.number} is on the way. Tracking code: ${updated.trackingCode}. Have your delivery PIN ready.`,
    })
  }
  if (t.to === 'Delivered') {
    await dispatch('OrderDelivered', buyerUser, {
      subject: 'Order delivered',
      body: `Order ${order.number} was delivered. Enjoy — and you can return it within 7 days if anything is wrong.`,
    })
  }
  return json({ order: updated })
}

export async function vendorProducts() {
  const vendor = await requireRole('Vendor')
  const products = await db.product.findMany({
    where: { vendorId: vendor.id },
    orderBy: { createdAt: 'desc' },
    include: {
      gallery: { orderBy: { sortOrder: 'asc' }, take: 1 },
      colors: { orderBy: { sortOrder: 'asc' } },
    },
  })
  return json({ items: products })
}
