// Checkout v2: address book (Ethiopian format) → stock reservation w/ countdown →
// review + coupon → mock gateway payment → confirmation with delivery PIN.
'use client'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { api, br, dateTimeFmt, lineUnitPrice, Order, ProductVariant, Zone, Address, CouponCheck } from '@/lib/api'
import { useApp } from '@/store/app'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Separator } from '@/components/ui/separator'
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group'
import { Checkbox } from '@/components/ui/checkbox'
import { ProductPhoto, Countdown } from './shared'
import { BadgeCheck, Banknote, CheckCircle2, ChevronLeft, CreditCard, KeyRound, MapPin, Plus, Smartphone, Ticket, Timer, Truck } from 'lucide-react'

type CartLine = { id: string; productId: string; quantity: number; availableQty?: number; color: { id: string; name: string; hex: string; extraPriceETB: number } | null; variantId?: string | null; variant?: ProductVariant | null; product: { id: string; title: string; priceETB: number; weightKg?: number; images: string | null; gallery?: { url: string }[] } }
type CartData = { items: CartLine[]; subtotal: number }

const PROVIDERS = [
  { id: 'telebirr', name: 'telebirr', desc: 'Ethio Telecom wallet', icon: Smartphone },
  { id: 'chapa', name: 'Chapa', desc: 'Cards, CBE Birr & banks', icon: CreditCard },
  { id: 'santimpay', name: 'SantimPay', desc: 'Wallet & bank transfer', icon: Banknote },
] as const

const EMPTY_ADDR = { label: 'Home', fullName: '', phone: '', region: 'Addis Ababa', city: 'Addis Ababa', subCity: '', woreda: '', kebele: '', landmark: '' }

export function CheckoutView() {
  const app = useApp()
  const [cart, setCart] = useState<CartData | null>(null)
  const [zones, setZones] = useState<Zone[]>([])
  const [addresses, setAddresses] = useState<Address[]>([])
  const [addrId, setAddrId] = useState<string>('')
  const [showNewAddr, setShowNewAddr] = useState(false)
  const [saveNew, setSaveNew] = useState(true)
  const [na, setNa] = useState({ ...EMPTY_ADDR })
  const [step, setStep] = useState<'address' | 'review' | 'pay' | 'done'>('address')
  const [phone, setPhone] = useState('')
  const [couponCode, setCouponCode] = useState('')
  const [coupon, setCoupon] = useState<CouponCheck | null>(null)
  const [reservation, setReservation] = useState<{ expiresAt: string; allOk: boolean; conflicts: string[] } | null>(null)
  const [provider, setProvider] = useState<string>('telebirr')
  const [order, setOrder] = useState<Order | null>(null)
  const [payOpen, setPayOpen] = useState(false)
  const [payStatus, setPayStatus] = useState<'waiting' | 'done'>('waiting')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  const load = useCallback(() => {
    api.get<{ items: Zone[] }>('/api/sales/zones').then(r => setZones(r.items)).catch(() => {})
    api.get<CartData>('/api/sales/cart').then(setCart).catch(() => setCart(null))
    api.get<{ items: Address[] }>('/api/sales/addresses').then(r => {
      setAddresses(r.items)
      const def = r.items.find(a => a.isDefault) ?? r.items[0]
      if (def) { setAddrId(def.id); setPhone(def.phone) }
      else setShowNewAddr(true)
    }).catch(() => {})
  }, [])
  useEffect(load, [load])

  // Reserve stock while the buyer fills in delivery details — prevents overselling.
  useEffect(() => {
    if (step !== 'address' || !app.user || !cart?.items.length) return
    api.post<{ expiresAt: string; allOk: boolean; reservations: { key: string; ok: boolean; available: number }[] }>('/api/sales/cart/reserve', {})
      .then(r => {
        const conflicts = r.reservations.filter(x => !x.ok).map(x => x.key)
        setReservation({ expiresAt: r.expiresAt, allOk: r.allOk, conflicts })
      })
      .catch(() => {})
    return () => { api.post('/api/sales/cart/release', {}).catch(() => {}) }
  }, [step, app.user, cart?.items.length])

  const [selZoneId, setSelZoneId] = useState('')
  const selAddr = addresses.find(a => a.id === addrId)
  const zone = useMemo(() => {
    if (selAddr) {
      const match = zones.find(z => z.name === selAddr.region)
      if (match) return match
    }
    return zones.find(z => z.id === selZoneId)
  }, [selAddr, zones, selZoneId])

  const totalWeight = useMemo(() => cart?.items.reduce((a, i) => a + (i.product.weightKg ?? 1) * i.quantity, 0) ?? 0, [cart])
  const subtotal = useMemo(() => cart?.items.reduce((a, i) => a + (i.variant?.priceETB ?? lineUnitPrice(i.product, i.color)) * i.quantity, 0) ?? 0, [cart])
  const discount = coupon?.ok ? coupon.discountETB : 0
  const deliveryFee = useMemo(() => {
    if (!zone || !cart) return 0
    let fee = zone.baseFeeETB + zone.perKgSurcharge * Math.max(0, totalWeight - 5)
    if (zone.freeThreshold > 0 && subtotal - discount >= zone.freeThreshold) fee = 0
    return Math.round(fee * 100) / 100
  }, [zone, cart, totalWeight, subtotal, discount])
  const vat = useMemo(() => (cart ? Math.round((subtotal - discount + deliveryFee) * 0.15 * 100) / 100 : 0), [cart, deliveryFee, discount, subtotal])
  const grand = useMemo(() => Math.round((subtotal - discount + deliveryFee + vat) * 100) / 100, [cart, deliveryFee, vat, discount, subtotal])

  const checkCoupon = async () => {
    if (!couponCode.trim()) return
    try {
      const r = await api.post<CouponCheck>('/api/sales/cart/coupon', { code: couponCode.trim(), subtotal })
      setCoupon(r)
      app.notify(r.ok ? r.message : r.message, r.ok ? 'ok' : 'err')
    } catch (e) { setCoupon({ ok: false, discountETB: 0, message: e instanceof Error ? e.message : 'Invalid coupon' }) }
  }

  const saveNewAddress = async () => {
    setBusy(true); setError('')
    try {
      const r = await api.post<{ address: Address }>('/api/sales/addresses', na)
      setAddresses(a => [...a, r.address])
      setAddrId(r.address.id); setPhone(r.address.phone); setShowNewAddr(false)
      app.notify('Address saved')
    } catch (e) { setError(e instanceof Error ? e.message : 'Could not save address') }
    finally { setBusy(false) }
  }

  if (!app.user) return (
    <main className="flex flex-col items-center justify-center gap-3 py-32 text-sm text-muted-foreground">
      <p>Sign in to checkout.</p><Button onClick={() => app.openAuth('signin')}>Sign in</Button>
    </main>
  )
  if (!cart) return <main className="mx-auto max-w-3xl px-4 py-16"><div className="h-64 animate-pulse rounded-xl bg-muted" /></main>
  if (cart.items.length === 0 && step !== 'done') return (
    <main className="py-32 text-center text-sm text-muted-foreground">Your cart is empty. <button className="text-primary underline" onClick={() => app.go('home')}>Browse products</button></main>
  )

  const addressValid = showNewAddr
    ? na.fullName && na.woreda && na.city && /^\+251[79]\d{8}$/.test(na.phone)
    : !!addrId
  const orderPhone = showNewAddr ? na.phone : phone

  const placeOrder = async () => {
    setBusy(true); setError('')
    try {
      const body: Record<string, unknown> = {
        zoneId: zone?.id, phone: orderPhone, couponCode: coupon?.ok ? couponCode.trim() : undefined,
        addressId: showNewAddr ? undefined : addrId,
        address: showNewAddr ? na : undefined,
        saveAddress: showNewAddr ? saveNew : undefined,
      }
      const res = await api.post<{ order: Order }>('/api/sales/checkout', body)
      setOrder(res.order)
      setStep('pay'); setPayOpen(true); setPayStatus('waiting')
    } catch (e) { setError(e instanceof Error ? e.message : 'Checkout failed') }
    finally { setBusy(false) }
  }

  const confirmPayment = async () => {
    if (!order) return
    setBusy(true)
    try {
      const ref = order.payments?.[0]?.providerRef
      await api.post('/api/payments/mock/confirm', { providerRef: ref })
      await app.refreshCart()
      setPayStatus('done')
      setTimeout(() => { setPayOpen(false); setStep('done') }, 900)
    } catch (e) { app.notify(e instanceof Error ? e.message : 'Payment failed', 'err'); setPayOpen(false) }
    finally { setBusy(false) }
  }

  const ProviderIcon = PROVIDERS.find(p => p.id === provider)?.icon ?? Smartphone

  return (
    <main className="mx-auto max-w-3xl px-4 py-8">
      <div className="mb-8 flex items-center gap-2 text-xs">
        {(['address', 'review', 'pay', 'done'] as const).map((s, i) => (
          <div key={s} className="flex items-center gap-2">
            <span className={`flex h-6 w-6 items-center justify-center rounded-full text-[11px] font-semibold ${(['address', 'review', 'pay', 'done'] as const).indexOf(step) >= i ? 'bg-primary text-primary-foreground' : 'bg-muted text-muted-foreground'}`}>{i + 1}</span>
            <span className={(['address', 'review', 'pay', 'done'] as const).indexOf(step) >= i ? 'font-medium' : 'text-muted-foreground'}>{s === 'address' ? 'Delivery' : s === 'review' ? 'Review' : s === 'pay' ? 'Payment' : 'Done'}</span>
            {i < 3 && <span className="mx-1 h-px w-6 bg-border" />}
          </div>
        ))}
      </div>

      {step === 'done' && order ? (
        <Card className="mx-auto max-w-lg text-center">
          <CardContent className="p-10">
            <CheckCircle2 className="mx-auto h-14 w-14 text-emerald-600" aria-hidden />
            <h1 className="mt-4 text-2xl font-semibold">Order confirmed</h1>
            <p className="mt-1 text-sm text-muted-foreground">Order <span className="font-mono font-medium text-foreground">{order.number}</span> · paid with {order.payments?.[0]?.provider} · {dateTimeFmt(order.createdAt)}</p>

            <div className="mx-auto mt-5 grid max-w-sm gap-2">
              <div className="flex items-center gap-3 rounded-xl border border-emerald-200 bg-emerald-50 p-3 text-left">
                <Truck className="h-5 w-5 shrink-0 text-emerald-700" />
                <div><p className="text-[10px] font-semibold uppercase tracking-wide text-emerald-700">Tracking code</p><p className="font-mono text-sm font-bold text-emerald-900">{order.trackingCode}</p></div>
              </div>
              <div className="flex items-center gap-3 rounded-xl border border-amber-200 bg-amber-50 p-3 text-left">
                <KeyRound className="h-5 w-5 shrink-0 text-amber-600" />
                <div>
                  <p className="text-[10px] font-semibold uppercase tracking-wide text-amber-700">Delivery PIN</p>
                  <p className="font-mono text-lg font-bold tracking-[0.3em] text-amber-900">{order.deliveryPin}</p>
                  <p className="text-[10px] text-amber-700">Share this PIN with the courier only after you receive and check your items.</p>
                </div>
              </div>
            </div>

            <div className="mx-auto mt-5 max-w-xs rounded-lg bg-muted/60 p-4 text-left text-sm">
              <div className="flex justify-between py-0.5"><span className="text-muted-foreground">Items ({order.items.length})</span><span>{br(order.subtotalETB - (order.couponDiscountETB ?? 0))}</span></div>
              {order.couponCode && <div className="flex justify-between py-0.5 text-emerald-700"><span>Coupon {order.couponCode}</span><span>−{br(order.couponDiscountETB ?? 0)}</span></div>}
              <div className="flex justify-between py-0.5"><span className="text-muted-foreground">Delivery ({order.zoneName})</span><span>{order.deliveryFeeETB === 0 ? 'FREE' : br(order.deliveryFeeETB)}</span></div>
              <div className="flex justify-between py-0.5"><span className="text-muted-foreground">VAT {order.vatRate}%</span><span>{br(order.vatETB)}</span></div>
              <Separator className="my-2" />
              <div className="flex justify-between font-semibold"><span>Total paid</span><span>{br(order.grandTotalETB)}</span></div>
            </div>
            <div className="mt-6 flex justify-center gap-2">
              <Button onClick={() => app.go('orders')}>Track my order</Button>
              <Button variant="outline" onClick={() => app.go('home')}>Continue shopping</Button>
            </div>
            <p className="mt-4 text-xs text-muted-foreground">A receipt was sent to your notifications{order.payments?.[0]?.provider !== 'Mock' ? '' : ' (mock gateway — production adapters read keys from env)'}. Returns accepted within 7 days of delivery.</p>
          </CardContent>
        </Card>
      ) : step === 'pay' ? (
        <Card className="mx-auto max-w-lg">
          <CardContent className="p-6">
            <h2 className="text-lg font-semibold">Choose payment method</h2>
            <p className="mt-1 text-sm text-muted-foreground">Order {order?.number} · {br(grand)} including VAT</p>
            <RadioGroup value={provider} onValueChange={setProvider} className="mt-4 gap-2">
              {PROVIDERS.map(p => (
                <Label key={p.id} htmlFor={p.id} className={`flex cursor-pointer items-center gap-3 rounded-xl border p-4 ${provider === p.id ? 'border-primary bg-primary/5' : ''}`}>
                  <RadioGroupItem id={p.id} value={p.id} />
                  <p.icon className="h-5 w-5 text-primary" aria-hidden />
                  <span className="flex-1"><span className="block text-sm font-medium">{p.name}</span><span className="block text-xs text-muted-foreground">{p.desc}</span></span>
                  <BadgeCheck className={`h-4 w-4 ${provider === p.id ? 'text-primary' : 'text-transparent'}`} aria-hidden />
                </Label>
              ))}
            </RadioGroup>
            <p className="mt-3 text-[11px] text-muted-foreground">Preview note: the production gateway adapters (Chapa / telebirr / SantimPay) read their API keys from environment config — this sandbox uses the identical MockGateway flow.</p>
            <Button className="mt-4 w-full" size="lg" disabled={busy} onClick={placeOrder}>Pay {br(grand)} with {PROVIDERS.find(p => p.id === provider)?.name}</Button>
            {error && <p className="mt-2 text-sm text-red-600">{error}</p>}
          </CardContent>
        </Card>
      ) : (
        <div className="grid gap-6 md:grid-cols-[1fr_320px]">
          <Card>
            <CardContent className="p-6">
              {step === 'address' ? (
                <>
                  <div className="flex items-center justify-between">
                    <h2 className="text-lg font-semibold">Delivery details</h2>
                    {reservation && (
                      <span className={`flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-[11px] font-medium ${reservation.allOk ? 'border-emerald-200 bg-emerald-50 text-emerald-700' : 'border-red-200 bg-red-50 text-red-700'}`}>
                        <Timer className="h-3 w-3" />
                        {reservation.allOk ? <>Stock held <Countdown to={reservation.expiresAt} /></> : 'Stock conflict — adjust quantities'}
                      </span>
                    )}
                  </div>

                  {/* address book */}
                  <div className="mt-4 space-y-2">
                    {addresses.map(a => (
                      <Label key={a.id} className={`flex cursor-pointer items-start gap-3 rounded-xl border p-3.5 ${addrId === a.id && !showNewAddr ? 'border-primary bg-primary/5' : ''}`}>
                        <RadioGroupItem value={a.id} checked={addrId === a.id && !showNewAddr} onClick={() => { setAddrId(a.id); setShowNewAddr(false); setPhone(a.phone); const z = zones.find(z => z.name === a.region); if (z) setSelZoneId(z.id) }} />
                        <MapPin className="mt-0.5 h-4 w-4 shrink-0 text-primary" />
                        <span className="min-w-0 flex-1 text-sm">
                          <span className="flex items-center gap-2 font-medium">{a.label}{a.isDefault && <span className="rounded bg-muted px-1.5 py-0.5 text-[9px]">DEFAULT</span>}</span>
                          <span className="mt-0.5 block text-xs leading-4 text-muted-foreground">
                            {a.fullName} · {a.phone}<br />
                            {a.subCity && <>{a.subCity} Sub-City, </>}Woreda {a.woreda}{a.kebele ? `, Kebele ${a.kebele}` : ''}{a.landmark ? ` · ${a.landmark}` : ''}, {a.city}, {a.region}
                          </span>
                        </span>
                      </Label>
                    ))}
                    <button className="flex w-full items-center gap-2 rounded-xl border border-dashed p-3 text-sm text-muted-foreground transition-colors hover:border-primary hover:text-primary" onClick={() => setShowNewAddr(true)}>
                      <Plus className="h-4 w-4" />Add new address
                    </button>
                  </div>

                  {showNewAddr && (
                    <div className="mt-3 space-y-3 rounded-xl border bg-muted/20 p-4">
                      <div className="grid grid-cols-2 gap-3">
                        <div><Label htmlFor="na-label">Label</Label><Input id="na-label" value={na.label} onChange={e => setNa({ ...na, label: e.target.value })} placeholder="Home / Office" /></div>
                        <div><Label htmlFor="na-name">Full name</Label><Input id="na-name" value={na.fullName} onChange={e => setNa({ ...na, fullName: e.target.value })} /></div>
                      </div>
                      <div className="grid grid-cols-2 gap-3">
                        <div>
                          <Label>Region</Label>
                          <Select value={na.region} onValueChange={v => setNa({ ...na, region: v, city: v === 'Dire Dawa' ? 'Dire Dawa' : v === 'Addis Ababa' ? 'Addis Ababa' : na.city })}>
                            <SelectTrigger aria-label="Region"><SelectValue /></SelectTrigger>
                            <SelectContent>{zones.map(z => <SelectItem key={z.id} value={z.name}>{z.name}</SelectItem>)}</SelectContent>
                          </Select>
                        </div>
                        <div><Label htmlFor="na-city">City / Town</Label><Input id="na-city" value={na.city} onChange={e => setNa({ ...na, city: e.target.value })} /></div>
                      </div>
                      <div className="grid grid-cols-3 gap-3">
                        <div><Label htmlFor="na-sc">Sub-city</Label><Input id="na-sc" value={na.subCity} onChange={e => setNa({ ...na, subCity: e.target.value })} placeholder="Bole" /></div>
                        <div><Label htmlFor="na-w">Woreda *</Label><Input id="na-w" value={na.woreda} onChange={e => setNa({ ...na, woreda: e.target.value })} placeholder="03" /></div>
                        <div><Label htmlFor="na-k">Kebele</Label><Input id="na-k" value={na.kebele} onChange={e => setNa({ ...na, kebele: e.target.value })} placeholder="08" /></div>
                      </div>
                      <div><Label htmlFor="na-l">Landmark *</Label><Input id="na-l" value={na.landmark} onChange={e => setNa({ ...na, landmark: e.target.value })} placeholder="e.g. Near Getu Commercial, behind Zefmesh Grand Mall" /></div>
                      <div><Label htmlFor="na-phone">Phone *</Label><Input id="na-phone" value={na.phone} onChange={e => setNa({ ...na, phone: e.target.value })} placeholder="+2519XXXXXXXX" /></div>
                      <div className="flex items-center gap-2">
                        <Checkbox id="save-addr" checked={saveNew} onCheckedChange={v => setSaveNew(v === true)} />
                        <Label htmlFor="save-addr" className="text-xs">Save to my address book</Label>
                      </div>
                      <div className="flex gap-2">
                        <Button size="sm" variant="outline" onClick={() => { setShowNewAddr(false); setError('') }}>Cancel</Button>
                        <Button size="sm" disabled={busy || !addressValid} onClick={saveNewAddress}>Save address</Button>
                      </div>
                    </div>
                  )}

                  {selAddr && !showNewAddr && (
                    <>
                      <div className="mt-4">
                        <Label>Contact phone</Label>
                        <Input value={phone} onChange={e => setPhone(e.target.value)} placeholder="+2519XXXXXXXX" className="mt-1" />
                      </div>
                      <div className="mt-3">
                        <Label>Delivery zone</Label>
                        <Select value={zone?.id ?? selZoneId} onValueChange={setSelZoneId}>
                          <SelectTrigger aria-label="Delivery zone" className="mt-1"><SelectValue placeholder="Select your region…" /></SelectTrigger>
                          <SelectContent>
                            {zones.map(z => <SelectItem key={z.id} value={z.id}>{z.name} — {z.baseFeeETB} Br{z.freeThreshold > 0 ? ` (free over ${z.freeThreshold} Br)` : ''}</SelectItem>)}
                          </SelectContent>
                        </Select>
                        {zone && <p className="mt-1.5 text-xs text-muted-foreground">{deliveryFee === 0 ? '🎉 Free delivery threshold reached' : `${zone.baseFeeETB} Br base + ${zone.perKgSurcharge} Br per kg over 5kg`}</p>}
                      </div>
                    </>
                  )}

                  <Button className="mt-4 w-full" disabled={!addressValid || !zone || !/^\+251[79]\d{8}$/.test(orderPhone) || (reservation ? !reservation.allOk : false)}
                    onClick={() => setStep('review')}>
                    Continue to review
                  </Button>
                  {orderPhone && !/^\+251[79]\d{8}$/.test(orderPhone) && <p className="mt-1 text-xs text-red-600">Phone must match +2519XXXXXXXX format</p>}
                  {reservation && !reservation.allOk && <p className="mt-1 text-xs text-red-600">Some items lost stock while you were shopping — adjust quantities in the cart.</p>}
                  {error && <p className="mt-1 text-xs text-red-600">{error}</p>}
                </>
              ) : (
                <>
                  <Button variant="ghost" size="sm" className="-ml-2 mb-2" onClick={() => setStep('address')}><ChevronLeft className="h-4 w-4" />Edit delivery</Button>
                  <h2 className="text-lg font-semibold">Review your order</h2>
                  <p className="mt-2 text-sm text-muted-foreground">
                    {showNewAddr ? `${na.subCity ? na.subCity + ' Sub-City, ' : ''}Woreda ${na.woreda}${na.kebele ? `, Kebele ${na.kebele}` : ''}, ${na.city}` : selAddr ? `${selAddr.subCity ? selAddr.subCity + ' Sub-City, ' : ''}Woreda ${selAddr.woreda}, ${selAddr.city}` : ''}
                    {' '}· {zone?.name} · {orderPhone}
                  </p>
                  <Separator className="my-4" />
                  {cart.items.map(i => {
                    const unit = i.variant?.priceETB ?? lineUnitPrice(i.product, i.color)
                    return (
                      <div key={i.id} className="flex items-center gap-3 py-2">
                        <ProductPhoto src={i.product.gallery?.[0]?.url} fallbackImages={i.product.images} title={i.product.title} sizes="48px" className="h-12 w-12 rounded-lg" />
                        <div className="min-w-0 flex-1">
                          <div className="truncate text-sm font-medium">{i.product.title}</div>
                          <div className="text-xs text-muted-foreground">
                            {i.variant?.sku && <span className="mr-2">{i.variant.size ? `Size ${i.variant.size} · ` : ''}{i.variant.sku}</span>}
                            {i.color && !i.variant && <span className="mr-2 inline-flex items-center gap-1"><span className="inline-block h-2.5 w-2.5 rounded-full border border-black/10" style={{ background: i.color.hex }} aria-hidden />{i.color.name}</span>}× {i.quantity}
                          </div>
                        </div>
                        <span className="text-sm">{br(unit * i.quantity)}</span>
                      </div>
                    )
                  })}
                  <Button className="mt-4 w-full" size="lg" disabled={busy || (reservation ? !reservation.allOk : false)} onClick={() => setStep('pay')}>Continue to payment</Button>
                </>
              )}
            </CardContent>
          </Card>

          <div className="space-y-4">
            <Card className="h-fit">
              <CardContent className="p-5 text-sm">
                <h3 className="font-semibold">Order summary</h3>
                {/* coupon */}
                <div className="mt-3">
                  <Label htmlFor="coupon" className="text-xs text-muted-foreground">Coupon code</Label>
                  <div className="mt-1 flex gap-2">
                    <div className="relative flex-1">
                      <Ticket className="absolute left-2.5 top-2.5 h-4 w-4 text-muted-foreground" aria-hidden />
                      <Input id="coupon" value={couponCode} onChange={e => { setCouponCode(e.target.value.toUpperCase()); setCoupon(null) }} placeholder="WELCOME10" className="pl-8 uppercase" />
                    </div>
                    <Button variant="outline" size="sm" disabled={!couponCode.trim() || busy} onClick={checkCoupon}>Apply</Button>
                  </div>
                  {coupon && <p className={`mt-1.5 text-xs ${coupon.ok ? 'text-emerald-700' : 'text-red-600'}`}>{coupon.message}</p>}
                </div>
                <Separator className="my-3" />
                <div className="space-y-1.5">
                  <div className="flex justify-between"><span className="text-muted-foreground">Subtotal</span><span>{br(subtotal)}</span></div>
                  {discount > 0 && <div className="flex justify-between text-emerald-700"><span>Discount</span><span>−{br(discount)}</span></div>}
                  <div className="flex justify-between"><span className="text-muted-foreground">Delivery {zone ? `(${zone.name})` : ''}</span><span>{deliveryFee === 0 ? 'FREE' : br(deliveryFee)}</span></div>
                  <div className="flex justify-between"><span className="text-muted-foreground">VAT (15%)</span><span>{br(vat)}</span></div>
                  <Separator className="my-2" />
                  <div className="flex justify-between text-base font-semibold"><span>Total</span><span>{br(grand)}</span></div>
                </div>
                <p className="mt-3 text-[11px] leading-4 text-muted-foreground">By placing this order you confirm agreement with our Terms of Service, Rules &amp; Regulations and Return &amp; Refund Policy. You&apos;ll receive a delivery PIN to confirm handoff.</p>
              </CardContent>
            </Card>
          </div>
        </div>
      )}

      {/* Mock hosted-gateway dialog */}
      <Dialog open={payOpen} onOpenChange={(v) => { setPayOpen(v); if (!v && payStatus === 'waiting') app.notify('Payment cancelled — order remains unpaid', 'err') }}>
        <DialogContent className="sm:max-w-md" aria-describedby={undefined}>
          <DialogHeader>
            <DialogTitle className="flex items-center gap-2"><ProviderIcon className="h-5 w-5 text-primary" />{PROVIDERS.find(p => p.id === provider)?.name} Secure Checkout</DialogTitle>
          </DialogHeader>
          {payStatus === 'waiting' ? (
            <div className="py-2 text-center">
              <p className="text-sm text-muted-foreground">Authorize {br(order?.grandTotalETB ?? 0)} to <strong>MarketHub Technologies PLC</strong></p>
              <p className="mt-1 font-mono text-xs text-muted-foreground">Ref: {order?.payments?.[0]?.providerRef}</p>
              <div className="mx-auto mt-4 h-1 w-40 overflow-hidden rounded bg-muted"><div className="h-full w-1/3 animate-pulse rounded bg-primary" /></div>
              <Button className="mt-5 w-full" disabled={busy} onClick={confirmPayment}>Confirm payment</Button>
              <p className="mt-2 text-[11px] text-muted-foreground">Mock gateway — mirrors the real provider&apos;s hosted flow + signed webhook.</p>
            </div>
          ) : (
            <div className="py-6 text-center"><CheckCircle2 className="mx-auto h-12 w-12 text-emerald-600" /><p className="mt-2 font-medium">Payment confirmed</p></div>
          )}
        </DialogContent>
      </Dialog>
    </main>
  )
}

