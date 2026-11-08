/**
 * Seed gallery data — real product photos (web-sourced, OSS-rehosted) + color variants.
 * Images with `colors` get per-color image sets; `images` = shared shots (all colors).
 */
const CDN = 'https://z-cdn.chatglm.cn/image-search-mcp/images-ppt'

export type SeedColor = { name: string; hex: string; stock: number; extraPriceETB?: number; images: string[] }
export type SeedGallery = { productId: string; images: string[]; colors?: SeedColor[] }

export const GALLERY: SeedGallery[] = [
  {
    // Samsung Galaxy A15 — two colorways, each with its own shots
    productId: 'p_01',
    images: [],
    colors: [
      { name: 'Blue Black', hex: '#1b1f24', stock: 20, images: [`${CDN}/2acd03551ec2.jpg`, `${CDN}/da09b50eab57.jpg`, `${CDN}/18886654c865.jpg`, `${CDN}/41c57ee5ee0f.jpg`] },
      { name: 'Light Blue', hex: '#7db4d8', stock: 15, images: [`${CDN}/97adfd30a83d.jpg`, `${CDN}/3445c601f702.jpg`] },
    ],
  },
  { productId: 'p_02', images: [`${CDN}/9694e7bd0968.jpg`, `${CDN}/c699d1db8880.jpg`, `${CDN}/866c90b2474a.jpg`, `${CDN}/3b79c9dc867a.jpg`] },
  {
    // BassPro speaker — Black / Blue
    productId: 'p_03',
    images: [],
    colors: [
      { name: 'Black', hex: '#17181c', stock: 25, images: [`${CDN}/6030cc0ead6f.jpg`, `${CDN}/fa2fad2c8f40.jpg`, `${CDN}/c076ab2f218c.png`, `${CDN}/e03679599543.png`] },
      { name: 'Blue', hex: '#1e40af', stock: 17, images: [`${CDN}/6d775a588f96.jpg`, `${CDN}/649c89c3e2fd.jpg`, `${CDN}/82ffbcb78e0e.jpg`] },
    ],
  },
  { productId: 'p_04', images: [`${CDN}/3616b2add597.jpg`, `${CDN}/fe0ecb3d7b9a.png`, `${CDN}/a6df23037cf2.jpg`, `${CDN}/89d7a5c09b78.jpeg`] },
  {
    // Earbuds Pro — Black / White
    productId: 'p_05',
    images: [],
    colors: [
      { name: 'Black', hex: '#141414', stock: 40, images: [`${CDN}/f025da1193b3.jpg`, `${CDN}/991114c12d1c.jpg`, `${CDN}/525f0adfac3f.jpg`, `${CDN}/897f94d1711a.jpg`] },
      { name: 'White', hex: '#f4f4f5', stock: 24, images: [`${CDN}/39f75b8ca8fc.jpg`, `${CDN}/a6c915ca1892.jpg`, `${CDN}/28afe80ee59a.jpg`] },
    ],
  },
  {
    // Cotton shirt — White / Beige
    productId: 'p_06',
    images: [],
    colors: [
      { name: 'White', hex: '#fafaf7', stock: 30, images: [`${CDN}/9ed2a9e730e4.jpg`, `${CDN}/7739ad90f339.jpg`, `${CDN}/5263a7e240fa.jpg`, `${CDN}/99a930a1857b.jpg`] },
      { name: 'Beige', hex: '#d9c7a7', stock: 20, images: [`${CDN}/54f6ec9e522c.jpg`, `${CDN}/3c3f9e1ebe56.jpg`, `${CDN}/76982ccc164b.jpg`] },
    ],
  },
  { productId: 'p_07', images: [`${CDN}/77545225c386.jpg`, `${CDN}/a997b68d9d42.jpg`, `${CDN}/a590f8e3e448.jpg`, `${CDN}/ca7d9d92edbd.jpg`, `${CDN}/f06c758d49a9.jpg`] },
  { productId: 'p_08', images: [`${CDN}/871e24a7c710.jpg`, `${CDN}/5e08ad724873.jpg`, `${CDN}/4e8150a060e0.jpg`, `${CDN}/516de17a593c.jpg`] },
  { productId: 'p_09', images: [`${CDN}/0ad117452313.png`, `${CDN}/644fa9f386b0.jpg`, `${CDN}/fe6e2700e06e.png`, `${CDN}/d8094f782b02.jpg`, `${CDN}/af5bfbe8807e.jpg`] },
  { productId: 'p_10', images: [`${CDN}/9a13ad22ed7d.png`, `${CDN}/41f5a8498fa6.png`, `${CDN}/febeb8c2701b.jpg`, `${CDN}/7242e5703c21.jpg`] },
  { productId: 'p_11', images: [`${CDN}/df692c321057.webp`, `${CDN}/636c8bdcbbef.jpg`, `${CDN}/342feb6bbb94.png`, `${CDN}/dc0f06d34feb.jpg`] },
  { productId: 'p_12', images: [`${CDN}/770ab1b2d9a8.jpg`, `${CDN}/cbfc9f15db2a.jpg`, `${CDN}/1a911f706916.jpg`, `${CDN}/ff01430c104d.jpg`] },
  { productId: 'p_13', images: [`${CDN}/ad0ad55ae7bb.png`, `${CDN}/2639a82a56f6.jpg`, `${CDN}/be6dfa0ea485.jpg`, `${CDN}/a108daf85fe5.jpg`] },
  { productId: 'p_14', images: [`${CDN}/63d113d1f3ba.jpg`, `${CDN}/a70c5d7654e9.png`, `${CDN}/99f33e3e1093.jpeg`, `${CDN}/bb9037079a00.jpg`] },
  { productId: 'p_15', images: [`${CDN}/e7d91b42d5d1.jpg`, `${CDN}/dab678438aa9.jpg`, `${CDN}/dd635ffba239.jpg`, `${CDN}/fa4252b6d303.jpeg`] },
]
