'use client'

import { useEffect } from 'react'
import dynamic from 'next/dynamic'
import { useApp } from '@/store/app'
import { Header, HomeView, ProductView, OrdersView, WishlistView } from '@/components/marketplace/storefront'
import { Skeleton } from '@/components/ui/skeleton'
import { Button } from '@/components/ui/button'

// Route-level code splitting: admin/vendor/checkout/auth are separate chunks,
// loaded only when actually opened. Keeps the initial JS bundle small — that's
// parse/CPU time saved on budget phones and KBs saved on metered 3G.
const ConsoleSkeleton = () => (
  <div className="mx-auto max-w-7xl px-4 py-16" aria-busy="true" aria-label="Loading">
    <Skeleton className="h-72 w-full rounded-3xl" />
  </div>
)

const CheckoutView = dynamic(() => import('@/components/marketplace/checkout').then(m => ({ default: m.CheckoutView })), { loading: ConsoleSkeleton })
const AdminConsole = dynamic(() => import('@/components/marketplace/admin').then(m => ({ default: m.AdminConsole })), { loading: ConsoleSkeleton })
const VendorConsole = dynamic(() => import('@/components/marketplace/vendor').then(m => ({ default: m.VendorConsole })), { loading: ConsoleSkeleton })
const AuthDialog = dynamic(() => import('@/components/marketplace/auth-dialog').then(m => ({ default: m.AuthDialog })), { ssr: false })
const PolicyViewer = dynamic(() => import('@/components/marketplace/auth-dialog').then(m => ({ default: m.PolicyViewer })), { ssr: false })

function Toast() {
  const toast = useApp(s => s.toast)
  if (!toast) return null
  return (
    <div
      role="status"
      className={`fixed bottom-6 left-1/2 z-50 -translate-x-1/2 rounded-full px-4 py-2.5 text-sm font-medium shadow-lg ${toast.kind === 'err' ? 'bg-red-600 text-white' : 'bg-emerald-700 text-white'}`}
    >
      {toast.msg}
    </div>
  )
}

function Footer() {
  const app = useApp()
  return (
    <footer className="mt-auto border-t border-black/5 bg-card">
      <div className="mx-auto max-w-7xl px-4 py-10">
        <div className="grid gap-8 sm:grid-cols-2 lg:grid-cols-4">
          <div className="sm:col-span-2">
            <div className="flex items-center gap-2 font-semibold">
              <span className="flex h-8 w-8 items-center justify-center rounded-xl bg-gradient-to-br from-emerald-700 to-teal-600 text-xs text-white">MH</span>
              <span className="font-display text-lg">MarketHub<span className="text-primary">.et</span></span>
            </div>
            <p className="mt-3 max-w-sm text-xs leading-5 text-muted-foreground">
              Ethiopia&apos;s multi-vendor marketplace platform — KYC-verified sellers, transparent 15% VAT,
              zone-based nationwide delivery and 12-hour vendor payouts. Sandbox preview running on mock data;
              the production stack is .NET 9 + PostgreSQL + Next.js 16.
            </p>
            <div className="mt-4 flex flex-wrap gap-1.5" aria-label="Payment methods">
              {['telebirr', 'Chapa', 'Visa', 'Mastercard', 'Cash on delivery'].map(m => (
                <span key={m} className="rounded-lg border border-black/5 bg-muted/60 px-2.5 py-1 text-[10px] font-semibold text-muted-foreground">{m}</span>
              ))}
            </div>
          </div>
          <div className="grid gap-2 content-start">
            <span className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">Policies</span>
            {['TermsOfService', 'RulesAndRegulations', 'ReturnAndRefundPolicy'].map(k => (
              <button key={k} className="text-left text-xs text-muted-foreground transition-colors hover:text-foreground hover:underline" onClick={() => app.setPolicyView(k)}>
                {k.replace(/([A-Z])/g, ' $1').trim()}
              </button>
            ))}
          </div>
          <div className="grid content-start gap-2 text-xs text-muted-foreground">
            <span className="text-xs font-semibold uppercase tracking-wider">Platform</span>
            <span>VAT 15% · ETB · +251 phone format</span>
            <span>Vendor KYC · 3+ legal documents</span>
            <span>Vendor payouts within 12 hours</span>
            <span>7-day return &amp; refund window</span>
          </div>
        </div>
        <div className="mt-8 flex flex-wrap items-center justify-between gap-2 border-t pt-4 text-[11px] text-muted-foreground">
          <span>© 2026 MarketHub Technologies PLC · Preview environment — payments are simulated via the MockGateway</span>
          <span>Made in Addis Ababa, for all of Ethiopia</span>
        </div>
      </div>
    </footer>
  )
}

export default function Home() {
  const route = useApp(s => s.route)
  const go = useApp(s => s.go)
  const refreshSession = useApp(s => s.refreshSession)
  const initDataSaver = useApp(s => s.initDataSaver)

  useEffect(() => {
    void refreshSession()
    initDataSaver() // localStorage pref → Network Information API detection
    // restore language preference
    try {
      const saved = window.localStorage.getItem('mh-lang')
      if (saved === 'am' || saved === 'en') useApp.getState().setLang(saved)
    } catch { /* private mode */ }
    const onHash = () => {
      const h = window.location.hash.replace(/^#\/?/, '')
      const [view, param] = h.split('/')
      if (view === 'product' && param) go('product', param)
      else if (['cart', 'checkout', 'orders', 'admin', 'vendor', 'wishlist', 'home'].includes(view || '')) go((view || 'home') as 'home')
    }
    window.addEventListener('hashchange', onHash)
    onHash()
    // PWA service worker — production only, keeps dev live.
    if (process.env.NODE_ENV === 'production' && 'serviceWorker' in navigator) {
      navigator.serviceWorker.register('/sw.js').catch(() => { /* preview environments */ })
    }
    return () => window.removeEventListener('hashchange', onHash)
  }, [])

  return (
    <div className="flex min-h-screen flex-col bg-background text-foreground">
      <Header />
      <div className="flex-1">
        {route.view === 'home' && <HomeView />}
        {route.view === 'product' && route.param && <ProductView slug={route.param} />}
        {route.view === 'cart' && <HomeView />}
        {route.view === 'checkout' && <CheckoutView />}
        {route.view === 'orders' && <OrdersView />}
        {route.view === 'wishlist' && <WishlistView />}
        {route.view === 'admin' && <AdminConsole />}
        {route.view === 'vendor' && <VendorConsole />}
      </div>
      <Footer />
      <AuthDialog />
      <PolicyViewer />
      <Toast />
    </div>
  )
}
