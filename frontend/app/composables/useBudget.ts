import type { AccountType } from '~/types/api'

/** 予算データを保存する localStorage キー。 */
const STORAGE_KEY = 'undeux.budgets'

/** 予算の集計軸。company=全社、department=部門、businessType=業態。 */
export type BudgetScope = 'company' | 'department' | 'businessType'

/** 予算1件（年度 × 集計軸 × コード）。 */
export interface BudgetEntry {
  id: string
  /** 西暦年度。 */
  year: number
  scope: BudgetScope
  /** scope!=company のときの対象コード（部門コード／業態コード）。company は null。 */
  code: string | null
  /** 表示ラベル（全社／部門名／業態名）。 */
  label: string
  /** 売上予算（円）。サプライヤー・バイヤー共通。 */
  salesBudget: number
  /** 仕入予算（円）。バイヤー（小売）のみ。サプライヤーは null。 */
  purchaseBudget: number | null
}

function generateId(): string {
  try {
    if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
      return crypto.randomUUID()
    }
  } catch {
    // fall through
  }
  return `b_${Date.now()}_${Math.floor(Math.random() * 1e6)}`
}

/**
 * 予算（売上予算・仕入予算）の登録・参照を提供するコンポーザブル。
 *
 * 本プロダクトはモック段階のため、当面の永続化は **localStorage**（ブラウザ単位）で行う。
 * 登録値は OTB サマリー（目標売上・仕入予算）など他ページから参照される（要件: 登録値を活用）。
 *
 * **下位互換/データ保護の注意:** localStorage はユーザー/端末を跨いで共有されない。実運用では
 * バックエンド（予算テーブル＋API）への置換が前提で、本コンポーザブルの I/F（load/upsert/remove/
 * selectors）はその差し替えを見据えて分離している。SoT 移行時は保存/取得のみ API へ振り替える。
 */
export function useBudget() {
  const entries = useState<BudgetEntry[]>('budgets', () => [])
  const loaded = useState<boolean>('budgets-loaded', () => false)

  /** localStorage から読み込む（クライアントのみ・初回のみ）。 */
  function load(): void {
    if (loaded.value || import.meta.server) return
    loaded.value = true
    try {
      const raw = window.localStorage.getItem(STORAGE_KEY)
      if (!raw) return
      const parsed = JSON.parse(raw)
      if (Array.isArray(parsed)) {
        // 一意性（年度×scope×code）は upsert が保証するが、手編集・旧スキーマ混入に備え
        // ロード時にも重複排除する（後勝ち）。companyBudget 等の find が先頭1件前提のため。
        entries.value = dedupe(parsed.filter(isBudgetEntry))
      }
    } catch (error) {
      console.error('[budget] 予算データの読み込みに失敗しました:', error)
    }
  }

  function persist(): void {
    if (import.meta.server) return
    try {
      window.localStorage.setItem(STORAGE_KEY, JSON.stringify(entries.value))
    } catch (error) {
      // 保存失敗（容量・プライベートモード等）でも入力操作は止めない（原則4: 非ブロッキング）。
      console.error('[budget] 予算データの保存に失敗しました:', error)
    }
  }

  /**
   * 予算を登録/更新する（同一 年度×scope×code は1件に集約＝冪等）。
   * @returns 確定した BudgetEntry。
   */
  function upsert(input: Omit<BudgetEntry, 'id'> & { id?: string }): BudgetEntry {
    const existingIndex = entries.value.findIndex(
      (e) =>
        (input.id && e.id === input.id)
        || (e.year === input.year && e.scope === input.scope && e.code === input.code),
    )
    const entry: BudgetEntry = {
      id: input.id ?? (existingIndex >= 0 ? entries.value[existingIndex]!.id : generateId()),
      year: input.year,
      scope: input.scope,
      code: input.code,
      label: input.label,
      salesBudget: input.salesBudget,
      purchaseBudget: input.purchaseBudget,
    }
    if (existingIndex >= 0) {
      entries.value = entries.value.map((e, i) => (i === existingIndex ? entry : e))
    } else {
      entries.value = [...entries.value, entry]
    }
    persist()
    return entry
  }

  /** 予算を削除する。 */
  function remove(id: string): void {
    entries.value = entries.value.filter((e) => e.id !== id)
    persist()
  }

  /** 指定年度の予算一覧（登録順）。 */
  function entriesForYear(year: number): BudgetEntry[] {
    return entries.value.filter((e) => e.year === year)
  }

  /** 指定年度の全社予算（未登録は undefined）。 */
  function companyBudget(year: number): BudgetEntry | undefined {
    return entries.value.find((e) => e.year === year && e.scope === 'company')
  }

  return { entries, load, upsert, remove, entriesForYear, companyBudget }
}

/** ロール別に編集可能な予算項目（売上予算は共通、仕入予算はバイヤーのみ）。 */
export function budgetFieldsForRole(role: AccountType): { sales: boolean; purchase: boolean } {
  return { sales: true, purchase: role === 'buyer' }
}

/**
 * 予算管理・OTB サマリーで共有する対象年度リスト（当年を中心に前2年〜翌年）。
 * 両画面で同一レンジを参照し、「登録した予算が OTB の年度に出ない」不整合を防ぐ。
 */
export function budgetYearOptions(reference: Date = new Date()): number[] {
  const cy = reference.getFullYear()
  return [cy - 2, cy - 1, cy, cy + 1]
}

/** 年度×scope×code で重複排除する（後勝ち）。 */
function dedupe(list: BudgetEntry[]): BudgetEntry[] {
  const byKey = new Map<string, BudgetEntry>()
  for (const e of list) {
    byKey.set(`${e.year}__${e.scope}__${e.code ?? ''}`, e)
  }
  return [...byKey.values()]
}

function isBudgetEntry(value: unknown): value is BudgetEntry {
  if (typeof value !== 'object' || value === null) return false
  const v = value as Record<string, unknown>
  return (
    typeof v.id === 'string'
    && typeof v.year === 'number'
    && (v.scope === 'company' || v.scope === 'department' || v.scope === 'businessType')
    && (v.code === null || typeof v.code === 'string')
    && typeof v.label === 'string'
    && typeof v.salesBudget === 'number'
    && (v.purchaseBudget === null || typeof v.purchaseBudget === 'number')
  )
}
