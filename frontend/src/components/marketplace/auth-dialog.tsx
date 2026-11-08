// Auth dialog: sign in / sign up with role selection, vendor document uploads (3+ required),
// mandatory consent to Terms, Rules & Regulations, and Return & Refund policy.
'use client'
import { useEffect, useRef, useState } from 'react'
import ReactMarkdown from 'react-markdown'
import { api, Policy, SessionUser } from '@/lib/api'
import { useApp } from '@/store/app'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group'
import { Checkbox } from '@/components/ui/checkbox'
import { ScrollArea } from '@/components/ui/scroll-area'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Separator } from '@/components/ui/separator'
import { FileUp, FileCheck2, X } from 'lucide-react'

const REGIONS = ['Addis Ababa', 'Oromia', 'Amhara', 'Tigray', 'Sidama', 'Dire Dawa', 'Somali', 'Afar', 'Benishangul-Gumuz', 'Gambela', 'Harari', 'SNNPR', 'South West Ethiopia']
const REQUIRED_DOCS = [
  { code: 'business_license', name: 'Business Registration License', hint: 'Ministry of Trade certificate (PDF/JPG)' },
  { code: 'trade_license', name: 'Trade License', hint: 'Current fiscal year license (PDF/JPG)' },
  { code: 'tin_certificate', name: 'TIN Certificate', hint: 'Ministry of Revenue TIN certificate (PDF/JPG)' },
]
const OPTIONAL_DOCS = [
  { code: 'owner_id', name: 'Owner Identification (optional)', hint: 'ID / passport of owner' },
  { code: 'sector_permit', name: 'Sector Permit (optional)', hint: 'If applicable to your business' },
]

type DocSlot = { typeCode: string; fileName: string; sizeBytes: number }

export function AuthDialog() {
  const app = useApp()
  const [mode, setMode] = useState<'signin' | 'signup'>('signin')
  const [role, setRole] = useState<'Buyer' | 'Vendor'>('Buyer')
  const [form, setForm] = useState({ email: '', password: '', fullName: '', phone: '', businessName: '', tin: '', region: '', city: '', storeDescription: '' })
  const [docs, setDocs] = useState<DocSlot[]>([])
  const [consents, setConsents] = useState<Record<string, boolean>>({})
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const fileRefs = useRef<Record<string, HTMLInputElement | null>>({})

  useEffect(() => { if (app.authOpen) setMode(app.authMode) }, [app.authOpen, app.authMode])

  const set = (k: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement>) => setForm(f => ({ ...f, [k]: e.target.value }))

  const addDoc = (typeCode: string, file: File) => {
    if (!/\.(pdf|jpe?g|png)$/i.test(file.name)) { app.notify('Only PDF, JPG or PNG files are accepted', 'err'); return }
    if (file.size > 10 * 1024 * 1024) { app.notify('Maximum file size is 10 MB', 'err'); return }
    setDocs(d => [...d.filter(x => x.typeCode !== typeCode), { typeCode, fileName: file.name, sizeBytes: file.size }])
  }

  const requiredOk = () => {
    const provided = new Set(docs.map(d => d.typeCode))
    return REQUIRED_DOCS.every(r => provided.has(r.code)) && docs.length >= 3
  }
  const allConsents = ['TermsOfService', 'RulesAndRegulations', 'ReturnAndRefundPolicy'].every(k => consents[k])

  const signIn = async () => {
    setBusy(true); setError('')
    try {
      const user = await api.post<SessionUser>('/api/auth/login', { email: form.email, password: form.password })
      app.setUser(user)
      app.closeAuth()
      app.notify(`Welcome back, ${user.fullName.split(' ')[0]}!`)
    } catch (e) { setError(e instanceof Error ? e.message : 'Sign in failed') }
    finally { setBusy(false) }
  }

  const signUp = async () => {
    setBusy(true); setError('')
    try {
      const payload = {
        email: form.email, password: form.password, fullName: form.fullName, phone: form.phone, role,
        ...(role === 'Vendor' ? { businessName: form.businessName, tin: form.tin, region: form.region, city: form.city, storeDescription: form.storeDescription } : {}),
        documents: docs.map(d => ({ typeCode: d.typeCode, fileName: d.fileName, sizeBytes: d.sizeBytes, contentType: d.fileName.toLowerCase().endsWith('.pdf') ? 'application/pdf' : 'image/jpeg' })),
        consents: ['TermsOfService', 'RulesAndRegulations', 'ReturnAndRefundPolicy'].map(k => ({ policyKey: k, policyVersion: '1.0.0' })),
      }
      const user = await api.post<SessionUser>('/api/auth/register', payload)
      app.setUser(user)
      app.closeAuth()
      app.notify(role === 'Vendor' ? 'Application submitted! The admin team will review your documents.' : `Welcome to MarketHub, ${user.fullName.split(' ')[0]}!`)
    } catch (e) { setError(e instanceof Error ? e.message : 'Registration failed') }
    finally { setBusy(false) }
  }

  const demoFill = (email: string, password: string) => { setForm(f => ({ ...f, email, password })); setError('') }

  return (
    <Dialog open={app.authOpen} onOpenChange={v => !v && app.closeAuth()}>
      <DialogContent className="max-h-[92vh] overflow-y-auto sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>{mode === 'signin' ? 'Sign in to MarketHub' : 'Create your account'}</DialogTitle>
          <DialogDescription>
            {mode === 'signin' ? 'Shop as a buyer, or manage your vendor store.' : 'Choose your role — buyers shop, vendors sell (documents required).'}
          </DialogDescription>
        </DialogHeader>

        {mode === 'signin' ? (
          <div className="space-y-3">
            <div><Label htmlFor="email">Email</Label><Input id="email" type="email" value={form.email} onChange={set('email')} placeholder="you@example.com" /></div>
            <div><Label htmlFor="pw">Password</Label><Input id="pw" type="password" value={form.password} onChange={set('password')} placeholder="••••••••" /></div>
            {error && <p className="text-sm text-red-600">{error}</p>}
            <Button className="w-full" disabled={busy || !form.email || !form.password} onClick={signIn}>Sign in</Button>
            <div className="text-center text-sm text-muted-foreground">New here? <button className="font-medium text-primary underline" onClick={() => { setMode('signup'); setError('') }}>Create an account</button></div>
            <Separator />
            <div className="rounded-lg bg-muted/60 p-3">
              <p className="mb-2 text-xs font-medium text-muted-foreground">Demo accounts (one click):</p>
              <div className="flex flex-wrap gap-1.5">
                <Button variant="outline" size="sm" className="h-7 text-xs" onClick={() => demoFill('admin@markethub.et', 'Admin@12345')}>Admin</Button>
                <Button variant="outline" size="sm" className="h-7 text-xs" onClick={() => demoFill('vendor1@markethub.et', 'Vendor@12345')}>Vendor (approved)</Button>
                <Button variant="outline" size="sm" className="h-7 text-xs" onClick={() => demoFill('vendor2@markethub.et', 'Vendor@12345')}>Vendor 2</Button>
                <Button variant="outline" size="sm" className="h-7 text-xs" onClick={() => demoFill('buyer@markethub.et', 'Buyer@12345')}>Buyer</Button>
              </div>
            </div>
          </div>
        ) : (
          <div className="space-y-4">
            <RadioGroup value={role} onValueChange={v => setRole(v as 'Buyer' | 'Vendor')} className="grid grid-cols-2 gap-2">
              {(['Buyer', 'Vendor'] as const).map(r => (
                <Label key={r} htmlFor={`role-${r}`} className={`flex cursor-pointer flex-col gap-0.5 rounded-xl border p-3 ${role === r ? 'border-primary bg-primary/5' : ''}`}>
                  <span className="flex items-center gap-2"><RadioGroupItem id={`role-${r}`} value={r} /><span className="text-sm font-medium">{r}</span></span>
                  <span className="text-[11px] text-muted-foreground">{r === 'Buyer' ? 'Shop products' : 'Sell products · KYC required'}</span>
                </Label>
              ))}
            </RadioGroup>

            <div className="grid grid-cols-2 gap-3">
              <div><Label htmlFor="fullName">Full name</Label><Input id="fullName" value={form.fullName} onChange={set('fullName')} /></div>
              <div><Label htmlFor="regPhone">Phone</Label><Input id="regPhone" value={form.phone} onChange={set('phone')} placeholder="+2519…" /></div>
            </div>
            <div className="grid grid-cols-2 gap-3">
              <div><Label htmlFor="regEmail">Email</Label><Input id="regEmail" type="email" value={form.email} onChange={set('email')} /></div>
              <div><Label htmlFor="regPw">Password</Label><Input id="regPw" type="password" value={form.password} onChange={set('password')} placeholder="min 8 characters" /></div>
            </div>

            {role === 'Vendor' && (
              <>
                <Separator />
                <h3 className="text-sm font-semibold">Business details</h3>
                <div className="grid grid-cols-2 gap-3">
                  <div><Label htmlFor="biz">Business name</Label><Input id="biz" value={form.businessName} onChange={set('businessName')} /></div>
                  <div><Label htmlFor="tin">TIN (10 digits)</Label><Input id="tin" value={form.tin} onChange={set('tin')} inputMode="numeric" maxLength={10} /></div>
                </div>
                <div className="grid grid-cols-2 gap-3">
                  <div>
                    <Label>Region</Label>
                    <Select value={form.region} onValueChange={v => setForm(f => ({ ...f, region: v }))}>
                      <SelectTrigger aria-label="Region"><SelectValue placeholder="Select region" /></SelectTrigger>
                      <SelectContent>{REGIONS.map(r => <SelectItem key={r} value={r}>{r}</SelectItem>)}</SelectContent>
                    </Select>
                  </div>
                  <div><Label htmlFor="city">City</Label><Input id="city" value={form.city} onChange={set('city')} /></div>
                </div>
                <div><Label htmlFor="desc">Store description (optional)</Label><Input id="desc" value={form.storeDescription} onChange={set('storeDescription')} placeholder="What do you sell?" /></div>

                <h3 className="text-sm font-semibold">Legal documents <span className="text-red-600">*</span></h3>
                <p className="-mt-2 text-[11px] text-muted-foreground">At least 3 documents are required. Every required type below must be provided — your application goes to the admin review queue.</p>
                <div className="space-y-2">
                  {[...REQUIRED_DOCS, ...OPTIONAL_DOCS].map(d => {
                    const has = docs.find(x => x.typeCode === d.code)
                    return (
                      <div key={d.code} className={`flex items-center gap-3 rounded-lg border p-2.5 ${has ? 'border-emerald-300 bg-emerald-50/50' : ''}`}>
                        {has ? <FileCheck2 className="h-4 w-4 shrink-0 text-emerald-600" /> : <FileUp className="h-4 w-4 shrink-0 text-muted-foreground" />}
                        <div className="min-w-0 flex-1">
                          <div className="text-xs font-medium">{d.name}</div>
                          <div className="truncate text-[11px] text-muted-foreground">{has ? has.fileName : d.hint}</div>
                        </div>
                        <input ref={el => { fileRefs.current[d.code] = el }} type="file" accept=".pdf,.jpg,.jpeg,.png" className="hidden" aria-label={`Upload ${d.name}`} onChange={e => { const f = e.target.files?.[0]; if (f) addDoc(d.code, f) }} />
                        {has ? (
                          <Button variant="ghost" size="sm" className="h-7 text-xs text-red-600" onClick={() => setDocs(x => x.filter(y => y.typeCode !== d.code))}><X className="h-3 w-3" /></Button>
                        ) : (
                          <Button variant="outline" size="sm" className="h-7 text-xs" onClick={() => fileRefs.current[d.code]?.click()}>Upload</Button>
                        )}
                      </div>
                    )
                  })}
                </div>
              </>
            )}

            <Separator />
            <h3 className="text-sm font-semibold">Policies &amp; agreements <span className="text-red-600">*</span></h3>
            <div className="space-y-2">
              {[
                { key: 'TermsOfService', label: 'Terms of Service' },
                { key: 'RulesAndRegulations', label: 'Rules & Regulations' },
                { key: 'ReturnAndRefundPolicy', label: 'Return & Refund Policy' },
              ].map(p => (
                <div key={p.key} className="flex items-start gap-2.5">
                  <Checkbox id={`consent-${p.key}`} checked={!!consents[p.key]} onCheckedChange={v => setConsents(c => ({ ...c, [p.key]: !!v }))} className="mt-0.5" />
                  <Label htmlFor={`consent-${p.key}`} className="text-xs font-normal leading-5">
                    I have read and agree to the <button type="button" className="font-medium text-primary underline" onClick={(e) => { e.preventDefault(); app.setPolicyView(p.key) }}>{p.label}</button>
                  </Label>
                </div>
              ))}
            </div>

            {error && <p className="text-sm text-red-600">{error}</p>}
            <Button
              className="w-full" disabled={busy || !form.email || !form.password || !form.fullName
                || (role === 'Vendor' && (!requiredOk() || !form.businessName || !/^\d{10}$/.test(form.tin) || !form.region || !form.city))
                || !allConsents}
              onClick={signUp}
            >
              {role === 'Vendor' ? 'Submit vendor application' : 'Create buyer account'}
            </Button>
            {role === 'Vendor' && !requiredOk() && <p className="text-center text-[11px] text-muted-foreground">Vendor applications require all 3 mandatory documents (upload at least 3 files).</p>}
            <div className="text-center text-sm text-muted-foreground">Already have an account? <button className="font-medium text-primary underline" onClick={() => { setMode('signin'); setError('') }}>Sign in</button></div>
          </div>
        )}
      </DialogContent>
    </Dialog>
  )
}

export function PolicyViewer() {
  const app = useApp()
  const [policy, setPolicy] = useState<Policy | null>(null)
  const [loadedFor, setLoadedFor] = useState<string | null>(null)
  const loading = app.policyView !== null && loadedFor !== app.policyView

  useEffect(() => {
    const key = app.policyView
    if (!key) return
    api.get<{ policy: Policy }>(`/api/policies/${key}`)
      .then(r => { setPolicy(r.policy); setLoadedFor(key) })
      .catch(() => { setPolicy(null); setLoadedFor(key) })
  }, [app.policyView])

  return (
    <Dialog open={!!app.policyView} onOpenChange={v => !v && app.setPolicyView(null)}>
      <DialogContent className="max-h-[88vh] sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>{policy ? policy.contentMarkdown.split('\n')[0].replace(/^#\s*/, '') : 'Policy'}</DialogTitle>
          {policy && <DialogDescription>Version {policy.version} · Effective {new Date(policy.effectiveFrom).toLocaleDateString('en-GB', { day: 'numeric', month: 'long', year: 'numeric' })}</DialogDescription>}
        </DialogHeader>
        <ScrollArea className="max-h-[62vh] pr-4">
          {loading ? <div className="h-40 animate-pulse rounded bg-muted" /> : policy ? (
            <div className="prose-sm space-y-3 text-sm leading-6 [&_h2]:mt-4 [&_h2]:text-base [&_h2]:font-semibold [&_h3]:mt-3 [&_h3]:font-semibold [&_li]:ml-4 [&_li]:list-disc [&_p]:text-muted-foreground [&_strong]:text-foreground">
              <ReactMarkdown>{policy.contentMarkdown.replace(/^#.*\n/, '')}</ReactMarkdown>
            </div>
          ) : <p className="text-sm text-muted-foreground">Policy not found.</p>}
        </ScrollArea>
      </DialogContent>
    </Dialog>
  )
}
