// MarketHub service worker — resilient caching for flaky Ethiopian networks.
// Strategy:
//   - Images (next/image optimized): cache-first + background refresh — a photo
//     seen once never costs data again, even fully offline.
//   - API GETs: network-first with 3.5s timeout, fall back to the last cached
//     response so the storefront still renders on a dead connection.
//   - Navigation/API writes: network only (never cache mutations).
// Registered only in production builds (see page.tsx) so dev stays live.
const VERSION = 'mh-v2'
const IMG_CACHE = `${VERSION}-img`
const API_CACHE = `${VERSION}-api`

self.addEventListener('install', e => e.waitUntil(caches.keys().then(keys => Promise.all(keys.filter(k => !k.startsWith(VERSION)).map(k => caches.delete(k))))))

self.addEventListener('activate', e => self.clients.claim())

self.addEventListener('fetch', event => {
  const { request } = event
  if (request.method !== 'GET') return
  const url = new URL(request.url)

  // Optimized images → cache-first (immutable content-hash URLs)
  if (url.pathname.startsWith('/_next/image') || /\.(png|jpe?g|webp|avif|svg|gif)$/.test(url.pathname)) {
    event.respondWith(cacheFirst(request, IMG_CACHE))
    return
  }
  // Same-origin API GETs → network-first w/ timeout fallback
  if (url.pathname.startsWith('/api/')) {
    event.respondWith(networkFirst(request, API_CACHE, 3500))
  }
})

async function cacheFirst(request, cacheName) {
  const cache = await caches.open(cacheName)
  const hit = await cache.match(request)
  if (hit) {
    // Refresh in the background; serve stale immediately (stale-while-revalidate).
    event_waitUntilSafe(cache, request)
    return hit
  }
  try {
    const res = await fetch(request)
    if (res.ok) cache.put(request, res.clone())
    return res
  } catch {
    return hit ?? Response.error()
  }
}

function event_waitUntilSafe(cache, request) {
  fetch(request).then(res => { if (res.ok) cache.put(request, res.clone()) }).catch(() => {})
}

async function networkFirst(request, cacheName, timeoutMs) {
  const cache = await caches.open(cacheName)
  try {
    const res = await Promise.race([
      fetch(request),
      new Promise((_, rej) => setTimeout(() => rej(new Error('timeout')), timeoutMs)),
    ])
    if (res && res.ok) cache.put(request, res.clone())
    return res
  } catch {
    const hit = await cache.match(request)
    if (hit) return hit
    return new Response(JSON.stringify({ error: 'offline' }), { status: 503, headers: { 'Content-Type': 'application/json' } })
  }
}
