// Global app state — session, hash routing, cart badge, dialogs, data saver.
'use client'
import { create } from 'zustand'
import { api, SessionUser } from '@/lib/api'
import { detectLowData, readDataSaverMode, resolveDataSaver, writeDataSaverMode, type DataSaverMode } from '@/lib/lowdata'
import type { Lang } from '@/lib/i18n'

/** Reflect the effective data-saver flag on <html data-saver> so CSS can stop
 *  looping animations (ticker, float) without re-rendering any component. */
function syncDataSaverDom(on: boolean) {
  if (typeof document !== 'undefined') document.documentElement.dataset.saver = on ? '1' : '0'
}

export type Route = { view: 'home' | 'product' | 'cart' | 'checkout' | 'orders' | 'admin' | 'vendor' | 'wishlist'; param?: string }

function parseHash(): Route {
  const h = window.location.hash.replace(/^#\/?/, '')
  const [view, param] = h.split('/')
  if (view === 'product' && param) return { view: 'product', param }
  if (['cart', 'checkout', 'orders', 'admin', 'vendor', 'wishlist'].includes(view)) return { view: view as Route['view'] }
  return { view: 'home' }
}

export type AppState = {
  route: Route
  go: (view: Route['view'], param?: string) => void
  user: SessionUser | null
  authLoading: boolean
  refreshSession: () => Promise<void>
  setUser: (u: SessionUser | null) => void
  cartCount: number
  refreshCart: () => Promise<void>
  wishlistCount: number
  refreshWishlist: () => Promise<void>
  lang: Lang
  setLang: (l: Lang) => void
  authOpen: boolean
  authMode: 'signin' | 'signup'
  openAuth: (mode?: 'signin' | 'signup') => void
  closeAuth: () => void
  policyView: string | null
  setPolicyView: (key: string | null) => void
  toast: { msg: string; kind: 'ok' | 'err' } | null
  notify: (msg: string, kind?: 'ok' | 'err') => void
  dataSaver: boolean
  dataSaverMode: DataSaverMode
  initDataSaver: () => void
  setDataSaverMode: (mode: DataSaverMode) => void
}

export const useApp = create<AppState>((set, get) => ({
  route: { view: 'home' },
  go: (view, param) => {
    window.location.hash = view === 'home' ? '#/' : `#/${view}${param ? `/${param}` : ''}`
    set({ route: { view, param } })
    window.scrollTo({ top: 0 })
  },
  user: null,
  authLoading: true,
  refreshSession: async () => {
    try {
      const { user } = await api.get<{ user: SessionUser | null }>('/api/auth/me')
      set({ user, authLoading: false })
      if (user) void get().refreshCart()
    } catch { set({ user: null, authLoading: false }) }
  },
  setUser: (u) => {
    set({ user: u })
    if (u) { void get().refreshCart(); void get().refreshWishlist() } else set({ cartCount: 0, wishlistCount: 0 })
    if (u?.lang && (u.lang === 'en' || u.lang === 'am')) set({ lang: u.lang })
  },
  cartCount: 0,
  refreshCart: async () => {
    try {
      const cart = await api.get<{ items: { quantity: number }[] }>('/api/sales/cart')
      set({ cartCount: cart.items.reduce((a, i) => a + i.quantity, 0) })
    } catch { set({ cartCount: 0 }) }
  },
  wishlistCount: 0,
  refreshWishlist: async () => {
    try {
      const wl = await api.get<{ items: unknown[] }>('/api/sales/wishlist')
      set({ wishlistCount: wl.items.length })
    } catch { set({ wishlistCount: 0 }) }
  },
  lang: 'en',
  setLang: (l) => {
    set({ lang: l })
    try { window.localStorage.setItem('mh-lang', l) } catch { /* private mode */ }
    document.documentElement.lang = l === 'am' ? 'am' : 'en'
  },
  authOpen: false,
  authMode: 'signin',
  openAuth: (mode = 'signin') => set({ authOpen: true, authMode: mode }),
  closeAuth: () => set({ authOpen: false }),
  policyView: null,
  setPolicyView: (key) => set({ policyView: key }),
  toast: null,
  notify: (msg, kind = 'ok') => {
    set({ toast: { msg, kind } })
    setTimeout(() => { if (get().toast?.msg === msg) set({ toast: null }) }, 4000)
  },
  // --- Data saver (low-bandwidth mode) -------------------------------------
  dataSaver: false,
  dataSaverMode: 'auto',
  initDataSaver: () => {
    const mode = readDataSaverMode()
    const on = resolveDataSaver(mode)
    set({ dataSaverMode: mode, dataSaver: on })
    syncDataSaverDom(on)
  },
  setDataSaverMode: (mode) => {
    writeDataSaverMode(mode)
    // 'auto' re-runs detection; the Network Information API can change live.
    const on = mode === 'auto' ? detectLowData() : mode === 'on'
    set({ dataSaverMode: mode, dataSaver: on })
    syncDataSaverDom(on)
  },
}))

/** Require an authenticated session for guarded views; opens auth dialog if guest. */
export async function requireLogin(app: AppState, then: () => void) {
  if (app.user) { then(); return }
  app.openAuth('signin')
}
