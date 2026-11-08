// Storefront: header (live search suggest + i18n + wishlist), home/catalog
// (banners, flash sale, categories, sections), product detail (variant picker,
// color/angle gallery, reviews, related, seller card), cart sheet, my orders
// (tracking timeline, delivery PIN, returns), wishlist.
// Performance: RSC-friendly data fetching, next/image AVIF/WebP + sizes, lazy
// sections, skeletons, CSS-only animations (stopped in data-saver mode).
'use client'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  api, br, Category, dateFmt, dateTimeFmt, HomeData, lineUnitPrice, Order, Paged, Product,
  ProductCard, ProductColor, ProductDetail, ProductVariant, Review, ReviewsPage, SuggestData, Zone,
} from '@/lib/api'
import { t } from '@/lib/i18n'
import { useApp, type AppState } from '@/store/app'
import { ColorDots, Countdown, PlatformChip, ProductCardSkeleton, ProductPhoto, SectionHead, Stars, StarsInput, StatusBadge, defaultPhoto, photosForColor } from './shared'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Sheet, SheetContent, SheetHeader, SheetTitle, SheetTrigger } from '@/components/ui/sheet'
import { Separator } from '@/components/ui/separator'
import { Skeleton } from '@/components/ui/skeleton'
import { Textarea } from '@/components/ui/textarea'
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group'
import {
  ShoppingBag, Search, MapPin, ShieldCheck, Clock, Package, Store, ChevronLeft, ChevronRight, Truck,
  Plus, Sparkles, Smartphone, Shirt, CookingPot, ShoppingBasket, Blocks, BadgeCheck, RotateCcw, ReceiptText,
  Star, SignalLow, Heart, Zap, Globe, X, PackageCheck, KeyRound, Camera, History,
} from 'lucide-react'

type CartLine = { id: string; productId: string; quantity: number; availableQty?: number; color: ProductColor | null; variantId?: string | null; variant?: ProductVariant | null; product: Product }
type CartData = { items: CartLine[]; subtotal: number }

const CAT_ICONS: Record<string, React.ComponentType<{ className?: string }>> = {
  Electronics: Smartphone, Fashion: Shirt, 'Home & Kitchen': CookingPot, Beauty: Sparkles, Groceries: ShoppingBasket, 'Baby & Toys': Blocks,
}

// ---------------- Product card (home sections / catalog / wishlist / related) ----------------
type Cardish = {
  id: string; slug: string; title: string; priceETB: number
  salePriceETB?: number | null; compareAtPriceETB?: number | null
  thumb?: string | null; images?: string | null
  ratingAvg: number; ratingCount: number; soldCount?: number
  vendorName?: string | null; platform: boolean; stock: number
  vendorId?: string | null; colors?: { name: string; hex: string }[]
}
export function ProductCardView({ p: raw, priority = false }: { p: ProductCard | (Product & { thumb?: string | null }); priority?: boolean }) {
  const app = useApp()
  const p = ('platform' in (raw as Record<string, unknown>)
    ? raw
    : { ...(raw as unknown as Record<string, unknown>), platform: (raw as unknown as { vendorId?: string | null }).vendorId == null, thumb: (raw as unknown as Product).gallery?.[0]?.url ?? null }) as unknown as Cardish
  const go = () => app.go('product', p.slug)
  const onSale = (p.salePriceETB ?? 0) > 0 && (p.salePriceETB ?? 0) < p.priceETB
  const price = onSale ? p.salePriceETB! : p.priceETB
  const off = onSale ? Math.round(((p.priceETB - price) / p.priceETB) * 100) : 0
  return (
    <button onClick={go} className="group overflow-hidden rounded-2xl border bg-card text-left shadow-sm transition-all hover:-translate-y-0.5 hover:shadow-md focus-visible:ring-2 focus-visible:ring-primary">
      <div className="relative aspect-square w-full overflow-hidden bg-muted">
        <ProductPhoto src={p.thumb ?? null} fallbackImages={p.images ?? null} title={p.title} priority={priority} sizes="(max-width: 640px) 50vw, (max-width: 1024px) 33vw, 20vw" className="h-full w-full transition-transform duration-300 group-hover:scale-105" />
        {off > 0 && <Badge className="absolute left-2 top-2 bg-red-600 text-white hover:bg-red-600">−{off}%</Badge>}
        {p.platform && <Badge className="absolute right-2 top-2 bg-teal-700 text-white hover:bg-teal-700">MarketHub</Badge>}
        {p.stock <= 0 && <span className="absolute inset-0 flex items-center justify-center bg-black/40 text-sm font-semibold text-white">Out of stock</span>}
      </div>
      <div className="space-y-1.5 p-3">
        <h3 className="line-clamp-2 min-h-9 text-[13px] font-medium leading-4.5">{p.title}</h3>
        <div className="flex items-center gap-1.5">
          <Stars rating={p.ratingAvg} count={p.ratingCount} />
          {(p.soldCount ?? 0) > 0 && <span className="text-[10px] text-muted-foreground">· {p.soldCount}+ {t('sold', app.lang)}</span>}
        </div>
        <div className="flex items-baseline gap-1.5">
          <span className="text-base font-bold text-primary">{br(price)}</span>
          {onSale && <span className="text-xs text-muted-foreground line-through">{br(p.priceETB)}</span>}
        </div>
        <div className="flex items-center justify-between">
          <span className="truncate text-[10px] text-muted-foreground">{p.platform ? 'MarketHub Store' : p.vendorName}</span>
          <ColorDots colors={p.colors?.map((c, i) => ({ id: String(i), ...c })) as ProductColor[]} max={4} />
        </div>
      </div>
    </button>
  )
}

// ---------------- Header ----------------
export function Header() {
  const app = useApp()
  const lang = app.lang
  const [q, setQ] = useState('')
  const [sug, setSug] = useState<SuggestData | null>(null)
  const [sugOpen, setSugOpen] = useState(false)
  const boxRef = useRef<HTMLDivElement>(null)
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null)

  // Debounced suggest — one small request per pause, not per keystroke (3G friendly).
  useEffect(() => {
    if (timer.current) clearTimeout(timer.current)
    if (q.trim().length < 2) { setSug(null); return }
    timer.current = setTimeout(() => {
      api.get<SuggestData>(`/api/catalog/search/suggest?q=${encodeURIComponent(q.trim())}`)
        .then(setSug).catch(() => setSug(null))
    }, 280)
    return () => { if (timer.current) clearTimeout(timer.current) }
  }, [q])

  useEffect(() => {
    const close = (e: MouseEvent) => { if (!boxRef.current?.contains(e.target as Node)) setSugOpen(false) }
    document.addEventListener('click', close)
    return () => document.removeEventListener('click', close)
  }, [])

  const submitSearch = () => {
    setSugOpen(false)
    window.dispatchEvent(new CustomEvent('mh-search', { detail: q.trim() }))
  }
  const pick = (slug: string) => { setSugOpen(false); setQ(''); app.go('product', slug) }

  return (
    <header className="sticky top-0 z-40 border-b bg-background/95 backdrop-blur supports-[backdrop-filter]:bg-background/80">
      {/* trust ticker */}
      <div className="mh-ticker overflow-hidden bg-teal-900 text-[11px] text-teal-50">
        <div className="mh-ticker-track flex w-max gap-8 py-1.5">
          {[0, 1].map(k => (
            <span key={k} className="flex gap-8 whitespace-nowrap" aria-hidden={k === 1}>
              <span className="flex items-center gap-1"><ShieldCheck className="h-3 w-3" /> KYC-verified vendors</span>
              <span className="flex items-center gap-1"><Truck className="h-3 w-3" /> Nationwide zone delivery</span>
              <span className="flex items-center gap-1"><Clock className="h-3 w-3" /> Vendor payouts within 12h</span>
              <span className="flex items-center gap-1"><RotateCcw className="h-3 w-3" /> 7-day returns</span>
              <span className="flex items-center gap-1"><BadgeCheck className="h-3 w-3" /> 15% VAT included</span>
            </span>
          ))}
        </div>
      </div>

      <div className="mx-auto flex max-w-7xl items-center gap-2 px-3 py-2.5 sm:gap-4 sm:px-4">
        <button className="flex shrink-0 items-center gap-2" onClick={() => app.go('home')} aria-label="MarketHub home">
          <span className="flex h-8 w-8 items-center justify-center rounded-xl bg-gradient-to-br from-emerald-700 to-teal-600 text-xs font-bold text-white">MH</span>
          <span className="hidden font-display text-lg font-semibold sm:block">MarketHub<span className="text-primary">.et</span></span>
        </button>

        {/* search + suggestions */}
        <div ref={boxRef} className="relative min-w-0 flex-1">
          <div className="flex items-center gap-2 rounded-full border bg-muted/40 px-3 py-1.5 focus-within:border-primary focus-within:bg-background">
            <Search className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
            <input
              className="w-full bg-transparent text-sm outline-none placeholder:text-muted-foreground"
              value={q} onChange={e => { setQ(e.target.value); setSugOpen(true) }}
              onKeyDown={e => { if (e.key === 'Enter') submitSearch() }}
              placeholder={t('search', lang)} aria-label={t('search', lang)}
            />
            {q && <button onClick={() => { setQ(''); setSug(null) }} aria-label="Clear search"><X className="h-3.5 w-3.5 text-muted-foreground" /></button>}
          </div>
          {sugOpen && sug && (sug.products.length > 0 || sug.categories.length > 0) && (
            <div className="absolute inset-x-0 top-full z-50 mt-1.5 overflow-hidden rounded-xl border bg-background shadow-lg">
              {sug.products.map(p => (
                <button key={p.slug} className="flex w-full items-center gap-2.5 px-3 py-2 text-left transition-colors hover:bg-muted/60" onClick={() => pick(p.slug)}>
                  <ProductPhoto src={p.thumb} title={p.title} sizes="36px" className="h-9 w-9 shrink-0 rounded-md" />
                  <span className="min-w-0 flex-1 truncate text-sm">{p.title}</span>
                  <span className="shrink-0 text-xs font-semibold text-primary">{br(p.priceETB)}</span>
                </button>
              ))}
              {sug.categories.length > 0 && (
                <div className="flex flex-wrap gap-1.5 border-t px-3 py-2">
                  {sug.categories.map(c => (
                    <Badge key={c.slug} variant="outline" className="cursor-pointer hover:bg-muted"><Search className="mr-1 h-2.5 w-2.5" />{c.name}</Badge>
                  ))}
                </div>
              )}
            </div>
          )}
        </div>

        <div className="flex shrink-0 items-center gap-0.5 sm:gap-1">
          {/* language */}
          <button
            className="hidden items-center gap-1 rounded-full px-2 py-1.5 text-xs font-medium text-muted-foreground transition-colors hover:bg-muted hover:text-foreground sm:flex"
            onClick={() => app.setLang(lang === 'en' ? 'am' : 'en')}
            aria-label="Toggle language English / አማርኛ" title="EN / አማርኛ"
          >
            <Globe className="h-4 w-4" />{lang === 'en' ? 'አማ' : 'EN'}
          </button>
          {/* data saver */}
          <button
            className={`hidden items-center gap-1 rounded-full px-2 py-1.5 text-xs font-medium transition-colors sm:flex ${app.dataSaver ? 'bg-emerald-100 text-emerald-800' : 'text-muted-foreground hover:bg-muted'}`}
            onClick={() => app.setDataSaverMode(app.dataSaver ? 'off' : 'on')}
            aria-pressed={app.dataSaver} title="Saves ~50% image data on 3G"
          >
            <SignalLow className="h-4 w-4" />{t('dataSaver', lang)}
          </button>
          {/* wishlist */}
          <button className="relative rounded-full p-2 transition-colors hover:bg-muted" onClick={() => app.go('wishlist')} aria-label={t('wishlist', lang)}>
            <Heart className="h-4.5 w-4.5" />
            {app.wishlistCount > 0 && <span className="absolute -right-0.5 -top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-red-600 px-0.5 text-[9px] font-bold text-white">{app.wishlistCount}</span>}
          </button>
          {/* cart */}
          <Sheet>
            <SheetTrigger asChild>
              <button className="relative rounded-full p-2 transition-colors hover:bg-muted" aria-label={t('cart', lang)}>
                <ShoppingBag className="h-4.5 w-4.5" />
                {app.cartCount > 0 && <span className="absolute -right-0.5 -top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-primary px-0.5 text-[9px] font-bold text-white">{app.cartCount}</span>}
              </button>
            </SheetTrigger>
            <SheetContent className="flex w-full flex-col sm:max-w-md"><CartSheet /></SheetContent>
          </Sheet>
          {/* user */}
          <Dropdownish app={app} />
        </div>
      </div>
    </header>
  )
}

function Dropdownish({ app }: { app: AppState }) {
  const [open, setOpen] = useState(false)
  const u = app.user
  const items: { label: string; onClick: () => void; show: boolean }[] = [
    { label: t('orders', app.lang), onClick: () => app.go('orders'), show: !!u },
    { label: t('wishlist', app.lang), onClick: () => app.go('wishlist'), show: !!u },
    { label: t('vendor', app.lang), onClick: () => app.go('vendor'), show: u?.role === 'Vendor' },
    { label: t('admin', app.lang), onClick: () => app.go('admin'), show: u?.role === 'Admin' },
  ]
  return (
    <div className="relative">
      {u ? (
        <>
          <button className="ml-1 flex items-center gap-1.5 rounded-full border py-1 pl-1 pr-2.5 transition-colors hover:bg-muted" onClick={() => setOpen(!open)} aria-expanded={open} aria-haspopup="menu">
            <span className="flex h-6 w-6 items-center justify-center rounded-full bg-primary/10 text-[10px] font-bold text-primary">{u.fullName.split(' ').map(x => x[0]).slice(0, 2).join('')}</span>
            <span className="hidden max-w-24 truncate text-xs font-medium sm:block">{u.fullName.split(' ')[0]}</span>
          </button>
          {open && (
            <div className="absolute right-0 top-full z-50 mt-1.5 w-52 overflow-hidden rounded-xl border bg-background shadow-lg" role="menu">
              <div className="border-b px-3 py-2.5">
                <p className="truncate text-sm font-medium">{u.fullName}</p>
                <p className="truncate text-[11px] text-muted-foreground">{u.email}{u.vendor ? ` · ${u.vendor.businessName}` : ''}{u.adminRole ? ` · ${u.adminRole}` : ''}</p>
              </div>
              {items.filter(i => i.show).map(i => (
                <button key={i.label} className="block w-full px-3 py-2 text-left text-sm transition-colors hover:bg-muted" onClick={() => { setOpen(false); i.onClick() }}>{i.label}</button>
              ))}
              <button className="block w-full border-t px-3 py-2 text-left text-sm text-red-600 transition-colors hover:bg-red-50" onClick={() => { setOpen(false); api.post('/api/auth/logout').then(() => { app.setUser(null); app.go('home') }) }}>{t('signOut', app.lang)}</button>
            </div>
          )}
        </>
      ) : (
        <Button size="sm" className="ml-1 rounded-full" onClick={() => app.openAuth('signin')}>{t('signIn', app.lang)}</Button>
      )}
    </div>
  )
}

// ---------------- Cart sheet ----------------
function CartSheet() {
  const app = useApp()
  const [cart, setCart] = useState<CartData | null>(null)
  const load = useCallback(() => { api.get<CartData>('/api/sales/cart').then(setCart).catch(() => setCart({ items: [], subtotal: 0 })) }, [])
  useEffect(load, [load])
  const setQty = async (id: string, qty: number) => {
    try { await api.patch('/api/sales/cart', { itemId: id, quantity: qty }); load(); app.refreshCart() }
    catch (e) { app.notify(e instanceof Error ? e.message : 'Update failed', 'err') }
  }
  const lines = cart?.items ?? []
  return (
    <>
      <SheetHeader><SheetTitle className="flex items-center gap-2"><ShoppingBag className="h-4 w-4 text-primary" />{t('cart', app.lang)} ({app.cartCount})</SheetTitle></SheetHeader>
      <div className="min-h-0 flex-1 overflow-y-auto px-4">
        {!cart ? <div className="space-y-3 py-4">{[1, 2, 3].map(i => <Skeleton key={i} className="h-20 rounded-xl" />)}</div>
          : lines.length === 0 ? (
            <div className="flex flex-col items-center gap-3 py-20 text-center text-sm text-muted-foreground">
              <ShoppingBag className="h-10 w-10 text-muted-foreground/30" />
              <p>Your cart is empty</p>
              <Button size="sm" onClick={() => app.go('home')}>Browse products</Button>
            </div>
          ) : lines.map(i => {
            const unit = i.variant?.priceETB ?? lineUnitPrice(i.product, i.color)
            const low = (i.availableQty ?? 99) < i.quantity
            return (
              <div key={i.id} className="flex gap-3 border-b py-3 last:border-0">
                <ProductPhoto src={i.product.gallery?.[0]?.url} fallbackImages={i.product.images} title={i.product.title} sizes="64px" className="h-16 w-16 shrink-0 rounded-xl" />
                <div className="min-w-0 flex-1">
                  <button className="line-clamp-2 text-left text-[13px] font-medium hover:text-primary" onClick={() => app.go('product', i.product.slug)}>{i.product.title}</button>
                  <p className="mt-0.5 text-[11px] text-muted-foreground">
                    {i.variant ? `${i.variant.size ? `Size ${i.variant.size}` : ''}${i.variant.colorHex ? ' ·' : ''}` : ''}
                    {i.color && <span className="inline-flex items-center gap-1"><span className="inline-block h-2.5 w-2.5 rounded-full border border-black/10" style={{ background: i.color.hex }} />{i.color.name}</span>}
                    {i.variant?.sku ? ` · ${i.variant.sku}` : ''}
                  </p>
                  {low && <p className="mt-0.5 text-[11px] font-medium text-red-600">Only {i.availableQty} left</p>}
                  <div className="mt-1.5 flex items-center justify-between">
                    <div className="flex items-center rounded-lg border">
                      <button className="px-2 py-0.5 text-sm disabled:opacity-40" onClick={() => setQty(i.id, i.quantity - 1)} aria-label="Decrease quantity" disabled={i.quantity <= 1}>−</button>
                      <span className="min-w-6 text-center text-sm tabular-nums">{i.quantity}</span>
                      <button className="px-2 py-0.5 text-sm" onClick={() => setQty(i.id, i.quantity + 1)} aria-label="Increase quantity">+</button>
                    </div>
                    <span className="text-sm font-semibold">{br(unit * i.quantity)}</span>
                  </div>
                </div>
              </div>
            )
          })}
      </div>
      {lines.length > 0 && (
        <div className="border-t p-4">
          <div className="mb-2 flex justify-between text-sm"><span className="text-muted-foreground">{t('subtotal', app.lang)}</span><span className="font-semibold">{br(cart!.subtotal)}</span></div>
          <p className="mb-3 text-[11px] text-muted-foreground">VAT 15% and zone delivery calculated at checkout</p>
          <Button className="w-full" size="lg" onClick={() => app.go('checkout')}>{t('placeOrder', app.lang)} →</Button>
        </div>
      )}
    </>
  )
}

// ---------------- Home / catalog ----------------
type CatalogState = { q: string; cat: string; sort: string; page: number }

export function HomeView() {
  const app = useApp()
  const [home, setHome] = useState<HomeData | null>(null)
  const [list, setList] = useState<Paged<Product> | null>(null)
  const [cat, setCat] = useState<Category[]>([])
  const [cs, setCs] = useState<CatalogState>({ q: '', cat: '', sort: 'featured', page: 1 })
  const [loading, setLoading] = useState(false)
  const [bannerIdx, setBannerIdx] = useState(0)
  const [flashEnd, setFlashEnd] = useState<string | null>(null)

  // search event from header
  useEffect(() => {
    const h = (e: Event) => { setCs(s => ({ ...s, q: (e as CustomEvent).detail as string, page: 1 })) }
    window.addEventListener('mh-search', h)
    return () => window.removeEventListener('mh-search', h)
  }, [])

  useEffect(() => {
    api.get<HomeData>('/api/catalog/home').then(d => {
      setHome(d); setCat(d.categories)
      if (d.flashSale) setFlashEnd(d.flashSale.endsAt)
    }).catch(() => {})
  }, [])

  useEffect(() => {
    setLoading(true)
    const p = new URLSearchParams({ page: String(cs.page), pageSize: '12', sort: cs.sort })
    if (cs.q) p.set('q', cs.q)
    if (cs.cat) p.set('cat', cs.cat)
    api.get<Paged<Product>>(`/api/catalog/products?${p}`).then(setList).catch(() => {}).finally(() => setLoading(false))
  }, [cs])

  // banner auto-rotate — paused entirely in data-saver (CPU + battery)
  const banners = home?.banners ?? []
  useEffect(() => {
    if (app.dataSaver || banners.length < 2) return
    const id = setInterval(() => setBannerIdx(i => (i + 1) % banners.length), 5000)
    return () => clearInterval(id)
  }, [app.dataSaver, banners.length])

  return (
    <main className="mx-auto max-w-7xl px-3 pb-16 pt-4 sm:px-4">
      {/* hero + banners */}
      {banners.length > 0 && (
        <section className="relative mb-6 overflow-hidden rounded-3xl" aria-roledescription="carousel" aria-label="Promotions">
          <div className="relative aspect-[21/8] w-full sm:aspect-[21/6]">
            {banners.map((b, i) => (
              <div key={b.id} className={`absolute inset-0 transition-opacity duration-700 ${i === bannerIdx ? 'opacity-100' : 'pointer-events-none opacity-0'}`} aria-hidden={i !== bannerIdx}>
                <ProductPhoto src={b.imageUrl} title={b.title} priority={i === 0} sizes="100vw" quality={app.dataSaver ? 45 : 70} className="h-full w-full" />
                <div className="absolute inset-0 flex flex-col justify-end bg-gradient-to-t from-black/70 via-black/20 to-transparent p-5 sm:p-8">
                  <h2 className="max-w-lg font-display text-lg font-bold text-white sm:text-2xl">{b.title}</h2>
                  {b.link && <Button size="sm" className="mt-2 w-fit" onClick={() => {
                    if (b.link!.includes('cat=')) {
                      const slugPart = b.link!.split('cat=')[1] ?? ''
                      const target = cat.find(c => c.slug === slugPart || c.id === slugPart)
                      if (target) setCs(s => ({ ...s, cat: target.id, page: 1 }))
                    } else { window.location.hash = b.link! }
                  }}>Shop now</Button>}
                </div>
              </div>
            ))}
          </div>
          {banners.length > 1 && !app.dataSaver && (
            <>
              <button className="absolute left-2 top-1/2 -translate-y-1/2 rounded-full bg-black/30 p-1.5 text-white backdrop-blur hover:bg-black/50" onClick={() => setBannerIdx(i => (i - 1 + banners.length) % banners.length)} aria-label="Previous banner"><ChevronLeft className="h-4 w-4" /></button>
              <button className="absolute right-2 top-1/2 -translate-y-1/2 rounded-full bg-black/30 p-1.5 text-white backdrop-blur hover:bg-black/50" onClick={() => setBannerIdx(i => (i + 1) % banners.length)} aria-label="Next banner"><ChevronRight className="h-4 w-4" /></button>
              <div className="absolute bottom-2 right-3 flex gap-1">
                {banners.map((_, i) => <span key={i} className={`h-1.5 rounded-full transition-all ${i === bannerIdx ? 'w-4 bg-white' : 'w-1.5 bg-white/50'}`} />)}
              </div>
            </>
          )}
        </section>
      )}

      {/* flash sale */}
      {home?.flashSale && home.flashSale.products.length > 0 && (
        <section className="mb-8 rounded-3xl border border-red-200 bg-gradient-to-br from-red-50 to-orange-50/60 p-4 sm:p-6" aria-label="Flash sale">
          <div className="mb-4 flex flex-wrap items-center justify-between gap-2">
            <h2 className="flex items-center gap-2 text-lg font-bold text-red-700 sm:text-xl"><Zap className="h-5 w-5 fill-red-600 text-red-600" aria-hidden />{t('flashSale', app.lang)}</h2>
            <Countdown to={home.flashSale.endsAt} prefix={t('endsIn', app.lang)} />
          </div>
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
            {home.flashSale.products.map((p, i) => <ProductCardView key={p.id} p={p} priority={i < 2} />)}
          </div>
        </section>
      )}

      {/* categories */}
      <section className="mb-8" aria-label="Categories">
        <SectionHead title={t('shopByCategory', app.lang)} />
        <div className="grid grid-cols-3 gap-2.5 sm:grid-cols-6">
          {cat.map(c => {
            const Icon = CAT_ICONS[c.name] ?? Blocks
            return (
              <button key={c.id} onClick={() => setCs(s => ({ ...s, cat: c.id, q: '', page: 1 }))} className={`flex flex-col items-center gap-2 rounded-2xl border p-3.5 transition-all hover:-translate-y-0.5 hover:border-primary hover:shadow-sm ${cs.cat === c.id ? 'border-primary bg-primary/5' : 'bg-card'}`}>
                <span className="flex h-10 w-10 items-center justify-center rounded-full bg-primary/10"><Icon className="h-5 w-5 text-primary" /></span>
                <span className="text-center text-[11px] font-medium leading-3.5">{c.name}</span>
                <span className="text-[10px] text-muted-foreground">{c.productCount} items</span>
              </button>
            )
          })}
        </div>
      </section>

      {/* sections */}
      {home?.featured && home.featured.length > 0 && (
        <section className="mb-8"><SectionHead title={t('featured', app.lang)} icon={Sparkles} sub="Hand-picked by the MarketHub team" />
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">{home.featured.slice(0, 5).map((p, i) => <ProductCardView key={p.id} p={p} priority={i < 2} />)}</div>
        </section>
      )}
      {home?.topRated && home.topRated.length > 0 && (
        <section className="mb-8"><SectionHead title={t('topRated', app.lang)} icon={Star} sub="Highest rated by verified buyers" onViewAll={() => setCs(s => ({ ...s, sort: 'rating', page: 1 }))} />
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">{home.topRated.slice(0, 5).map(p => <ProductCardView key={p.id} p={p} />)}</div>
        </section>
      )}
      {home?.newArrivals && home.newArrivals.length > 0 && (
        <section className="mb-10"><SectionHead title={t('newArrivals', app.lang)} icon={Zap} sub="Fresh on the marketplace" onViewAll={() => setCs(s => ({ ...s, sort: 'newest', page: 1 }))} />
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">{home.newArrivals.slice(0, 5).map(p => <ProductCardView key={p.id} p={p} />)}</div>
        </section>
      )}

      {/* catalog */}
      <section aria-label="All products">
        <SectionHead title={cs.q ? `Results for "${cs.q}"` : cs.cat ? cat.find(c => c.id === cs.cat)?.name ?? 'Catalog' : 'All products'} icon={ShoppingBag}
          onViewAll={(cs.q || cs.cat || cs.sort !== 'featured') ? () => setCs({ q: '', cat: '', sort: 'featured', page: 1 }) : undefined} />
        <div className="mb-4 flex flex-wrap items-center gap-2">
          <Select value={cs.sort} onValueChange={v => setCs(s => ({ ...s, sort: v, page: 1 }))}>
            <SelectTrigger className="w-44" aria-label="Sort products"><SelectValue /></SelectTrigger>
            <SelectContent>
              <SelectItem value="featured">Featured</SelectItem>
              <SelectItem value="newest">Newest</SelectItem>
              <SelectItem value="price_asc">Price: low → high</SelectItem>
              <SelectItem value="price_desc">Price: high → low</SelectItem>
              <SelectItem value="rating">Top rated</SelectItem>
              <SelectItem value="sold">Best selling</SelectItem>
            </SelectContent>
          </Select>
          {cs.cat && <Badge variant="outline" className="cursor-pointer hover:bg-muted" onClick={() => setCs(s => ({ ...s, cat: '', page: 1 }))}>{cat.find(c => c.id === cs.cat)?.name} <X className="ml-1 h-3 w-3" /></Badge>}
          {(list?.totalCount ?? 0) > 0 && <span className="ml-auto text-xs text-muted-foreground">{list!.totalCount} products</span>}
        </div>
        {loading && !list ? (
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">{Array.from({ length: 10 }).map((_, i) => <ProductCardSkeleton key={i} />)}</div>
        ) : (list?.items.length ?? 0) === 0 ? (
          <div className="flex flex-col items-center gap-2 py-20 text-sm text-muted-foreground"><Package className="h-8 w-8 text-muted-foreground/30" />No products match your filters.</div>
        ) : (
          <>
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
              {list!.items.map((p, i) => <ProductCardView key={p.id} p={{ ...p, thumb: p.gallery?.[0]?.url ?? null }} priority={i < 5 && cs.page === 1} />)}
            </div>
            {(list!.page * list!.pageSize) < list!.totalCount && (
              <div className="mt-6 text-center">
                <Button variant="outline" disabled={loading} onClick={() => setCs(s => ({ ...s, page: s.page + 1 }))}>{loading ? 'Loading…' : 'Load more products'}</Button>
              </div>
            )}
          </>
        )}
      </section>

      {/* trust strip */}
      <section className="mt-12 grid gap-3 rounded-3xl border bg-card p-5 sm:grid-cols-2 lg:grid-cols-4">
        {[
          { icon: ShieldCheck, title: 'Verified vendors', sub: 'Every seller passes KYC: registration license, trade license & TIN' },
          { icon: Truck, title: 'Nationwide delivery', sub: 'Zone-based pricing across all Ethiopian regions + 15% VAT' },
          { icon: RotateCcw, title: '7-day returns', sub: 'Return & refund policy enforced per order with evidence upload' },
          { icon: Clock, title: '12h vendor payouts', sub: 'Vendors request payouts, processed within 12 hours — guaranteed SLA' },
        ].map(f => (
          <div key={f.title} className="flex gap-3">
            <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-primary/10"><f.icon className="h-5 w-5 text-primary" /></span>
            <div><p className="text-sm font-semibold">{f.title}</p><p className="mt-0.5 text-xs leading-4 text-muted-foreground">{f.sub}</p></div>
          </div>
        ))}
      </section>
    </main>
  )
}

const catById = (cats: { id: string; name: string; slug: string }[], id: string) => cats.find(c => c.id === id)?.id ?? ''
void catById

// ---------------- Product detail ----------------
export function ProductView({ slug }: { slug: string }) {
  const app = useApp()
  const [data, setData] = useState<ProductDetail | null>(null)
  const [err, setErr] = useState('')
  const [colorId, setColorId] = useState<string | null>(null)
  const [size, setSize] = useState<string | null>(null)
  const [qty, setQty] = useState(1)
  const [imgIdx, setImgIdx] = useState(0)
  const [reviews, setReviews] = useState<ReviewsPage | null>(null)
  const [reviewOpen, setReviewOpen] = useState(false)
  const [busy, setBusy] = useState(false)
  const reviewsBlockRef = useRef<HTMLDivElement | null>(null)

  useEffect(() => {
    setData(null); setErr(''); setColorId(null); setSize(null); setQty(1); setImgIdx(0)
    api.get<ProductDetail>(`/api/catalog/products/${slug}`)
      .then(d => {
        setData(d)
        if (d.variants.length > 0) {
          const firstColor = d.variants.find(v => v.colorId)?.colorId ?? null
          setColorId(firstColor)
          setSize(pickDefaultSize(d.variants, firstColor))
        }
      })
      .catch(e => setErr(e instanceof Error ? e.message : 'Product not found'))
  }, [slug])

  useEffect(() => {
    if (data) api.get<ReviewsPage>(`/api/catalog/products/${data.product.id}/reviews?page=1`).then(setReviews).catch(() => {})
  }, [data?.product.id])

  const p = data?.product
  const gallery = useMemo(() => (p ? photosForColor(p.gallery, colorId) : []), [p, colorId])
  const photo = gallery[Math.min(imgIdx, Math.max(0, gallery.length - 1))]

  const sizesForColor = useMemo(() => {
    if (!data) return [] as ProductVariant[]
    const withColor = data.variants.filter(v => v.colorId)
    if (withColor.length === 0) return data.variants // size-only or single product
    return withColor.filter(v => v.colorId === colorId)
  }, [data, colorId])

  const variant = useMemo(() => {
    if (!data) return null
    if (data.variants.length === 0) return null
    if (sizesForColor.length === 1 && !sizesForColor[0].size) return sizesForColor[0]
    return sizesForColor.find(v => v.size === size) ?? null
  }, [data, sizesForColor, size])

  const basePrice = variant?.priceETB ?? p?.priceETB ?? 0
  const onSale = (p?.salePriceETB ?? 0) > 0 && p!.salePriceETB! < basePrice && (!p?.saleEndsAt || new Date(p.saleEndsAt) > new Date())
  const price = onSale ? p!.salePriceETB! : basePrice
  const stock = variant ? variant.stock : (p?.stock ?? 0)

  const pickDefaultSize = (vs: ProductVariant[], cid: string | null) => {
    const scope = vs.filter(v => (vs[0]?.colorId ? v.colorId === cid : true))
    return scope.find(v => v.stock > 0)?.size ?? scope[0]?.size ?? null
  }

  const addToCart = async (thenCheckout = false) => {
    if (!p) return
    if (data!.variants.length > 0 && !variant) { app.notify('Please choose a variant', 'err'); return }
    setBusy(true)
    try {
      await api.post('/api/sales/cart', { productId: p.id, quantity: qty, colorId: variant?.colorId ?? colorId ?? undefined, variantId: variant?.id })
      await app.refreshCart()
      app.notify(thenCheckout ? 'Added — going to checkout' : 'Added to cart')
      if (thenCheckout) app.go('checkout')
    } catch (e) { app.notify(e instanceof Error ? e.message : 'Could not add to cart', 'err') }
    finally { setBusy(false) }
  }

  const toggleWish = async () => {
    if (!p) return
    if (!app.user) { app.openAuth('signin'); return }
    try {
      const r = await api.post<{ wishlisted: boolean }>(`/api/sales/wishlist/${p.id}`)
      app.notify(r.wishlisted ? 'Saved to wishlist ♥' : 'Removed from wishlist')
      app.refreshWishlist()
    } catch { app.notify('Could not update wishlist', 'err') }
  }

  if (err) return <main className="py-32 text-center text-sm text-muted-foreground">{err} · <button className="text-primary underline" onClick={() => app.go('home')}>Back to marketplace</button></main>
  if (!data || !p) return <main className="mx-auto max-w-7xl px-4 py-8"><div className="grid gap-8 lg:grid-cols-2"><Skeleton className="aspect-square rounded-3xl" /><div className="space-y-4"><Skeleton className="h-8 w-3/4" /><Skeleton className="h-6 w-1/3" /><Skeleton className="h-32 w-full" /></div></div></main>

  const jsonLd = {
    '@context': 'https://schema.org', '@type': 'Product', name: p.title, description: p.description,
    image: gallery.map(g => g.url), sku: variant?.sku ?? p.sku ?? p.id,
    brand: { '@type': 'Brand', name: p.vendorName ?? 'MarketHub Store' },
    aggregateRating: p.ratingCount > 0 ? { '@type': 'AggregateRating', ratingValue: p.ratingAvg, reviewCount: p.ratingCount } : undefined,
    offers: { '@type': 'Offer', priceCurrency: 'ETB', price, availability: stock > 0 ? 'https://schema.org/InStock' : 'https://schema.org/OutOfStock', seller: { '@type': 'Organization', name: p.vendorName ?? 'MarketHub Store' } },
  }

  return (
    <main className="mx-auto max-w-7xl px-3 pb-16 pt-4 sm:px-4">
      <script type="application/ld+json" dangerouslySetInnerHTML={{ __html: JSON.stringify(jsonLd) }} />
      <div className="grid gap-8 lg:grid-cols-[minmax(0,5fr)_minmax(0,4fr)] xl:gap-12">
        {/* gallery */}
        <div>
          <div className="relative aspect-square w-full overflow-hidden rounded-3xl border bg-muted">
            <ProductPhoto key={photo?.id} src={photo?.url} fallbackImages={p.images} alt={`${p.title} — ${photo?.alt || 'photo'}`} title={p.title} priority quality={app.dataSaver ? 50 : 80} sizes="(max-width: 1024px) 100vw, 560px" className="h-full w-full" />
            {onSale && <Badge className="absolute left-3 top-3 bg-red-600 text-white hover:bg-red-600">−{Math.round(((basePrice - price) / basePrice) * 100)}% flash sale</Badge>}
          </div>
          {gallery.length > 1 && (
            <div className="mt-3 flex gap-2 overflow-x-auto pb-1" role="listbox" aria-label="Product photos by angle">
              {gallery.map((g, i) => (
                <button key={g.id} onClick={() => setImgIdx(i)} className={`relative h-16 w-16 shrink-0 overflow-hidden rounded-xl border-2 transition-all ${i === imgIdx ? 'border-primary' : 'border-transparent opacity-70 hover:opacity-100'}`} role="option" aria-selected={i === imgIdx} aria-label={g.alt || `Photo ${i + 1}`}>
                  <ProductPhoto src={g.url} fallbackImages={p.images} title={p.title} sizes="64px" className="h-full w-full" />
                  {g.alt && <span className="absolute inset-x-0 bottom-0 bg-black/50 py-0.5 text-center text-[8px] font-medium text-white">{g.alt}</span>}
                </button>
              ))}
            </div>
          )}
        </div>

        {/* buy box */}
        <div className="flex flex-col">
          <div className="flex items-start justify-between gap-3">
            <div>
              <div className="flex flex-wrap items-center gap-1.5">
                <PlatformChip vendorId={p.vendorId} />
                {p.category && <Badge variant="outline">{p.category.name}</Badge>}
                {(p.soldCount ?? 0) > 20 && <Badge variant="outline" className="border-amber-300 text-amber-700">🔥 {p.soldCount}+ sold</Badge>}
              </div>
              <h1 className="mt-2 text-xl font-semibold leading-7 tracking-tight sm:text-2xl">{p.title}</h1>
              <button className="mt-1.5 flex items-center gap-2 text-xs text-muted-foreground hover:text-primary" onClick={() => reviewsBlockRef.current?.scrollIntoView({ behavior: 'smooth' })}>
                <Stars rating={p.ratingAvg} count={p.ratingCount} /><span className="underline">see reviews</span> · {p.viewCount ?? 0} views
              </button>
            </div>
            <button className="rounded-full border p-2.5 transition-colors hover:bg-muted" onClick={toggleWish} aria-label="Toggle wishlist">
              <Heart className="h-4.5 w-4.5" />
            </button>
          </div>

          <div className="mt-4 flex items-baseline gap-2.5">
            <span className="text-3xl font-bold tracking-tight text-primary">{br(price)}</span>
            {onSale && <span className="text-base text-muted-foreground line-through">{br(basePrice)}</span>}
            {onSale && <Countdown to={p.saleEndsAt ?? ''} />}
          </div>
          <p className="mt-1 text-[11px] text-muted-foreground">Includes 15% VAT at checkout · delivery per zone</p>

          {/* color swatches */}
          {(p.colors?.length ?? 0) > 0 && (
            <div className="mt-5">
              <p className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-muted-foreground">{t('color', app.lang)}: <span className="font-medium normal-case text-foreground">{p.colors!.find(c => c.id === colorId)?.name ?? '—'}</span></p>
              <div className="flex flex-wrap gap-2">
                {p.colors!.map(c => {
                  const hasStock = data.variants.length ? data.variants.some(v => v.colorId === c.id && v.stock > 0) : c.stock > 0
                  return (
                    <button key={c.id} onClick={() => { setColorId(c.id); setImgIdx(0); setSize(pickDefaultSize(data.variants, c.id)) }}
                      className={`relative h-9 w-9 rounded-full border-2 p-0.5 transition-all ${colorId === c.id ? 'border-primary scale-110' : 'border-transparent hover:scale-105'}`}
                      style={{ boxShadow: colorId === c.id ? '0 0 0 2px hsl(var(--primary) / 0.25)' : undefined }} aria-label={c.name} aria-pressed={colorId === c.id} title={c.name}>
                      <span className="block h-full w-full rounded-full border border-black/10" style={{ background: c.hex }} />
                      {!hasStock && <span className="absolute inset-0 flex items-center justify-center rounded-full bg-white/70 text-[9px]">✕</span>}
                    </button>
                  )
                })}
              </div>
            </div>
          )}

          {/* size variants */}
          {sizesForColor.some(v => v.size) && (
            <div className="mt-4">
              <p className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-muted-foreground">{t('size', app.lang)}</p>
              <div className="flex flex-wrap gap-2">
                {sizesForColor.map(v => (
                  <button key={v.id} disabled={!v.isActive || v.stock <= 0}
                    onClick={() => setSize(v.size)}
                    className={`min-w-11 rounded-xl border px-3 py-1.5 text-sm font-medium transition-all disabled:cursor-not-allowed disabled:opacity-40 ${size === v.size ? 'border-primary bg-primary text-white' : 'bg-card hover:border-primary'}`}
                    aria-pressed={size === v.size}>
                    {v.size}
                    {v.stock > 0 && v.stock <= 5 && <span className="ml-1 text-[9px] opacity-80">({v.stock})</span>}
                  </button>
                ))}
              </div>
              {variant && <p className="mt-1.5 text-[11px] text-muted-foreground">SKU {variant.sku} · {variant.priceETB !== null ? `variant price ${br(variant.priceETB)}` : `base price ${br(p.priceETB)}`}</p>}
            </div>
          )}

          {/* stock + qty */}
          <div className="mt-5 flex items-center gap-3">
            <div className="flex items-center rounded-xl border">
              <button className="px-3 py-2 disabled:opacity-40" onClick={() => setQty(q => Math.max(1, q - 1))} aria-label="Decrease quantity" disabled={qty <= 1}>−</button>
              <span className="min-w-8 text-center text-sm font-semibold tabular-nums">{qty}</span>
              <button className="px-3 py-2" onClick={() => setQty(q => Math.min(stock, q + 1))} aria-label="Increase quantity">+</button>
            </div>
            {stock > 0 ? <span className={`text-xs ${stock <= 5 ? 'font-medium text-red-600' : 'text-muted-foreground'}`}>{stock <= 5 ? `Only ${stock} ${t('leftInStock', app.lang)}!` : `${stock} ${t('leftInStock', app.lang)}`}</span>
              : <Badge variant="outline" className="border-red-200 text-red-700">{t('outOfStock', app.lang)}</Badge>}
          </div>

          <div className="mt-4 flex flex-col gap-2 sm:flex-row">
            <Button size="lg" className="flex-1 rounded-xl" disabled={busy || stock <= 0} onClick={() => addToCart(false)}><ShoppingBag className="mr-2 h-4 w-4" />{t('addToCart', app.lang)}</Button>
            <Button size="lg" variant="secondary" className="flex-1 rounded-xl bg-teal-800 text-white hover:bg-teal-900" disabled={busy || stock <= 0} onClick={() => addToCart(true)}><Zap className="mr-2 h-4 w-4" />{t('buyNow', app.lang)}</Button>
          </div>

          {/* seller card */}
          <div className="mt-5 rounded-2xl border bg-card p-4">
            <div className="flex items-center justify-between gap-3">
              <div className="flex items-center gap-3">
                <span className="flex h-10 w-10 items-center justify-center rounded-xl bg-primary/10"><Store className="h-5 w-5 text-primary" /></span>
                <div>
                  <p className="flex items-center gap-1 text-sm font-semibold">{p.vendorName ?? 'MarketHub Store'}{!p.vendorId && <BadgeCheck className="h-3.5 w-3.5 text-teal-700" />}</p>
                  <p className="text-[11px] text-muted-foreground">{t('soldBy', app.lang)} {p.vendorName ?? 'the official MarketHub store'}</p>
                </div>
              </div>
              <Badge variant="outline" className="border-emerald-200 text-emerald-700"><ShieldCheck className="mr-1 h-3 w-3" />KYC verified</Badge>
            </div>
          </div>

          {/* delivery info */}
          <div className="mt-3 grid grid-cols-2 gap-2 text-xs">
            <div className="rounded-xl border bg-muted/30 p-3"><p className="flex items-center gap-1.5 font-medium"><Truck className="h-3.5 w-3.5 text-primary" />{t('deliveryTo', app.lang)}</p><p className="mt-1 text-muted-foreground">All Ethiopian regions · 150–350 Br · free over thresholds</p></div>
            <div className="rounded-xl border bg-muted/30 p-3"><p className="flex items-center gap-1.5 font-medium"><RotateCcw className="h-3.5 w-3.5 text-primary" />Returns</p><p className="mt-1 text-muted-foreground">7-day window · evidence upload · 5-day refund</p></div>
          </div>
        </div>
      </div>

      {/* description + reviews */}
      <div className="mt-12 grid gap-8 lg:grid-cols-[minmax(0,5fr)_minmax(0,4fr)] xl:gap-12">
        <section aria-label="Description">
          <h2 className="mb-3 text-lg font-semibold">Product description</h2>
          <p className="whitespace-pre-line text-sm leading-6 text-muted-foreground">{p.description}</p>
        </section>
        <section ref={reviewsBlockRef} aria-label="Reviews">
          <div className="mb-3 flex items-center justify-between">
            <h2 className="text-lg font-semibold">{t('reviews', app.lang)} ({data.reviewsSummary.count})</h2>
            <Button size="sm" variant="outline" onClick={() => { if (!app.user) return app.openAuth('signin'); setReviewOpen(true) }}><Plus className="mr-1 h-3.5 w-3.5" />{t('writeReview', app.lang)}</Button>
          </div>
          <ReviewBlock summary={data.reviewsSummary} page={reviews} />
        </section>
      </div>

      {/* related */}
      {data.related.length > 0 && (
        <section className="mt-12"><SectionHead title={t('related', app.lang)} icon={Sparkles} />
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6">{data.related.map(r => <ProductCardView key={r.id} p={r} />)}</div>
        </section>
      )}

      {reviewOpen && <ReviewDialog productId={p.id} onClose={() => setReviewOpen(false)} onDone={() => {
        api.get<ReviewsPage>(`/api/catalog/products/${p.id}/reviews?page=1`).then(setReviews).catch(() => {})
        api.get<ProductDetail>(`/api/catalog/products/${slug}`).then(d => setData(d)).catch(() => {})
      }} />}
    </main>
  )
}

function ReviewBlock({ summary, page }: { summary: ProductDetail['reviewsSummary']; page: ReviewsPage | null }) {
  const rows: [number, number][] = [[5, summary.breakdown.r5], [4, summary.breakdown.r4], [3, summary.breakdown.r3], [2, summary.breakdown.r2], [1, summary.breakdown.r1]]
  return (
    <div>
      {summary.count === 0 ? (
        <div className="rounded-2xl border bg-card p-8 text-center text-sm text-muted-foreground">No reviews yet — be the first to review this product.</div>
      ) : (
        <>
          <div className="flex items-center gap-5 rounded-2xl border bg-card p-4">
            <div className="text-center">
              <p className="text-3xl font-bold">{summary.avg.toFixed(1)}</p>
              <Stars rating={summary.avg} /><p className="mt-1 text-[11px] text-muted-foreground">{summary.count} reviews</p>
            </div>
            <div className="min-w-0 flex-1 space-y-1">
              {rows.map(([star, n]) => (
                <div key={star} className="flex items-center gap-2 text-[11px]">
                  <span className="w-6 text-right">{star}★</span>
                  <div className="h-1.5 flex-1 overflow-hidden rounded-full bg-muted"><div className="h-full rounded-full bg-amber-400" style={{ width: `${summary.count ? (n / summary.count) * 100 : 0}%` }} /></div>
                  <span className="w-6 text-muted-foreground">{n}</span>
                </div>
              ))}
            </div>
          </div>
          <div className="mt-3 space-y-3">
            {(page?.items ?? []).map(r => <ReviewCard key={r.id} r={r} />)}
          </div>
        </>
      )}
    </div>
  )
}

export function ReviewCard({ r }: { r: Review }) {
  return (
    <div className="rounded-2xl border bg-card p-4">
      <div className="flex items-center justify-between gap-2">
        <div className="flex items-center gap-2">
          <span className="flex h-7 w-7 items-center justify-center rounded-full bg-primary/10 text-[10px] font-bold text-primary">{(r.userName ?? 'B')[0]}</span>
          <div>
            <p className="text-xs font-medium">{r.userName ?? 'Buyer'}</p>
            <div className="flex items-center gap-1.5">
              <Stars rating={r.rating} />
              {r.verified && <Badge variant="outline" className="border-emerald-200 px-1 py-0 text-[9px] text-emerald-700"><BadgeCheck className="mr-0.5 h-2.5 w-2.5" />Verified purchase</Badge>}
            </div>
          </div>
        </div>
        <span className="text-[10px] text-muted-foreground">{dateFmt(r.createdAt)}</span>
      </div>
      {r.title && <p className="mt-2 text-sm font-medium">{r.title}</p>}
      <p className="mt-1 text-sm leading-5 text-muted-foreground">{r.body}</p>
      {(r.imageUrls?.length ?? 0) > 0 && (
        <div className="mt-2 flex gap-2">
          {r.imageUrls!.slice(0, 3).map((u, i) => <ProductPhoto key={i} src={u} title="review photo" sizes="56px" className="h-14 w-14 rounded-lg" />)}
        </div>
      )}
      {r.vendorReply && (
        <div className="mt-2.5 rounded-xl bg-muted/50 p-3">
          <p className="text-[11px] font-semibold text-primary">Seller response</p>
          <p className="mt-0.5 text-xs leading-4 text-muted-foreground">{r.vendorReply}</p>
        </div>
      )}
    </div>
  )
}

function ReviewDialog({ productId, onClose, onDone }: { productId: string; onClose: () => void; onDone: () => void }) {
  const app = useApp()
  const [rating, setRating] = useState(5)
  const [title, setTitle] = useState('')
  const [body, setBody] = useState('')
  const [images, setImages] = useState<string[]>([])
  const [orderId, setOrderId] = useState('')
  const [orders, setOrders] = useState<Order[]>([])
  const [busy, setBusy] = useState(false)
  const fileRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    // Delivered/Completed orders containing this product → verified purchase
    api.get<{ items: Order[] }>('/api/sales/orders').then(r => {
      const eligible = r.items.filter(o => ['Delivered', 'Completed'].includes(o.status) && o.items.some(i => i.productId === productId))
      setOrders(eligible)
      if (eligible[0]) setOrderId(eligible[0].id)
    }).catch(() => {})
  }, [productId])

  const onFiles = (files: FileList | null) => {
    if (!files) return
    Array.from(files).slice(0, 3 - images.length).forEach(f => {
      if (!f.type.startsWith('image/')) return
      // Client-side downscale — keep evidence photos small on 3G uploads.
      const img = new Image()
      img.onload = () => {
        const canvas = document.createElement('canvas')
        const scale = Math.min(1, 720 / Math.max(img.width, img.height))
        canvas.width = Math.round(img.width * scale); canvas.height = Math.round(img.height * scale)
        canvas.getContext('2d')!.drawImage(img, 0, 0, canvas.width, canvas.height)
        setImages(prev => [...prev, canvas.toDataURL('image/jpeg', 0.7)].slice(0, 3))
      }
      img.src = URL.createObjectURL(f)
    })
  }

  const submit = async () => {
    setBusy(true)
    try {
      await api.post('/api/catalog/reviews', { productId, orderId: orderId || undefined, rating, title: title || undefined, body, imageUrls: images.length ? images : undefined })
      app.notify('Review submitted — pending moderation, thank you!')
      onDone(); onClose()
    } catch (e) { app.notify(e instanceof Error ? e.message : 'Could not submit review', 'err') }
    finally { setBusy(false) }
  }

  return (
    <Dialog open onOpenChange={v => !v && onClose()}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader><DialogTitle>Write a review</DialogTitle></DialogHeader>
        <div className="space-y-3.5">
          <div><Label>Rating</Label><div className="mt-1"><StarsInput value={rating} onChange={setRating} /></div></div>
          {orders.length > 0 && (
            <div>
              <Label>Link to an order (verified purchase ✓)</Label>
              <Select value={orderId} onValueChange={setOrderId}>
                <SelectTrigger aria-label="Order"><SelectValue placeholder="No order (unverified)" /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="none">No order (unverified)</SelectItem>
                  {orders.map(o => <SelectItem key={o.id} value={o.id}>{o.number} · {dateTimeFmt(o.createdAt)}</SelectItem>)}
                </SelectContent>
              </Select>
            </div>
          )}
          <div><Label htmlFor="rv-title">Title (optional)</Label><Input id="rv-title" value={title} onChange={e => setTitle(e.target.value)} placeholder="Sum it up" maxLength={80} /></div>
          <div><Label htmlFor="rv-body">Your review</Label><Textarea id="rv-body" value={body} onChange={e => setBody(e.target.value)} rows={4} placeholder="What did you like or dislike? (min 10 characters)" /></div>
          <div>
            <Label>Photos (optional, max 3)</Label>
            <div className="mt-1.5 flex items-center gap-2">
              {images.map((u, i) => (
                <span key={i} className="relative">
                  <ProductPhoto src={u} title="upload" sizes="56px" className="h-14 w-14 rounded-lg" />
                  <button className="absolute -right-1 -top-1 rounded-full bg-red-600 p-0.5 text-white" onClick={() => setImages(arr => arr.filter((_, j) => j !== i))} aria-label="Remove photo"><X className="h-3 w-3" /></button>
                </span>
              ))}
              {images.length < 3 && (
                <button className="flex h-14 w-14 items-center justify-center rounded-lg border border-dashed text-muted-foreground hover:border-primary hover:text-primary" onClick={() => fileRef.current?.click()} aria-label="Add photo">
                  <Camera className="h-5 w-5" />
                </button>
              )}
              <input ref={fileRef} type="file" accept="image/*" multiple hidden onChange={e => onFiles(e.target.files)} />
            </div>
          </div>
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>Cancel</Button>
          <Button disabled={busy || body.trim().length < 10 || rating < 1} onClick={submit}>Submit review</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

// ---------------- My orders (timeline, PIN, returns) ----------------
const TIMELINE: { key: string; label: string; icon: React.ComponentType<{ className?: string }> }[] = [
  { key: 'Placed', label: 'Order placed', icon: ReceiptText },
  { key: 'PaymentConfirmed', label: 'Payment confirmed', icon: BadgeCheck },
  { key: 'VendorAccepted', label: 'Vendor accepted', icon: Store },
  { key: 'Shipped', label: 'Shipped', icon: Truck },
  { key: 'Delivered', label: 'Delivered', icon: PackageCheck },
  { key: 'Completed', label: 'Completed', icon: ShieldCheck },
]

export function OrdersView() {
  const app = useApp()
  const [orders, setOrders] = useState<Order[] | null>(null)
  const [filter, setFilter] = useState('all')
  const [expanded, setExpanded] = useState<string | null>(null)
  const [returnItem, setReturnItem] = useState<{ orderId: string; itemId: string; title: string } | null>(null)

  const load = useCallback(() => {
    api.get<{ items: Order[] }>('/api/sales/orders').then(r => setOrders(r.items)).catch(() => setOrders([]))
  }, [])
  useEffect(load, [load])

  if (!app.user) return (
    <main className="flex flex-col items-center justify-center gap-3 py-32 text-sm text-muted-foreground">
      <p>Sign in to see your orders.</p><Button onClick={() => app.openAuth('signin')}>Sign in</Button>
    </main>
  )
  if (!orders) return <main className="mx-auto max-w-4xl px-4 py-8 space-y-3">{[1, 2, 3].map(i => <Skeleton key={i} className="h-28 rounded-2xl" />)}</main>

  const counts = { all: orders.length, active: orders.filter(o => !['Completed', 'Cancelled', 'Refunded', 'Delivered'].includes(o.status)).length, delivered: orders.filter(o => ['Delivered', 'Completed'].includes(o.status)).length, closed: orders.filter(o => ['Cancelled', 'Refunded'].includes(o.status)).length }
  const filtered = orders.filter(o => filter === 'all' ? true : filter === 'active' ? counts.active > 0 && !['Completed', 'Cancelled', 'Refunded', 'Delivered'].includes(o.status) : filter === 'delivered' ? ['Delivered', 'Completed'].includes(o.status) : ['Cancelled', 'Refunded'].includes(o.status))

  return (
    <main className="mx-auto max-w-4xl px-3 pb-16 pt-6 sm:px-4">
      <h1 className="text-2xl font-semibold tracking-tight">{t('orders', app.lang)}</h1>
      <div className="mt-4 flex gap-2">
        {([['all', `All (${counts.all})`], ['active', `Active (${counts.active})`], ['delivered', `Delivered (${counts.delivered})`], ['closed', `Closed (${counts.closed})`]] as const).map(([k, label]) => (
          <button key={k} onClick={() => setFilter(k)} className={`rounded-full border px-3.5 py-1.5 text-xs font-medium transition-colors ${filter === k ? 'border-primary bg-primary text-white' : 'bg-card hover:bg-muted'}`}>{label}</button>
        ))}
      </div>

      <div className="mt-4 space-y-3">
        {filtered.length === 0 && <div className="flex flex-col items-center gap-2 py-20 text-sm text-muted-foreground"><Package className="h-8 w-8 text-muted-foreground/30" />No orders here yet.</div>}
        {filtered.map(o => (
          <OrderCard key={o.id} o={o} open={expanded === o.id} onToggle={() => setExpanded(expanded === o.id ? null : o.id)}
            onReturn={item => setReturnItem({ orderId: o.id, itemId: item.id, title: item.titleSnapshot })}
            onChanged={load} />
        ))}
      </div>

      {returnItem && <ReturnDialog orderId={returnItem.orderId} orderItemId={returnItem.itemId} title={returnItem.title} onClose={() => setReturnItem(null)} onDone={load} />}
    </main>
  )
}

function OrderCard({ o, open, onToggle, onReturn, onChanged }: { o: Order; open: boolean; onToggle: () => void; onReturn: (item: Order['items'][number]) => void; onChanged: () => void }) {
  const app = useApp()
  const [busy, setBusy] = useState(false)
  const pay = async () => {
    setBusy(true)
    try { await api.post('/api/payments/mock/confirm', { providerRef: o.payments?.[0]?.providerRef }); app.notify('Payment confirmed — vendors notified'); onChanged() }
    catch (e) { app.notify(e instanceof Error ? e.message : 'Payment failed', 'err') }
    finally { setBusy(false) }
  }
  const cancel = async () => {
    setBusy(true)
    try { await api.post(`/api/sales/orders/${o.id}/cancel`); app.notify('Order cancelled — reservations released'); onChanged() }
    catch (e) { app.notify(e instanceof Error ? e.message : 'Cancel failed', 'err') }
    finally { setBusy(false) }
  }
  const stageIdx = TIMELINE.findIndex(tl => tl.key === o.status)
  const failed = ['Cancelled', 'Refunded'].includes(o.status)

  return (
    <Card>
      <CardContent className="p-4">
        <button className="flex w-full flex-wrap items-center gap-x-3 gap-y-1 text-left" onClick={onToggle} aria-expanded={open}>
          <span className="font-mono text-xs text-muted-foreground">{o.number}</span>
          <StatusBadge status={o.status} />
          <span className="text-xs text-muted-foreground">{dateTimeFmt(o.createdAt)}</span>
          <span className="ml-auto text-sm font-semibold">{br(o.grandTotalETB)}</span>
          <ChevronRight className={`h-4 w-4 text-muted-foreground transition-transform ${open ? 'rotate-90' : ''}`} />
        </button>

        {open && (
          <div className="mt-3 border-t pt-3">
            {/* tracking timeline */}
            {!failed && (
              <div className="mb-4 flex items-start overflow-x-auto pb-1">
                {TIMELINE.map((tl, i) => {
                  const done = stageIdx >= i
                  const ev = o.events?.find(e => e.status === tl.key)
                  return (
                    <div key={tl.key} className="flex min-w-0 flex-1 flex-col items-center">
                      <div className="flex w-full items-center">
                        <span className={`h-0.5 flex-1 ${i === 0 ? 'opacity-0' : done ? 'bg-primary' : 'bg-border'}`} />
                        <span className={`flex h-7 w-7 shrink-0 items-center justify-center rounded-full border-2 ${done ? 'border-primary bg-primary text-white' : 'border-border bg-background text-muted-foreground'}`}>
                          <tl.icon className="h-3.5 w-3.5" />
                        </span>
                        <span className={`h-0.5 flex-1 ${i === TIMELINE.length - 1 ? 'opacity-0' : stageIdx > i ? 'bg-primary' : 'bg-border'}`} />
                      </div>
                      <p className={`mt-1 text-center text-[9px] leading-3 ${done ? 'font-medium' : 'text-muted-foreground'}`}>{tl.label}</p>
                      {ev?.note && <p className="text-center text-[8px] text-muted-foreground">{ev.note}</p>}
                    </div>
                  )
                })}
              </div>
            )}

            {/* tracking + PIN */}
            <div className="mb-3 grid gap-2 sm:grid-cols-2">
              {o.trackingCode && (
                <div className="flex items-center gap-2 rounded-xl border bg-muted/30 px-3 py-2 text-sm">
                  <Truck className="h-4 w-4 shrink-0 text-primary" />
                  <span className="text-xs text-muted-foreground">{t('trackingCode', app.lang)}</span>
                  <span className="ml-auto font-mono text-xs font-bold">{o.trackingCode}</span>
                </div>
              )}
              {o.deliveryPin && ['VendorAccepted', 'Shipped', 'Delivered'].includes(o.status) && (
                <div className="flex items-center gap-2 rounded-xl border border-amber-200 bg-amber-50 px-3 py-2 text-sm">
                  <KeyRound className="h-4 w-4 shrink-0 text-amber-600" />
                  <div>
                    <p className="text-[10px] font-medium uppercase tracking-wide text-amber-700">{t('deliveryPin', app.lang)}</p>
                    <p className="font-mono text-base font-bold tracking-[0.3em] text-amber-900">{o.deliveryPin}</p>
                  </div>
                </div>
              )}
            </div>
            {o.deliveryPin && ['VendorAccepted', 'Shipped'].includes(o.status) && <p className="mb-3 text-[11px] text-muted-foreground">{t('pinHint', app.lang)}.</p>}

            {/* items */}
            {o.items.map(i => (
              <div key={i.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 py-1.5 text-sm">
                <span className="min-w-0 flex-1 truncate">{i.titleSnapshot}
                  {(i.colorName || i.variantLabel) && <span className="text-xs text-muted-foreground"> · {i.variantLabel ?? i.colorName}</span>}
                  <span className="text-muted-foreground"> × {i.quantity}</span>
                </span>
                <span>{br(i.unitPriceETB * i.quantity)}</span>
                {['Delivered', 'Completed'].includes(o.status) && i.refundStatus === 'None' && (
                  <Button size="sm" variant="ghost" className="h-7 text-xs text-orange-600" onClick={() => onReturn(i)}><RotateCcw className="mr-1 h-3 w-3" />{t('requestReturn', app.lang)}</Button>
                )}
                {i.refundStatus !== 'None' && <StatusBadge status={i.refundStatus === 'Requested' ? 'Pending' : i.refundStatus} />}
              </div>
            ))}

            <div className="mt-3 rounded-xl bg-muted/40 p-3 text-xs">
              <div className="flex justify-between py-0.5"><span className="text-muted-foreground">Subtotal{o.couponCode ? ` (coupon ${o.couponCode} −${br(o.couponDiscountETB ?? 0)})` : ''}</span><span>{br(o.subtotalETB - (o.couponDiscountETB ?? 0))}</span></div>
              <div className="flex justify-between py-0.5"><span className="text-muted-foreground">Delivery ({o.zoneName})</span><span>{o.deliveryFeeETB === 0 ? 'FREE' : br(o.deliveryFeeETB)}</span></div>
              <div className="flex justify-between py-0.5"><span className="text-muted-foreground">VAT {o.vatRate}%</span><span>{br(o.vatETB)}</span></div>
              <Separator className="my-1.5" />
              <div className="flex justify-between font-semibold"><span>Total</span><span>{br(o.grandTotalETB)}</span></div>
              <p className="mt-1.5 text-[11px] text-muted-foreground"><MapPin className="mr-1 inline h-3 w-3" />{o.addressLine}, {o.city} · {o.phone}</p>
            </div>

            <div className="mt-3 flex flex-wrap gap-2">
              {o.status === 'Placed' && <Button size="sm" disabled={busy} onClick={pay}><BadgeCheck className="mr-1 h-3.5 w-3.5" />Complete payment</Button>}
              {o.status === 'Placed' && <Button size="sm" variant="outline" className="text-red-600" disabled={busy} onClick={cancel}>{t('cancelOrder', app.lang)}</Button>}
              {o.status === 'RefundRequested' && <Badge variant="outline" className="border-orange-300 text-orange-700"><History className="mr-1 h-3 w-3" />Refund under review by admin</Badge>}
            </div>
          </div>
        )}
      </CardContent>
    </Card>
  )
}

function ReturnDialog({ orderId, orderItemId, title, onClose, onDone }: { orderId: string; orderItemId: string; title: string; onClose: () => void; onDone: () => void }) {
  const app = useApp()
  const [reason, setReason] = useState('Damaged')
  const [description, setDescription] = useState('')
  const [evidence, setEvidence] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const fileRef = useRef<HTMLInputElement>(null)

  const onFiles = (files: FileList | null) => {
    if (!files) return
    Array.from(files).slice(0, 3 - evidence.length).forEach(f => {
      if (!f.type.startsWith('image/')) return
      const img = new Image()
      img.onload = () => {
        const canvas = document.createElement('canvas')
        const scale = Math.min(1, 720 / Math.max(img.width, img.height))
        canvas.width = Math.round(img.width * scale); canvas.height = Math.round(img.height * scale)
        canvas.getContext('2d')!.drawImage(img, 0, 0, canvas.width, canvas.height)
        setEvidence(prev => [...prev, canvas.toDataURL('image/jpeg', 0.7)].slice(0, 3))
      }
      img.src = URL.createObjectURL(f)
    })
  }

  const submit = async () => {
    setBusy(true)
    try {
      await api.post('/api/sales/returns', { orderId, orderItemId, reason, description, evidenceUrls: evidence.length ? evidence : undefined })
      app.notify('Return requested — the vendor has 48h to respond, then MarketHub arbitrates')
      onDone(); onClose()
    } catch (e) { app.notify(e instanceof Error ? e.message : 'Return request failed', 'err') }
    finally { setBusy(false) }
  }

  return (
    <Dialog open onOpenChange={v => !v && onClose()}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader><DialogTitle>Return — {title}</DialogTitle></DialogHeader>
        <div className="space-y-3.5">
          <div>
            <Label>Reason</Label>
            <Select value={reason} onValueChange={setReason}>
              <SelectTrigger aria-label="Return reason"><SelectValue /></SelectTrigger>
              <SelectContent>
                <SelectItem value="Damaged">Arrived damaged</SelectItem>
                <SelectItem value="WrongItem">Wrong item received</SelectItem>
                <SelectItem value="NotAsDescribed">Not as described</SelectItem>
                <SelectItem value="ChangedMind">Changed my mind</SelectItem>
                <SelectItem value="Other">Other</SelectItem>
              </SelectContent>
            </Select>
          </div>
          <div><Label>Description</Label><Textarea rows={3} value={description} onChange={e => setDescription(e.target.value)} placeholder="Explain what happened (min 10 characters)" /></div>
          <div>
            <Label>Photo evidence (recommended)</Label>
            <div className="mt-1.5 flex items-center gap-2">
              {evidence.map((u, i) => (
                <span key={i} className="relative">
                  <ProductPhoto src={u} title="evidence" sizes="56px" className="h-14 w-14 rounded-lg" />
                  <button className="absolute -right-1 -top-1 rounded-full bg-red-600 p-0.5 text-white" onClick={() => setEvidence(a => a.filter((_, j) => j !== i))} aria-label="Remove"><X className="h-3 w-3" /></button>
                </span>
              ))}
              {evidence.length < 3 && <button className="flex h-14 w-14 items-center justify-center rounded-lg border border-dashed text-muted-foreground hover:border-primary hover:text-primary" onClick={() => fileRef.current?.click()} aria-label="Add evidence photo"><Camera className="h-5 w-5" /></button>}
              <input ref={fileRef} type="file" accept="image/*" multiple hidden onChange={e => onFiles(e.target.files)} />
            </div>
          </div>
          <p className="rounded-lg bg-muted/50 p-2.5 text-[11px] leading-4 text-muted-foreground">Per the Return &amp; Refund Policy: 7-day window from delivery, items must be unused with original packaging. Delivery fee is non-refundable unless the seller is at fault. Refunds arrive within 5 business days.</p>
        </div>
        <DialogFooter><Button variant="outline" onClick={onClose}>Cancel</Button><Button disabled={busy || description.trim().length < 10} onClick={submit}>Submit return request</Button></DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

// ---------------- Wishlist ----------------
export function WishlistView() {
  const app = useApp()
  const [items, setItems] = useState<{ id: string; productId: string; product: ProductCard }[] | null>(null)

  const load = useCallback(() => {
    api.get<{ items: { id: string; productId: string; product: ProductCard }[] }>('/api/sales/wishlist').then(r => setItems(r.items)).catch(() => setItems([]))
  }, [])
  useEffect(load, [load])

  if (!app.user) return (
    <main className="flex flex-col items-center justify-center gap-3 py-32 text-sm text-muted-foreground">
      <p>Sign in to see your wishlist.</p><Button onClick={() => app.openAuth('signin')}>Sign in</Button>
    </main>
  )
  return (
    <main className="mx-auto max-w-7xl px-3 pb-16 pt-6 sm:px-4">
      <SectionHead title={`${t('wishlist', app.lang)} (${items?.length ?? 0})`} icon={Heart} />
      {!items ? <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">{Array.from({ length: 5 }).map((_, i) => <ProductCardSkeleton key={i} />)}</div>
        : items.length === 0 ? <div className="flex flex-col items-center gap-3 py-24 text-sm text-muted-foreground"><Heart className="h-8 w-8 text-muted-foreground/30" /><p>Nothing saved yet — tap the ♡ on any product.</p><Button size="sm" onClick={() => app.go('home')}>Browse products</Button></div>
          : <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">{items.map(w => <ProductCardView key={w.id} p={w.product} />)}</div>}
    </main>
  )
}

