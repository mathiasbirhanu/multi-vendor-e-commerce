import type { NextConfig } from "next";

// ---------------------------------------------------------------------------
// Image optimization (see README "Image pipeline" section)
// - AVIF first, WebP fallback → 30–50% smaller than JPEG at equal quality
// - deviceSizes/imageSizes drive next/image `srcset`; cards pass `sizes` so a
//   phone downloads a ~20–30 KB thumb instead of the 400 KB+ original
// - minimumCacheTTL: optimized outputs are immutable (content-negotiated URLs),
//   cached by the CDN/browsers for 30 days
// - remotePatterns: strict allow-list — only the product-image CDN may be
//   optimized/proxied (blocks SSRF-style abuse of /_next/image)
// ---------------------------------------------------------------------------
const nextConfig: NextConfig = {
  output: "standalone",
  typescript: {
    ignoreBuildErrors: true,
  },
  reactStrictMode: false,
  images: {
    formats: ["image/avif", "image/webp"],
    remotePatterns: [
      { protocol: "https", hostname: "z-cdn.chatglm.cn" },
    ],
    deviceSizes: [400, 640, 750, 828, 1080, 1200, 1920, 2048],
    imageSizes: [64, 96, 128, 200, 256, 384],
    minimumCacheTTL: 60 * 60 * 24 * 30,
    // 40 = data-saver mode (2G/3G / "Data saver" toggle), 60 = list thumbs,
    // 75/80 = cards & gallery, 90 = zoom/detail views.
    qualities: [40, 60, 75, 80, 90],
  },
};

export default nextConfig;
