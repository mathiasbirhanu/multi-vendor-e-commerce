// Low-bandwidth / low-power detection for Ethiopian network conditions.
//
// Ethiopia reality: many users are on 3G metered data with budget Android
// devices. We adapt the experience with a "Data saver" mode that:
//   - serves images at quality 40 (AVIF/WebP still negotiated by next/image)
//   - skips decorative network requests (hover-shot preloads)
//   - stops looping CSS animations (ticker, floating cards) → saves CPU/battery
//
// Mode resolution: user preference (localStorage) wins over auto-detection.
// Auto-detection uses the Network Information API when available:
//   navigator.connection.saveData === true  (user enabled "Data saver" in Android)
//   effectiveType is 2g / slow-2g / 3g      (est. downlink < ~700 Kbps)

export type DataSaverMode = 'auto' | 'on' | 'off'

const LS_KEY = 'mh-datasaver'

export function readDataSaverMode(): DataSaverMode {
  if (typeof window === 'undefined') return 'auto'
  try {
    const v = window.localStorage.getItem(LS_KEY)
    return v === 'on' || v === 'off' ? v : 'auto'
  } catch { return 'auto' }
}

export function writeDataSaverMode(mode: DataSaverMode) {
  try { window.localStorage.setItem(LS_KEY, mode) } catch { /* private mode */ }
}

type NMConnection = { saveData?: boolean; effectiveType?: string }

/** Best-effort auto-detection of a slow/expensive connection. */
export function detectLowData(): boolean {
  if (typeof navigator === 'undefined') return false
  const conn = (navigator as unknown as { connection?: NMConnection }).connection
  if (!conn) return false
  if (conn.saveData) return true
  return conn.effectiveType === '2g' || conn.effectiveType === 'slow-2g' || conn.effectiveType === '3g'
}

/** Mode → effective boolean. */
export function resolveDataSaver(mode: DataSaverMode): boolean {
  if (mode === 'on') return true
  if (mode === 'off') return false
  return detectLowData()
}
