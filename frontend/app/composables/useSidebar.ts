// localStorage キー命名規約: 本プロジェクトでは `undeuxsales:` プレフィックスを付け、
// コロン区切りでスコープを表現する。useState キーはハイフン区切り（Nuxt のキー慣習）。
const STORAGE_KEY = 'undeuxsales:sidebar:collapsed'

// サイドバー幅クラスを 1 箇所に集約（SoT）。
// 将来サイズを変えるときはここだけ書き換える。Tailwind JIT 検出のため
// 完全なクラス名リテラルを保持する。
const WIDTH_CLASS_EXPANDED = 'w-64'
const WIDTH_CLASS_COLLAPSED = 'w-16'
const LG_MAIN_PADDING_EXPANDED = 'lg:pl-64'
const LG_MAIN_PADDING_COLLAPSED = 'lg:pl-16'

// PC 表示と判定するブレイクポイント（Tailwind の lg と一致）。
const DESKTOP_BREAKPOINT_QUERY = '(min-width: 1024px)'

function readStoredCollapsed(): boolean {
  if (typeof window === 'undefined') return false
  try {
    return window.localStorage.getItem(STORAGE_KEY) === '1'
  } catch (error) {
    console.warn('[useSidebar] localStorage 読込失敗（プライベートブラウジング等）:', error)
    return false
  }
}

function writeStoredCollapsed(value: boolean): void {
  if (typeof window === 'undefined') return
  try {
    window.localStorage.setItem(STORAGE_KEY, value ? '1' : '0')
  } catch (error) {
    // 非ブロッキング: 書込失敗でも UI 状態は維持する。
    console.warn('[useSidebar] localStorage 書込失敗（プライベートブラウジング/容量超過等）:', error)
  }
}

function removeStoredCollapsed(): void {
  if (typeof window === 'undefined') return
  try {
    window.localStorage.removeItem(STORAGE_KEY)
  } catch (error) {
    console.warn('[useSidebar] localStorage 削除失敗:', error)
  }
}

/**
 * サイドバーの開閉状態と関連クラスを集約するコンポーザブル。
 *
 * 状態:
 * - `collapsed`: PC 時の折りたたみ（アイコンのみ表示）状態。localStorage に永続化。
 * - `mobileOpen`: モバイル時のドロワーオーバーレイ表示状態（永続化しない）。
 * - `transitionsEnabled`: 初回マウント完了後に true。永続化された collapsed=true の
 *   ユーザーが、最初の paint で展開→折りたたみのアニメーションを見ない（FOUC 抑止）。
 *
 * 派生クラス（SoT を 1 箇所に集約）:
 * - `sidebarWidthClass`: サイドバー本体の幅クラス（`w-16` / `w-64`）
 * - `mainPaddingClass`: メイン領域の左パディングクラス（`lg:pl-16` / `lg:pl-64`）
 *
 * 副作用フック（呼び出し側が onMounted で start、onBeforeUnmount で cleanup を呼ぶ）:
 * - `subscribeStorageSync`: 他タブとの開閉状態同期
 * - `subscribeBreakpoint`: PC ブレイクポイント以上に広がったらモバイルドロワーを閉じる
 *
 * SSR は無効（nuxt.config.ts: ssr: false）だが、useState で複数コンポーネント間の
 * 状態を共有するため Nuxt のグローバル state パターンを利用する。
 */
export function useSidebar() {
  const collapsed = useState<boolean>('sidebar-collapsed', readStoredCollapsed)
  const mobileOpen = useState<boolean>('sidebar-mobile-open', () => false)
  const transitionsEnabled = useState<boolean>('sidebar-transitions-enabled', () => false)

  const sidebarWidthClass = computed(() =>
    collapsed.value ? WIDTH_CLASS_COLLAPSED : WIDTH_CLASS_EXPANDED,
  )
  const mainPaddingClass = computed(() =>
    collapsed.value ? LG_MAIN_PADDING_COLLAPSED : LG_MAIN_PADDING_EXPANDED,
  )

  function toggleCollapsed(): void {
    collapsed.value = !collapsed.value
    writeStoredCollapsed(collapsed.value)
  }

  function closeMobile(): void {
    mobileOpen.value = false
  }

  function toggleMobile(): void {
    mobileOpen.value = !mobileOpen.value
  }

  function enableTransitions(): void {
    transitionsEnabled.value = true
  }

  /** ログアウト時に呼び出し、別ユーザーへ設定が引き継がれないようにする。 */
  function clearStored(): void {
    removeStoredCollapsed()
    collapsed.value = false
  }

  /**
   * 他タブで storage が変更されたときに collapsed を同期する。
   * 戻り値は登録解除関数。onBeforeUnmount で必ず呼ぶこと。
   */
  function subscribeStorageSync(): () => void {
    if (typeof window === 'undefined') return () => {}
    const handler = (event: StorageEvent): void => {
      if (event.key !== STORAGE_KEY) return
      // event.newValue は null（removeItem 時）または '1' / '0'
      collapsed.value = event.newValue === '1'
    }
    window.addEventListener('storage', handler)
    return () => window.removeEventListener('storage', handler)
  }

  /**
   * PC ブレイクポイント以上に画面が広がったら mobileOpen を強制的に閉じる。
   * モバイルでドロワーを開いたままウィンドウをリサイズした際、再びモバイル幅に
   * 戻したときに意図しないドロワー復元が起こるのを防ぐ。
   *
   * 戻り値は登録解除関数。onBeforeUnmount で必ず呼ぶこと。
   */
  function subscribeBreakpoint(): () => void {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
      return () => {}
    }
    const mql = window.matchMedia(DESKTOP_BREAKPOINT_QUERY)
    const handler = (event: MediaQueryListEvent): void => {
      if (event.matches) {
        mobileOpen.value = false
      }
    }
    // 初期判定: PC 幅にいる場合は念のため閉じる。
    if (mql.matches) {
      mobileOpen.value = false
    }
    mql.addEventListener('change', handler)
    return () => mql.removeEventListener('change', handler)
  }

  return {
    collapsed,
    mobileOpen,
    transitionsEnabled,
    sidebarWidthClass,
    mainPaddingClass,
    toggleCollapsed,
    closeMobile,
    toggleMobile,
    enableTransitions,
    clearStored,
    subscribeStorageSync,
    subscribeBreakpoint,
  }
}
