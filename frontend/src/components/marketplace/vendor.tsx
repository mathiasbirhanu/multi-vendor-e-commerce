// Vendor console v2: dashboard, order queue (accept / ship w/ tracking / deliver w/ PIN),
// products (inline variant stock & price editor, CSV bulk import), returns, reviews (reply),
// earnings & payouts (12h SLA), performance scorecard.
'use client'
import { useCallback, useEffect, useState } from 'react'
import { Area, AreaChart, Bar, BarChart, CartesianGrid, ResponsiveContainer, Tooltip as ChartTooltip, XAxis, YAxis } from 'recharts'
import { api, br, brShort, Category, dateTimeFmt, Order, Product, ProductVariant, Payout, ReturnRequest, Review, VendorDashboard, VendorEarnings, VendorPerformance } from '@/lib/api'
import { useApp } from '@/store/app'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { Badge } from '@/components/ui/badge'
import { Switch } from '@/components/ui/switch'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { ColorDots, KpiCard, ProductPhoto, StatusBadge, Stars, defaultPhoto } from './shared'
import { ProductFormDialog } from './product-form'
import { Banknote, BarChart3, CheckCircle2, ClipboardList, Clock, FileUp, MessageSquareReply, Package, RotateCcw, ShieldCheck, Star, Store, TrendingUp, Truck, Wallet, XCircle } from 'lucide-react'

export function VendorConsole() {
  const app = useApp()
  const v = app.user?.vendor
  if (!app.user || app.user.role !== 'Vendor') {
    return <main className="flex flex-col items-center justify-center gap-3 py-32 text-sm text-muted-foreground"><Store className="h-10 w-10 text-muted-foreground/40" /><p>Vendor access only.</p><Button onClick={() => app.openAuth('signin')}>Sign in as vendor</Button></main>
  }
  if (v && v.status !== 'Approved') {
    return (
      <main className="mx-auto max-w-lg py-24 text-center">
        <Card><CardContent className="p-10">
          <Clock className="mx-auto h-12 w-12 text-amber-500" />
          <h1 className="mt-3 text-xl font-semibold">Application {v.status === 'PendingReview' ? 'under review' : v.status.toLowerCase()}</h1>
          <p className="mt-2 text-sm text-muted-foreground">{v.status === 'PendingReview'
            ? 'The admin team is verifying your legal documents (business registration license, trade license, TIN certificate). You will be notified the moment a decision is made — usually within one business day.'
            : v.status === 'Rejected' ? `Your application was rejected: ${v.rejectionReason ?? 'contact support'}. You may correct your documents and re-apply.` : 'Your store is currently suspended. Contact the platform team.'}</p>
          <Button variant="outline" className="mt-6" onClick={() => app.go('home')}>Back to marketplace</Button>
        </CardContent></Card>
      </main>
    )
  }

  return (
    <main className="mx-auto max-w-7xl px-4 py-8">
      <div className="mb-6">
        <h1 className="text-2xl font-semibold tracking-tight">{v?.businessName}</h1>
        <p className="text-sm text-muted-foreground">Vendor console — orders, stock, returns, reviews, earnings &amp; payouts (12h processing SLA)</p>
      </div>
      <Tabs defaultValue="dashboard">
        <TabsList className="flex-wrap">
          <TabsTrigger value="dashboard">Dashboard</TabsTrigger>
          <TabsTrigger value="orders">Orders</TabsTrigger>
          <TabsTrigger value="products">Products</TabsTrigger>
          <TabsTrigger value="returns">Returns</TabsTrigger>
          <TabsTrigger value="reviews">Reviews</TabsTrigger>
          <TabsTrigger value="earnings">Earnings &amp; Payouts</TabsTrigger>
          <TabsTrigger value="performance">Performance</TabsTrigger>
        </TabsList>
        <TabsContent value="dashboard"><VendorDashboardTab /></TabsContent>
        <TabsContent value="orders"><VendorOrders /></TabsContent>
        <TabsContent value="products"><VendorProducts /></TabsContent>
        <TabsContent value="returns"><VendorReturns /></TabsContent>
        <TabsContent value="reviews"><VendorReviews /></TabsContent>
        <TabsContent value="earnings"><Earnings /></TabsContent>
        <TabsContent value="performance"><VendorPerformanceTab /></TabsContent>
      </Tabs>
    </main>
  )
}

// ---------------- Dashboard ----------------
function VendorDashboardTab() {
  const [data, setData] = useState<VendorDashboard | null>(null)
  useEffect(() => { api.get<VendorDashboard>('/api/vendor/dashboard').then(setData).catch(() => {}) }, [])
  if (!data) return <Skeleton className="h-96 rounded-xl" />
  const chart = data.daily.map(d => ({ date: d.date.slice(5), Revenue: d.revenue, Orders: d.orders }))
  return (
    <div className="space-y-6">
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <KpiCard label="Products" value={String(data.totals.products)} sub="live + under review" />
        <KpiCard label="Active orders" value={String(data.totals.activeOrders)} sub="paid, awaiting fulfilment" tone="primary" />
        <KpiCard label="Open returns" value={String(data.totals.pendingReturns)} sub="need your response (48h)" tone={data.totals.pendingReturns > 0 ? 'warn' : 'default'} />
        <KpiCard label="Low stock" value={String(data.totals.lowStock)} sub="variants with ≤ 3 units" tone={data.totals.lowStock > 0 ? 'warn' : 'default'} />
      </div>
      <Card>
        <CardContent className="p-5">
          <h3 className="mb-3 flex items-center gap-2 text-sm font-semibold"><BarChart3 className="h-4 w-4 text-primary" />Last 14 days — revenue &amp; orders</h3>
          {chart.every(c => c.Revenue === 0) ? <p className="py-10 text-center text-sm text-muted-foreground">No sales in the last 14 days.</p> : (
            <ResponsiveContainer width="100%" height={240}>
              <AreaChart data={chart} margin={{ top: 4, right: 8, bottom: 0, left: 8 }}>
                <defs><linearGradient id="vrev" x1="0" y1="0" x2="0" y2="1"><stop offset="0%" stopColor="oklch(0.42 0.09 165)" stopOpacity={0.35} /><stop offset="100%" stopColor="oklch(0.42 0.09 165)" stopOpacity={0.02} /></linearGradient></defs>
                <CartesianGrid strokeDasharray="3 3" stroke="oklch(0.915 0.008 140)" />
                <XAxis dataKey="date" tick={{ fontSize: 11 }} tickLine={false} axisLine={false} />
                <YAxis tick={{ fontSize: 11 }} tickLine={false} axisLine={false} tickFormatter={(v: number) => brShort(v)} width={60} />
                <ChartTooltip formatter={(v, n) => [n === 'Revenue' ? br(Number(v)) : String(v), n as string]} contentStyle={{ borderRadius: 10, fontSize: 12 }} />
                <Area type="monotone" dataKey="Revenue" stroke="oklch(0.42 0.09 165)" strokeWidth={2} fill="url(#vrev)" />
              </AreaChart>
            </ResponsiveContainer>
          )}
        </CardContent>
      </Card>
    </div>
  )
}

// ---------------- Orders ----------------
function VendorOrders() {
  const app = useApp()
  const [orders, setOrders] = useState<Order[] | null>(null)
  const [expanded, setExpanded] = useState<string | null>(null)
  const [shipDlg, setShipDlg] = useState<Order | null>(null)
  const [tracking, setTracking] = useState('')
  const [pinDlg, setPinDlg] = useState<Order | null>(null)
  const [pin, setPin] = useState('')
  const [busy, setBusy] = useState(false)

  const load = useCallback(() => { api.get<{ items: Order[] }>('/api/vendor/orders').then(r => setOrders(r.items)).catch(() => setOrders([])) }, [])
  useEffect(load, [load])

  const act = async (orderId: string, action: 'accept' | 'reject') => {
    try {
      await api.post(`/api/vendor/orders/action/${orderId}`, { action })
      app.notify(action === 'accept' ? 'Order accepted — prepare the items for shipment' : 'Order rejected — buyer refund initiated')
      load()
    } catch (e) { app.notify(e instanceof Error ? e.message : 'Action failed', 'err') }
  }
  const ship = async () => {
    if (!shipDlg) return
    setBusy(true)
    try {
      await api.post(`/api/vendor/orders/action/${shipDlg.id}`, { action: 'ship', trackingCode: tracking.trim() })
      app.notify('Order marked as shipped — buyer notified with the tracking code')
      setShipDlg(null); setTracking(''); load()
    } catch (e) { app.notify(e instanceof Error ? e.message : 'Ship failed', 'err') }
    finally { setBusy(false) }
  }
  const deliver = async () => {
    if (!pinDlg) return
    setBusy(true)
    try {
      await api.post(`/api/vendor/orders/action/${pinDlg.id}`, { action: 'deliver', pin: pin.trim() })
      app.notify('Delivery confirmed with PIN — order complete')
      setPinDlg(null); setPin(''); load()
    } catch (e) { app.notify(e instanceof Error ? e.message : 'PIN did not match', 'err') }
    finally { setBusy(false) }
  }

  if (!orders) return <Skeleton className="h-96 rounded-xl" />
  if (orders.length === 0) return <div className="flex flex-col items-center gap-3 py-24 text-sm text-muted-foreground"><Package className="h-8 w-8 text-muted-foreground/40" />No orders yet.</div>

  return (
    <div className="space-y-3">
      {orders.map(o => (
        <Card key={o.id}>
          <CardContent className="p-4">
            <button className="flex w-full flex-wrap items-center gap-x-4 gap-y-1 text-left" onClick={() => setExpanded(expanded === o.id ? null : o.id)} aria-expanded={expanded === o.id}>
              <span className="font-mono text-xs text-muted-foreground">{o.number}</span>
              <StatusBadge status={o.status} />
              <span className="text-xs text-muted-foreground">{dateTimeFmt(o.createdAt)}</span>
              {o.trackingCode && <Badge variant="outline" className="font-mono text-[10px]"><Truck className="mr-1 h-2.5 w-2.5" />{o.trackingCode}</Badge>}
              <span className="ml-auto text-sm font-semibold">{br(o.items.reduce((a, i) => a + i.lineNetETB - i.commissionETB, 0))}<span className="ml-1 text-xs font-normal text-muted-foreground">net</span></span>
            </button>
            {expanded === o.id && (
              <div className="mt-3 border-t pt-3">
                {o.items.map(i => (
                  <div key={i.id} className="flex flex-wrap items-center gap-x-3 py-1 text-sm">
                    <span className="min-w-0 flex-1 truncate">{i.titleSnapshot}{i.variantLabel && <span className="text-xs text-muted-foreground"> · {i.variantLabel}</span>} <span className="text-muted-foreground">× {i.quantity}</span></span>
                    <span className="text-xs text-muted-foreground">gross {br(i.lineNetETB)}</span>
                    <span className="text-xs text-muted-foreground">−{br(i.commissionETB)} commission</span>
                    <span className="font-medium">{br(i.lineNetETB - i.commissionETB)}</span>
                  </div>
                ))}
                <div className="mt-2 text-xs text-muted-foreground">Ship to: {o.addressLine}, {o.city} ({o.zoneName}) · {o.phone}</div>
                <div className="mt-3 flex flex-wrap gap-2">
                  {o.status === 'PaymentConfirmed' && <>
                    <Button size="sm" onClick={() => act(o.id, 'accept')}>Accept order</Button>
                    <Button size="sm" variant="outline" className="text-red-600" onClick={() => act(o.id, 'reject')}>Reject</Button>
                  </>}
                  {o.status === 'VendorAccepted' && <Button size="sm" onClick={() => { setShipDlg(o); setTracking(o.trackingCode ?? '') }}><Truck className="mr-1 h-3.5 w-3.5" />Ship (add tracking)</Button>}
                  {o.status === 'Shipped' && <Button size="sm" onClick={() => setPinDlg(o)}><CheckCircle2 className="mr-1 h-3.5 w-3.5" />Confirm delivery (PIN)</Button>}
                  {o.status === 'RefundRequested' && <Badge variant="outline" className="border-orange-300 text-orange-700">Buyer return requested — see Returns tab</Badge>}
                </div>
              </div>
            )}
          </CardContent>
        </Card>
      ))}

      {/* ship dialog */}
      <Dialog open={!!shipDlg} onOpenChange={v => !v && setShipDlg(null)}>
        <DialogContent className="sm:max-w-md">
          <DialogHeader><DialogTitle>Ship order {shipDlg?.number}</DialogTitle><DialogDescription>Enter the courier tracking code — the buyer sees it on their order timeline and gets an SMS.</DialogDescription></DialogHeader>
          <div><Label htmlFor="trk">Tracking code</Label><Input id="trk" value={tracking} onChange={e => setTracking(e.target.value)} placeholder="e.g. DEL-4471-AA" className="font-mono" /></div>
          <DialogFooter><Button variant="outline" onClick={() => setShipDlg(null)}>Cancel</Button><Button disabled={busy || tracking.trim().length < 4} onClick={ship}>Confirm shipment</Button></DialogFooter>
        </DialogContent>
      </Dialog>

      {/* PIN delivery dialog */}
      <Dialog open={!!pinDlg} onOpenChange={v => !v && setPinDlg(null)}>
        <DialogContent className="sm:max-w-md">
          <DialogHeader><DialogTitle>Confirm delivery — {pinDlg?.number}</DialogTitle><DialogDescription>Ask the buyer for their 4-digit delivery PIN at handoff. This proves the items were received.</DialogDescription></DialogHeader>
          <div><Label htmlFor="pin">Delivery PIN</Label><Input id="pin" value={pin} onChange={e => setPin(e.target.value.replace(/\D/g, '').slice(0, 4))} placeholder="••••" className="text-center font-mono text-2xl tracking-[0.5em]" inputMode="numeric" maxLength={4} /></div>
          <DialogFooter><Button variant="outline" onClick={() => setPinDlg(null)}>Cancel</Button><Button disabled={busy || pin.length !== 4} onClick={deliver}>Confirm handoff</Button></DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  )
}

// ---------------- Products + variants + CSV bulk ----------------
function VendorProducts() {
  const app = useApp()
  const [products, setProducts] = useState<Product[] | null>(null)
  const [cats, setCats] = useState<Category[]>([])
  const [open, setOpen] = useState(false)
  const [bulkOpen, setBulkOpen] = useState(false)
  const [csv, setCsv] = useState('title,description,priceETB,stock,categorySlug,sku\n')
  const [bulkResult, setBulkResult] = useState<{ created: number; errors: string[] } | null>(null)
  const [editing, setEditing] = useState<Record<string, { priceETB: string; stock: string }>>({})

  const load = useCallback(() => {
    api.get<{ items: Product[] }>('/api/vendor/products').then(r => setProducts(r.items)).catch(() => setProducts([]))
    api.get<{ items: Category[] }>('/api/catalog/categories').then(r => setCats(r.items)).catch(() => {})
  }, [])
  useEffect(load, [load])

  const patchVariant = async (id: string, body: { priceETB?: number; stock?: number; isActive?: boolean }) => {
    try { await api.patch(`/api/vendor/variants/${id}`, body); app.notify('Variant updated'); load() }
    catch (e) { app.notify(e instanceof Error ? e.message : 'Update failed', 'err') }
  }

  const bulkImport = async () => {
    try {
      const r = await api.post<{ created: number; errors: string[] }>('/api/vendor/products/bulk', { csv })
      setBulkResult(r)
      if (r.created > 0) { app.notify(`${r.created} products imported (pending review)`); load() }
    } catch (e) { app.notify(e instanceof Error ? e.message : 'Import failed', 'err') }
  }

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-sm text-muted-foreground">Listings need admin approval before going live. Manage per-variant stock &amp; prices inline.</p>
        <div className="flex gap-2">
          <Button size="sm" variant="outline" onClick={() => { setBulkResult(null); setBulkOpen(true)}}><FileUp className="mr-1 h-3.5 w-3.5" />Bulk CSV</Button>
          <Button size="sm" onClick={() => setOpen(true)}>+ Add product</Button>
        </div>
      </div>
      {!products ? <Skeleton className="h-96 rounded-xl" /> : products.length === 0 ? <div className="py-24 text-center text-sm text-muted-foreground">No products yet — add your first product or import a CSV.</div> : (
        <Table>
          <TableHeader><TableRow><TableHead className="w-12">Photo</TableHead><TableHead>Product</TableHead><TableHead>Price</TableHead><TableHead>Stock / Variants</TableHead><TableHead>Status</TableHead></TableRow></TableHeader>
          <TableBody>
            {products.map(p => (
              <TableRow key={p.id}>
                <TableCell><ProductPhoto src={defaultPhoto(p.gallery)?.url} fallbackImages={p.images} title={p.title} className="h-10 w-10 rounded-md" /></TableCell>
                <TableCell><div className="font-medium">{p.title}</div><div className="text-xs text-muted-foreground">{p.sku} · {p.ratingAvg.toFixed(1)}★ ({p.ratingCount})</div></TableCell>
                <TableCell>
                  <div className="font-medium">{br(p.priceETB)}</div>
                  {(p.salePriceETB ?? 0) > 0 && <Badge variant="outline" className="border-red-200 text-[10px] text-red-700">sale {br(p.salePriceETB!)}</Badge>}
                </TableCell>
                <TableCell>
                  <div className="text-sm">{p.stock} total</div>
                  {(p.colors?.length ?? 0) > 0 && <div className="mt-0.5 flex items-center gap-1.5"><ColorDots colors={p.colors} max={5} /><span className="text-[10px] text-muted-foreground">{p.colors!.map(c => c.stock).join('/')}</span></div>}
                  {(p.variants ?? []).length > 0 && (
                    <div className="mt-1.5 space-y-1">
                      {p.variants!.map(v => (
                        <div key={v.id} className="flex items-center gap-1.5 text-[11px]">
                          <Switch checked={v.isActive} onCheckedChange={on => patchVariant(v.id, { isActive: on })} aria-label="Variant active" />
                          <span className="min-w-16 truncate text-muted-foreground">{v.size ? `Sz ${v.size}` : 'std'}{v.colorHex ? '' : ''}</span>
                          <Input className="h-6 w-16 px-1 text-[11px]" defaultValue={v.priceETB ?? p.priceETB} onBlur={e => { const n = parseFloat(e.target.value); if (!isNaN(n) && n > 0 && n !== (v.priceETB ?? p.priceETB)) patchVariant(v.id, { priceETB: n }) }} aria-label="Variant price" />
                          <Input className="h-6 w-14 px-1 text-[11px]" defaultValue={v.stock} onBlur={e => { const n = parseInt(e.target.value, 10); if (!isNaN(n) && n >= 0 && n !== v.stock) patchVariant(v.id, { stock: n }) }} aria-label="Variant stock" />
                          <span className={v.stock <= 3 ? 'font-medium text-red-600' : 'text-muted-foreground'}>{v.stock <= 3 ? 'low!' : ''}</span>
                        </div>
                      ))}
                    </div>
                  )}
                </TableCell>
                <TableCell><StatusBadge status={p.status} />{p.moderationNote && <p className="mt-1 max-w-36 truncate text-[10px] text-muted-foreground">{p.moderationNote}</p>}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}

      <ProductFormDialog open={open} onOpenChange={setOpen} categories={cats} onCreated={load} />

      {/* CSV bulk import */}
      <Dialog open={bulkOpen} onOpenChange={setBulkOpen}>
        <DialogContent className="sm:max-w-lg">
          <DialogHeader>
            <DialogTitle>Bulk import products (CSV)</DialogTitle>
            <DialogDescription>Columns: <code className="rounded bg-muted px-1">title,description,priceETB,stock,categorySlug,sku</code> — one product per line. Each lands as PendingReview with one default variant; add colors &amp; photos after import.</DialogDescription>
          </DialogHeader>
          <Textarea rows={8} value={csv} onChange={e => setCsv(e.target.value)} className="font-mono text-xs" aria-label="CSV content" />
          {bulkResult && (
            <div className="rounded-lg border p-3 text-xs">
              <p className="font-medium text-emerald-700">{bulkResult.created} product(s) created</p>
              {bulkResult.errors.length > 0 && (
                <ul className="mt-1.5 list-inside list-disc space-y-0.5 text-red-600">{bulkResult.errors.slice(0, 6).map((e, i) => <li key={i}>{e}</li>)}</ul>
              )}
            </div>
          )}
          <DialogFooter><Button variant="outline" onClick={() => setBulkOpen(false)}>Close</Button><Button onClick={bulkImport}>Import</Button></DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  )
}

// ---------------- Returns ----------------
function VendorReturns() {
  const app = useApp()
  const [items, setItems] = useState<ReturnRequest[] | null>(null)
  const [dlg, setDlg] = useState<{ r: ReturnRequest; approve: boolean } | null>(null)
  const [note, setNote] = useState('')
  const [busy, setBusy] = useState(false)

  const load = useCallback(() => { api.get<{ items: ReturnRequest[] }>('/api/vendor/returns').then(r => setItems(r.items)).catch(() => setItems([])) }, [])
  useEffect(load, [load])

  const decide = async () => {
    if (!dlg) return
    setBusy(true)
    try {
      await api.post(`/api/vendor/returns/${dlg.r.id}/decision`, { approve: dlg.approve, note: note || undefined })
      app.notify(dlg.approve ? 'Return approved — MarketHub will issue the refund' : 'Return rejected — buyer notified, admin informed')
      setDlg(null); setNote(''); load()
    } catch (e) { app.notify(e instanceof Error ? e.message : 'Decision failed', 'err') }
    finally { setBusy(false) }
  }

  if (!items) return <Skeleton className="h-96 rounded-xl" />
  if (items.length === 0) return <div className="flex flex-col items-center gap-3 py-24 text-sm text-muted-foreground"><RotateCcw className="h-8 w-8 text-muted-foreground/40" />No return requests.</div>

  return (
    <div className="space-y-3">
      {items.map(r => (
        <Card key={r.id}>
          <CardContent className="p-4">
            <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
              <StatusBadge status={r.status} />
              <span className="text-sm font-medium">{r.itemTitle}</span>
              <span className="font-mono text-xs text-muted-foreground">{r.orderNumber}</span>
              <span className="ml-auto text-xs text-muted-foreground">{dateTimeFmt(r.createdAt)}</span>
            </div>
            <p className="mt-2 text-sm text-muted-foreground"><Badge variant="outline" className="mr-2">{r.reason}</Badge>{r.description}</p>
            {(r.evidenceUrls?.length ?? 0) > 0 && (
              <div className="mt-2 flex gap-2">
                {r.evidenceUrls!.slice(0, 3).map((u, i) => <ProductPhoto key={i} src={u} title="evidence" sizes="56px" className="h-14 w-14 rounded-lg" />)}
              </div>
            )}
            {r.vendorNote && <p className="mt-2 rounded-lg bg-muted/50 p-2 text-xs">Your response: {r.vendorNote}</p>}
            {r.status === 'Requested' && (
              <div className="mt-3 flex gap-2">
                <Button size="sm" onClick={() => { setDlg({ r, approve: true }); setNote('') }}><CheckCircle2 className="mr-1 h-3.5 w-3.5" />Approve return</Button>
                <Button size="sm" variant="outline" className="text-red-600" onClick={() => { setDlg({ r, approve: false }); setNote('') }}><XCircle className="mr-1 h-3.5 w-3.5" />Reject</Button>
              </div>
            )}
          </CardContent>
        </Card>
      ))}
      <Dialog open={!!dlg} onOpenChange={v => !v && setDlg(null)}>
        <DialogContent className="sm:max-w-md">
          <DialogHeader><DialogTitle>{dlg?.approve ? 'Approve' : 'Reject'} return</DialogTitle><DialogDescription>{dlg?.approve ? 'MarketHub will process the refund to the buyer (within 5 business days).' : 'Explain why — the buyer and the MarketHub team both see your note.'}</DialogDescription></DialogHeader>
          <Textarea rows={3} value={note} onChange={e => setNote(e.target.value)} placeholder={dlg?.approve ? 'Optional note…' : 'Required rejection reason…'} />
          <DialogFooter><Button variant="outline" onClick={() => setDlg(null)}>Cancel</Button><Button disabled={busy || (!dlg?.approve && note.trim().length < 5)} onClick={decide} className={dlg?.approve ? '' : 'text-red-600'}>{dlg?.approve ? 'Approve' : 'Reject'}</Button></DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  )
}

// ---------------- Reviews ----------------
function VendorReviews() {
  const app = useApp()
  const [items, setItems] = useState<Review[] | null>(null)
  const [replyFor, setReplyFor] = useState<Review | null>(null)
  const [reply, setReply] = useState('')
  const [busy, setBusy] = useState(false)

  const load = useCallback(() => {
    api.get<{ items: Review[] }>('/api/vendor/reviews').then(r => setItems(r.items)).catch(() => setItems([]))
  }, [])
  useEffect(load, [load])

  const sendReply = async () => {
    if (!replyFor) return
    setBusy(true)
    try {
      await api.post(`/api/vendor/reviews/${replyFor.id}/reply`, { reply })
      app.notify('Reply posted — visible under the review')
      setReplyFor(null); setReply(''); load()
    } catch (e) { app.notify(e instanceof Error ? e.message : 'Reply failed', 'err') }
    finally { setBusy(false) }
  }

  if (!items) return <Skeleton className="h-96 rounded-xl" />
  if (items.length === 0) return <div className="flex flex-col items-center gap-3 py-24 text-sm text-muted-foreground"><MessageSquareReply className="h-8 w-8 text-muted-foreground/40" />No reviews on your products yet.</div>

  return (
    <div className="space-y-3">
      {items.map(r => (
        <Card key={r.id}>
          <CardContent className="p-4">
            <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
              <Stars rating={r.rating} />
              <span className="text-xs text-muted-foreground">{r.userName ?? 'Buyer'} on</span>
              <span className="text-sm font-medium">{r.product?.title ?? r.productTitle}</span>
              <StatusBadge status={r.status} />
              <span className="ml-auto text-xs text-muted-foreground">{dateTimeFmt(r.createdAt)}</span>
            </div>
            {r.title && <p className="mt-1.5 text-sm font-medium">{r.title}</p>}
            <p className="mt-0.5 text-sm text-muted-foreground">{r.body}</p>
            {r.vendorReply && <p className="mt-2 rounded-lg bg-muted/50 p-2 text-xs">Your reply: {r.vendorReply}</p>}
            {!r.vendorReply && <Button size="sm" variant="outline" className="mt-2" onClick={() => { setReplyFor(r); setReply('') }}><MessageSquareReply className="mr-1 h-3.5 w-3.5" />Reply</Button>}
          </CardContent>
        </Card>
      ))}
      <Dialog open={!!replyFor} onOpenChange={v => !v && setReplyFor(null)}>
        <DialogContent className="sm:max-w-md">
          <DialogHeader><DialogTitle>Reply to review</DialogTitle><DialogDescription>Professional, short replies lift your seller rating.</DialogDescription></DialogHeader>
          <Textarea rows={3} value={reply} onChange={e => setReply(e.target.value)} placeholder="Thank the buyer, address the issue, offer a fix…" />
          <DialogFooter><Button variant="outline" onClick={() => setReplyFor(null)}>Cancel</Button><Button disabled={busy || reply.trim().length < 3} onClick={sendReply}>Post reply</Button></DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  )
}

// ---------------- Performance ----------------
function VendorPerformanceTab() {
  const [perf, setPerf] = useState<VendorPerformance | null>(null)
  useEffect(() => { api.get<VendorPerformance>('/api/vendor/performance').then(setPerf).catch(() => {}) }, [])
  if (!perf) return <Skeleton className="h-96 rounded-xl" />
  const metric = (label: string, value: string, good: boolean, sub: string) => (
    <div className={`rounded-2xl border p-4 ${good ? 'border-emerald-200 bg-emerald-50/50' : 'border-amber-200 bg-amber-50/50'}`}>
      <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">{label}</p>
      <p className="mt-1 text-2xl font-semibold">{value}</p>
      <p className="mt-0.5 text-xs text-muted-foreground">{sub}</p>
    </div>
  )
  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3 rounded-2xl border bg-gradient-to-r from-emerald-50 to-teal-50 p-5">
        <span className="flex h-12 w-12 items-center justify-center rounded-2xl bg-white shadow-sm"><TrendingUp className="h-6 w-6 text-emerald-700" /></span>
        <div>
          <p className="text-sm font-semibold">Seller scorecard</p>
          <p className="text-xs text-muted-foreground">Keep fulfillment above 90% and disputes near 0% to keep top placement in search.</p>
        </div>
        <div className="ml-auto text-right">
          <Stars rating={perf.ratingAvg} count={perf.ratingCount} className="text-sm" />
          <p className="mt-1 flex items-center justify-end gap-1 text-[11px] text-muted-foreground"><Star className="h-3 w-3 fill-amber-400 text-amber-400" />buyer rating</p>
        </div>
      </div>
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {metric('Fulfillment rate', `${perf.fulfillmentRate.toFixed(1)}%`, perf.fulfillmentRate >= 90, 'accepted & shipped ÷ paid orders')}
        {metric('Cancellation rate', `${perf.cancelRate.toFixed(1)}%`, perf.cancelRate <= 5, 'orders you rejected')}
        {metric('Dispute rate', `${perf.disputeRate.toFixed(1)}%`, perf.disputeRate <= 2, 'returns lost in arbitration')}
        {metric('Open returns', String(perf.openReturns), perf.openReturns === 0, 'awaiting your response')}
        {metric('Units sold', String(perf.totalSold), true, 'lifetime quantity')}
        {metric('Rated reviews', String(perf.ratingCount), true, 'approved buyer reviews')}
      </div>
    </div>
  )
}

// ---------------- Earnings (existing behaviour) ----------------
function Earnings() {
  const app = useApp()
  const [data, setData] = useState<VendorEarnings | null>(null)
  const [payouts, setPayouts] = useState<Payout[] | null>(null)
  const [open, setOpen] = useState(false)
  const [amount, setAmount] = useState('')
  const [method, setMethod] = useState('Telebirr')
  const [destination, setDestination] = useState('')
  const [busy, setBusy] = useState(false)

  const load = useCallback(() => {
    api.get<VendorEarnings>('/api/vendor/earnings').then(setData).catch(() => {})
    api.get<{ items: Payout[] }>('/api/vendor/payouts').then(r => setPayouts(r.items)).catch(() => {})
  }, [])
  useEffect(load, [load])

  const slaLeft = (dueBy: string) => {
    const ms = new Date(dueBy).getTime() - Date.now()
    if (ms <= 0) return 'OVERDUE'
    const h = Math.floor(ms / 36e5), m = Math.floor((ms % 36e5) / 6e4)
    return `${h}h ${m}m left`
  }

  const request = async () => {
    setBusy(true)
    try {
      await api.post('/api/vendor/payouts', { amount: parseFloat(amount), paymentMethod: method, destinationAccount: destination })
      app.notify(`Payout requested — MarketHub will process it within ${data?.slaHours ?? 12} hours`)
      setOpen(false); setAmount(''); load()
    } catch (e) { app.notify(e instanceof Error ? e.message : 'Request failed', 'err') }
    finally { setBusy(false) }
  }

  if (!data) return <Skeleton className="h-96 rounded-xl" />
  const chart = data.monthly.map(m => ({ month: m.month.slice(5) + '/' + m.month.slice(2, 4), Income: m.amount }))

  return (
    <div className="space-y-6">
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <KpiCard label="Ledger balance" value={brShort(data.balanceETB)} sub="lifetime vendor payable" tone="primary" />
        <KpiCard label="Available for payout" value={brShort(data.availableForPayout)} sub={data.pendingPayout > 0 ? `${br(data.pendingPayout)} locked in pending requests` : 'no pending requests'} />
        <KpiCard label="Pending payout" value={brShort(data.pendingPayout)} sub={data.pendingPayout > 0 ? `processing within ${data.slaHours}h SLA` : '—'} tone={data.pendingPayout > 0 ? 'warn' : 'default'} />
        <KpiCard label="Total paid out" value={brShort(data.totalPaidOut)} sub="completed transfers" />
      </div>

      <div className="grid gap-6 lg:grid-cols-2">
        <Card>
          <CardContent className="p-5">
            <div className="mb-3 flex items-center justify-between">
              <h3 className="text-sm font-semibold">Monthly vendor income</h3>
              <Button size="sm" onClick={() => setOpen(true)}><Wallet className="mr-1 h-3.5 w-3.5" />Request payout</Button>
            </div>
            {chart.length === 0 ? <p className="py-10 text-center text-sm text-muted-foreground">No income yet — start selling!</p> : (
              <ResponsiveContainer width="100%" height={220}>
                <BarChart data={chart} margin={{ top: 4, right: 8, bottom: 0, left: 8 }}>
                  <CartesianGrid strokeDasharray="3 3" stroke="oklch(0.915 0.008 140)" />
                  <XAxis dataKey="month" tick={{ fontSize: 11 }} tickLine={false} axisLine={false} />
                  <YAxis tick={{ fontSize: 11 }} tickLine={false} axisLine={false} tickFormatter={(v: number) => brShort(v)} width={60} />
                  <ChartTooltip formatter={(v) => br(Number(v))} contentStyle={{ borderRadius: 10, fontSize: 12 }} />
                  <Bar dataKey="Income" fill="oklch(0.42 0.09 165)" radius={[6, 6, 0, 0]} />
                </BarChart>
              </ResponsiveContainer>
            )}
          </CardContent>
        </Card>
        <Card>
          <CardContent className="p-5">
            <h3 className="mb-3 text-sm font-semibold">Payout requests</h3>
            {!payouts ? <Skeleton className="h-40 w-full" /> : payouts.length === 0 ? <p className="py-10 text-center text-sm text-muted-foreground">No payout requests yet.</p> : (
              <Table>
                <TableHeader><TableRow><TableHead>Amount</TableHead><TableHead>Method</TableHead><TableHead>Requested</TableHead><TableHead>SLA</TableHead><TableHead>Status</TableHead></TableRow></TableHeader>
                <TableBody>
                  {payouts.map(p => (
                    <TableRow key={p.id}>
                      <TableCell className="font-medium">{br(p.amountETB)}</TableCell>
                      <TableCell className="text-xs">{p.paymentMethod === 'Telebirr' ? 'telebirr' : 'bank'}</TableCell>
                      <TableCell className="text-xs text-muted-foreground">{dateTimeFmt(p.requestedAt)}</TableCell>
                      <TableCell>{['Requested', 'Processing'].includes(p.status) ? <Badge variant="outline" className={p.overdue ? 'border-red-300 text-red-700' : 'border-amber-300 text-amber-800'}>{slaLeft(p.dueBy)}</Badge> : <span className="text-xs text-muted-foreground">—</span>}</TableCell>
                      <TableCell><StatusBadge status={p.status} /></TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
            <p className="mt-3 flex items-start gap-1.5 text-[11px] leading-4 text-muted-foreground"><Banknote className="mt-0.5 h-3.5 w-3.5 shrink-0" />Funds are transferred to your telebirr wallet or bank account. The platform SLA guarantees processing within {data.slaHours} hours of your request.</p>
          </CardContent>
        </Card>
      </div>

      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent className="sm:max-w-md">
          <DialogHeader><DialogTitle>Request payout</DialogTitle><DialogDescription>Available balance: {br(data.availableForPayout)} · minimum 100 Br · processed within {data.slaHours} hours.</DialogDescription></DialogHeader>
          <div className="space-y-3">
            <div><Label htmlFor="amount">Amount (Br)</Label><Input id="amount" value={amount} onChange={e => setAmount(e.target.value)} inputMode="decimal" placeholder="e.g. 2500" /></div>
            <div>
              <Label>Transfer method</Label>
              <Select value={method} onValueChange={setMethod}>
                <SelectTrigger aria-label="Transfer method"><SelectValue /></SelectTrigger>
                <SelectContent><SelectItem value="Telebirr">telebirr wallet</SelectItem><SelectItem value="BankTransfer">Bank transfer (CBE / others)</SelectItem></SelectContent>
              </Select>
            </div>
            <div><Label htmlFor="dest">Destination account</Label><Input id="dest" value={destination} onChange={e => setDestination(e.target.value)} placeholder={method === 'Telebirr' ? '+2519…' : 'Account number'} /></div>
          </div>
          <DialogFooter><Button variant="outline" onClick={() => setOpen(false)}>Cancel</Button><Button disabled={busy || !amount || parseFloat(amount) < 100 || parseFloat(amount) > data.availableForPayout || !destination} onClick={request}>Submit request</Button></DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  )
}

void ClipboardList
void ShieldCheck
