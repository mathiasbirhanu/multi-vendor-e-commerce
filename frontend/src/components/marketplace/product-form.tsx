// Product listing form (vendor + admin/platform).
// Photos: multi-file upload, client-side resize/compress → data URLs (preview) — the .NET
// backend accepts the same payload and stores blobs via IFileStorage (MinIO/S3) instead.
// Colors: per-color swatch, stock and optional extra price; each photo can be assigned
// to one color (that color's gallery) or left shared, plus an angle/direction label.
'use client'
import { useRef, useState } from 'react'
import { api, Category } from '@/lib/api'
import { useApp } from '@/store/app'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { ImagePlus, Loader2, Plus, Star, Trash2, X } from 'lucide-react'

const MAX_PHOTOS = 8
const MAX_CHARS = 480_000 // ≈350KB data URL, mirrors the server limit
const MIN_DIM = 500 // px — below this the zoom/detail view looks blurry
const MAX_DIM_INPUT = 6000 // px — reject absurd sources instead of silently crushing them
const PRESETS = ['#17181c', '#f4f4f5', '#1e40af', '#b91c1c', '#166534', '#d9c7a7', '#7db4d8', '#a21caf']

type ColorRow = { name: string; hex: string; stock: string; extraPriceETB: string }
type FormShape = { title: string; description: string; priceETB: string; stock: string; weightKg: string; categoryId: string; sizes: string }
type PhotoRow = { dataUrl: string; alt: string; colorIndex: number | null }

/**
 * Client-side image normalization (upload-side optimization):
 * 1. decode + downscale to max 1600px (plenty for the zoom view, tiny payload)
 * 2. encode WebP **and** JPEG, keep the smaller (WebP ≈ 25–35% lighter; older
 *    Safari silently returns PNG, so we verify the mime prefix)
 * 3. re-encode at falling quality until under the size budget
 * Canvas re-drawing also strips EXIF (GPS/location) automatically.
 */
async function compressImage(file: File, maxDim = 1600, quality = 0.82): Promise<string> {
  const dataUrl = await new Promise<string>((resolve, reject) => {
    const fr = new FileReader()
    fr.onload = () => resolve(fr.result as string)
    fr.onerror = () => reject(new Error('Could not read the file'))
    fr.readAsDataURL(file)
  })
  const img = await new Promise<HTMLImageElement>((resolve, reject) => {
    const im = new Image()
    im.onload = () => resolve(im)
    im.onerror = () => reject(new Error('Not a readable image — use JPG, PNG or WebP'))
    im.src = dataUrl
  })
  const longest = Math.max(img.width, img.height)
  if (longest < MIN_DIM) throw new Error(`"${file.name}" is too small (${img.width}×${img.height}) — use at least ${MIN_DIM}px`)
  if (longest > MAX_DIM_INPUT) throw new Error(`"${file.name}" is too large — max ${MAX_DIM_INPUT}px on the longest side`)
  const scale = Math.min(1, maxDim / longest)
  const w = Math.max(1, Math.round(img.width * scale))
  const h = Math.max(1, Math.round(img.height * scale))
  const canvas = document.createElement('canvas')
  canvas.width = w
  canvas.height = h
  const ctx = canvas.getContext('2d')
  if (!ctx) return dataUrl
  ctx.drawImage(img, 0, 0, w, h)
  const encode = (fmt: string, q: number) => canvas.toDataURL(fmt, q)
  const jpeg = encode('image/jpeg', quality)
  const webp = encode('image/webp', quality)
  const preferWebp = webp.startsWith('data:image/webp') && webp.length <= jpeg.length
  let fmt = preferWebp ? 'image/webp' : 'image/jpeg'
  let q = quality
  let out = preferWebp ? webp : jpeg
  while (out.length > MAX_CHARS && q > 0.4) {
    q -= 0.15
    out = encode(fmt, q)
    // if WebP still can't fit at q=0.4, JPEG usually compresses photos better
    if (out.length > MAX_CHARS && fmt === 'image/webp' && q <= 0.4) { fmt = 'image/jpeg'; q = quality; out = encode(fmt, q) }
  }
  return out
}

export function ProductFormDialog({
  open, onOpenChange, categories, onCreated, platform,
}: {
  open: boolean
  onOpenChange: (v: boolean) => void
  categories: Category[]
  onCreated: () => void
  platform?: boolean
}) {
  const app = useApp()
  const fileRef = useRef<HTMLInputElement>(null)
  const [busy, setBusy] = useState(false)
  const [uploading, setUploading] = useState(false)
  const [dragOver, setDragOver] = useState(false)
  const [error, setError] = useState('')
  const [hasColors, setHasColors] = useState(false)
  const [form, setForm] = useState<FormShape>({ title: '', description: '', priceETB: '', stock: '', weightKg: '1', categoryId: '', sizes: '' })
  const [colors, setColors] = useState<ColorRow[]>([])
  const [photos, setPhotos] = useState<PhotoRow[]>([])

  const reset = () => {
    setForm({ title: '', description: '', priceETB: '', stock: '', weightKg: '1', categoryId: '', sizes: '' })
    setColors([]); setPhotos([]); setHasColors(false); setError('')
  }

  const addFiles = async (files: FileList | File[]) => {
    setError('')
    const list = [...files].filter(f => f.type.startsWith('image/'))
    if (!list.length) return
    setUploading(true)
    try {
      const room = MAX_PHOTOS - photos.length
      const taken = list.slice(0, Math.max(0, room))
      const urls: PhotoRow[] = []
      for (const f of taken) urls.push({ dataUrl: await compressImage(f), alt: '', colorIndex: hasColors && colors.length ? 0 : null })
      setPhotos(prev => [...prev, ...urls])
      if (list.length > room) app.notify(`Only ${MAX_PHOTOS} photos per listing — ${list.length - room} file(s) skipped`)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'One of the files could not be read')
    } finally {
      setUploading(false)
      if (fileRef.current) fileRef.current.value = ''
    }
  }

  const addColor = () => {
    if (colors.length >= 6) return
    setColors(c => [...c, { name: '', hex: PRESETS[c.length % PRESETS.length], stock: '', extraPriceETB: '0' }])
    setHasColors(true)
  }

  const colorWarning = hasColors && colors.length > 0 && photos.some(p => p.colorIndex === null)
  const canSubmit =
    !!form.title && !!form.description && !!parseFloat(form.priceETB) && !!form.categoryId && photos.length > 0 && !uploading &&
    (!hasColors || (colors.length > 0 && colors.every(c => c.name.trim() && c.stock !== ''))) &&
    (hasColors || form.stock !== '')

  const submit = async () => {
    setBusy(true); setError('')
    try {
      const payload: Record<string, unknown> = {
        title: form.title, description: form.description,
        priceETB: parseFloat(form.priceETB), weightKg: parseFloat(form.weightKg) || 1,
        categoryId: form.categoryId,
        stock: hasColors ? 0 : parseInt(form.stock, 10),
        sizes: form.sizes.trim() ? form.sizes.split(',').map(s => s.trim()).filter(Boolean) : undefined,
        images: photos.map(p => ({ url: p.dataUrl, alt: p.alt, colorIndex: hasColors ? p.colorIndex : null })),
        colors: hasColors
          ? colors.map(c => ({ name: c.name.trim(), hex: c.hex, stock: parseInt(c.stock, 10), extraPriceETB: parseFloat(c.extraPriceETB) || 0 }))
          : [],
      }
      await api.post('/api/catalog/products', payload)
      app.notify(platform ? 'Product published to the MarketHub store' : 'Product submitted — it goes live after admin moderation')
      onOpenChange(false); reset(); onCreated()
    } catch (e) { setError(e instanceof Error ? e.message : 'Failed to create product') }
    finally { setBusy(false) }
  }

  const totalColorStock = colors.reduce((a, c) => a + (parseInt(c.stock, 10) || 0), 0)

  return (
    <Dialog open={open} onOpenChange={v => { onOpenChange(v); if (!v) reset() }}>
      <DialogContent className="max-h-[92vh] overflow-y-auto sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>{platform ? 'Add platform product' : 'Add product'}</DialogTitle>
          <DialogDescription>
            {platform ? 'First-party listings go live immediately.' : 'New listings go to the admin moderation queue before going live.'}{' '}
            Photos are auto-compressed in the browser; add up to {MAX_PHOTOS} from different angles, and tag each with a color to build that color&apos;s gallery.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-5">
          {/* ---- Basics ---- */}
          <div className="space-y-3">
            <div><Label htmlFor="pTitle">Title</Label><Input id="pTitle" maxLength={140} value={form.title} onChange={e => setForm(f => ({ ...f, title: e.target.value }))} /></div>
            <div><Label htmlFor="pDesc">Description</Label><Textarea id="pDesc" value={form.description} onChange={e => setForm(f => ({ ...f, description: e.target.value }))} rows={3} /></div>
            <div className="grid grid-cols-2 gap-3">
              <div><Label htmlFor="pPrice">Base price (Br)</Label><Input id="pPrice" value={form.priceETB} onChange={e => setForm(f => ({ ...f, priceETB: e.target.value }))} inputMode="decimal" /></div>
              <div><Label htmlFor="pWeight">Weight (kg)</Label><Input id="pWeight" value={form.weightKg} onChange={e => setForm(f => ({ ...f, weightKg: e.target.value }))} inputMode="decimal" /></div>
            </div>
            <div className="grid grid-cols-2 gap-3">
              <div>
                <Label>Category</Label>
                <Select value={form.categoryId} onValueChange={v => setForm(f => ({ ...f, categoryId: v }))}>
                  <SelectTrigger aria-label="Category"><SelectValue placeholder="Select…" /></SelectTrigger>
                  <SelectContent>{categories.map(c => <SelectItem key={c.id} value={c.id}>{c.name}</SelectItem>)}</SelectContent>
                </Select>
              </div>
              {hasColors ? (
                <div>
                  <Label htmlFor="pStockColors">Stock across colors</Label>
                  <Input id="pStockColors" disabled value={String(totalColorStock)} />
                  <p className="mt-1 text-[10px] text-muted-foreground">= sum of per-color stock</p>
                </div>
              ) : (
                <div><Label htmlFor="pStock">Stock</Label><Input id="pStock" value={form.stock} onChange={e => setForm(f => ({ ...f, stock: e.target.value }))} inputMode="numeric" /></div>
              )}
            </div>
            <div>
              <Label htmlFor="pSizes">Sizes / options (optional)</Label>
              <Input id="pSizes" value={form.sizes} onChange={e => setForm(f => ({ ...f, sizes: e.target.value }))} placeholder="S, M, L, XL — or 128GB, 256GB" />
              <p className="mt-1 text-[10px] text-muted-foreground">Comma-separated. Each color × size becomes its own SKU with its own stock — buyers pick exactly what they want.</p>
            </div>
          </div>

          {/* ---- Color variants ---- */}
          <div className="rounded-xl border p-3">
            <div className="flex items-center justify-between">
              <div>
                <p className="text-sm font-medium">Color variants</p>
                <p className="text-xs text-muted-foreground">Optional — each color gets its own stock, gallery and optional extra price.</p>
              </div>
              <div className="flex items-center gap-2">
                <Label htmlFor="hasColors" className="text-xs text-muted-foreground">{hasColors ? 'On' : 'Off'}</Label>
                <Switch id="hasColors" checked={hasColors} onCheckedChange={v => { setHasColors(v); if (!v) { setColors([]); setPhotos(ps => ps.map(p => ({ ...p, colorIndex: null }))) } }} />
              </div>
            </div>
            {hasColors && (
              <div className="mt-3 space-y-2">
                {colors.length === 0 && <p className="rounded-lg bg-muted/60 p-2 text-xs text-muted-foreground">No colors yet — add at least one (e.g. Black, Leather Brown…).</p>}
                {colors.map((c, i) => (
                  <div key={i} className="grid grid-cols-[auto_1fr_1fr_84px_28px] items-center gap-2">
                    <input
                      type="color" aria-label={`Color ${i + 1} swatch`} value={c.hex}
                      onChange={e => setColors(arr => arr.map((x, j) => j === i ? { ...x, hex: e.target.value } : x))}
                      className="h-8 w-8 cursor-pointer rounded-md border bg-transparent p-0.5"
                    />
                    <Input value={c.name} placeholder="Name (e.g. Black)" aria-label={`Color ${i + 1} name`} onChange={e => setColors(arr => arr.map((x, j) => j === i ? { ...x, name: e.target.value } : x))} />
                    <Input value={c.stock} placeholder="Stock" aria-label={`Color ${i + 1} stock`} inputMode="numeric" onChange={e => setColors(arr => arr.map((x, j) => j === i ? { ...x, stock: e.target.value } : x))} />
                    <Input value={c.extraPriceETB} placeholder="+Br" aria-label={`Color ${i + 1} extra price`} inputMode="decimal" onChange={e => setColors(arr => arr.map((x, j) => j === i ? { ...x, extraPriceETB: e.target.value } : x))} />
                    <Button variant="ghost" size="sm" className="h-8 w-8 p-0 text-muted-foreground" aria-label={`Remove color ${i + 1}`} onClick={() => {
                      setColors(arr => arr.filter((_, j) => j !== i))
                      setPhotos(ps => ps.map(p => (p.colorIndex === i ? { ...p, colorIndex: null } : p.colorIndex !== null && p.colorIndex > i ? { ...p, colorIndex: p.colorIndex - 1 } : p)))
                    }}><Trash2 className="h-4 w-4" /></Button>
                  </div>
                ))}
                <div className="flex flex-wrap items-center gap-2">
                  <Button type="button" variant="outline" size="sm" disabled={colors.length >= 6} onClick={addColor}><Plus className="mr-1 h-3.5 w-3.5" />Add color</Button>
                  <div className="flex flex-wrap gap-1">
                    {PRESETS.map(hex => (
                      <button key={hex} type="button" title={`Use ${hex}`} aria-label={`Preset swatch ${hex}`} className="h-5 w-5 rounded-full border border-black/10" style={{ background: hex }}
                        onClick={() => setColors(arr => arr.length ? arr.map((x, j) => j === arr.length - 1 ? { ...x, hex } : x) : arr)} />
                    ))}
                  </div>
                  <span className="ml-auto text-[11px] text-muted-foreground">+Br = extra price for that color</span>
                </div>
              </div>
            )}
          </div>

          {/* ---- Photos ---- */}
          <div className="rounded-xl border p-3">
            <p className="text-sm font-medium">Photos <span className="font-normal text-muted-foreground">({photos.length}/{MAX_PHOTOS}) — first one is the cover</span></p>
            <div
              onDragOver={e => { e.preventDefault(); setDragOver(true) }}
              onDragLeave={() => setDragOver(false)}
              onDrop={e => { e.preventDefault(); setDragOver(false); void addFiles(e.dataTransfer.files) }}
              className={`mt-2 flex flex-col items-center justify-center gap-1 rounded-lg border-2 border-dashed p-4 text-center transition-colors ${dragOver ? 'border-primary bg-primary/5' : 'border-border'}`}
            >
              <ImagePlus className="h-6 w-6 text-muted-foreground" aria-hidden />
              <p className="text-xs text-muted-foreground">Drag &amp; drop photos here, or</p>
              <Button type="button" variant="secondary" size="sm" disabled={uploading || photos.length >= MAX_PHOTOS} onClick={() => fileRef.current?.click()}>
                {uploading ? <><Loader2 className="mr-1 h-3.5 w-3.5 animate-spin" />Processing…</> : <>Choose files</>}
              </Button>
              <input ref={fileRef} type="file" accept="image/jpeg,image/png,image/webp" multiple className="hidden" aria-label="Upload product photos" onChange={e => e.target.files && addFiles(e.target.files)} />
            </div>

            {photos.length > 0 && (
              <div className="mt-3 grid gap-3 sm:grid-cols-2">
                {photos.map((p, i) => (
                  <div key={i} className="rounded-lg border p-2">
                    <div className="relative">
                      <img src={p.dataUrl} alt={`Photo ${i + 1} preview`} className="h-28 w-full rounded-md object-cover" />
                      {i === 0 && <span className="absolute left-1 top-1 inline-flex items-center gap-1 rounded bg-black/60 px-1.5 py-0.5 text-[10px] text-white"><Star className="h-2.5 w-2.5 fill-amber-400 text-amber-400" />Cover</span>}
                      <button type="button" aria-label={`Remove photo ${i + 1}`} className="absolute right-1 top-1 rounded bg-black/60 p-1 text-white hover:bg-black/80"
                        onClick={() => setPhotos(ps => ps.filter((_, j) => j !== i))}><X className="h-3 w-3" /></button>
                    </div>
                    <div className="mt-2 space-y-1.5">
                      <Input value={p.alt} placeholder="Angle / view (e.g. Front, Back…)" aria-label={`Photo ${i + 1} angle label`} className="h-7 text-xs"
                        onChange={e => setPhotos(ps => ps.map((x, j) => j === i ? { ...x, alt: e.target.value } : x))} />
                      {hasColors && colors.length > 0 && (
                        <Select value={p.colorIndex === null ? 'shared' : String(p.colorIndex)}
                          onValueChange={v => setPhotos(ps => ps.map((x, j) => j === i ? { ...x, colorIndex: v === 'shared' ? null : parseInt(v, 10) } : x))}>
                          <SelectTrigger className="h-7 text-xs" aria-label={`Photo ${i + 1} color`}><SelectValue /></SelectTrigger>
                          <SelectContent>
                            <SelectItem value="shared">All colors (shared)</SelectItem>
                            {colors.map((c, ci) => (
                              <SelectItem key={ci} value={String(ci)}>{c.name || `Color ${ci + 1}`}</SelectItem>
                            ))}
                          </SelectContent>
                        </Select>
                      )}
                      {i !== 0 && (
                        <Button type="button" variant="ghost" size="sm" className="h-6 w-full text-[11px] text-muted-foreground"
                          onClick={() => setPhotos(ps => { const copy = [...ps]; [copy[0], copy[i]] = [copy[i], copy[0]]; return copy })}>
                          <Star className="mr-1 h-3 w-3" />Make cover
                        </Button>
                      )}
                    </div>
                  </div>
                ))}
              </div>
            )}
            {colorWarning && <p className="mt-2 text-[11px] text-amber-700">Tip: photos tagged “All colors” appear in every color&apos;s gallery — tag some photos to a specific color to give each colorway its own shots.</p>}
          </div>

          {error && <p className="text-sm text-red-600">{error}</p>}
        </div>

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)}>Cancel</Button>
          <Button disabled={busy || !canSubmit} onClick={submit}>
            {busy ? 'Saving…' : platform ? 'Publish product' : 'Submit for review'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
