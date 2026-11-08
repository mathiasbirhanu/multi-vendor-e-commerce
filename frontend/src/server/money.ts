// Shared money/tax helpers for the preview server.
// Mirrors the .NET VatCalculator / CommissionResolver / LedgerPoster logic exactly.
export const r2 = (x: number) => Math.round(x * 100) / 100

/** VAT = rate% of (subtotal + delivery), rounded 2dp away from zero — mirrors Sales.Pricing.VatCalculator */
export const calculateVat = (subtotal: number, deliveryFee: number, ratePercent: number) =>
  r2((subtotal + deliveryFee) * ratePercent / 100)

/** Commission resolution precedence: vendor → category → global (mirrors Finance.Services.CommissionResolver) */
export const resolveCommission = (opts: { vendorRate?: number | null; categoryRate?: number | null; globalRate: number }) =>
  opts.vendorRate ?? opts.categoryRate ?? opts.globalRate

export const LEDGER = {
  CASH: '1000', VENDOR_PAYABLE: '2000', VAT_PAYABLE: '2100',
  COMMISSION: '3000', DELIVERY: '3100', FIRST_PARTY: '3200', REFUND_EXPENSE: '4000',
} as const

export type Leg = { accountCode: string; orderId: string; vendorId?: string | null; debitETB?: number; creditETB?: number; description: string }

/** Double-entry ledger posting with hard balance assertion (mirrors Finance.Services.LedgerPoster) */
export function assertBalanced(legs: Leg[]) {
  const dr = r2(legs.reduce((a, l) => a + (l.debitETB ?? 0), 0))
  const cr = r2(legs.reduce((a, l) => a + (l.creditETB ?? 0), 0))
  if (Math.abs(dr - cr) > 0.001) throw new Error(`Ledger entry unbalanced: DR ${dr} vs CR ${cr}`)
  return legs
}

/** Post payment confirmation for an order — returns balanced legs (DR Cash; CR VAT/Delivery/Commission/Vendor/First-party) */
export function paymentLegs(o: {
  id: string; vatETB: number; deliveryFeeETB: number
  items: { vendorId: string | null; commissionETB: number; lineNetETB: number }[]
}) {
  const grand = r2(o.items.reduce((a, i) => a + i.lineNetETB, 0) + o.deliveryFeeETB + o.vatETB)
  const commission = r2(o.items.reduce((a, i) => a + i.commissionETB, 0))
  const legs: Leg[] = [
    { accountCode: LEDGER.CASH, orderId: o.id, debitETB: grand, description: 'Order payment received' },
    { accountCode: LEDGER.VAT_PAYABLE, orderId: o.id, creditETB: o.vatETB, description: 'VAT payable on order' },
    { accountCode: LEDGER.DELIVERY, orderId: o.id, creditETB: o.deliveryFeeETB, description: 'Delivery revenue' },
  ]
  if (commission > 0) legs.push({ accountCode: LEDGER.COMMISSION, orderId: o.id, creditETB: commission, description: 'Platform commission' })
  const vendorBucket = new Map<string, number>()
  let platformNet = 0
  for (const i of o.items) {
    const net = i.lineNetETB - i.commissionETB
    if (i.vendorId) vendorBucket.set(i.vendorId, (vendorBucket.get(i.vendorId) ?? 0) + net)
    else platformNet += net
  }
  const extra: Leg[] = []
  for (const [v, amt] of vendorBucket) extra.push({ accountCode: LEDGER.VENDOR_PAYABLE, orderId: o.id, vendorId: v, creditETB: r2(amt), description: 'Vendor payable' })
  if (platformNet > 0.009) extra.push({ accountCode: LEDGER.FIRST_PARTY, orderId: o.id, creditETB: r2(platformNet), description: 'First-party sales revenue' })
  // rounding absorb on the last credit leg so total credits == grandTotal (base legs counted once)
  const extraSum = r2(extra.filter(l => (l.creditETB ?? 0) > 0).reduce((a, l) => a + (l.creditETB ?? 0), 0))
  const creditsSoFar = r2(o.vatETB + o.deliveryFeeETB + commission + extraSum)
  const diff = r2(grand - creditsSoFar)
  if (Math.abs(diff) > 0.001 && extra.length) {
    const last = extra[extra.length - 1]
    last.creditETB = r2((last.creditETB ?? 0) + diff)
  }
  legs.push(...extra)
  return assertBalanced(legs)
}

/** Order state machine — mirrors Sales.Domain.OrderStateMachine */
export const ORDER_TRANSITIONS: Record<string, string[]> = {
  Placed: ['PaymentConfirmed', 'Cancelled'],
  PaymentConfirmed: ['VendorAccepted', 'RefundRequested', 'Disputed'],
  VendorAccepted: ['Shipped', 'RefundRequested', 'Disputed'],
  Shipped: ['Delivered', 'Disputed'],
  Delivered: ['Completed', 'RefundRequested', 'Disputed'],
  Completed: ['RefundRequested'],
  RefundRequested: ['Refunded', 'Disputed'],
  Cancelled: [], Refunded: [], Disputed: [],
}
export const canTransition = (from: string, to: string) => (ORDER_TRANSITIONS[from] ?? []).includes(to)
