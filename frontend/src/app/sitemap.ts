import type { MetadataRoute } from 'next'

// Generated sitemap for SEO — static routes; product URLs are fetched from the
// catalog at build/request time. In production the .NET backend exposes the same
// data, so this file works unchanged when NEXT_PUBLIC_API_MODE=dotnet.
export const dynamic = 'force-dynamic'

export default async function sitemap(): Promise<MetadataRoute.Sitemap> {
  const base = process.env.NEXT_PUBLIC_SITE_URL ?? 'https://markethub.et'
  const statics: MetadataRoute.Sitemap = ['', '#/', '#/orders', '#/checkout'].map(h => ({
    url: `${base}/${h}`, lastModified: new Date(), changeFrequency: 'daily', priority: h === '' ? 1 : 0.6,
  }))
  try {
    const res = await fetch(`${process.env.NEXT_PUBLIC_SITE_URL ?? ''}/api/catalog/products?pageSize=48`, { cache: 'no-store' })
    if (!res.ok) return statics
    const data = (await res.json()) as { items: { slug: string; createdAt?: string }[] }
    return [
      ...statics,
      ...data.items.map(p => ({
        url: `${base}/#/product/${p.slug}`,
        lastModified: p.createdAt ? new Date(p.createdAt) : new Date(),
        changeFrequency: 'weekly' as const,
        priority: 0.8,
      })),
    ]
  } catch {
    return statics
  }
}
