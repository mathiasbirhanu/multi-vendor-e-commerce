// Shared UI primitives for the marketplace UI.
'use client'
import { useEffect, useState } from 'react'
import NextImage from 'next/image'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Star } from 'lucide-react'
import { cn } from '@/lib/utils'
import { br, ProductColor } from '@/lib/api'
import { useApp } from '@/store/app'
import type { ProductImage as ProductImageSpec } from '@/lib/api'

// Legacy gradient tile — used as the loading/failure fallback behind every product photo.
export function ProductImage({ images, title, className = '' }: { images: string | null; title: string; className?: string }) {
  let from = '#134e4a', to = '#0f766e'
  try {
    if (images) { const g = JSON.parse(images); from = g.from ?? from; to = g.to ?? to }
  } catch { /* default gradient */ }
  return (
    <div
      aria-label={`Image of ${title}`}
      className={`relative flex items-center justify-center ${className}`}
      style={{ background: `linear-gradient(135deg, ${from}, ${to})` }}
    >
      <span className="select-none px-4 text-center text-sm font-medium text-white/90 drop-shadow-sm">
        {title.split(' ').slice(0, 3).join(' ')}
      </span>
    </div>
  )
}

/**
 * Optimized product photo.
 * - next/image → AVIF/WebP negotiation + a responsive srcset from `sizes`,
 *   so phones download a small variant instead of the original file.
 * - Shimmer placeholder until decode, then a soft fade-in (perceived speed).
 * - Any failure (404, offline CDN, bad file) degrades to the gradient tile,
 *   so the layout never shows a broken-image glyph.
 * - Data URLs (vendor uploads in preview mode) bypass the optimizer.
 */
export function ProductPhoto({
  src, alt, title, className = '', sizes, priority = false, quality = 75, fallbackImages,
}: {
  src?: string | null
  alt?: string
  title: string
  className?: string
  sizes?: string
  priority?: boolean
  quality?: number
  fallbackImages?: string | null
}) {
  const [failed, setFailed] = useState(false)
  const [loaded, setLoaded] = useState(false)
  // Data saver (auto-detected on 2G/3G or user-set): drop to quality 40 —
  // the srcset still adapts, AVIF/WebP still negotiated, ~40-50% less bytes.
  const dataSaver = useApp(s => s.dataSaver)
  const q = dataSaver ? 40 : quality
  if (!src || failed) return <ProductImage images={fallbackImages ?? null} title={title} className={className} />
  const isDataUrl = src.startsWith('data:')
  return (
    // cn/twMerge lets callers override `relative` (e.g. the card hover shot passes
    // `absolute inset-0` — position utilities are a twMerge conflict group, last wins).
    <span className={cn('relative block overflow-hidden bg-muted', className)}>
      {!loaded && <span className="img-skeleton absolute inset-0" aria-hidden />}
      <NextImage
        src={src}
        alt={alt || title}
        fill
        sizes={sizes}
        quality={q}
        priority={priority}
        unoptimized={isDataUrl}
        onLoad={() => setLoaded(true)}
        onError={() => setFailed(true)}
        className={`object-cover transition-opacity duration-500 ${loaded ? 'opacity-100' : 'opacity-0'}`}
      />
    </span>
  )
}

export const defaultPhoto = (gallery?: ProductImageSpec[] | null): ProductImageSpec | undefined =>
  gallery && gallery.length > 0 ? gallery[0] : undefined

// Photos visible for a selected color: that color's shots first, then shared shots (colorId null).
export const photosForColor = (gallery: ProductImageSpec[] | null | undefined, colorId: string | null): ProductImageSpec[] => {
  const g = gallery ?? []
  const mine = colorId ? g.filter(i => i.colorId === colorId) : []
  const shared = g.filter(i => !i.colorId)
  return [...mine, ...shared]
}

// Up to 4 color swatches, e.g. on product cards & tables.
export function ColorDots({ colors, max = 4, className = '' }: { colors?: ProductColor[] | null; max?: number; className?: string }) {
  const list = (colors ?? []).slice(0, max)
  if (list.length === 0) return null
  return (
    <span className={`inline-flex items-center gap-1 ${className}`} aria-label={`${colors!.length} color${colors!.length > 1 ? 's' : ''} available`}>
      {list.map(c => (
        <span key={c.id} title={c.name} className="h-3 w-3 rounded-full border border-black/10 shadow-sm" style={{ background: c.hex }} />
      ))}
      {(colors!.length ?? 0) > max && <span className="text-[10px] text-muted-foreground">+{colors!.length - max}</span>}
    </span>
  )
}

const STATUS_STYLES: Record<string, string> = {
  // order / vendor / payment statuses
  Placed: 'bg-zinc-100 text-zinc-700 border-zinc-200',
  PaymentConfirmed: 'bg-emerald-50 text-emerald-700 border-emerald-200',
  VendorAccepted: 'bg-teal-50 text-teal-700 border-teal-200',
  Shipped: 'bg-amber-50 text-amber-700 border-amber-200',
  Delivered: 'bg-green-50 text-green-700 border-green-200',
  Completed: 'bg-emerald-50 text-emerald-800 border-emerald-200',
  Cancelled: 'bg-zinc-100 text-zinc-500 border-zinc-200',
  RefundRequested: 'bg-orange-50 text-orange-700 border-orange-200',
  Refunded: 'bg-red-50 text-red-700 border-red-200',
  Disputed: 'bg-red-50 text-red-800 border-red-200',
  Approved: 'bg-emerald-50 text-emerald-700 border-emerald-200',
  PendingReview: 'bg-amber-50 text-amber-700 border-amber-200',
  Rejected: 'bg-red-50 text-red-700 border-red-200',
  Suspended: 'bg-zinc-100 text-zinc-600 border-zinc-200',
  UnderReview: 'bg-sky-50 text-sky-700 border-sky-200',
  Active: 'bg-emerald-50 text-emerald-700 border-emerald-200',
  Archived: 'bg-zinc-100 text-zinc-500 border-zinc-200',
  Draft: 'bg-zinc-100 text-zinc-600 border-zinc-200',
  Paid: 'bg-emerald-50 text-emerald-700 border-emerald-200',
  Requested: 'bg-amber-50 text-amber-700 border-amber-200',
  Processing: 'bg-sky-50 text-sky-700 border-sky-200',
  Verified: 'bg-emerald-50 text-emerald-700 border-emerald-200',
  Pending: 'bg-amber-50 text-amber-700 border-amber-200',
  Initiated: 'bg-sky-50 text-sky-700 border-sky-200',
  Confirmed: 'bg-emerald-50 text-emerald-700 border-emerald-200',
  Failed: 'bg-red-50 text-red-700 border-red-200',
  // gap-fill v2 statuses
  VendorApproved: 'bg-emerald-50 text-emerald-700 border-emerald-200',
  VendorRejected: 'bg-red-50 text-red-700 border-red-200',
  AdminReview: 'bg-sky-50 text-sky-700 border-sky-200',
  Expired: 'bg-zinc-100 text-zinc-500 border-zinc-200',
  Consumed: 'bg-teal-50 text-teal-700 border-teal-200',
  Released: 'bg-zinc-100 text-zinc-500 border-zinc-200',
  Skipped: 'bg-zinc-100 text-zinc-500 border-zinc-200',
  Sent: 'bg-emerald-50 text-emerald-700 border-emerald-200',
  Queued: 'bg-sky-50 text-sky-700 border-sky-200',
}

export function StatusBadge({ status, className = '' }: { status: string; className?: string }) {
  return (
    <Badge variant="outline" className={`${STATUS_STYLES[status] ?? 'bg-zinc-100 text-zinc-700 border-zinc-200'} ${className}`}>
      {status.replace(/([A-Z])/g, ' $1').trim()}
    </Badge>
  )
}

export function Stars({ rating, count, className = '' }: { rating: number; count?: number; className?: string }) {
  return (
    <span className={`inline-flex items-center gap-1 text-xs ${className}`}>
      <Star className="h-3.5 w-3.5 fill-amber-400 text-amber-400" aria-hidden />
      <span className="font-medium">{rating.toFixed(1)}</span>
      {count !== undefined && <span className="text-muted-foreground">({count})</span>}
    </span>
  )
}

/** Interactive star picker for the review form. */
export function StarsInput({ value, onChange }: { value: number; onChange: (v: number) => void }) {
  const [hover, setHover] = useState(0)
  return (
    <div className="flex items-center gap-0.5" role="radiogroup" aria-label="Rating">
      {[1, 2, 3, 4, 5].map(n => (
        <button
          key={n} type="button" role="radio" aria-checked={value === n} aria-label={`${n} star${n > 1 ? 's' : ''}`}
          className="rounded p-0.5 transition-transform hover:scale-110"
          onMouseEnter={() => setHover(n)} onMouseLeave={() => setHover(0)} onClick={() => onChange(n)}
        >
          <Star className={`h-6 w-6 ${(hover || value) >= n ? 'fill-amber-400 text-amber-400' : 'text-zinc-300'}`} />
        </button>
      ))}
    </div>
  )
}

/** Live countdown (d/h/m/s) for flash sales & reservation timers — pauses on data-saver to save CPU. */
export function Countdown({ to, prefix, className = '' }: { to: string | Date; prefix?: string; className?: string }) {
  const [left, setLeft] = useState(() => new Date(to).getTime() - Date.now())
  useEffect(() => {
    // 1s re-render only when the tab is visible; budget-device CPUs stay idle otherwise.
    const id = setInterval(() => { if (!document.hidden) setLeft(new Date(to).getTime() - Date.now()) }, 1000)
    return () => clearInterval(id)
  }, [to])
  if (left <= 0) return <span className={className}>ended</span>
  const d = Math.floor(left / 864e5), h = Math.floor((left % 864e5) / 36e5), m = Math.floor((left % 36e5) / 6e4), s = Math.floor((left % 6e4) / 1e3)
  const box = (v: number, l: string) => (
    <span className="inline-flex min-w-7 flex-col items-center rounded-md bg-black/80 px-1 py-0.5 text-white">
      <span className="text-xs font-bold tabular-nums leading-3.5">{String(v).padStart(2, '0')}</span>
      <span className="text-[8px] uppercase opacity-70 leading-2.5">{l}</span>
    </span>
  )
  return (
    <span className={`inline-flex items-center gap-1 ${className}`}>
      {prefix && <span className="text-xs text-muted-foreground">{prefix}</span>}
      {d > 0 && box(d, 'd')}{box(h, 'h')}{box(m, 'm')}{box(s, 's')}
    </span>
  )
}

/** Section heading with optional "view all" link. */
export function SectionHead({ title, icon: Icon, onViewAll, sub }: { title: string; icon?: React.ComponentType<{ className?: string }>; onViewAll?: () => void; sub?: string }) {
  return (
    <div className="mb-4 flex items-end justify-between gap-3">
      <div>
        <h2 className="flex items-center gap-2 text-lg font-semibold tracking-tight sm:text-xl">
          {Icon && <Icon className="h-5 w-5 text-primary" aria-hidden />}{title}
        </h2>
        {sub && <p className="mt-0.5 text-xs text-muted-foreground">{sub}</p>}
      </div>
      {onViewAll && <Button variant="ghost" size="sm" className="text-primary" onClick={onViewAll}>View all →</Button>}
    </div>
  )
}

export function Money({ value, className = '' }: { value: number; className?: string }) {
  return <span className={className}>{br(value)}</span>
}

export function PlatformChip({ vendorId }: { vendorId: string | null }) {
  if (vendorId) return null
  return <Badge className="bg-teal-700 text-white hover:bg-teal-700">MarketHub Store</Badge>
}

export function KpiCard({ label, value, sub, tone = 'default' }: { label: string; value: string; sub?: string; tone?: 'default' | 'primary' | 'warn' }) {
  return (
    <div className={`rounded-xl border p-5 ${tone === 'primary' ? 'border-emerald-200 bg-emerald-50/60' : tone === 'warn' ? 'border-amber-200 bg-amber-50/60' : 'bg-card'}`}>
      <div className="text-xs font-medium uppercase tracking-wide text-muted-foreground">{label}</div>
      <div className="mt-1 text-2xl font-semibold tracking-tight">{value}</div>
      {sub && <div className="mt-1 text-xs text-muted-foreground">{sub}</div>}
    </div>
  )
}

// Card-grid loading placeholder — mirrors ProductCard's shape (image + 3 text lines).
export function ProductCardSkeleton() {
  return (
    <div className="overflow-hidden rounded-2xl border bg-card">
      <div className="img-skeleton aspect-square w-full" />
      <div className="space-y-2 p-4">
        <div className="img-skeleton h-4 w-3/4 rounded" />
        <div className="img-skeleton h-4 w-1/2 rounded" />
        <div className="img-skeleton h-3 w-2/3 rounded" />
      </div>
    </div>
  )
}
