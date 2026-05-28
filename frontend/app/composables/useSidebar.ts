const STORAGE_KEY = 'undeuxsales:sidebar:collapsed'

function readStoredCollapsed(): boolean {
  if (typeof window === 'undefined') return false
  try {
    return window.localStorage.getItem(STORAGE_KEY) === '1'
  } catch {
    return false
  }
}

function writeStoredCollapsed(value: boolean): void {
  if (typeof window === 'undefined') return
  try {
    window.localStorage.setItem(STORAGE_KEY, value ? '1' : '0')
  } catch {
    // localStorage が無効/容量超過でも UI 状態は維持する（非ブロッキング）。
  }
}

/**
 * サイドバーの開閉状態を管理するコンポーザブル。
 *
 * - `collapsed`: PC 時の折りたたみ（アイコンのみ表示）状態。localStorage に永続化する。
 * - `mobileOpen`: モバイル時のドロワーオーバーレイ表示状態（永続化しない）。
 *
 * SSR は無効（nuxt.config.ts: ssr: false）だが、useState で複数コンポーネント間の
 * 状態を共有するため Nuxt のグローバル state パターンを利用する。
 */
export function useSidebar() {
  const collapsed = useState<boolean>('sidebar-collapsed', readStoredCollapsed)
  const mobileOpen = useState<boolean>('sidebar-mobile-open', () => false)

  function toggleCollapsed(): void {
    collapsed.value = !collapsed.value
    writeStoredCollapsed(collapsed.value)
  }

  function setCollapsed(value: boolean): void {
    collapsed.value = value
    writeStoredCollapsed(value)
  }

  function openMobile(): void {
    mobileOpen.value = true
  }

  function closeMobile(): void {
    mobileOpen.value = false
  }

  function toggleMobile(): void {
    mobileOpen.value = !mobileOpen.value
  }

  return {
    collapsed,
    mobileOpen,
    toggleCollapsed,
    setCollapsed,
    openMobile,
    closeMobile,
    toggleMobile,
  }
}
