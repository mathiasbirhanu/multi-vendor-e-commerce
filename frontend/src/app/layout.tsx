import type { Metadata } from "next";
import { Geist, Geist_Mono, Space_Grotesk } from "next/font/google";
import "./globals.css";
import { Toaster } from "@/components/ui/toaster";

const geistSans = Geist({
  variable: "--font-geist-sans",
  subsets: ["latin"],
});

const geistMono = Geist_Mono({
  variable: "--font-geist-mono",
  subsets: ["latin"],
});

const spaceGrotesk = Space_Grotesk({
  variable: "--font-space-grotesk",
  subsets: ["latin"],
  weight: ["500", "600", "700"],
  display: "swap",
});

export const metadata: Metadata = {
  metadataBase: new URL(process.env.NEXT_PUBLIC_SITE_URL ?? "https://markethub.et"),
  title: {
    default: "MarketHub.et — Ethiopia's Multi-Vendor Marketplace",
    template: "%s · MarketHub.et",
  },
  description: "Shop verified vendors and the MarketHub first-party store. Transparent 15% VAT, zone-based nationwide delivery, telebirr/Chapa payments, 7-day returns.",
  keywords: ["MarketHub", "Ethiopia", "marketplace", "e-commerce", "multivendor", "Addis Ababa", "online shopping Ethiopia"],
  manifest: "/manifest.webmanifest",
  applicationName: "MarketHub",
  openGraph: {
    type: "website",
    locale: "en_US",
    siteName: "MarketHub.et",
    title: "MarketHub.et — Ethiopia's Multi-Vendor Marketplace",
    description: "KYC-verified sellers, 15% VAT, nationwide zone delivery, 7-day returns and 12-hour vendor payouts.",
  },
  twitter: { card: "summary_large_image", title: "MarketHub.et", description: "Ethiopia's multi-vendor marketplace" },
  icons: {
    icon: "https://z-cdn.chatglm.cn/z-ai/static/logo.svg",
  },
};

export const viewport = {
  themeColor: "#0f766e",
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en" suppressHydrationWarning>
      {/* PWA + SEO essentials are in metadata above; JSON-LD org schema below. */}
      <body
        className={`${geistSans.variable} ${geistMono.variable} ${spaceGrotesk.variable} antialiased bg-background text-foreground`}
      >
        {/* React 19 hoists these to <head>: warm up the image CDN connection
            before the first <Image> resolves — saves a DNS+TLS round trip
            (~300-600ms on high-latency Ethiopian routes). */}
        <link rel="preconnect" href="https://z-cdn.chatglm.cn" crossOrigin="anonymous" />
        <link rel="dns-prefetch" href="https://z-cdn.chatglm.cn" />
        {children}
        <Toaster />
      </body>
    </html>
  );
}
