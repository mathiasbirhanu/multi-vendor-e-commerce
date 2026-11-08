// Lightweight i18n — English + Amharic (አማርኛ) for the storefront shell.
// Ethiopia reality: Amharic-first users are a huge untapped segment; a full page
// translation is phase 2, key commerce strings are covered here.
export type Lang = 'en' | 'am'

const dict = {
  // nav / header
  search: { en: 'Search products, brands…', am: 'ፍለጋ — ምርቶች፣ ብራንዶች…' },
  home: { en: 'Home', am: 'መነሻ' },
  wishlist: { en: 'Wishlist', am: 'የምወዳቸው' },
  cart: { en: 'Cart', am: 'ወረዳ' },
  orders: { en: 'My orders', am: 'ትዕዛዞቼ' },
  signIn: { en: 'Sign in', am: 'ግባ' },
  signOut: { en: 'Sign out', am: 'ውጣ' },
  sell: { en: 'Sell on MarketHub', am: 'ሸጥ በMarketHub' },
  admin: { en: 'Admin', am: 'አስተዳዳሪ' },
  vendor: { en: 'Vendor console', am: 'የነጋሪ ኮንሶል' },
  // home sections
  flashSale: { en: 'Flash sale', am: 'ፍላሽ ቅናሽ' },
  endsIn: { en: 'Ends in', am: 'ይከናወናል በ' },
  featured: { en: 'Featured for you', am: 'ለእርስዎ ተመርጧል' },
  topRated: { en: 'Top rated', am: 'ከፍተኛ ደረጃ' },
  newArrivals: { en: 'New arrivals', am: 'አዲስ የመጡ' },
  shopByCategory: { en: 'Shop by category', am: 'በምድብ ይግዙ' },
  viewAll: { en: 'View all', am: 'ሁሉንም ይመልከቱ' },
  sold: { en: 'sold', am: 'ተሽጧል' },
  // product
  addToCart: { en: 'Add to cart', am: 'ወደ ወረዳ ጨምር' },
  buyNow: { en: 'Buy now', am: 'አሁን ይግዙ' },
  color: { en: 'Color', am: 'ቀለም' },
  size: { en: 'Size', am: 'መጠን' },
  outOfStock: { en: 'Out of stock', am: 'አልቋል' },
  leftInStock: { en: 'left in stock', am: 'ቀሪ አለ' },
  reviews: { en: 'Reviews', am: 'ግምገማዎች' },
  writeReview: { en: 'Write a review', am: 'ግምገማ ጻፍ' },
  related: { en: 'You may also like', am: 'ሊወዱት ይችላሉ' },
  soldBy: { en: 'Sold by', am: 'ተሽጧል በ' },
  verifiedPurchase: { en: 'Verified purchase', am: 'የተረጋገጠ ግዢ' },
  deliveryTo: { en: 'Delivery', am: 'ማድረስ' },
  // checkout
  deliveryAddress: { en: 'Delivery address', am: 'የማድረስ አድራሻ' },
  addNewAddress: { en: 'Add new address', am: 'አዲስ አድራሻ ጨምር' },
  couponCode: { en: 'Coupon code', am: 'ኩፖን ኮድ' },
  apply: { en: 'Apply', am: 'ተጠቀም' },
  subtotal: { en: 'Subtotal', am: 'ድምር' },
  discount: { en: 'Discount', am: 'ቅናሽ' },
  vat: { en: 'VAT', am: 'ተገቢ ቫት' },
  total: { en: 'Total', am: 'ጠቅላላ' },
  placeOrder: { en: 'Place order', am: 'ትዕዛዝ ስጥ' },
  // orders
  deliveryPin: { en: 'Delivery PIN', am: 'የማድረስ ኮድ' },
  pinHint: { en: 'Share this PIN with the courier only when you receive your items', am: 'እቃዎት ሲደርስዎ ኮዱን ለአስረካቢው ይስጡ' },
  trackingCode: { en: 'Tracking', am: 'መከታተያ' },
  requestReturn: { en: 'Request return', am: 'መመለስ ጠይቅ' },
  cancelOrder: { en: 'Cancel order', am: 'ትዕዛዝ ሰርዝ' },
  // generic
  loading: { en: 'Loading…', am: 'በመጫን ላይ…' },
  dataSaver: { en: 'Data saver', am: 'ዳታ ቆጣቢ' },
} as const

export type I18nKey = keyof typeof dict
export const t = (key: I18nKey, lang: Lang): string => dict[key][lang] ?? dict[key].en
