/**
 * MarketHub preview seed — realistic Ethiopian marketplace data.
 * Money: ETB, VAT 15%, delivery zones = Ethiopian regions.
 * Run: bun prisma/seed.ts
 */
import { PrismaClient } from '@prisma/client'
import { GALLERY } from './gallery-data'

const db = new PrismaClient()

// deterministic RNG for reproducible data
let s = 42
const rnd = () => { s = (s * 1103515245 + 12345) % 2147483648; return s / 2147483648 }
const pick = <T,>(arr: T[]): T => arr[Math.floor(rnd() * arr.length)]
const ri = (min: number, max: number) => Math.floor(rnd() * (max - min + 1)) + min
const r2 = (x: number) => Math.round(x * 100) / 100

const GRAD = [
  ['#0f172a', '#1d4ed8'], ['#3b0764', '#a21caf'], ['#052e16', '#15803d'],
  ['#431407', '#c2410c'], ['#0c4a6e', '#0284c7'], ['#4a044e', '#be123c'],
  ['#1e1b4b', '#4f46e5'], ['#14532d', '#65a30d'], ['#713f12', '#ca8a04'],
]
const grad = (i: number) => JSON.stringify({ from: GRAD[i % GRAD.length][0], to: GRAD[i % GRAD.length][1] })

const POLICY_TERMS = `# Terms of Service — MarketHub Ethiopia

**Effective date:** 1 November 2026 · **Version:** 1.0.0

## 1. About these terms
MarketHub Technologies PLC ("MarketHub", "we", "us") operates an electronic marketplace that connects buyers with independent vendors and with our own first-party store. By creating an account you agree to these Terms of Service, the Rules & Regulations, and the Return & Refund Policy.

## 2. Accounts and roles
You must provide accurate identification information when registering. Buyers must supply a valid government identification document when requested. Vendors must upload a valid business registration license, trade license and Tax Identification Number (TIN) certificate before their store can be activated. You are responsible for all activity under your account and must keep your password confidential.

## 3. The role of MarketHub
Unless we are the seller of record (first-party products marked "MarketHub Store"), MarketHub acts as an agent and technology platform only. Contracts of sale are formed between you and the vendor. We process payments in Ethiopian Birr (ETB), hold vendor settlements, charge commission on vendor sales, and operate the dispute and refund process described in the Return & Refund Policy.

## 4. Pricing and taxes
All prices are displayed and charged in ETB. Value Added Tax at the rate published in platform settings (currently 15%) is added at checkout on goods and delivery fees. Delivery fees are calculated per zone and shown before you confirm payment.

## 5. Prohibited uses
You may not use the platform for unlawful trade, payment fraud, or abuse of our systems. Attempting to interfere with platform security controls is grounds for immediate suspension and referral to Ethiopian authorities.

## 6. Liability
We provide the platform "as is". To the maximum extent permitted by Ethiopian law, our aggregate liability to you is limited to the value of the order giving rise to the claim. Nothing limits liability for fraud or willful misconduct.

## 7. Governing law
These terms are governed by the laws of the Federal Democratic Republic of Ethiopia. Disputes are subject to the jurisdiction of the Ethiopian courts.`

const POLICY_RULES = `# Rules & Regulations — MarketHub Ethiopia

**Effective date:** 1 November 2026 · **Version:** 1.0.0

## 1. Vendor admission
Vendors must submit a business registration license, trade license and TIN certificate (at least three documents). All documents must be genuine and legible; forged documents result in permanent rejection and may be reported to the Ministry of Trade and Regional Integration. The MarketHub review team verifies every application before a store is activated.

## 2. Listing rules
- Listings must have truthful titles, descriptions, prices and stock levels.
- Prohibited items include counterfeit goods, weapons, narcotics, protected wildlife products, and any item whose sale is restricted under Ethiopian law.
- Prices must be in ETB; platform commission is charged transparently and reported in the vendor console.

## 3. Commission and payouts
MarketHub charges a commission on vendor sales. The default commission rate is shown in platform settings and may vary by vendor or category. Vendors may request withdrawal of their available balance at any time; **payout requests are processed within 12 hours of submission**. Vendor income, commission and payout history are always visible in the vendor console.

## 4. Order handling
Vendors must accept or reject new orders within 24 hours of payment confirmation, ship promptly, and mark orders delivered. Repeated late acceptance or cancellation may reduce store visibility or lead to suspension.

## 5. Conduct
Honest communication with buyers is required. Manipulating reviews, fake orders, or off-platform diversion of buyers is prohibited. Accounts that breach these rules may be suspended with notice and reason.

## 6. Changes
We may update these rules; material changes are announced on the platform and require re-acceptance by affected users.`

const POLICY_RETURN = `# Return & Refund Policy — MarketHub Ethiopia

**Effective date:** 1 November 2026 · **Version:** 1.0.0

## 1. Return window
You may request a return or refund within **7 days of delivery**. Items must be unused, complete with all accessories, and in their original packaging. Products with hygiene seals must have the seal intact.

## 2. How to request
Open "My Orders", select the order, and choose "Request Return/Refund", then state the reason. Pre-delivery cancellations are accepted while the order has not shipped.

## 3. Inspection and decision
MarketHub or the vendor inspects the returned item within 2 business days of receipt. If the item passes inspection the refund is approved; if the item is used or damaged by the buyer the refund may be partially or fully rejected with a written reason.

## 4. Refund method and timing
Approved refunds are returned to the **original payment method** (telebirr, card, or bank account) within **5 business days** of approval. Refunds are issued in ETB and include the item price and applicable VAT.

## 5. Delivery fees
Delivery fees are non-refundable unless the return is due to seller fault (wrong, damaged, or counterfeit item delivered), in which case both delivery legs are refunded.

## 6. Non-returnable items
Perishable groceries, custom-made products, and downloaded services cannot be returned unless faulty.

## 7. Disputes
If you disagree with a refund decision you may escalate to MarketHub support within 7 days; our dispute team reviews the full order record and issues a final decision.`

type Leg = { accountCode: string; orderId?: string; vendorId?: string; debitETB?: number; creditETB?: number; description: string }
const legs: Leg[] = []
function postPaidOrder(o: { id: string; vatETB: number; deliveryFeeETB: number; grandTotalETB: number; items: { vendorId: string | null; commissionETB: number; lineNetETB: number }[] }, date: Date) {
  legs.push({ accountCode: '1000', orderId: o.id, debitETB: o.grandTotalETB, description: `Order payment received` })
  legs.push({ accountCode: '2100', orderId: o.id, creditETB: o.vatETB, description: `VAT payable on order` })
  legs.push({ accountCode: '3100', orderId: o.id, creditETB: o.deliveryFeeETB, description: `Delivery revenue` })
  const commission = r2(o.items.reduce((a, i) => a + i.commissionETB, 0))
  if (commission > 0) legs.push({ accountCode: '3000', orderId: o.id, creditETB: commission, description: `Platform commission` })
  // vendor payable = Σ(lineNet - commission) per vendor; platform margin → 3200 First-Party Sales Revenue
  const vendorBucket = new Map<string, number>()
  let platformNet = 0
  for (const i of o.items) {
    const net = i.lineNetETB - i.commissionETB
    if (i.vendorId) vendorBucket.set(i.vendorId, (vendorBucket.get(i.vendorId) ?? 0) + net)
    else platformNet += net
  }
  const entries: Leg[] = [...vendorBucket.entries()].map(([v, amt]) => ({ accountCode: '2000', orderId: o.id, vendorId: v, creditETB: r2(amt), description: `Vendor payable` }))
  if (platformNet > 0.009) entries.push({ accountCode: '3200', orderId: o.id, creditETB: r2(platformNet), description: `First-party sales revenue` })
  // rounding absorb on the last credit leg so total credits == grandTotal
  const creditsSoFar = r2(o.vatETB + o.deliveryFeeETB + commission + entries.reduce((a, e) => a + (e.creditETB ?? 0), 0))
  const diff = r2(o.grandTotalETB - creditsSoFar)
  if (Math.abs(diff) > 0.001 && entries.length) entries[entries.length - 1].creditETB = r2((entries[entries.length - 1].creditETB ?? 0) + diff)
  legs.push(...entries)
}
function postRefund(o: { id: string; vatETB: number; deliveryFeeETB: number; grandTotalETB: number }, amount: number) {
  const ratio = amount / o.grandTotalETB
  const legSum = (code: string) => legs.filter(l => l.orderId === o.id && l.accountCode === code).reduce((a, l) => a + (l.creditETB ?? 0) - (l.debitETB ?? 0), 0)
  const vendorNetTotal = legSum('2000')
  const platformNetTotal = legSum('3200')
  const commissionTotal = legSum('3000')
  legs.push({ accountCode: '1000', orderId: o.id, creditETB: amount, description: `Refund to buyer` })
  legs.push({ accountCode: '2100', orderId: o.id, debitETB: r2(o.vatETB * ratio), description: `Reverse VAT` })
  legs.push({ accountCode: '3100', orderId: o.id, debitETB: r2(o.deliveryFeeETB * ratio), description: `Reverse delivery revenue` })
  legs.push({ accountCode: '3000', orderId: o.id, debitETB: r2(commissionTotal * ratio), description: `Reverse commission` })
  legs.push({ accountCode: '2000', orderId: o.id, debitETB: r2(vendorNetTotal * ratio), description: `Reverse vendor payable` })
  if (platformNetTotal > 0.009) legs.push({ accountCode: '3200', orderId: o.id, debitETB: r2(platformNetTotal * ratio), description: `Reverse first-party revenue` })
}

async function main() {
  console.log('Seeding MarketHub preview database...')
  // --- gap-fill tables (children first) ---
  await db.outboundMessage.deleteMany()
  await db.couponRedemption.deleteMany()
  await db.coupon.deleteMany()
  await db.returnRequest.deleteMany()
  await db.orderEvent.deleteMany()
  await db.stockReservation.deleteMany()
  await db.cart.deleteMany()
  await db.wishlistItem.deleteMany()
  await db.review.deleteMany()
  await db.address.deleteMany()
  await db.productVariant.deleteMany()
  await db.banner.deleteMany()
  await db.notification.deleteMany()
  await db.ledgerEntry.deleteMany()
  await db.payoutRequest.deleteMany()
  await db.payment.deleteMany()
  await db.orderItem.deleteMany()
  await db.order.deleteMany()
  await db.cartItem.deleteMany()
  await db.consent.deleteMany()
  await db.document.deleteMany()
  await db.product.deleteMany()
  await db.category.deleteMany()
  await db.policy.deleteMany()
  await db.deliveryZone.deleteMany()
  await db.vendorProfile.deleteMany()
  await db.user.deleteMany()
  await db.documentType.deleteMany()
  await db.settings.deleteMany()

  // ---------- Document types ----------
  await db.documentType.createMany({
    data: [
      { code: 'business_license', name: 'Business Registration License', appliesTo: 'Vendor', isRequired: true, minFiles: 1, maxFiles: 2, description: 'Valid business registration certificate from the Ministry of Trade', sortOrder: 1 },
      { code: 'trade_license', name: 'Trade License', appliesTo: 'Vendor', isRequired: true, minFiles: 1, maxFiles: 2, description: 'Current trade license for the current fiscal year', sortOrder: 2 },
      { code: 'tin_certificate', name: 'TIN Certificate', appliesTo: 'Vendor', isRequired: true, minFiles: 1, maxFiles: 2, description: 'Tax Identification Number certificate from Ministry of Revenue', sortOrder: 3 },
      { code: 'owner_id', name: 'Owner Identification', appliesTo: 'Vendor', isRequired: false, minFiles: 0, maxFiles: 2, description: 'ID card or passport of the owner/general manager', sortOrder: 4 },
      { code: 'sector_permit', name: 'Sector Permit (optional)', appliesTo: 'Vendor', isRequired: false, minFiles: 0, maxFiles: 2, description: 'Additional sector-specific permit if applicable', sortOrder: 5 },
      { code: 'government_id', name: 'Government ID', appliesTo: 'Buyer', isRequired: false, minFiles: 0, maxFiles: 2, description: 'Fayda ID, passport or driving license (optional verification)', sortOrder: 6 },
    ],
  })
  const docTypes = await db.documentType.findMany()
  const dtId = (code: string) => docTypes.find(d => d.code === code)!.id

  // ---------- Policies ----------
  for (const [key, md] of [['TermsOfService', POLICY_TERMS], ['RulesAndRegulations', POLICY_RULES], ['ReturnAndRefundPolicy', POLICY_RETURN]] as const) {
    await db.policy.create({ data: { key, version: '1.0.0', contentMarkdown: md } })
  }

  // ---------- Users ----------
  const mkUser = (id: string, email: string, phone: string, password: string, fullName: string, role: string, adminRole: string | null = null) =>
    db.user.create({ data: { id, email, phone, password, fullName, role, adminRole } })
  await mkUser('u_admin', 'admin@markethub.et', '+251911000001', 'Admin@12345', 'Platform Administrator', 'Admin', 'SuperAdmin')
  await mkUser('u_staff', 'support@markethub.et', '+251911000006', 'Admin@12345', 'MarketHub Support Desk', 'Admin', 'Support')
  await mkUser('u_v1', 'vendor1@markethub.et', '+251911000002', 'Vendor@12345', 'Selam Girmay', 'Vendor')
  await mkUser('u_v2', 'vendor2@markethub.et', '+251911000003', 'Vendor@12345', 'Daniel Mekonnen', 'Vendor')
  await mkUser('u_v3', 'gada.garments@markethub.et', '+251911000004', 'Vendor@12345', 'Hanna Tesfaye', 'Vendor')
  await mkUser('u_buyer', 'buyer@markethub.et', '+251921000005', 'Buyer@12345', 'Abebe Kebede', 'Buyer')
  // additional demo buyers (write reviews, wishlists, abandoned carts)
  await mkUser('u_b1', 'meron.haile@markethub.et', '+251921000011', 'Buyer@12345', 'Meron Haile', 'Buyer')
  await mkUser('u_b2', 'yonas.tadesse@markethub.et', '+251921000012', 'Buyer@12345', 'Yonas Tadesse', 'Buyer')
  await mkUser('u_b3', 'genet.alemu@markethub.et', '+251921000013', 'Buyer@12345', 'Genet Alemu', 'Buyer')
  await mkUser('u_b4', 'dawit.bekele@markethub.et', '+251921000014', 'Buyer@12345', 'Dawit Bekele', 'Buyer')
  await mkUser('u_b5', 'hirut.molla@markethub.et', '+251921000015', 'Buyer@12345', 'Hirut Molla', 'Buyer')

  await db.vendorProfile.create({ data: { id: 'u_v1', businessName: 'Sheba Electronics Trading PLC', tin: '0012345678', status: 'Approved', region: 'Addis Ababa', city: 'Addis Ababa', description: 'Authorized electronics and accessories importer.', reviewedAt: new Date(Date.now() - 40 * 864e5), ratingAvg: 4.7, ratingCount: 263 } })
  await db.vendorProfile.create({ data: { id: 'u_v2', businessName: 'Habesha Home & Kitchen', tin: '0087654321', status: 'Approved', region: 'Oromia', city: 'Adama', description: 'Handcrafted Ethiopian kitchenware and organic food products.', reviewedAt: new Date(Date.now() - 35 * 864e5), ratingAvg: 4.8, ratingCount: 190 } })
  await db.vendorProfile.create({ data: { id: 'u_v3', businessName: 'Gada Garments PLC', tin: '0045678912', status: 'PendingReview', region: 'Amhara', city: 'Bahir Dar', description: 'Traditional garment manufacturer expanding online.', ratingAvg: 0 } })

  // Documents: verified for approved vendors, pending for the approval-queue demo
  const mkDoc = async (ownerId: string, code: string, fileName: string, status: 'Pending' | 'Verified' = 'Verified', expiresAt?: Date) =>
    db.document.create({ data: { ownerId, typeId: dtId(code), fileName, contentType: 'application/pdf', sizeBytes: ri(180000, 2400000), status, verifiedAt: status === 'Verified' ? new Date(Date.now() - ri(5, 20) * 864e5) : null, expiresAt: expiresAt ?? (status === 'Verified' ? new Date(Date.now() + ri(120, 600) * 864e5) : null) } })
  for (const v of ['u_v1', 'u_v2']) {
    await mkDoc(v, 'business_license', `${v}-business-registration-license.pdf`)
    await mkDoc(v, 'trade_license', `${v}-trade-license-2026.pdf`)
    await mkDoc(v, 'tin_certificate', `${v}-tin-certificate.pdf`)
    await mkDoc(v, 'owner_id', `${v}-owner-passport.pdf`)
  }
  // one expired license so the admin document-expiry job has a real target
  await mkDoc('u_v1', 'trade_license', 'u_v1-trade-license-2025-EXPIRED.pdf', 'Verified', new Date(Date.now() - 12 * 864e5))
  await mkDoc('u_v3', 'business_license', 'gada-business-license.pdf', 'Pending')
  await mkDoc('u_v3', 'trade_license', 'gada-trade-license.pdf', 'Pending')
  await mkDoc('u_v3', 'tin_certificate', 'gada-tin-certificate.pdf', 'Pending')
  await mkDoc('u_v3', 'owner_id', 'gada-owner-id.pdf', 'Pending')

  // Consents (all users accepted v1.0.0 of all three policies at signup)
  for (const u of ['u_admin', 'u_staff', 'u_v1', 'u_v2', 'u_v3', 'u_buyer', 'u_b1', 'u_b2', 'u_b3', 'u_b4', 'u_b5'])
    for (const k of ['TermsOfService', 'RulesAndRegulations', 'ReturnAndRefundPolicy'])
      await db.consent.create({ data: { userId: u, policyKey: k, policyVersion: '1.0.0', acceptedAt: new Date(Date.now() - ri(30, 45) * 864e5), ipAddress: '196.188.x.x' } })

  // ---------- Categories ----------
  const cats = [
    { id: 'c_electronics', name: 'Electronics', slug: 'electronics' },
    { id: 'c_fashion', name: 'Fashion', slug: 'fashion' },
    { id: 'c_home', name: 'Home & Kitchen', slug: 'home-kitchen' },
    { id: 'c_beauty', name: 'Beauty', slug: 'beauty' },
    { id: 'c_groceries', name: 'Groceries', slug: 'groceries' },
    { id: 'c_baby', name: 'Baby & Toys', slug: 'baby-toys' },
  ]
  for (const [i, c] of cats.entries()) await db.category.create({ data: { ...c, sortOrder: i } })

  // ---------- Products ----------
  type P = { id: string; vendorId: string | null; vendorName: string | null; cat: string; title: string; slug: string; desc: string; price: number; cmp?: number; stock: number; w: number; featured?: boolean; rating?: number; rc?: number }
  const products: P[] = [
    { id: 'p_01', vendorId: null, vendorName: null, cat: 'c_electronics', title: 'Samsung Galaxy A15 128GB', slug: 'samsung-galaxy-a15-128gb', desc: 'Brand-new Samsung Galaxy A15 with 128GB storage, 6.5" Super AMOLED display and 5000mAh battery. 1-year official warranty.', price: 24999, cmp: 27999, stock: 35, w: 0.4, featured: true, rating: 4.8, rc: 214 },
    { id: 'p_02', vendorId: null, vendorName: null, cat: 'c_electronics', title: 'Huawei Y9 Prime (Refurbished)', slug: 'huawei-y9-prime-refurbished', desc: 'Certified refurbished Huawei Y9 Prime, 128GB, pop-up camera. Fully tested with 6-month warranty.', price: 8999, cmp: 10500, stock: 18, w: 0.4, rating: 4.3, rc: 87 },
    { id: 'p_03', vendorId: 'u_v1', vendorName: 'Sheba Electronics Trading PLC', cat: 'c_electronics', title: 'BassPro Bluetooth Speaker', slug: 'basspro-bluetooth-speaker', desc: 'Portable 20W Bluetooth speaker with deep bass, 12-hour battery and IPX6 water resistance.', price: 3200, cmp: 3800, stock: 42, w: 1.2, rating: 4.6, rc: 156 },
    { id: 'p_04', vendorId: 'u_v1', vendorName: 'Sheba Electronics Trading PLC', cat: 'c_electronics', title: 'Fast Charger 33W USB-C', slug: 'fast-charger-33w-usb-c', desc: 'Original 33W SuperCharge USB-C wall charger with cable. Compatible with all major Android phones.', price: 899, stock: 120, w: 0.2, rating: 4.5, rc: 302 },
    { id: 'p_05', vendorId: 'u_v1', vendorName: 'Sheba Electronics Trading PLC', cat: 'c_electronics', title: 'Wireless Earbuds Pro', slug: 'wireless-earbuds-pro', desc: 'Active noise-cancelling TWS earbuds with 28h total playtime and transparent case.', price: 2450, cmp: 2900, stock: 64, w: 0.3, featured: true, rating: 4.4, rc: 198 },
    { id: 'p_06', vendorId: null, vendorName: null, cat: 'c_fashion', title: "Men's Habesha Kemis Cotton Shirt", slug: 'mens-habesha-kemis-cotton-shirt', desc: 'Hand-woven 100% cotton shirt with traditional tibeb embroidery. Made in Addis Ababa.', price: 1850, stock: 50, w: 0.3, rating: 4.7, rc: 64 },
    { id: 'p_07', vendorId: null, vendorName: null, cat: 'c_fashion', title: 'Habesha Kemis Traditional Dress', slug: 'habesha-kemis-traditional-dress', desc: 'Elegant handwoven habesha kemis with full tibeb design, ideal for holidays and weddings.', price: 4500, cmp: 5200, stock: 22, w: 0.8, rating: 4.9, rc: 41 },
    { id: 'p_08', vendorId: null, vendorName: null, cat: 'c_home', title: 'Non-Stick Injera Mitad Pan', slug: 'non-stick-injera-mitad-pan', desc: '56cm electric non-stick mitad for perfect injera at home. 1500W with temperature control.', price: 1250, stock: 30, w: 2.5, rating: 4.5, rc: 118 },
    { id: 'p_09', vendorId: 'u_v2', vendorName: 'Habesha Home & Kitchen', cat: 'c_home', title: 'Traditional Clay Coffee Set (Jebena)', slug: 'traditional-clay-coffee-set-jebena', desc: 'Handcrafted clay jebena with six sini cups — the complete traditional coffee ceremony set.', price: 2100, stock: 25, w: 1.5, featured: true, rating: 4.8, rc: 93 },
    { id: 'p_10', vendorId: 'u_v2', vendorName: 'Habesha Home & Kitchen', cat: 'c_home', title: 'Handwoven Mesob Basket', slug: 'handwoven-mesob-basket', desc: 'Colorful handwoven mesob table basket from Harar artisans. Diameter 55cm.', price: 1750, stock: 15, w: 2.2, rating: 4.6, rc: 57 },
    { id: 'p_11', vendorId: 'u_v2', vendorName: 'Habesha Home & Kitchen', cat: 'c_groceries', title: 'Ethiopian Coffee Beans Yirgacheffe 1kg', slug: 'yirgacheffe-coffee-beans-1kg', desc: 'Single-origin washed Yirgacheffe arabica, medium roast, notes of citrus and jasmine.', price: 1150, cmp: 1350, stock: 80, w: 1.0, featured: true, rating: 4.9, rc: 340 },
    { id: 'p_12', vendorId: null, vendorName: null, cat: 'c_groceries', title: 'Kolo Grains Mix 5kg', slug: 'kolo-grains-mix-5kg', desc: 'Roasted barley, chickpea and sunflower kolo mix. Freshly packed 5kg family size.', price: 640, stock: 100, w: 5.0, rating: 4.4, rc: 76 },
    { id: 'p_13', vendorId: null, vendorName: null, cat: 'c_beauty', title: 'Shea Butter & Beeswax Balm 250g', slug: 'shea-butter-beeswax-balm-250g', desc: 'Natural shea butter and beeswax balm for skin and hair. No artificial additives.', price: 480, stock: 90, w: 0.3, rating: 4.6, rc: 122 },
    { id: 'p_14', vendorId: null, vendorName: null, cat: 'c_baby', title: 'Amharic Alphabet Wooden Blocks', slug: 'amharic-alphabet-wooden-blocks', desc: '30-piece wooden block set with Amharic fidel letters. Safe water-based paint.', price: 780, stock: 45, w: 1.0, rating: 4.7, rc: 35 },
  ]
  const productsCreated: { id: string; vendorId: string | null; title: string; price: number; w: number; commissionRate: number }[] = []
  for (const [i, p] of products.entries()) {
    const commissionRate = p.vendorId === 'u_v2' ? 0.08 : p.vendorId ? 0.10 : 0 // platform items carry no commission
    await db.product.create({
      data: {
        id: p.id, vendorId: p.vendorId, vendorName: p.vendorName, categoryId: p.cat, title: p.title, slug: p.slug, description: p.desc,
        priceETB: p.price, compareAtPriceETB: p.cmp ?? null, stock: p.stock, sku: `MH-${(1000 + i).toString()}`, weightKg: p.w,
        status: 'Active', isFeatured: !!p.featured, ratingAvg: p.rating ?? 4.5, ratingCount: p.rc ?? 10,
        images: grad(i), createdAt: new Date(Date.now() - (45 - i) * 864e5),
      },
    })
    productsCreated.push({ id: p.id, vendorId: p.vendorId, title: p.title, price: p.price, w: p.w, commissionRate })
  }
  // one product awaiting moderation
  await db.product.create({
    data: {
      id: 'p_15', vendorId: 'u_v3', vendorName: 'Gada Garments PLC', categoryId: 'c_fashion', title: "Kids' Traditional Gabi Set", slug: 'kids-traditional-gabi-set',
      description: 'Soft woven gabi set for kids.', priceETB: 990, stock: 30, sku: 'MH-1015', weightKg: 0.9, status: 'PendingReview', ratingAvg: 0, ratingCount: 0, images: grad(14),
    },
  })

  // ---------- Product gallery (real photos) + color variants ----------
  for (const g of GALLERY) {
    let sort = 0
    for (const url of g.images)
      await db.productImage.create({ data: { productId: g.productId, url, alt: '', colorId: null, sortOrder: sort++ } })
    let cSort = 0
    for (const c of g.colors ?? []) {
      const color = await db.productColor.create({
        data: { productId: g.productId, name: c.name, hex: c.hex, stock: c.stock, extraPriceETB: c.extraPriceETB ?? 0, sortOrder: cSort++ },
      })
      for (const url of c.images)
        await db.productImage.create({ data: { productId: g.productId, url, alt: c.name, colorId: color.id, sortOrder: sort++ } })
    }
  }

  // ---------- Delivery zones ----------
  const zones = [
    { id: 'z_addis', name: 'Addis Ababa', baseFeeETB: 150, perKgSurcharge: 20, freeThreshold: 2000 },
    { id: 'z_oromia', name: 'Oromia', baseFeeETB: 250, perKgSurcharge: 25, freeThreshold: 0 },
    { id: 'z_amhara', name: 'Amhara', baseFeeETB: 250, perKgSurcharge: 25, freeThreshold: 0 },
    { id: 'z_tigray', name: 'Tigray', baseFeeETB: 300, perKgSurcharge: 30, freeThreshold: 0 },
    { id: 'z_sidama', name: 'Sidama', baseFeeETB: 250, perKgSurcharge: 25, freeThreshold: 0 },
    { id: 'z_ddawa', name: 'Dire Dawa', baseFeeETB: 300, perKgSurcharge: 30, freeThreshold: 0 },
    { id: 'z_somali', name: 'Somali', baseFeeETB: 350, perKgSurcharge: 30, freeThreshold: 0 },
  ]
  await db.deliveryZone.createMany({ data: zones })

  // ---------- Settings ----------
  await db.settings.createMany({
    data: [
      { key: 'VatRate', value: '15', description: 'VAT percentage applied to subtotal + delivery' },
      { key: 'DefaultCommissionRate', value: '0.10', description: 'Platform commission on vendor sales' },
      { key: 'ReturnWindowDays', value: '7', description: 'Return & refund window after delivery' },
      { key: 'PaymentProvider', value: 'Mock', description: 'Active payment gateway (Mock | Chapa | Telebirr | Santimpay)' },
      { key: 'PayoutSlaHours', value: '12', description: 'Payout request processing SLA in hours' },
      { key: 'PlatformName', value: 'MarketHub', description: 'Platform display name' },
      // gap-fill settings keys (GAP_PLAN section A)
      { key: 'ReservationMinutes', value: '20', description: 'Checkout stock reservation TTL in minutes' },
      { key: 'AbandonedCartHours', value: '2', description: 'Cart idle hours before the abandoned-cart reminder' },
      { key: 'SmsProvider', value: 'Mock', description: 'Outbound SMS adapter (Mock | EthioTelecom | Safaricom)' },
      { key: 'EmailProvider', value: 'Mock', description: 'Outbound email adapter (Mock | Smtp)' },
      { key: 'SearchProvider', value: 'Meilisearch', description: 'Product search indexer (Meilisearch | Null)' },
      { key: 'FreeDeliveryAddisThreshold', value: '1000', description: 'Free delivery threshold for Addis Ababa in ETB' },
    ],
  })

  // ---------- Orders ----------
  const zoneOf = (id: string) => zones.find(z => z.id === id)!
  const buyerCities: Record<string, string> = { z_addis: 'Addis Ababa', z_oromia: 'Adama', z_amhara: 'Bahir Dar', z_tigray: 'Mekelle', z_sidama: 'Hawassa', z_ddawa: 'Dire Dawa', z_somali: 'Jijiga' }
  const buyers = ['Abebe Kebede', 'Meron Haile', 'Yonas Tadesse', 'Genet Alemu', 'Dawit Bekele', 'Hirut Molla', 'Samuel Negash', 'Bethlehem Ayele']
  const streets = ['Bole Road', 'Kazanchis', 'Piassa', 'Gerji Mebrat Hail', 'Summit', 'Ayat', 'Megenagna', 'Sarbet']
  let orderSeq = 1
  const now = Date.now()
  for (let day = 30; day >= 0; day--) {
    const perDay = 1 + (rnd() < 0.55 ? 1 : 0) + (rnd() < 0.22 ? 1 : 0)
    for (let k = 0; k < perDay; k++) {
      const created = new Date(now - day * 864e5 - ri(0, 20) * 36e5)
      const buyerName = pick(buyers)
      const zid = rnd() < 0.62 ? 'z_addis' : pick(zones).id
      const zone = zoneOf(zid)
      const nItems = rnd() < 0.6 ? 1 : rnd() < 0.85 ? 2 : 3
      const chosen = [...productsCreated].sort(() => rnd() - 0.5).slice(0, nItems)
      const items = chosen.map(p => {
        const qty = rnd() < 0.75 ? 1 : 2
        const lineNet = r2(p.price * qty)
        return { productId: p.id, title: p.title, vendorId: p.vendorId, vendorName: p.vendorId ? (p.vendorId === 'u_v1' ? 'Sheba Electronics Trading PLC' : 'Habesha Home & Kitchen') : null, unitPrice: p.price, qty, lineNet, weight: p.w * qty, commissionRate: p.commissionRate, commission: r2(lineNet * p.commissionRate) }
      })
      const subtotal = r2(items.reduce((a, i) => a + i.lineNet, 0))
      const totalW = r2(items.reduce((a, i) => a + i.weight, 0))
      let delivery = zone.baseFeeETB + zone.perKgSurcharge * Math.max(0, totalW - 5)
      if (zone.freeThreshold > 0 && subtotal >= zone.freeThreshold) delivery = 0
      delivery = r2(delivery)
      const vat = r2((subtotal + delivery) * 0.15)
      const grand = r2(subtotal + delivery + vat)
      let status: string
      if (day > 20) status = 'Completed'
      else if (day > 12) status = 'Delivered'
      else if (day > 7) status = rnd() < 0.5 ? 'Shipped' : 'VendorAccepted'
      else if (day > 3) status = 'PaymentConfirmed'
      else status = rnd() < 0.6 ? 'PaymentConfirmed' : 'Placed'
      const delivered = ['Delivered', 'Completed'].includes(status) ? new Date(created.getTime() + ri(2, 5) * 864e5) : null
      const trk = () => `MH-TRK-${Array.from({ length: 6 }, () => 'ABCDEFGHJKLMNPQRSTUVWXYZ0123456789'[ri(0, 33)]).join('')}`
      const order = await db.order.create({
        data: {
          number: `MH-2026${String(created.getMonth() + 1).padStart(2, '0')}${String(created.getDate()).padStart(2, '0')}-${String(orderSeq++).padStart(4, '0')}`,
          buyerId: 'u_buyer', status, addressLine: `${pick(streets)}, House ${ri(1, 300)}`, city: buyerCities[zid], zoneId: zid, zoneName: zone.name,
          phone: '+2519' + String(ri(10000000, 99999999)), subtotalETB: subtotal, deliveryFeeETB: delivery, vatRate: 15, vatETB: vat, grandTotalETB: grand,
          deliveryPin: String(ri(1000, 9999)),
          trackingCode: ['Shipped', 'Delivered', 'Completed', 'RefundRequested', 'Refunded'].includes(status) ? trk() : null,
          deliveredAt: delivered, createdAt: created,
          items: { create: items.map(i => ({ productId: i.productId, titleSnapshot: i.title, vendorId: i.vendorId, vendorName: i.vendorName, unitPriceETB: i.unitPrice, quantity: i.qty, lineNetETB: i.lineNet, vatETB: r2((i.lineNet / subtotal) * vat), commissionRate: i.commissionRate, commissionETB: i.commission, deliveryAllocETB: r2((i.lineNet / subtotal) * delivery) })) },
        },
        include: { items: true },
      })
      // payment
      await db.payment.create({
        data: {
          orderId: order.id, provider: 'Mock', providerRef: `MOCK-${order.number}`, amountETB: grand,
          status: status === 'Placed' ? 'Initiated' : 'Confirmed', createdAt: created,
        },
      })
      // ledger for paid orders
      if (status !== 'Placed') postPaidOrder({ id: order.id, vatETB: vat, deliveryFeeETB: delivery, grandTotalETB: grand, items: items.map(i => ({ vendorId: i.vendorId, commissionETB: i.commission, lineNetETB: i.lineNet })) }, created)
    }
  }

  // two refunded completed orders
  const completed = await db.order.findMany({ where: { status: 'Completed' }, include: { items: true }, orderBy: { createdAt: 'asc' }, take: 2 })
  for (const o of completed) {
    const amount = o.grandTotalETB
    await db.order.update({
      where: { id: o.id },
      data: { status: 'Refunded', previousStatus: 'Completed', refundReason: 'Item arrived damaged — refund approved after inspection' },
    })
    await db.orderItem.updateMany({ where: { orderId: o.id }, data: { refundStatus: 'Approved' } })
    postRefund({ id: o.id, vatETB: o.vatETB, deliveryFeeETB: o.deliveryFeeETB, grandTotalETB: o.grandTotalETB }, amount)
    legs.push({ accountCode: '4000', orderId: o.id, debitETB: amount, description: 'Refund expense (buyer refund)' })
    legs.push({ accountCode: '1000', orderId: o.id, creditETB: amount, description: 'Refund payout from PSP' })
  }

  // ---------- Gap-fill: order events, variants, banners, flash sale, coupons, reviews, address, return ----------
  // Order status timeline events for every seeded order (status chain replay)
  const CHAIN: Record<string, { status: string; byRole: string }[]> = {
    Placed: [{ status: 'Placed', byRole: 'Buyer' }],
    PaymentConfirmed: [{ status: 'Placed', byRole: 'Buyer' }, { status: 'PaymentConfirmed', byRole: 'System' }],
    VendorAccepted: [{ status: 'Placed', byRole: 'Buyer' }, { status: 'PaymentConfirmed', byRole: 'System' }, { status: 'VendorAccepted', byRole: 'Vendor' }],
    Shipped: [{ status: 'Placed', byRole: 'Buyer' }, { status: 'PaymentConfirmed', byRole: 'System' }, { status: 'VendorAccepted', byRole: 'Vendor' }, { status: 'Shipped', byRole: 'Vendor' }],
    Delivered: [{ status: 'Placed', byRole: 'Buyer' }, { status: 'PaymentConfirmed', byRole: 'System' }, { status: 'VendorAccepted', byRole: 'Vendor' }, { status: 'Shipped', byRole: 'Vendor' }, { status: 'Delivered', byRole: 'Courier' }],
    Completed: [{ status: 'Placed', byRole: 'Buyer' }, { status: 'PaymentConfirmed', byRole: 'System' }, { status: 'VendorAccepted', byRole: 'Vendor' }, { status: 'Shipped', byRole: 'Vendor' }, { status: 'Delivered', byRole: 'Courier' }, { status: 'Completed', byRole: 'System' }],
    RefundRequested: [{ status: 'Placed', byRole: 'Buyer' }, { status: 'PaymentConfirmed', byRole: 'System' }, { status: 'VendorAccepted', byRole: 'Vendor' }, { status: 'Shipped', byRole: 'Vendor' }, { status: 'Delivered', byRole: 'Courier' }, { status: 'Completed', byRole: 'System' }, { status: 'RefundRequested', byRole: 'Buyer' }],
    Refunded: [{ status: 'Placed', byRole: 'Buyer' }, { status: 'PaymentConfirmed', byRole: 'System' }, { status: 'VendorAccepted', byRole: 'Vendor' }, { status: 'Shipped', byRole: 'Vendor' }, { status: 'Delivered', byRole: 'Courier' }, { status: 'Completed', byRole: 'System' }, { status: 'RefundRequested', byRole: 'Buyer' }, { status: 'Refunded', byRole: 'Admin' }],
  }
  const allOrders = await db.order.findMany({ include: { items: true } })
  const eventRows: { orderId: string; status: string; byRole: string; createdAt: Date; note?: string }[] = []
  for (const o of allOrders) {
    const chain = CHAIN[o.status] ?? CHAIN.Placed
    const span = (o.deliveredAt?.getTime() ?? Math.min(o.createdAt.getTime() + 4 * 864e5, now)) - o.createdAt.getTime()
    chain.forEach((step, idx) => {
      eventRows.push({
        orderId: o.id, status: step.status, byRole: step.byRole,
        createdAt: new Date(o.createdAt.getTime() + Math.round((span / Math.max(1, chain.length - 1)) * idx)),
        note: step.status === 'Refunded' ? 'Refund approved after inspection' : undefined,
      })
    })
  }
  await db.orderEvent.createMany({ data: eventRows })

  // soldCount + viewCount backfill from seeded orders
  const paidOrders = await db.order.findMany({ where: { status: { notIn: ['Placed', 'Cancelled'] } }, select: { id: true } })
  const paidItems = await db.orderItem.findMany({ where: { orderId: { in: paidOrders.map(o => o.id) } } })
  const soldBy = new Map<string, number>()
  for (const it of paidItems) soldBy.set(it.productId, (soldBy.get(it.productId) ?? 0) + it.quantity)
  for (const [pid, qty] of soldBy) await db.product.update({ where: { id: pid }, data: { soldCount: qty, viewCount: qty * ri(18, 55) + ri(80, 400) } })

  // SKU-level variants for the four colored products — stock split across variants, product.stock = Σ
  const split = (total: number, parts: number) => Array.from({ length: parts }, (_, i) => (i === parts - 1 ? total - Math.floor(total / parts) * (parts - 1) : Math.floor(total / parts)))
  const VARIANT_PLAN: { productId: string; sizes: string[]; sizePriceETB?: Record<string, number> }[] = [
    { productId: 'p_01', sizes: ['128GB', '256GB'], sizePriceETB: { '128GB': 24999, '256GB': 27999 } },
    { productId: 'p_03', sizes: ['Standard'] },
    { productId: 'p_05', sizes: ['Standard'] },
    { productId: 'p_06', sizes: ['S', 'M', 'L'] },
  ]
  let vSeq = 1
  for (const plan of VARIANT_PLAN) {
    const colors = await db.productColor.findMany({ where: { productId: plan.productId }, orderBy: { sortOrder: 'asc' } })
    let totalStock = 0
    for (const [ci, color] of colors.entries()) {
      const stocks = split(color.stock, plan.sizes.length)
      for (const [si, size] of plan.sizes.entries()) {
        const stock = stocks[si]
        totalStock += stock
        await db.productVariant.create({
          data: {
            productId: plan.productId, sku: `MH-V-${vSeq++}`, colorId: color.id, size,
            priceETB: plan.sizePriceETB ? (plan.sizePriceETB[size] ?? null) : null,
            stock, isActive: true, sortOrder: ci * plan.sizes.length + si,
          },
        })
      }
    }
    await db.product.update({ where: { id: plan.productId }, data: { stock: totalStock } })
  }

  // Marketing banners (real gallery-style CDN images)
  const CDN = 'https://z-cdn.chatglm.cn/image-search-mcp/images-ppt'
  await db.banner.createMany({
    data: [
      { title: 'Meskel & Gena Savings — up to 40% off electronics', imageUrl: `${CDN}/2acd03551ec2.jpg`, link: '/catalog?sort=price_asc', position: 0, isActive: true, startsAt: new Date(now - 2 * 864e5), endsAt: new Date(now + 14 * 864e5) },
      { title: 'Fresh Yirgacheffe coffee, roasted this week', imageUrl: `${CDN}/df692c321057.webp`, link: '/catalog?cat=c_groceries', position: 1, isActive: true, startsAt: new Date(now - 864e5), endsAt: new Date(now + 20 * 864e5) },
      { title: 'Habesha kemis season — handwoven in Addis', imageUrl: `${CDN}/9ed2a9e730e4.jpg`, link: '/catalog?cat=c_fashion', position: 2, isActive: true, startsAt: new Date(now - 3 * 864e5), endsAt: null },
    ],
  })

  // Flash sale — active for ~48h on 4 products
  const saleEnds = new Date(now + 48 * 36e5)
  const flash = [
    { id: 'p_01', pct: 0.15 }, { id: 'p_05', pct: 0.25 }, { id: 'p_11', pct: 0.20 }, { id: 'p_13', pct: 0.30 },
  ]
  for (const f of flash) {
    const p = await db.product.findUnique({ where: { id: f.id } })
    if (p) await db.product.update({ where: { id: f.id }, data: { salePriceETB: r2(p.priceETB * (1 - f.pct)), saleEndsAt: saleEnds } })
  }

  // Coupons
  await db.coupon.create({ data: { code: 'WELCOME10', description: '10% off your first order (max 300 Br discount)', type: 'Percent', value: 10, minSubtotal: 500, maxDiscount: 300, maxUses: 0, usedCount: 0, startsAt: new Date(now - 10 * 864e5), expiresAt: new Date(now + 30 * 864e5), isActive: true } })
  await db.coupon.create({ data: { code: 'SAVE200', description: '200 Br off orders above 1,500 Br', type: 'Fixed', value: 200, minSubtotal: 1500, maxDiscount: null, maxUses: 500, usedCount: 37, startsAt: new Date(now - 5 * 864e5), expiresAt: new Date(now + 60 * 864e5), isActive: true } })

  // Reviews — 14 Approved across the marketplace, Ethiopian buyer names, Amharic-flavored English
  const reviewSeed: { productId: string; userId: string; rating: number; title: string; body: string; reply?: { note: string; by: string } }[] = [
    { productId: 'p_01', userId: 'u_buyer', rating: 5, title: 'Original phone, betam teru!', body: 'Sealed box, genuine warranty card and delivery to Bole took less than a day. Kit met neber — very happy with this purchase.' },
    { productId: 'p_01', userId: 'u_b1', rating: 4, title: 'Battery is iw ketachew', body: 'Screen is beautiful and camera is sharp. Battery could last a bit longer, but at this price chigir yellem. Recommended.' },
    { productId: 'p_01', userId: 'u_b4', rating: 5, title: 'Gift for my sister', body: 'Bought it for my sister in Bahir Dar — arrived in two days, well packed. She says it is konjo! Would buy from Sheba again.' },
    { productId: 'p_03', userId: 'u_b2', rating: 5, title: 'Bass dingil new!', body: 'The bass on this speaker is serious — coffee ceremony at home felt like a concert. Battery lasted the whole afternoon.' , reply: { by: 'u_v1', note: 'Betam ameseginalehu! Enjoy the speaker — 12-month warranty is active.' } },
    { productId: 'p_03', userId: 'u_b5', rating: 4, title: 'Good sound, small cable', body: 'Sound quality is teru for the price. The charging cable is a bit short, otherwise no complaints. Delivery was fast.' },
    { productId: 'p_03', userId: 'u_b1', rating: 4, title: 'Solid for outdoor', body: 'Took it to Entoto for a picnic — survived some dust and it kept playing. Water resistance seems real.' },
    { productId: 'p_05', userId: 'u_b3', rating: 5, title: 'Konjo earbuds', body: 'Noise cancelling works on minibus rides, case feels premium. Pairing was instant with my phone. Ameseginalehu MarketHub!' },
    { productId: 'p_05', userId: 'u_b2', rating: 4, title: 'Great value', body: 'Sound is clear, touch controls work well. Only wish the ear tips came in one more size. Chigir yellem overall.' , reply: { by: 'u_v1', note: 'Thank you Yonas! Extra ear tips are available at our Megenagna branch any time.' } },
    { productId: 'p_05', userId: 'u_b4', rating: 5, title: 'Second pair bought', body: 'First pair lasted a year of daily use. Bought again for my brother — consistent quality, iw ketachew.' },
    { productId: 'p_06', userId: 'u_b5', rating: 5, title: 'Soft cotton, true tibeb', body: 'The weaving is beautiful and the cotton is soft — my husband wore it for Meskel and everyone asked where we bought it.' },
    { productId: 'p_06', userId: 'u_b3', rating: 3, title: 'Beige is lighter than photo', body: 'Quality is good but the beige color looks lighter than the pictures. Size L fits well though. Exchange was handled politely.' },
    { productId: 'p_09', userId: 'u_b1', rating: 5, title: 'Coffee tastes better!', body: 'The jebena keeps the buna hot and the sini cups are perfectly sized. Handcraft quality — betam konjo set.' },
    { productId: 'p_11', userId: 'u_buyer', rating: 5, title: 'Fresh roast, real Yirgacheffe', body: 'Citrus and jasmine notes are there exactly as described. Grinding the beans fills the whole kitchen with aroma. Will reorder.' },
    { productId: 'p_11', userId: 'u_b5', rating: 4, title: 'Good buna, fast delivery', body: 'Beans were freshly roasted, sealed well. Delivery to Addis took one day. Slightly acidic for my taste but quality is top.' },
  ]
  // verified = buyer has a Delivered/Completed order containing the product
  const verifyMap = new Map<string, string>() // userId:productId -> orderId
  const deliveredOrders = await db.order.findMany({ where: { status: { in: ['Delivered', 'Completed'] } }, include: { items: true } })
  for (const o of deliveredOrders) for (const i of o.items) if (!verifyMap.has(`${o.buyerId}:${i.productId}`)) verifyMap.set(`${o.buyerId}:${i.productId}`, o.id)
  for (const [i, r] of reviewSeed.entries()) {
    await db.review.create({
      data: {
        productId: r.productId, userId: r.userId, orderId: verifyMap.get(`${r.userId}:${r.productId}`) ?? null,
        rating: r.rating, title: r.title, body: r.body, status: 'Approved',
        vendorReply: r.reply?.note ?? null, vendorRepliedAt: r.reply ? new Date(now - ri(1, 6) * 864e5) : null,
        createdAt: new Date(now - (20 - i) * 864e5 - ri(0, 20) * 36e5),
      },
    })
  }
  // recompute affected Product.ratingAvg / ratingCount from Approved reviews
  const touched = [...new Set(reviewSeed.map(r => r.productId))]
  for (const pid of touched) {
    const agg = await db.review.aggregate({ where: { productId: pid, status: 'Approved' }, _avg: { rating: true }, _count: { _all: true } })
    await db.product.update({ where: { id: pid }, data: { ratingAvg: r2(agg._avg.rating ?? 0), ratingCount: agg._count._all } })
  }

  // Demo address book entry for the buyer (Bole, Addis Ababa)
  await db.address.create({
    data: {
      userId: 'u_buyer', label: 'Home', fullName: 'Abebe Kebede', phone: '+251921000005',
      region: 'Addis Ababa', city: 'Addis Ababa', subCity: 'Bole', woreda: '03', kebele: '08',
      landmark: 'Near Getu Commercial Center', isDefault: true,
    },
  })

  // One pending return request on a completed order (Damaged)
  const completedOrder = await db.order.findFirst({ where: { status: 'Completed', buyerId: 'u_buyer' }, include: { items: true }, orderBy: { createdAt: 'desc' } })
  if (completedOrder && completedOrder.items[0]) {
    const item = completedOrder.items[0]
    await db.returnRequest.create({
      data: {
        orderItemId: item.id, orderId: completedOrder.id, buyerId: 'u_buyer', vendorId: item.vendorId,
        reason: 'Damaged', description: 'The item arrived with a cracked part — the box was clearly crushed during delivery. Photos of the damage are attached.',
        evidenceUrls: JSON.stringify([`${CDN}/fe6e2700e06e.png`]),
        status: 'Requested', createdAt: new Date(now - 26 * 36e5),
      },
    })
    await db.orderItem.update({ where: { id: item.id }, data: { refundStatus: 'Requested' } })
  }

  // Wishlist demo items for the buyer
  await db.wishlistItem.createMany({ data: [{ userId: 'u_buyer', productId: 'p_07' }, { userId: 'u_buyer', productId: 'p_09' }] })

  // Sample outbound message (mock SMS provider)
  await db.outboundMessage.create({
    data: {
      userId: 'u_buyer', channel: 'Sms', template: 'OrderConfirmed', destination: '+251921000005',
      payloadJson: JSON.stringify({ orderNumber: allOrders[0]?.number ?? 'MH-20260001', amountETB: allOrders[0]?.grandTotalETB ?? 0 }),
      provider: 'Mock', providerRef: `MOCK-SMS-${Date.now()}`, status: 'Sent', sentAt: new Date(now - 36e5),
    },
  })

  // ---------- Ledger entries ----------
  for (const l of legs) await db.ledgerEntry.create({ data: { accountCode: l.accountCode, orderId: l.orderId, vendorId: l.vendorId, debitETB: r2(l.debitETB ?? 0), creditETB: r2(l.creditETB ?? 0), description: l.description, createdAt: l.orderId ? (await db.order.findUnique({ where: { id: l.orderId } }))!.createdAt : new Date() } })

  // sanity: ledger must balance
  const all = await db.ledgerEntry.findMany()
  const dr = r2(all.reduce((a, e) => a + e.debitETB, 0))
  const cr = r2(all.reduce((a, e) => a + e.creditETB, 0))
  if (Math.abs(dr - cr) > 0.01) throw new Error(`Ledger unbalanced in seed: DR ${dr} vs CR ${cr}`)
  console.log(`Ledger balanced: DR=${dr} CR=${cr}`)

  // ---------- Payouts ----------
  await db.payoutRequest.create({ data: { vendorId: 'u_v1', amountETB: 2000, status: 'Paid', requestedAt: new Date(now - 6 * 864e5), dueBy: new Date(now - 6 * 864e5 + 12 * 36e5), processedAt: new Date(now - 6 * 864e5 + 4 * 36e5), processedBy: 'u_admin', paymentMethod: 'Telebirr', destinationAccount: '+251911000002' } })
  await db.payoutRequest.create({ data: { vendorId: 'u_v1', amountETB: 1500, status: 'Requested', requestedAt: new Date(now - 2 * 36e5), dueBy: new Date(now + 10 * 36e5), paymentMethod: 'Telebirr', destinationAccount: '+251911000002' } })
  await db.payoutRequest.create({ data: { vendorId: 'u_v2', amountETB: 3000, status: 'Paid', requestedAt: new Date(now - 9 * 864e5), dueBy: new Date(now - 9 * 864e5 + 12 * 36e5), processedAt: new Date(now - 9 * 864e5 + 5 * 36e5), processedBy: 'u_admin', paymentMethod: 'BankTransfer', destinationAccount: 'CBE 1000123456789' } })

  // ---------- Notifications ----------
  await db.notification.createMany({
    data: [
      { userId: 'u_buyer', channel: 'InApp', type: 'OrderConfirmed', subject: 'Order confirmed', body: 'Your payment was confirmed and your order is being prepared.' },
      { userId: 'u_v1', channel: 'InApp', type: 'NewOrder', subject: 'New order received', body: 'You have a new paid order. Accept it within 24 hours.' },
      { userId: 'u_v1', channel: 'InApp', type: 'PayoutReminder', subject: 'Payout request submitted', body: 'Your payout request will be processed within 12 hours.' },
      { userId: 'u_v3', channel: 'InApp', type: 'DocumentsReceived', subject: 'Application received', body: 'Your vendor application documents are under review.' },
    ],
  })

  // ---------- Demo cart for buyer ----------
  await db.cartItem.createMany({ data: [{ userId: 'u_buyer', productId: 'p_09', quantity: 1 }, { userId: 'u_buyer', productId: 'p_11', quantity: 2 }] })

  const counts = {
    users: await db.user.count(), products: await db.product.count(), orders: await db.order.count(),
    ledger: await db.ledgerEntry.count(), payouts: await db.payoutRequest.count(), policies: await db.policy.count(),
    variants: await db.productVariant.count(), reviews: await db.review.count(), banners: await db.banner.count(),
    coupons: await db.coupon.count(), addresses: await db.address.count(), returns: await db.returnRequest.count(),
    orderEvents: await db.orderEvent.count(), outbound: await db.outboundMessage.count(),
  }
  console.log('Seed complete:', counts)
}

main()
  .catch((e) => { console.error(e); process.exit(1) })
  .finally(() => db.$disconnect())
