// Admin console v2: reports (graphs + tables), vendor KYC approvals, product &
// review moderation, returns arbitration, orders + refunds, payouts (12h SLA),
// marketing (banners / flash sale / coupons), staff management, platform settings,
// ops (abandoned carts, outbound messages, job triggers).
'use client'
import { useCallback, useEffect, useState } from 'react'
import { Area, AreaChart, Bar, BarChart, CartesianGrid, Legend, ResponsiveContainer, Tooltip as ChartTooltip, XAxis, YAxis } from 'recharts'
import {
  api, br, brShort, Category, dateTimeFmt, AbandonedCart, AdminRole, AdminSettings, Banner, BulkImportResult,
  Coupon, Order, OutboundMessage, Payout, Product, ReportSummary, ReturnRequest, RevenueOverTime,
  Review, StaffMember, TopProduct, TopVendor, VendorApplication, VendorPerformance,
} from '@/lib/api'
import { useApp } from '@/store/app'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Badge } from '@/components/ui/badge'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Textarea } from '@/components/ui/textarea'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { Switch } from '@/components/ui/switch'
import { Separator } from '@/components/ui/separator'
import { ColorDots, KpiCard, ProductPhoto, StatusBadge, Stars, defaultPhoto } from './shared'
import { ProductFormDialog } from './product-form'
import {
  AlertTriangle, BadgeCheck, Bell, CheckCircle2, Clock, Cog, DollarSign, FileText, Heart, ImageIcon,
  LayoutDashboard, Megaphone, Package, Percent, Plus, RotateCcw, ScrollText, ShieldCheck, ShoppingBag,
  Star, Store, TrendingUp, Truck, Users, Wallet, XCircle, Zap,
} from 'lucide-react'

export function AdminConsole() {
  const app = useApp()
  if (!app.user || app.user.role !== 'Admin') {
    return <main className="flex flex-col items-center justify-center gap-3 py-32 text-sm text-muted-foreground"><ShieldCheck className="h-10 w-10 text-muted-foreground/40" /><p>Admin access only.</p><Button onClick={() => app.openAuth('signin')}>Sign in as admin</Button></main>
  }
  return (
    <main className="mx-auto max-w-7xl px-4 py-8">
      <div className="mb-6 flex flex-wrap items-end justify-between gap-2">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Platform console</h1>
          <p className="text-sm text-muted-foreground">MarketHub Ethiopia — operations, finance &amp; moderation{app.user.adminRole ? ` · signed in as ${app.user.adminRole}` : ''}</p>
        </div>
      </div>
      <Tabs defaultValue="overview">
        <TabsList className="flex-wrap">
          <TabsTrigger value="overview">Overview</TabsTrigger>
          <TabsTrigger value="vendors">Vendors</TabsTrigger>
          <TabsTrigger value="products">Products</TabsTrigger>
          <TabsTrigger value="reviews">Reviews</TabsTrigger>
          <TabsTrigger value="returns">Returns</TabsTrigger>
          <TabsTrigger value="orders">Orders</TabsTrigger>
          <TabsTrigger value="payouts">Payouts</TabsTrigger>
          <TabsTrigger value="marketing">Marketing</TabsTrigger>
          <TabsTrigger value="staff">Staff</TabsTrigger>
          <TabsTrigger value="settings">Settings</TabsTrigger>
          <TabsTrigger value="ops">Ops</TabsTrigger>
        </TabsList>
        <TabsContent value="overview"><Overview /></TabsContent>
        <TabsContent value="vendors"><Vendors /></TabsContent>
        <TabsContent value="products"><Products /></TabsContent>
        <TabsContent value="reviews"><ReviewModeration /></TabsContent>
        <TabsContent value="returns"><ReturnArbitration /></TabsContent>
        <TabsContent value="orders"><Orders /></TabsContent>
        <TabsContent value="payouts"><Payouts /></TabsContent>
        <TabsContent value="marketing"><Marketing /></TabsContent>
        <TabsContent value="staff"><Staff /></TabsContent>
        <TabsContent value="settings"><Settings /></TabsContent>
        <TabsContent value="ops"><Ops /></TabsContent>
      </Tabs>
    </main>
  )
}

// ---------------- Overview ----------------
function Overview() {
  const [summary, setSummary] = useState<ReportSummary | null>(null)
  const [rev, setRev] = useState<RevenueOverTime | null>(null)
  const [topV, setTopV] = useState<TopVendor[] | null>(null)
  const [topP, setTopP] = useState<TopProduct[] | null>(null)
  const [range, setRange] = useState('30d')

  const load = useCallback(() => {
    api.get<ReportSummary>(`/api/admin/reports/summary?range=${range}`).then(setSummary).catch(() => {})
    api.get<RevenueOverTime>('/api/admin/reports/revenue-over-time?days=30').then(setRev).catch(() => {})
    api.get<{ items: TopVendor[] }>('/api/admin/reports/top-vendors?limit=5').then(r => setTopV(r.items)).catch(() => {})
    api.get<{ items: TopProduct[] }>('/api/admin/reports/top-products?limit=5').then(r => setTopP(r.items)).catch(() => {})
  }, [range])
  useEffect(load, [load])

  if (!summary) return <Skeleton className="h-96 rounded-xl" />
  return (
    <div className="space-y-6">
      <div className="flex items-center gap-2">
        <Select value={range} onValueChange={setRange}>
          <SelectTrigger className="w-36" aria-label="Report range"><SelectValue /></SelectTrigger>
          <SelectContent><SelectItem value="today">Today</SelectItem><SelectItem value="7d">Last 7 days</SelectItem><SelectItem value="30d">Last 30 days</SelectItem><SelectItem value="all">All time</SelectItem></SelectContent>
        </Select>
      </div>
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <KpiCard label="Gross sales" value={brShort(summary.grossSales)} sub={`${summary.ordersCount} orders`} tone="primary" />
        <KpiCard label="Platform income" value={brShort(summary.platformIncome)} sub="commission + delivery + first-party" tone="primary" />
        <KpiCard label="Vendor income" value={brShort(summary.vendorIncome)} sub="owed / paid to vendors" />
        <KpiCard label="VAT collected" value={brShort(summary.vatCollected)} sub="15% remittable" />
        <KpiCard label="Commission earned" value={brShort(summary.commissionEarned)} sub="vendor sales commission" />
        <KpiCard label="Delivery revenue" value={brShort(summary.deliveryRevenue)} sub="zone-based fees" />
        <KpiCard label="Refunds" value={brShort(summary.refundTotal)} sub="paid back to buyers" tone={summary.refundTotal > 0 ? 'warn' : 'default'} />
        <KpiCard label="Pending payouts" value={`${summary.pendingPayoutCount} · ${brShort(summary.pendingPayoutAmount)}`} sub="12h SLA queue" tone={summary.pendingPayoutCount > 0 ? 'warn' : 'default'} />
      </div>
      <Card>
        <CardContent className="p-5">
          <h3 className="mb-3 text-sm font-semibold">Revenue — last 30 days (graphs &amp; tables)</h3>
          {rev && (
            <ResponsiveContainer width="100%" height={260}>
              <AreaChart data={rev.labels.map((l, i) => ({ date: l, Gross: rev.series[0]?.data[i] ?? 0, Platform: rev.series[1]?.data[i] ?? 0, Vendor: rev.series[2]?.data[i] ?? 0 }))} margin={{ top: 4, right: 8, bottom: 0, left: 8 }}>
                <defs>
                  <linearGradient id="gGross" x1="0" y1="0" x2="0" y2="1"><stop offset="0%" stopColor="oklch(0.42 0.09 165)" stopOpacity={0.3} /><stop offset="100%" stopColor="oklch(0.42 0.09 165)" stopOpacity={0.02} /></linearGradient>
                  <linearGradient id="gPlat" x1="0" y1="0" x2="0" y2="1"><stop offset="0%" stopColor="oklch(0.55 0.13 250)" stopOpacity={0.3} /><stop offset="100%" stopColor="oklch(0.55 0.13 250)" stopOpacity={0.02} /></linearGradient>
                </defs>
                <CartesianGrid strokeDasharray="3 3" stroke="oklch(0.915 0.008 140)" />
                <XAxis dataKey="date" tick={{ fontSize: 10 }} tickLine={false} axisLine={false} interval={4} />
                <YAxis tick={{ fontSize: 11 }} tickLine={false} axisLine={false} tickFormatter={(v: number) => brShort(v)} width={60} />
                <ChartTooltip formatter={(v) => br(Number(v))} contentStyle={{ borderRadius: 10, fontSize: 12 }} />
                <Legend wrapperStyle={{ fontSize: 11 }} />
                <Area type="monotone" dataKey="Gross" stroke="oklch(0.42 0.09 165)" strokeWidth={2} fill="url(#gGross)" />
                <Area type="monotone" dataKey="Platform" stroke="oklch(0.55 0.13 250)" strokeWidth={2} fill="url(#gPlat)" />
                <Area type="monotone" dataKey="Vendor" stroke="oklch(0.65 0.15 70)" strokeWidth={1.5} fill="none" />
              </AreaChart>
            </ResponsiveContainer>
          )}
        </CardContent>
      </Card>
      <div className="grid gap-6 lg:grid-cols-2">
        <Card><CardContent className="p-5">
          <h3 className="mb-3 text-sm font-semibold">Top vendors by income</h3>
          <Table>
            <TableHeader><TableRow><TableHead>Vendor</TableHead><TableHead>City</TableHead><TableHead className="text-right">Income</TableHead></TableRow></TableHeader>
            <TableBody>{(topV ?? []).map(v => <TableRow key={v.vendorId}><TableCell className="font-medium">{v.businessName}</TableCell><TableCell className="text-xs text-muted-foreground">{v.city}</TableCell><TableCell className="text-right">{br(v.incomeETB)}</TableCell></TableRow>)}</TableBody>
          </Table>
        </CardContent></Card>
        <Card><CardContent className="p-5">
          <h3 className="mb-3 text-sm font-semibold">Top products by revenue</h3>
          <Table>
            <TableHeader><TableRow><TableHead>Product</TableHead><TableHead>Sold</TableHead><TableHead className="text-right">Revenue</TableHead></TableRow></TableHeader>
            <TableBody>{(topP ?? []).map(p => <TableRow key={p.productId}><TableCell className="max-w-56 truncate font-medium">{p.title}</TableCell><TableCell>{p.soldQty}</TableCell><TableCell className="text-right">{br(p.revenueETB)}</TableCell></TableRow>)}</TableBody>
          </Table>
        </CardContent></Card>
      </div>
    </div>
  )
}

// ---------------- Vendors ----------------
function Vendors() {
  const app = useApp()
  const [items, setItems] = useState<(VendorApplication & { productCount?: number })[] | null>(null)
  const [status, setStatus] = useState('PendingReview')
  const [detail, setDetail] = useState<(VendorApplication & { productCount?: number }) | null>(null)
  const [reason, setReason] = useState('')
  const [perf, setPerf] = useState<VendorPerformance | null>(null)

  const load = useCallback(() => {
    api.get<{ items: VendorApplication[] }>(`/api/admin/vendors?status=${status}`).then(r => setItems(r.items)).catch(() => setItems([]))
  }, [status])
  useEffect(load, [load])

  const review = async (vendorId: string, approve: boolean) => {
    try {
      await api.post(`/api/admin/vendors/review/${vendorId}`, { approve, rejectionReason: approve ? undefined : reason })
      app.notify(approve ? 'Vendor approved — they can now list products' : 'Vendor rejected with reason')
      setDetail(null); setReason(''); load()
    } catch (e) { app.notify(e instanceof Error ? e.message : 'Action failed', 'err') }
  }

  if (!items) return <Skeleton className="h-96 rounded-xl" />
  return (
    <div className="space-y-4">
      <div className="flex items-center gap-2">
        <Select value={status} onValueChange={setStatus}>
          <SelectTrigger className="w-48" aria-label="Vendor status filter"><SelectValue /></SelectTrigger>
          <SelectContent>{['PendingReview', 'Approved', 'Rejected', 'Suspended'].map(s => <SelectItem key={s} value={s}>{s}</SelectItem>)}</SelectContent>
        </Select>
        <span className="text-xs text-muted-foreground">{items.length} vendor(s)</span>
      </div>
      {items.length === 0 ? <div className="py-20 text-center text-sm text-muted-foreground">No vendors with this status.</div> : (
        <Table>
          <TableHeader><TableRow><TableHead>Business</TableHead><TableHead>Owner</TableHead><TableHead>Docs</TableHead><TableHead>Products</TableHead><TableHead>Status</TableHead><TableHead /></TableRow></TableHeader>
          <TableBody>
            {items.map(v => {
              const req = v.owner?.documents?.filter(d => d.type.isRequired) ?? []
              const allVerified = req.length > 0 && req.every(d => d.status === 'Verified')
              return (
                <TableRow key={v.id}>
                  <TableCell><div className="font-medium">{v.businessName}</div><div className="text-xs text-muted-foreground">TIN {v.tin} · {v.city}, {v.region}</div></TableCell>
                  <TableCell className="text-xs">{v.owner?.fullName}<br /><span className="text-muted-foreground">{v.owner?.email}</span></TableCell>
                  <TableCell>
                    <Badge variant="outline" className={allVerified ? 'border-emerald-200 text-emerald-700' : 'border-amber-300 text-amber-700'}>
                      <FileText className="mr-1 h-2.5 w-2.5" />{v.owner?.documents?.length ?? 0} docs {allVerified ? '✓' : ''}
                    </Badge>
                  </TableCell>
                  <TableCell>{v.productCount ?? 0}</TableCell>
                  <TableCell><StatusBadge status={v.status} /></TableCell>
                  <TableCell className="space-x-1.5 text-right whitespace-nowrap">
                    <Button size="sm" variant="outline" onClick={() => { setDetail(v); setReason('') }}>Documents</Button>
                    {v.status === 'PendingReview' && <>
                      <Button size="sm" onClick={() => review(v.id, true)}><BadgeCheck className="mr-1 h-3 w-3" />Approve</Button>
                      <Button size="sm" variant="outline" className="text-red-600" onClick={() => { setDetail(v); setReason('') }}>Reject</Button>
                    </>}
                  </TableCell>
                </TableRow>
              )
            })}
          </TableBody>
        </Table>
      )}

      <Dialog open={!!detail} onOpenChange={v => !v && setDetail(null)}>
        <DialogContent className="max-h-[85vh] overflow-y-auto sm:max-w-lg">
          <DialogHeader><DialogTitle>{detail?.businessName} — legal documents</DialogTitle><DialogDescription>{detail?.owner?.fullName} · {detail?.owner?.email} · TIN {detail?.tin}</DialogDescription></DialogHeader>
          <div className="space-y-2">
            {(detail?.owner?.documents ?? []).map(d => (
              <div key={d.id} className="flex items-center gap-3 rounded-xl border p-3 text-sm">
                <FileText className="h-4 w-4 shrink-0 text-muted-foreground" />
                <div className="min-w-0 flex-1">
                  <p className="truncate font-medium">{d.type.name}{d.type.isRequired && <span className="ml-1 text-[10px] text-red-600">required</span>}</p>
                  <p className="truncate text-xs text-muted-foreground">{d.fileName} · {(d.sizeBytes / 1024).toFixed(0)} KB · {dateTimeFmt(d.uploadedAt)}</p>
                </div>
                <StatusBadge status={d.status} />
              </div>
            ))}
          </div>
          {perf && <div className="rounded-xl bg-muted/40 p-3 text-xs">Rating {perf.ratingAvg} ({perf.ratingCount}) · fulfillment {perf.fulfillmentRate.toFixed(0)}% · disputes {perf.disputeRate.toFixed(1)}%</div>}
          {detail?.status === 'PendingReview' && (
            <>
              <Textarea rows={2} value={reason} onChange={e => setReason(e.target.value)} placeholder="Rejection reason (required if rejecting)" />
              <DialogFooter>
                <Button variant="outline" className="text-red-600" disabled={reason.trim().length < 5} onClick={() => detail && review(detail.id, false)}>Reject</Button>
                <Button onClick={() => detail && review(detail.id, true)}>Approve vendor</Button>
              </DialogFooter>
            </>
          )}
        </DialogContent>
      </Dialog>
    </div>
  )
}

// ---------------- Product moderation ----------------
function Products() {
  const app = useApp()
  const [items, setItems] = useState<Product[] | null>(null)
  const [status, setStatus] = useState('PendingReview')
  const [cats, setCats] = useState<Category[]>([])
  const [open, setOpen] = useState(false)

  const load = useCallback(() => {
    api.get<{ items: Product[] }>(`/api/admin/products?status=${status}`).then(r => setItems(r.items)).catch(() => setItems([]))
    api.get<{ items: Category[] }>('/api/catalog/categories').then(r => setCats(r.items)).catch(() => {})
  }, [status])
  useEffect(load, [load])

  const moderate = async (productId: string, approve: boolean) => {
    try {
      await api.post(`/api/admin/products/moderate/${productId}`, { approve })
      app.notify(approve ? 'Product approved and live' : 'Product rejected')
      load()
    } catch (e) { app.notify(e instanceof Error ? e.message : 'Action failed', 'err') }
  }

  if (!items) return <Skeleton className="h-96 rounded-xl" />
  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-2">
        <Select value={status} onValueChange={setStatus}>
          <SelectTrigger className="w-44" aria-label="Product status filter"><SelectValue /></SelectTrigger>
          <SelectContent>{['PendingReview', 'Active', 'Rejected', 'Archived'].map(s => <SelectItem key={s} value={s}>{s}</SelectItem>)}</SelectContent>
        </Select>
        <Button size="sm" className="ml-auto" onClick={() => setOpen(true)}><Plus className="mr-1 h-3.5 w-3.5" />Add platform product</Button>
      </div>
      {items.length === 0 ? <div className="py-20 text-center text-sm text-muted-foreground">Nothing here.</div> : (
        <Table>
          <TableHeader><TableRow><TableHead className="w-12">Photo</TableHead><TableHead>Product</TableHead><TableHead>Vendor</TableHead><TableHead>Price</TableHead><TableHead>Variants</TableHead><TableHead>Status</TableHead><TableHead /></TableRow></TableHeader>
          <TableBody>
            {items.map(p => (
              <TableRow key={p.id}>
                <TableCell><ProductPhoto src={defaultPhoto(p.gallery)?.url} fallbackImages={p.images} title={p.title} className="h-10 w-10 rounded-md" /></TableCell>
                <TableCell><div className="max-w-56 truncate font-medium">{p.title}</div><div className="text-xs text-muted-foreground">{p.category?.name} · {p.photoCount ?? p.gallery?.length ?? 0} photos</div></TableCell>
                <TableCell className="text-xs">{p.vendorName ?? <Badge className="bg-teal-700 text-white hover:bg-teal-700">Platform</Badge>}</TableCell>
                <TableCell>{br(p.priceETB)}{(p.salePriceETB ?? 0) > 0 && <div className="text-[10px] text-red-600">sale {br(p.salePriceETB!)}</div>}</TableCell>
                <TableCell><div className="flex items-center gap-1">{(p.colors?.length ?? 0) > 0 ? <ColorDots colors={p.colors} max={4} /> : <span className="text-xs text-muted-foreground">{p.stock} units</span>}{(p.variants?.length ?? 0) > 0 && <Badge variant="outline" className="text-[9px]">{p.variants!.length} SKU</Badge>}</div></TableCell>
                <TableCell><StatusBadge status={p.status} /></TableCell>
                <TableCell className="whitespace-nowrap text-right">
                  {p.status === 'PendingReview' && p.vendorId && <>
                    <Button size="sm" variant="outline" className="text-red-600" onClick={() => moderate(p.id, false)}>Reject</Button>{' '}
                    <Button size="sm" onClick={() => moderate(p.id, true)}>Approve</Button>
                  </>}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      <ProductFormDialog open={open} onOpenChange={setOpen} categories={cats} onCreated={load} />
    </div>
  )
}

// ---------------- Review moderation ----------------
function ReviewModeration() {
  const app = useApp()
  const [items, setItems] = useState<Review[] | null>(null)
  const [counts, setCounts] = useState<Record<string, number> | null>(null)
  const [status, setStatus] = useState('Pending')

  const load = useCallback(() => {
    api.get<{ items: Review[]; counts?: Record<string, number> }>(`/api/admin/reviews?status=${status}`).then(r => { setItems(r.items); setCounts(r.counts ?? null) }).catch(() => setItems([]))
  }, [status])
  useEffect(load, [load])

  const moderate = async (id: string, approve: boolean) => {
    try {
      await api.post(`/api/admin/reviews/${id}/moderate`, { approve })
      app.notify(approve ? 'Review published — product rating updated' : 'Review rejected')
      load()
    } catch (e) { app.notify(e instanceof Error ? e.message : 'Action failed', 'err') }
  }

  if (!items) return <Skeleton className="h-96 rounded-xl" />
  return (
    <div className="space-y-4">
      <div className="flex items-center gap-2">
        <Select value={status} onValueChange={setStatus}>
          <SelectTrigger className="w-40" aria-label="Review status"><SelectValue /></SelectTrigger>
          <SelectContent>{['Pending', 'Approved', 'Rejected'].map(s => <SelectItem key={s} value={s}>{s}{counts?.[s] !== undefined ? ` (${counts[s]})` : ''}</SelectItem>)}</SelectContent>
        </Select>
        <span className="text-xs text-muted-foreground">Reviews publish only after moderation — product ratings recompute on approval.</span>
      </div>
      {items.length === 0 ? <div className="py-20 text-center text-sm text-muted-foreground">Queue is clear ✓</div> : (
        <div className="grid gap-3 lg:grid-cols-2">
          {items.map(r => (
            <Card key={r.id}>
              <CardContent className="p-4">
                <div className="flex flex-wrap items-center gap-x-2.5 gap-y-1">
                  <Stars rating={r.rating} />
                  <span className="text-xs text-muted-foreground">{r.userName ?? 'Buyer'} on</span>
                  <span className="max-w-40 truncate text-sm font-medium">{r.product?.title ?? r.productTitle}</span>
                  {r.verified && <Badge variant="outline" className="border-emerald-200 text-[9px] text-emerald-700">verified</Badge>}
                  <span className="ml-auto text-[10px] text-muted-foreground">{dateTimeFmt(r.createdAt)}</span>
                </div>
                {r.title && <p className="mt-1.5 text-sm font-medium">{r.title}</p>}
                <p className="mt-0.5 line-clamp-3 text-sm text-muted-foreground">{r.body}</p>
                {(r.imageUrls?.length ?? 0) > 0 && <div className="mt-1.5 flex gap-1.5">{r.imageUrls!.slice(0, 3).map((u, i) => <ProductPhoto key={i} src={u} title="review photo" sizes="48px" className="h-12 w-12 rounded-md" />)}</div>}
                <div className="mt-2.5 flex gap-2">
                  {r.status !== 'Approved' && <Button size="sm" onClick={() => moderate(r.id, true)}><CheckCircle2 className="mr-1 h-3.5 w-3.5" />Approve</Button>}
                  {r.status !== 'Rejected' && <Button size="sm" variant="outline" className="text-red-600" onClick={() => moderate(r.id, false)}><XCircle className="mr-1 h-3.5 w-3.5" />Reject</Button>}
                </div>
              </CardContent>
            </Card>
          ))}
        </div>
      )}
    </div>
  )
}

// ---------------- Returns arbitration ----------------
function ReturnArbitration() {
  const app = useApp()
  const [items, setItems] = useState<ReturnRequest[] | null>(null)
  const [dlg, setDlg] = useState<{ r: ReturnRequest; approve: boolean } | null>(null)
  const [refund, setRefund] = useState('')
  const [note, setNote] = useState('')
  const [busy, setBusy] = useState(false)

  const load = useCallback(() => {
    api.get<{ items: ReturnRequest[] }>('/api/admin/returns').then(r => setItems(r.items)).catch(() => setItems([]))
  }, [])
  useEffect(load, [load])

  const arbitrate = async () => {
    if (!dlg) return
    setBusy(true)
    try {
      await api.post(`/api/admin/returns/${dlg.r.id}/arbitrate`, { approve: dlg.approve, refundETB: dlg.approve ? parseFloat(refund) : undefined, note: note || undefined })
      app.notify(dlg.approve ? `Refund of ${br(parseFloat(refund))} posted — ledger balanced, stock restored` : 'Return rejected — buyer notified')
      setDlg(null); setNote(''); load()
    } catch (e) { app.notify(e instanceof Error ? e.message : 'Arbitration failed', 'err') }
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
              <span className="text-sm font-medium">{r.itemTitle}{r.variantLabel ? ` · ${r.variantLabel}` : ''}</span>
              <span className="font-mono text-xs text-muted-foreground">{r.orderNumber}</span>
              <Badge variant="outline">{r.reason}</Badge>
              <span className="ml-auto text-xs text-muted-foreground">{r.buyerName} → {r.vendorName} · {dateTimeFmt(r.createdAt)}</span>
            </div>
            <p className="mt-2 text-sm text-muted-foreground">{r.description}</p>
            {(r.evidenceUrls?.length ?? 0) > 0 && <div className="mt-2 flex gap-2">{r.evidenceUrls!.slice(0, 3).map((u, i) => <ProductPhoto key={i} src={u} title="evidence" sizes="56px" className="h-14 w-14 rounded-lg" />)}</div>}
            <div className="mt-2 flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
              <span>Item price: {br(r.itemUnitPriceETB ?? 0)} × {r.itemQuantity ?? 1}</span>
              {r.vendorNote && <Badge variant="outline" className="border-sky-200 text-sky-700">vendor: {r.status === 'VendorApproved' ? 'approved' : 'rejected'} — {r.vendorNote}</Badge>}
            </div>
            {(r.status === 'Requested' || r.status === 'VendorApproved' || r.status === 'VendorRejected' || r.status === 'AdminReview') && (
              <div className="mt-3 flex gap-2">
                <Button size="sm" onClick={() => { setDlg({ r, approve: true }); setRefund(String(r.itemUnitPriceETB ?? 0)); setNote('') }}><CheckCircle2 className="mr-1 h-3.5 w-3.5" />Approve &amp; refund</Button>
                <Button size="sm" variant="outline" className="text-red-600" onClick={() => { setDlg({ r, approve: false }); setNote('') }}><XCircle className="mr-1 h-3.5 w-3.5" />Reject</Button>
              </div>
            )}
            {r.adminDecision && <p className="mt-2 rounded-lg bg-muted/50 p-2 text-xs">Decision: {r.adminDecision}</p>}
          </CardContent>
        </Card>
      ))}

      <Dialog open={!!dlg} onOpenChange={v => !v && setDlg(null)}>
        <DialogContent className="sm:max-w-md">
          <DialogHeader>
            <DialogTitle>{dlg?.approve ? 'Approve return & refund' : 'Reject return'}</DialogTitle>
            <DialogDescription>{dlg?.approve ? 'Posts balanced reversal legs to the ledger, restocks the item and notifies buyer + vendor. Delivery fee is non-refundable per policy.' : 'Buyer is notified with your reason and may escalate.'}</DialogDescription>
          </DialogHeader>
          {dlg?.approve && <div><Label htmlFor="refund">Refund amount (Br)</Label><Input id="refund" value={refund} onChange={e => setRefund(e.target.value)} inputMode="decimal" /></div>}
          <Textarea rows={2} value={note} onChange={e => setNote(e.target.value)} placeholder={dlg?.approve ? 'Optional note to buyer…' : 'Reason (required)…'} />
          <DialogFooter><Button variant="outline" onClick={() => setDlg(null)}>Cancel</Button>
            <Button disabled={busy || (dlg?.approve ? !(parseFloat(refund) > 0) : note.trim().length < 5)} onClick={arbitrate} className={dlg?.approve ? '' : 'text-red-600'}>{dlg?.approve ? 'Approve & post refund' : 'Reject'}</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  )
}

// ---------------- Orders + refund decisions ----------------
function Orders() {
  const app = useApp()
  const [items, setItems] = useState<Order[] | null>(null)
  const [status, setStatus] = useState('')
  const [dlg, setDlg] = useState<{ o: Order; approve: boolean } | null>(null)
  const [note, setNote] = useState('')

  const load = useCallback(() => {
    api.get<{ items: Order[] }>(`/api/admin/orders${status ? `?status=${status}` : ''}`).then(r => setItems(r.items)).catch(() => setItems([]))
  }, [status])
  useEffect(load, [load])

  const decide = async (approve: boolean) => {
    if (!dlg) return
    try {
      await api.post(`/api/admin/orders/refund-decision/${dlg.o.id}`, { approve, note: note || undefined })
      app.notify(approve ? 'Refund approved — buyer credited within 5 business days' : 'Refund rejected')
      setDlg(null); setNote(''); load()
    } catch (e) { app.notify(e instanceof Error ? e.message : 'Decision failed', 'err') }
  }

  if (!items) return <Skeleton className="h-96 rounded-xl" />
  return (
    <div className="space-y-4">
      <Select value={status} onValueChange={setStatus}>
        <SelectTrigger className="w-44" aria-label="Order status filter"><SelectValue placeholder="All statuses" /></SelectTrigger>
        <SelectContent>{['', 'Placed', 'PaymentConfirmed', 'VendorAccepted', 'Shipped', 'Delivered', 'Completed', 'RefundRequested', 'Refunded', 'Cancelled'].map(s => <SelectItem key={s || 'all'} value={s}>{s || 'All statuses'}</SelectItem>)}</SelectContent>
      </Select>
      <Table>
        <TableHeader><TableRow><TableHead>Order</TableHead><TableHead>Buyer</TableHead><TableHead>Zone</TableHead><TableHead>Total</TableHead><TableHead>Tracking</TableHead><TableHead>Status</TableHead><TableHead /></TableRow></TableHeader>
        <TableBody>
          {items.map(o => (
            <TableRow key={o.id}>
              <TableCell><div className="font-mono text-xs">{o.number}</div><div className="text-[10px] text-muted-foreground">{dateTimeFmt(o.createdAt)}</div></TableCell>
              <TableCell className="text-xs">{o.phone}</TableCell>
              <TableCell className="text-xs">{o.zoneName}</TableCell>
              <TableCell className="whitespace-nowrap">{br(o.grandTotalETB)}{o.couponCode && <div className="text-[10px] text-emerald-700">−{br(o.couponDiscountETB ?? 0)} {o.couponCode}</div>}</TableCell>
              <TableCell className="font-mono text-[10px]">{o.trackingCode ?? '—'}</TableCell>
              <TableCell><StatusBadge status={o.status} /></TableCell>
              <TableCell className="text-right">
                {o.status === 'RefundRequested' && <Button size="sm" variant="outline" onClick={() => { setDlg({ o, approve: true }); setNote('') }}>Review refund</Button>}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      <Dialog open={!!dlg} onOpenChange={v => !v && setDlg(null)}>
        <DialogContent className="sm:max-w-md">
          <DialogHeader><DialogTitle>Refund decision — {dlg?.o.number}</DialogTitle><DialogDescription>Approving posts balanced reversal legs to the ledger (VAT, delivery, commission, vendor payable) and notifies the buyer.</DialogDescription></DialogHeader>
          <Textarea rows={2} value={note} onChange={e => setNote(e.target.value)} placeholder="Note to buyer (optional on approve, required on reject)" />
          <DialogFooter>
            <Button variant="outline" className="text-red-600" disabled={note.trim().length < 5} onClick={() => decide(false)}>Reject refund</Button>
            <Button onClick={() => decide(true)}>Approve refund ({br(dlg?.o.grandTotalETB ?? 0)})</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  )
}

// ---------------- Payouts (existing) ----------------
function Payouts() {
  const app = useApp()
  const [items, setItems] = useState<Payout[] | null>(null)
  const load = useCallback(() => { api.get<{ items: Payout[] }>('/api/admin/payouts').then(r => setItems(r.items)).catch(() => setItems([])) }, [])
  useEffect(load, [load])
  const slaLeft = (dueBy: string) => {
    const ms = new Date(dueBy).getTime() - Date.now()
    if (ms <= 0) return 'OVERDUE'
    return `${Math.floor(ms / 36e5)}h ${Math.floor((ms % 36e5) / 6e4)}m left`
  }
  const process = async (id: string, approve: boolean) => {
    try {
      await api.post(`/api/admin/payouts/process/${id}`, { approve, adminNote: approve ? undefined : 'Rejected by finance' })
      app.notify(approve ? 'Payout marked as sent — vendor notified by SMS' : 'Payout rejected — amount returned to balance')
      load()
    } catch (e) { app.notify(e instanceof Error ? e.message : 'Action failed', 'err') }
  }
  if (!items) return <Skeleton className="h-96 rounded-xl" />
  return (
    <Table>
      <TableHeader><TableRow><TableHead>Vendor</TableHead><TableHead>Amount</TableHead><TableHead>Method</TableHead><TableHead>Requested</TableHead><TableHead>SLA</TableHead><TableHead>Status</TableHead><TableHead /></TableRow></TableHeader>
      <TableBody>
        {items.map(p => (
          <TableRow key={p.id}>
            <TableCell className="font-medium">{p.vendorName}</TableCell>
            <TableCell>{br(p.amountETB)}</TableCell>
            <TableCell className="text-xs">{p.paymentMethod === 'Telebirr' ? 'telebirr' : 'bank'} · {p.destinationAccount}</TableCell>
            <TableCell className="text-xs text-muted-foreground">{dateTimeFmt(p.requestedAt)}</TableCell>
            <TableCell>{['Requested', 'Processing'].includes(p.status) ? <Badge variant="outline" className={p.overdue ? 'border-red-300 text-red-700' : 'border-amber-300 text-amber-800'}>{slaLeft(p.dueBy)}</Badge> : '—'}</TableCell>
            <TableCell><StatusBadge status={p.status} /></TableCell>
            <TableCell className="space-x-1.5 text-right whitespace-nowrap">
              {['Requested', 'Processing'].includes(p.status) && <>
                <Button size="sm" onClick={() => process(p.id, true)}><Wallet className="mr-1 h-3 w-3" />Mark paid</Button>
                <Button size="sm" variant="outline" className="text-red-600" onClick={() => process(p.id, false)}>Reject</Button>
              </>}
            </TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  )
}

// ---------------- Marketing: banners + flash sale + coupons ----------------
function Marketing() {
  const app = useApp()
  const [banners, setBanners] = useState<Banner[] | null>(null)
  const [coupons, setCoupons] = useState<Coupon[] | null>(null)
  const [saleDlg, setSaleDlg] = useState(false)
  const [products, setProducts] = useState<Product[]>([])
  const [saleProductIds, setSaleProductIds] = useState<string[]>([])
  const [salePct, setSalePct] = useState('20')
  const [saleEnds, setSaleEnds] = useState('')
  const [couponDlg, setCouponDlg] = useState(false)
  const [nc, setNc] = useState({ code: '', type: 'Percent', value: '10', minSubtotal: '0', maxDiscount: '', maxUses: '100', days: '30' })

  const load = useCallback(() => {
    api.get<{ items: Banner[] }>('/api/admin/banners').then(r => setBanners(r.items)).catch(() => setBanners([]))
    api.get<{ items: Coupon[] }>('/api/admin/coupons').then(r => setCoupons(r.items)).catch(() => setCoupons([]))
    api.get<{ items: Product[] }>('/api/catalog/products?pageSize=48&sort=sold').then(r => setProducts(r.items)).catch(() => {})
  }, [])
  useEffect(load, [load])

  const addBanner = async () => {
    try {
      await api.post('/api/admin/banners', { title: 'New campaign banner', imageUrl: 'https://z-cdn.chatglm.cn/image-search-mcp/images-ppt/2acd03551ec2.jpg', link: '/catalog', position: (banners?.length ?? 0) })
      app.notify('Banner added — edit its title/link next'); load()
    } catch (e) { app.notify(e instanceof Error ? e.message : 'Failed', 'err') }
  }
  const toggleBanner = async (b: Banner) => {
    try { await api.patch(`/api/admin/banners/${b.id}`, { isActive: !b.isActive }); load() } catch { app.notify('Failed', 'err') }
  }
  const deleteBanner = async (id: string) => { try { await api.delete(`/api/admin/banners/${id}`); load() } catch { app.notify('Failed', 'err') } }

  const setFlashSale = async () => {
    try {
      await api.post('/api/admin/flash-sale', { productIds: saleProductIds, discountPct: parseFloat(salePct), endsAt: saleEnds ? new Date(saleEnds).toISOString() : new Date(Date.now() + 48 * 36e5).toISOString() })
      app.notify(`Flash sale live on ${saleProductIds.length} products`); setSaleDlg(false); load()
    } catch (e) { app.notify(e instanceof Error ? e.message : 'Failed', 'err') }
  }
  const clearFlashSale = async () => {
    try { await api.delete('/api/admin/flash-sale'); app.notify('Flash sale ended'); load() } catch { app.notify('Failed', 'err') }
  }
  const createCoupon = async () => {
    try {
      await api.post('/api/admin/coupons', {
        code: nc.code.toUpperCase(), type: nc.type, value: parseFloat(nc.value), minSubtotal: parseFloat(nc.minSubtotal) || 0,
        maxDiscount: nc.maxDiscount ? parseFloat(nc.maxDiscount) : undefined, maxUses: parseInt(nc.maxUses, 10) || 100,
        startsAt: new Date().toISOString(), expiresAt: new Date(Date.now() + (parseInt(nc.days, 10) || 30) * 864e5).toISOString(),
        description: `${nc.type === 'Percent' ? `${nc.value}% off` : `${nc.value} Br off`}${nc.minSubtotal !== '0' ? ` over ${nc.minSubtotal} Br` : ''}`,
      })
      app.notify('Coupon created'); setCouponDlg(false); load()
    } catch (e) { app.notify(e instanceof Error ? e.message : 'Failed', 'err') }
  }
  const toggleCoupon = async (c: Coupon) => { try { await api.patch(`/api/admin/coupons/${c.id}`, { isActive: !c.isActive }); load() } catch { app.notify('Failed', 'err') } }

  const flashActive = products.some(p => (p.salePriceETB ?? 0) > 0)

  return (
    <div className="space-y-8">
      {/* banners */}
      <section>
        <div className="mb-3 flex items-center justify-between">
          <h3 className="flex items-center gap-2 text-sm font-semibold"><ImageIcon className="h-4 w-4 text-primary" />Homepage banners</h3>
          <Button size="sm" variant="outline" onClick={addBanner}><Plus className="mr-1 h-3.5 w-3.5" />Add banner</Button>
        </div>
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {(banners ?? []).map(b => (
            <div key={b.id} className="overflow-hidden rounded-2xl border">
              <ProductPhoto src={b.imageUrl} title={b.title} sizes="320px" className="aspect-[21/8] w-full" />
              <div className="flex items-center gap-2 p-3">
                <div className="min-w-0 flex-1"><p className="truncate text-sm font-medium">{b.title}</p><p className="text-[10px] text-muted-foreground">{b.link ?? '—'}</p></div>
                <Switch checked={b.isActive} onCheckedChange={() => toggleBanner(b)} aria-label="Banner active" />
                <Button size="sm" variant="ghost" className="text-red-600" onClick={() => deleteBanner(b.id)}>✕</Button>
              </div>
            </div>
          ))}
        </div>
      </section>

      {/* flash sale */}
      <section>
        <div className="mb-3 flex items-center justify-between">
          <h3 className="flex items-center gap-2 text-sm font-semibold"><Zap className="h-4 w-4 text-red-600" />Flash sale {flashActive && <Badge className="bg-red-600 text-white hover:bg-red-600">LIVE</Badge>}</h3>
          <div className="flex gap-2">
            {flashActive && <Button size="sm" variant="outline" onClick={clearFlashSale}>End sale</Button>}
            <Button size="sm" onClick={() => { setSaleProductIds(products.filter(p => (p.salePriceETB ?? 0) > 0).map(p => p.id)); setSalePct('20'); setSaleEnds(''); setSaleDlg(true) }}>Configure sale</Button>
          </div>
        </div>
        <div className="grid grid-cols-2 gap-2 sm:grid-cols-4 lg:grid-cols-6">
          {products.filter(p => (p.salePriceETB ?? 0) > 0).map(p => (
            <div key={p.id} className="rounded-xl border p-2 text-center">
              <ProductPhoto src={defaultPhoto(p.gallery)?.url} fallbackImages={p.images} title={p.title} sizes="96px" className="mx-auto aspect-square w-full rounded-lg" />
              <p className="mt-1 truncate text-[11px] font-medium">{p.title}</p>
              <p className="text-[10px]"><span className="text-muted-foreground line-through">{br(p.priceETB)}</span> <span className="font-bold text-red-600">{br(p.salePriceETB!)}</span></p>
            </div>
          ))}
          {!flashActive && <p className="col-span-full py-6 text-center text-xs text-muted-foreground">No active flash sale — pick products and a discount to run one.</p>}
        </div>
      </section>

      {/* coupons */}
      <section>
        <div className="mb-3 flex items-center justify-between">
          <h3 className="flex items-center gap-2 text-sm font-semibold"><Percent className="h-4 w-4 text-primary" />Coupons</h3>
          <Button size="sm" onClick={() => setCouponDlg(true)}><Plus className="mr-1 h-3.5 w-3.5" />New coupon</Button>
        </div>
        <Table>
          <TableHeader><TableRow><TableHead>Code</TableHead><TableHead>Discount</TableHead><TableHead>Min subtotal</TableHead><TableHead>Used</TableHead><TableHead>Expires</TableHead><TableHead>Active</TableHead></TableRow></TableHeader>
          <TableBody>
            {(coupons ?? []).map(c => (
              <TableRow key={c.id}>
                <TableCell className="font-mono font-semibold">{c.code}</TableCell>
                <TableCell>{c.type === 'Percent' ? `${c.value}%${c.maxDiscount ? ` (max ${br(c.maxDiscount)})` : ''}` : br(c.value)}</TableCell>
                <TableCell>{c.minSubtotal > 0 ? br(c.minSubtotal) : '—'}</TableCell>
                <TableCell>{c.usedCount}/{c.maxUses}</TableCell>
                <TableCell className="text-xs">{dateFmtS(c.expiresAt)}</TableCell>
                <TableCell><Switch checked={c.isActive} onCheckedChange={() => toggleCoupon(c)} aria-label="Coupon active" /></TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </section>

      {/* flash sale dialog */}
      <Dialog open={saleDlg} onOpenChange={setSaleDlg}>
        <DialogContent className="sm:max-w-lg">
          <DialogHeader><DialogTitle>Configure flash sale</DialogTitle><DialogDescription>Pick products, set the discount % (5–70) and the end time. Sale prices apply instantly across the storefront.</DialogDescription></DialogHeader>
          <div className="grid grid-cols-2 gap-3">
            <div><Label htmlFor="sp">Discount %</Label><Input id="sp" value={salePct} onChange={e => setSalePct(e.target.value)} inputMode="numeric" /></div>
            <div><Label htmlFor="se">Ends at</Label><Input id="se" type="datetime-local" value={saleEnds} onChange={e => setSaleEnds(e.target.value)} /></div>
          </div>
          <div className="max-h-52 space-y-1 overflow-y-auto rounded-xl border p-2">
            {products.map(p => (
              <label key={p.id} className="flex cursor-pointer items-center gap-2 rounded-lg px-2 py-1.5 text-sm hover:bg-muted/50">
                <input type="checkbox" className="h-3.5 w-3.5 accent-emerald-700" checked={saleProductIds.includes(p.id)}
                  onChange={e => setSaleProductIds(ids => e.target.checked ? [...ids, p.id] : ids.filter(i => i !== p.id))} />
                <span className="min-w-0 flex-1 truncate">{p.title}</span>
                <span className="text-xs text-muted-foreground">{br(p.priceETB)}</span>
              </label>
            ))}
          </div>
          <DialogFooter><Button variant="outline" onClick={() => setSaleDlg(false)}>Cancel</Button><Button disabled={saleProductIds.length === 0 || !(parseFloat(salePct) >= 5 && parseFloat(salePct) <= 70)} onClick={setFlashSale}>Start flash sale</Button></DialogFooter>
        </DialogContent>
      </Dialog>

      {/* coupon dialog */}
      <Dialog open={couponDlg} onOpenChange={setCouponDlg}>
        <DialogContent className="sm:max-w-md">
          <DialogHeader><DialogTitle>New coupon</DialogTitle></DialogHeader>
          <div className="grid grid-cols-2 gap-3">
            <div><Label htmlFor="cc">Code</Label><Input id="cc" value={nc.code} onChange={e => setNc({ ...nc, code: e.target.value.toUpperCase() })} placeholder="GENA15" className="uppercase" /></div>
            <div><Label>Type</Label><Select value={nc.type} onValueChange={v => setNc({ ...nc, type: v })}><SelectTrigger><SelectValue /></SelectTrigger><SelectContent><SelectItem value="Percent">Percent %</SelectItem><SelectItem value="Fixed">Fixed Br</SelectItem></SelectContent></Select></div>
            <div><Label htmlFor="cv">{nc.type === 'Percent' ? 'Percent' : 'Amount (Br)'}</Label><Input id="cv" value={nc.value} onChange={e => setNc({ ...nc, value: e.target.value })} inputMode="decimal" /></div>
            <div><Label htmlFor="cm">Min subtotal (Br)</Label><Input id="cm" value={nc.minSubtotal} onChange={e => setNc({ ...nc, minSubtotal: e.target.value })} inputMode="decimal" /></div>
            {nc.type === 'Percent' && <div><Label htmlFor="cx">Max discount (Br)</Label><Input id="cx" value={nc.maxDiscount} onChange={e => setNc({ ...nc, maxDiscount: e.target.value })} placeholder="cap (optional)" inputMode="decimal" /></div>}
            <div><Label htmlFor="cu">Max uses</Label><Input id="cu" value={nc.maxUses} onChange={e => setNc({ ...nc, maxUses: e.target.value })} inputMode="numeric" /></div>
            <div><Label htmlFor="cd">Valid for (days)</Label><Input id="cd" value={nc.days} onChange={e => setNc({ ...nc, days: e.target.value })} inputMode="numeric" /></div>
          </div>
          <DialogFooter><Button variant="outline" onClick={() => setCouponDlg(false)}>Cancel</Button><Button disabled={nc.code.length < 3 || !(parseFloat(nc.value) > 0)} onClick={createCoupon}>Create coupon</Button></DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  )
}

const dateFmtS = (iso: string) => new Date(iso).toLocaleDateString('en-GB', { day: '2-digit', month: 'short' })

// ---------------- Staff ----------------
const ROLE_DESC: Record<AdminRole, string> = {
  SuperAdmin: 'Full access incl. staff management',
  Support: 'Vendor approvals, orders, customer escalations',
  Finance: 'Payouts, refunds, reports',
  Moderator: 'Products & review moderation',
}
function Staff() {
  const app = useApp()
  const [items, setItems] = useState<StaffMember[] | null>(null)
  const [dlg, setDlg] = useState(false)
  const [ns, setNs] = useState({ email: '', fullName: '', password: '', adminRole: 'Support' as AdminRole })

  const load = useCallback(() => { api.get<{ items: StaffMember[] }>('/api/admin/staff').then(r => setItems(r.items)).catch(() => setItems([])) }, [])
  useEffect(load, [load])

  const create = async () => {
    try {
      await api.post('/api/admin/staff', ns)
      app.notify('Staff account created'); setDlg(false); setNs({ email: '', fullName: '', password: '', adminRole: 'Support' }); load()
    } catch (e) { app.notify(e instanceof Error ? e.message : 'Failed', 'err') }
  }
  const patch = async (id: string, body: Record<string, unknown>) => {
    try { await api.patch(`/api/admin/staff/${id}`, body); load() } catch (e) { app.notify(e instanceof Error ? e.message : 'Only SuperAdmin can edit staff', 'err'); load() }
  }

  if (!items) return <Skeleton className="h-96 rounded-xl" />
  const isSuper = app.user?.adminRole === 'SuperAdmin'
  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <p className="text-sm text-muted-foreground">Least-privilege staff accounts. {isSuper ? '' : 'Only SuperAdmin can create or edit staff.'}</p>
        {isSuper && <Button size="sm" onClick={() => setDlg(true)}><Plus className="mr-1 h-3.5 w-3.5" />New staff</Button>}
      </div>
      <Table>
        <TableHeader><TableRow><TableHead>Name</TableHead><TableHead>Email</TableHead><TableHead>Role</TableHead><TableHead>Status</TableHead></TableRow></TableHeader>
        <TableBody>
          {items.map(s => (
            <TableRow key={s.id}>
              <TableCell className="font-medium">{s.fullName}</TableCell>
              <TableCell className="text-xs">{s.email}</TableCell>
              <TableCell>
                {isSuper ? (
                  <Select value={s.adminRole ?? 'Support'} onValueChange={v => patch(s.id, { adminRole: v })}>
                    <SelectTrigger className="h-8 w-44" aria-label="Admin role"><SelectValue /></SelectTrigger>
                    <SelectContent>{(Object.keys(ROLE_DESC) as AdminRole[]).map(r => <SelectItem key={r} value={r}>{r} — {ROLE_DESC[r]}</SelectItem>)}</SelectContent>
                  </Select>
                ) : <Badge variant="outline">{s.adminRole}</Badge>}
              </TableCell>
              <TableCell>
                {isSuper ? <Switch checked={s.status === 'Active'} onCheckedChange={on => patch(s.id, { status: on ? 'Active' : 'Suspended' })} aria-label="Staff active" />
                  : <StatusBadge status={s.status} />}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      <Dialog open={dlg} onOpenChange={setDlg}>
        <DialogContent className="sm:max-w-md">
          <DialogHeader><DialogTitle>New staff account</DialogTitle></DialogHeader>
          <div className="space-y-3">
            <div><Label htmlFor="st-n">Full name</Label><Input id="st-n" value={ns.fullName} onChange={e => setNs({ ...ns, fullName: e.target.value })} /></div>
            <div><Label htmlFor="st-e">Email</Label><Input id="st-e" value={ns.email} onChange={e => setNs({ ...ns, email: e.target.value })} placeholder="name@markethub.et" /></div>
            <div><Label htmlFor="st-p">Password</Label><Input id="st-p" type="password" value={ns.password} onChange={e => setNs({ ...ns, password: e.target.value })} placeholder="min 8 characters" /></div>
            <div><Label>Role</Label><Select value={ns.adminRole} onValueChange={v => setNs({ ...ns, adminRole: v as AdminRole })}><SelectTrigger><SelectValue /></SelectTrigger><SelectContent>{(Object.keys(ROLE_DESC) as AdminRole[]).map(r => <SelectItem key={r} value={r}>{r} — {ROLE_DESC[r]}</SelectItem>)}</SelectContent></Select></div>
          </div>
          <DialogFooter><Button variant="outline" onClick={() => setDlg(false)}>Cancel</Button><Button disabled={ns.fullName.length < 3 || !/^\S+@\S+\.\S+$/.test(ns.email) || ns.password.length < 8} onClick={create}>Create</Button></DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  )
}

// ---------------- Settings ----------------
function Settings() {
  const app = useApp()
  const [data, setData] = useState<AdminSettings | null>(null)
  const [draft, setDraft] = useState<Record<string, string>>({})

  const load = useCallback(() => {
    api.get<AdminSettings>('/api/admin/settings').then(d => { setData(d); setDraft(Object.fromEntries(d.entries.map(e => [e.key, e.value]))) }).catch(() => {})
  }, [])
  useEffect(load, [load])

  const save = async () => {
    try {
      await api.put('/api/admin/settings', { entries: Object.entries(draft).map(([key, value]) => ({ key, value })) })
      app.notify('Settings saved — applied platform-wide'); load()
    } catch (e) { app.notify(e instanceof Error ? e.message : 'Invalid values', 'err') }
  }

  if (!data) return <Skeleton className="h-96 rounded-xl" />
  return (
    <div className="space-y-6">
      {data.groups.map(g => (
        <Card key={g.name}>
          <CardContent className="p-5">
            <h3 className="mb-4 flex items-center gap-2 text-sm font-semibold"><Cog className="h-4 w-4 text-primary" />{g.name}</h3>
            <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
              {g.keys.map(k => {
                const e = data.entries.find(x => x.key === k)
                if (!e) return null
                return (
                  <div key={k}>
                    <Label htmlFor={`set-${k}`} className="text-xs font-medium">{k}</Label>
                    {e.type === 'enum' && e.values ? (
                      <Select value={draft[k] ?? e.value} onValueChange={v => setDraft(d => ({ ...d, [k]: v }))}>
                        <SelectTrigger className="mt-1" aria-label={k}><SelectValue /></SelectTrigger>
                        <SelectContent>{e.values.map(v => <SelectItem key={v} value={v}>{v}</SelectItem>)}</SelectContent>
                      </Select>
                    ) : (
                      <Input id={`set-${k}`} className="mt-1" value={draft[k] ?? e.value} onChange={ev => setDraft(d => ({ ...d, [k]: ev.target.value }))} inputMode={e.type === 'number' || e.type === 'int' ? 'decimal' : 'text'} />
                    )}
                    {e.description && <p className="mt-1 text-[11px] leading-3.5 text-muted-foreground">{e.description}</p>}
                  </div>
                )
              })}
            </div>
          </CardContent>
        </Card>
      ))}
      <div className="flex justify-end"><Button onClick={save}>Save all settings</Button></div>
    </div>
  )
}

// ---------------- Ops: abandoned carts, outbound, jobs ----------------
function Ops() {
  const app = useApp()
  const [carts, setCarts] = useState<AbandonedCart[] | null>(null)
  const [outbound, setOutbound] = useState<OutboundMessage[] | null>(null)
  const [busy, setBusy] = useState('')

  const load = useCallback(() => {
    api.get<{ items: AbandonedCart[] }>('/api/admin/abandoned-carts').then(r => setCarts(r.items)).catch(() => setCarts([]))
    api.get<{ items: OutboundMessage[] }>('/api/admin/outbound').then(r => setOutbound(r.items)).catch(() => setOutbound([]))
  }, [])
  useEffect(load, [load])

  const remind = async (userId: string) => {
    setBusy(userId)
    try { await api.post('/api/admin/abandoned-carts/remind', { userId }); app.notify('SMS reminder dispatched via provider adapter'); load() }
    catch (e) { app.notify(e instanceof Error ? e.message : 'Failed', 'err') }
    finally { setBusy('') }
  }
  const runJob = async (name: 'document-expiry' | 'stock-sweeper') => {
    setBusy(name)
    try { await api.post(`/api/admin/jobs/${name}`); app.notify(`Job "${name}" executed — see response in outbound log`); load() }
    catch (e) { app.notify(e instanceof Error ? e.message : 'Job failed', 'err') }
    finally { setBusy('') }
  }

  return (
    <div className="space-y-8">
      <section>
        <h3 className="mb-3 flex items-center gap-2 text-sm font-semibold"><Bell className="h-4 w-4 text-primary" />Job triggers <span className="text-xs font-normal text-muted-foreground">(in production these run on Quartz schedules)</span></h3>
        <div className="grid gap-3 sm:grid-cols-2">
          <Card><CardContent className="flex items-center gap-3 p-4">
            <Clock className="h-5 w-5 text-muted-foreground" />
            <div className="min-w-0 flex-1"><p className="text-sm font-medium">Document expiry monitor</p><p className="text-xs text-muted-foreground">Expire licenses past their date → auto-suspend vendors (hourly)</p></div>
            <Button size="sm" variant="outline" disabled={busy === 'document-expiry'} onClick={() => runJob('document-expiry')}>Run now</Button>
          </CardContent></Card>
          <Card><CardContent className="flex items-center gap-3 p-4">
            <ShoppingBag className="h-5 w-5 text-muted-foreground" />
            <div className="min-w-0 flex-1"><p className="text-sm font-medium">Stock reservation sweeper</p><p className="text-xs text-muted-foreground">Release expired holds so others can buy (every minute)</p></div>
            <Button size="sm" variant="outline" disabled={busy === 'stock-sweeper'} onClick={() => runJob('stock-sweeper')}>Run now</Button>
          </CardContent></Card>
        </div>
      </section>

      <section>
        <h3 className="mb-3 flex items-center gap-2 text-sm font-semibold"><Heart className="h-4 w-4 text-primary" />Abandoned carts — SMS recovery</h3>
        {!carts ? <Skeleton className="h-32 rounded-xl" /> : carts.length === 0 ? <p className="py-6 text-center text-xs text-muted-foreground">No abandoned carts.</p> : (
          <Table>
            <TableHeader><TableRow><TableHead>Buyer</TableHead><TableHead>Items</TableHead><TableHead>Idle since</TableHead><TableHead>Reminder</TableHead><TableHead /></TableRow></TableHeader>
            <TableBody>
              {carts.map(c => (
                <TableRow key={c.userId}>
                  <TableCell><div className="text-sm font-medium">{c.fullName}</div><div className="text-xs text-muted-foreground">{c.phone ?? c.email}</div></TableCell>
                  <TableCell className="text-xs">{c.items.length} line(s)</TableCell>
                  <TableCell className="text-xs text-muted-foreground">{dateTimeFmt(c.updatedAt)}</TableCell>
                  <TableCell>{c.reminderSentAt ? <Badge variant="outline" className="border-emerald-200 text-emerald-700">sent {dateFmtS(c.reminderSentAt)}</Badge> : <Badge variant="outline">not sent</Badge>}</TableCell>
                  <TableCell className="text-right"><Button size="sm" variant="outline" disabled={busy === c.userId || !c.phone} onClick={() => remind(c.userId)}><Bell className="mr-1 h-3 w-3" />Send SMS</Button></TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </section>

      <section>
        <h3 className="mb-3 flex items-center gap-2 text-sm font-semibold"><Truck className="h-4 w-4 text-primary" />Outbound messages <span className="text-xs font-normal text-muted-foreground">(SMS/Email provider log — production: Ethio Telecom / Safaricom / SMTP)</span></h3>
        {!outbound ? <Skeleton className="h-32 rounded-xl" /> : outbound.length === 0 ? <p className="py-6 text-center text-xs text-muted-foreground">No outbound messages yet.</p> : (
          <Table>
            <TableHeader><TableRow><TableHead>Channel</TableHead><TableHead>Template</TableHead><TableHead>Destination</TableHead><TableHead>Provider</TableHead><TableHead>Status</TableHead><TableHead>When</TableHead></TableRow></TableHeader>
            <TableBody>
              {outbound.map(m => (
                <TableRow key={m.id}>
                  <TableCell><Badge variant="outline">{m.channel}</Badge></TableCell>
                  <TableCell className="text-xs font-medium">{m.template}</TableCell>
                  <TableCell className="text-xs">{m.destination || '—'}</TableCell>
                  <TableCell className="text-xs text-muted-foreground">{m.provider}</TableCell>
                  <TableCell><StatusBadge status={m.status} /></TableCell>
                  <TableCell className="text-xs text-muted-foreground">{dateTimeFmt(m.createdAt)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </section>
    </div>
  )
}

void AlertTriangle
void DollarSign
void LayoutDashboard
void Megaphone
void Package
void ScrollText
void TrendingUp
void Users
void Star
